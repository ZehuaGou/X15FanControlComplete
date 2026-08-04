using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using X15FanCore.Control;
using X15FanCore.Models;

namespace X15FanControl
{
    public partial class MainForm
    {
        private static bool TryGetTierForDchuPower(int pl1, int pl2, uint timeSeconds, out AdaptivePowerTier tier)
        {
            // 档位识别必须命中安全预设（与桥接白名单一致），自定义功耗
            // 不会被识别，避免主程序识别成功但桥接拒绝。
            return AdaptivePowerTierController.TryGetTierForPower(pl1, pl2, timeSeconds, out tier);
        }

        private void UpdateAdaptivePowerPolicy(FanSnapshot snapshot, ControlDecision decision, DateTime nowUtc)
        {
            if (_config == null || _runMode != RunMode.Active ||
                _adaptivePowerTierController == null || snapshot == null)
                return;

            StrategyMode mode = _config.StrategyMode;
            AdaptivePowerTier tier;
            if (mode == StrategyMode.Auto)
            {
                if (!_config.AdaptivePower.Enabled)
                {
                    // 自动策略显式禁用：固定日常档，状态机不参与。
                    _adaptivePowerTierController.ForceTier(AdaptivePowerTier.Daily, "自动策略已禁用");
                    tier = AdaptivePowerTier.Daily;
                    _adaptiveLastReason = "自动策略已禁用，保持日常档";
                }
                else
                {
                    // CPU requested tier 只由 CPU 证据决定（架构收束）：
                    // 利用率 + 温度 + 持续时间；GPU 字段仅诊断不参与判定。
                    tier = _adaptivePowerTierController.Update(new AdaptivePowerSample
                    {
                        TimestampUtc = snapshot.TimestampUtc,
                        CpuUtilizationPercent = snapshot.CpuUtilizationPercent,
                        CpuPerformancePercent = snapshot.CpuPerformancePercent,
                        CpuTemperatureC = snapshot.CpuTemperatureC,
                        GpuTemperatureC = snapshot.GpuTemperatureC,
                        // 诊断字段（不参与 CPU 档位判定）
                        GpuUtilizationPercent = snapshot.GpuTelemetryAvailable ? snapshot.GpuTelemetryUtilization : 0,
                        GpuTelemetryAvailable = snapshot.GpuTelemetryAvailable,
                        GpuPowerWatts = snapshot.GpuTelemetryPowerWatts,
                        GpuPState = snapshot.GpuTelemetryPState
                    });
                    _adaptiveLastReason = _adaptivePowerTierController.LastReason;
                }
            }
            else
            {
                tier = GetTierForMode(mode);
                _adaptivePowerTierController.ForceTier(tier, "固定策略：" + StrategyModeInfo.GetName(mode));
                _adaptiveLastReason = "固定策略：" + StrategyModeInfo.GetName(mode) + "，不会自动升降档";
            }
            _adaptiveDesiredTier = tier;
            _adaptiveCurrentTier = tier;

            // GPU 热需求等级：独立状态机（GPU 利用率 + 实际功耗遥测 +
            // GPU 温度 + 持续时间 + 滞回）。输出只作用于 GPU 风扇曲线偏置、
            // 跨风扇辅助判定和日志/UI 诊断——不映射到任何 GPU 瓦数预设。
            _gpuThermalDemand = _gpuThermalDemandController == null
                ? GpuThermalDemand.Low
                : _gpuThermalDemandController.Update(
                    snapshot.GpuTelemetryAvailable ? snapshot.GpuTelemetryUtilization : 0,
                    snapshot.GpuTelemetryPowerWatts,
                    snapshot.GpuTelemetryAvailable ? snapshot.GpuTemperatureC : 0,
                    snapshot.GpuTelemetryAvailable,
                    snapshot.TimestampUtc);
            if (_engine != null)
            {
                _engine.GpuDemandBiasPercent = _gpuThermalDemandController == null
                    ? 0
                    : _gpuThermalDemandController.CurrentFanBiasPercent;
            }

            // 声学/热治理：把"负载请求档位"转为"声学/热预算允许档位"。
            // 固定策略为用户显式选择，不经过治理器。
            AdaptivePowerTier effective = mode == StrategyMode.Auto
                ? ApplyAcousticGovernance(tier, snapshot, decision, nowUtc)
                : tier;
            _adaptiveEffectiveTier = effective;

            // 整机协调（架构收束）：输出只有 CPU effective。CPU 有效档只在
            // CPU 自身热饱和、明确整机 Emergency、或 GPU 热饱和且共享热影响
            // 证据全部成立时被降低；单纯 GPU 利用率高不构成证据（协调器不
            // 接收 GPU 利用率输入）。GPU 无有效功率档。
            // 共享热预算让出（2026-08-03，仅 Auto）：GPU 接近热上限 + GPU 风扇
            // 接近全速 + CPU 同步高热持续 20s → CPU 有效功耗至多 Quiet；风扇
            // profile 档位与功耗档解耦（协调器 CpuFanProfileTier + 进入档位下限）。
            bool sharedThermalShedding = UpdateSharedThermalBudget(snapshot, decision, mode);
            CoolingStateInput cooling = BuildCoolingStateInput(snapshot, decision);
            cooling.SharedThermalSheddingActive = sharedThermalShedding;
            CoordinatorDecision resolved = _platformPowerCoordinator == null
                ? new CoordinatorDecision
                {
                    CpuEffective = effective,
                    CpuFanProfileTier = effective,
                    Reason = "协调器未初始化"
                }
                : _platformPowerCoordinator.Resolve(effective, cooling);
            _adaptiveEffectiveTier = resolved.CpuEffective;
            AssertGpuProductionTelemetryOnly(resolved.Reason);

            // 事件日志必须记录本周期最终结果：先填充 effective/cooling 再记录。
            if (_adaptivePowerTierController != null)
            {
                _adaptivePowerTierController.Diagnostics.EffectiveTier = _adaptiveEffectiveTier;
                _adaptivePowerTierController.Diagnostics.CoolingState = _acousticGovernor == null
                    ? CoolingState.Normal
                    : _acousticGovernor.State;
            }
            UpdateTrayStrategyStatus();
            LogTierStateEvents();

            // 1) 风扇曲线：跟随 CpuFanProfileTier（与 CPU 功耗档解耦——共享热
            //    预算让出把功耗压到 Quiet 时，风扇曲线保持让出前档位；受限时
            //    使用更低档曲线），保留控制状态平滑切换（不再 Reset，避免
            //    突响）。CPU/GPU 风扇仍按各自温度、温升率、热饱和独立控制；
            //    另一侧只有跨风扇辅助控制器判定时才提供辅助散热，不因 GPU
            //    需求高就直接拉高 CPU 风扇。
            AdaptivePowerTier fanProfileTier = resolved.CpuFanProfileTier;
            if (sharedThermalShedding)
            {
                // 风扇 profile 至少保持进入前的有效档位（让出只压功耗）。
                if (!_sharedThermalFanFloorSet)
                {
                    // 首次进入时保留进入前已经应用的风扇档；若这是一次
                    // Active 重入且尚未应用任何档（-1），则使用本周期协调器
                    // 给出的有效风扇档，绝不把无效枚举传给 profile 构造。
                    AdaptivePowerTier previousFanTier = Enum.IsDefined(
                        typeof(AdaptivePowerTier), _adaptiveFanAppliedTier)
                        ? _adaptiveFanAppliedTier
                        : resolved.CpuFanProfileTier;
                    _sharedThermalFanFloor = TierPower.IsHigherPowerTier(
                        previousFanTier, resolved.CpuFanProfileTier)
                        ? previousFanTier
                        : resolved.CpuFanProfileTier;
                    _sharedThermalFanFloorSet = true;
                }
                if (TierPower.Rank(fanProfileTier) < TierPower.Rank(_sharedThermalFanFloor))
                    fanProfileTier = _sharedThermalFanFloor;
            }
            else
            {
                _sharedThermalFanFloorSet = false;
            }
            if (_adaptiveFanAppliedTier != fanProfileTier)
            {
                ApplyFixedFanProfile(fanProfileTier);
                _adaptiveFanAppliedTier = fanProfileTier;
            }

            // 2) CPU 功耗：应用 CPU effective 档位（只应用 CPU DCHU 安全预设）。
            //    desired 变化（不同于正在应用的档位）时立即废弃旧 generation
            //    并取消旧请求，避免旧档位继续写入真实硬件。
            AdaptivePowerTier? pendingApply = _adaptivePendingApplyTier;
            bool retryExternalBackend = !_adaptiveXtuConfirmed && nowUtc >= _adaptiveNextApplyUtc;
            if (_adaptiveEffectiveTier != _adaptiveAppliedTier)
            {
                if (pendingApply != _adaptiveEffectiveTier)
                {
                    ++_adaptiveApplyGeneration;
                    try { _adaptiveApplyCts?.Cancel(); } catch { }
                    _adaptivePowerApplying = false;
                    _adaptivePendingApplyTier = null;
                    StartApplyPowerTierAsync(_adaptiveEffectiveTier, nowUtc);
                }
            }
            else if (retryExternalBackend && !_adaptivePowerApplying)
            {
                StartApplyPowerTierAsync(_adaptiveEffectiveTier, nowUtc);
            }
        }

        // 共享热预算让出控制器驱动（2026-08-03）：仅 Auto 模式启用；进入条件
        // CPU/GPU 温度遥测均有效（各自 profile min/max 校验）、GPU >= 84C、
        // CPU >= 85C（平坦 87C 也计入，不要求继续升温）、GPU 实际风扇占空
        // >= 95%，连续累计 20s；恢复 GPU <= 78C 且 CPU <= 80C 连续 60s。
        // 返回本周期是否处于让出激活。GPU 后端是否可写不参与判断（生产路径
        // 恒 TelemetryOnly）。
        private bool UpdateSharedThermalBudget(
            FanSnapshot snapshot,
            ControlDecision decision,
            StrategyMode mode)
        {
            if (_sharedThermalBudget == null || snapshot == null)
                return false;

            FanProfile profile = CreateFixedFanProfile(mode, _adaptiveFanAppliedTier);
            bool cpuValid = snapshot.CpuTemperatureC >= profile.Cpu.MinimumValidTemperatureC &&
                            snapshot.CpuTemperatureC <= profile.Cpu.MaximumValidTemperatureC;
            bool gpuValid = snapshot.GpuTelemetryAvailable &&
                            snapshot.GpuTemperatureC > 0 &&
                            snapshot.GpuTemperatureC >= profile.Gpu.MinimumValidTemperatureC &&
                            snapshot.GpuTemperatureC <= profile.Gpu.MaximumValidTemperatureC;
            // 进入门禁必须使用本周期 EC 快照中的实际占空，而不是控制器希望
            // 达到的 AppliedPercent。只有真实风扇已到 95% 才能证明散热余量耗尽。
            int gpuFanDuty = snapshot.GpuDutyPercent;

            _sharedThermalBudget.Update(
                autoMode: mode == StrategyMode.Auto,
                cpuTelemetryValid: cpuValid,
                cpuTemperatureC: snapshot.CpuTemperatureC,
                gpuTelemetryValid: gpuValid,
                gpuTemperatureC: snapshot.GpuTemperatureC,
                gpuFanDutyPercent: gpuFanDuty,
                timestampUtc: snapshot.TimestampUtc);
            return _sharedThermalBudget.SheddingActive;
        }

        // 构建协调器热输入：CPU/GPU 热饱和来自 AcousticGovernor；共享热影响
        // 证据（GPU 温度接近上限、GPU 主风扇达到软上限、温度持续不下降、
        // CPU 温度同步恶化）全部成立时才允许因 GPU 热饱和降低 CPU 档位。
        private CoolingStateInput BuildCoolingStateInput(FanSnapshot snapshot, ControlDecision decision)
        {
            CoolingStateInput input = new CoolingStateInput
            {
                CpuSaturated = _acousticGovernor != null && _acousticGovernor.CpuSaturated,
                GpuSaturated = _acousticGovernor != null && _acousticGovernor.GpuSaturated,
                // 传入协调器的 cpuRequested 已是 AcousticGovernor 输出的
                // effective，禁止对同一 CPU 饱和证据重复降档。
                CpuPowerAlreadyGoverned = true,
                Emergency = decision != null &&
                    ((decision.Cpu != null && decision.Cpu.State == ControlState.Emergency) ||
                     (decision.Gpu != null && decision.Gpu.State == ControlState.Emergency))
            };
            if (snapshot == null || decision == null || decision.Gpu == null || decision.Cpu == null)
                return input;

            StrategyMode mode = _config == null ? StrategyMode.Auto : _config.StrategyMode;
            FanProfile profile = CreateFixedFanProfile(mode, _adaptiveFanAppliedTier);
            double gpuSoftMax = profile.Gpu.SoftMaximumFanDutyPercent;
            double gpuTarget = profile.Gpu.TargetTemperatureC;

            input.GpuTemperatureNearLimit = snapshot.GpuTemperatureC > 0 &&
                snapshot.GpuTemperatureC >= gpuTarget - 2.0;
            input.GpuFanAtSoftMaximum = decision.Gpu.AppliedPercent >= gpuSoftMax - 2.0;
            input.GpuTemperatureNotFalling = decision.Gpu.TemperatureRiseRateCPerSec > -0.1;
            input.CpuTemperatureWorsening = decision.Cpu.TemperatureRiseRateCPerSec > 0.05;
            return input;
        }

        // 生产路径 GPU 后端断言（架构收束）：GPU 功耗后端必须为
        // TelemetryOnly（零 Set）。此断言只做类型检查与记录，不改变行为。
        private void AssertGpuProductionTelemetryOnly(string coordinatorReason)
        {
            if (_gpuPowerBackend == null)
                return;
            bool telemetryOnly = _gpuPowerBackend is TelemetryOnlyGpuPowerBackend;
            if (!telemetryOnly)
            {
                // 生产路径出现非 TelemetryOnly 后端：降级为 TelemetryOnly
                // 并记录（防御性兜底，绝不执行任何 GPU Set）。
                _gpuPowerBackend = ProductionGpuBackendFactory.Create();
                _gpuBackendDetail = "检测到非 TelemetryOnly GPU 后端，已强制降级为 TelemetryOnly（" + coordinatorReason + "）";
                AppendLog(_gpuBackendDetail);
            }
            else if (!string.Equals(_gpuBackendDetail, "GPU 功耗后端=" + _gpuPowerBackend.Name + "（生产路径 TelemetryOnly，零 GPU Set）", StringComparison.Ordinal))
            {
                _gpuBackendDetail = "GPU 功耗后端=" + _gpuPowerBackend.Name + "（生产路径 TelemetryOnly，零 GPU Set）";
            }
        }

        // GPU 有效档应用门禁已删除（架构收束 2026-08-02）：GPU 无有效功率
        // 档。GPU Set（NVML SetPowerLimit/OC/Offset/VF/Lock/GC6）在 MainForm
        // 中不存在任何调用路径；生产后端 TelemetryOnly 的 ApplyLimitWatts
        // 恒拒绝。

        // 构建 CPU/GPU 联合运行时诊断（供 CSV 记录；命名遵循架构收束：
        // gpu_thermal_demand / cpu_cooling_state / gpu_cooling_state /
        // cpu_fan_assist / gpu_fan_assist / emergency_override /
        // gpu_power_backend / oem_mode_observed）。
        private JointRuntimeDiagnostics BuildJointPowerDiagnostics(FanSnapshot snapshot)
        {
            JointRuntimeDiagnostics joint = new JointRuntimeDiagnostics
            {
                CpuRequestedPowerTier = _adaptivePowerTierController == null
                    ? AdaptivePowerTier.Daily
                    : _adaptivePowerTierController.CurrentTier,
                CpuEffectivePowerTier = _adaptiveEffectiveTier,
                GpuThermalDemand = _gpuThermalDemand,
                CpuCoolingState = _acousticGovernor == null
                    ? CoolingState.Normal
                    : _acousticGovernor.CpuCoolingState,
                GpuCoolingState = _acousticGovernor == null
                    ? CoolingState.Normal
                    : _acousticGovernor.GpuCoolingState,
                EmergencyOverride = _acousticGovernor != null && _acousticGovernor.State == CoolingState.Emergency,
                CpuFanAssistPercent = _engine != null ? _engine.AssistController.CpuFanAssistPercent : 0,
                GpuFanAssistPercent = _engine != null ? _engine.AssistController.GpuFanAssistPercent : 0,
                // 共享热预算让出可观测字段（仅诊断；不暗示 GPU 瓦数被控制）。
                SharedThermalSheddingActive = _sharedThermalBudget != null && _sharedThermalBudget.SheddingActive,
                SharedThermalEnterCreditSeconds = _sharedThermalBudget != null ? _sharedThermalBudget.EnterCreditSeconds : 0,
                SharedThermalRecoveryCreditSeconds = _sharedThermalBudget != null ? _sharedThermalBudget.RecoveryCreditSeconds : 0,
                CpuFanProfileTier = Enum.IsDefined(typeof(AdaptivePowerTier), _adaptiveFanAppliedTier)
                    ? _adaptiveFanAppliedTier
                    : _adaptiveEffectiveTier,
                SharedThermalReason = _sharedThermalBudget != null ? _sharedThermalBudget.Reason : string.Empty,
                GpuPowerBackend = _gpuPowerBackend == null ? "TelemetryOnly" : (_gpuPowerBackend.Name ?? "TelemetryOnly"),
                OemModeObserved = _oemModeObserver == null ? -1 : _oemModeObserver.ObservedMode,
                CpuPl1RequestedWatts = _cpuPl1RequestedWatts,
                CpuPl2RequestedWatts = _cpuPl2RequestedWatts,
                CpuTauRequestedSeconds = _cpuTauRequestedSeconds,
                CpuPl1ReadbackWatts = _cpuPl1ReadbackWatts,
                CpuPl2ReadbackWatts = _cpuPl2ReadbackWatts,
                CpuTauReadbackSeconds = _cpuTauReadbackSeconds,
                GpuPowerEvent = _gpuBackendDetail ?? string.Empty
            };
            return joint;
        }

        // 声学/热治理：风扇顶住软上限且温度持续上升时，禁止提高有效功耗；
        // 热饱和持续足够久可主动降低一档。紧急状态保持有效档不升。
        private AdaptivePowerTier ApplyAcousticGovernance(
            AdaptivePowerTier requested,
            FanSnapshot snapshot,
            ControlDecision decision,
            DateTime nowUtc)
        {
            if (_acousticGovernor == null || snapshot == null)
                return requested;

            bool emergency = decision != null &&
                ((decision.Cpu != null && decision.Cpu.State == ControlState.Emergency) ||
                 (decision.Gpu != null && decision.Gpu.State == ControlState.Emergency));

            StrategyMode mode = _config == null ? StrategyMode.Auto : _config.StrategyMode;
            FanProfile profile = CreateFixedFanProfile(mode, _adaptiveFanAppliedTier);
            ChannelAcousticLimits cpuLimits = new ChannelAcousticLimits
            {
                ComfortFanDutyPercent = profile.Cpu.ComfortFanDutyPercent,
                SoftMaximumFanDutyPercent = profile.Cpu.SoftMaximumFanDutyPercent,
                TargetTemperatureC = profile.Cpu.TargetTemperatureC
            };
            ChannelAcousticLimits gpuLimits = new ChannelAcousticLimits
            {
                ComfortFanDutyPercent = profile.Gpu.ComfortFanDutyPercent,
                SoftMaximumFanDutyPercent = profile.Gpu.SoftMaximumFanDutyPercent,
                TargetTemperatureC = profile.Gpu.TargetTemperatureC
            };

            double cpuFan = decision != null && decision.Cpu != null ? decision.Cpu.AppliedPercent : 0;
            double gpuFan = decision != null && decision.Gpu != null ? decision.Gpu.AppliedPercent : 0;
            double cpuRise = decision != null && decision.Cpu != null ? decision.Cpu.TemperatureRiseRateCPerSec : 0;
            double gpuRise = decision != null && decision.Gpu != null ? decision.Gpu.TemperatureRiseRateCPerSec : 0;

            return _acousticGovernor.Apply(
                requested,
                snapshot.CpuTemperatureC,
                snapshot.GpuTemperatureC,
                cpuFan,
                gpuFan,
                cpuRise,
                gpuRise,
                cpuLimits,
                gpuLimits,
                emergency,
                nowUtc);
        }

        // 档位状态事件日志：pending 开始/取消、档位完成时各记录一次
        //（状态变化才记录，不每帧刷），附完整证据字段，应用日志可独立
        // 回答"为什么切换"。
        private void LogTierStateEvents()
        {
            if (_config == null || _config.StrategyMode != StrategyMode.Auto ||
                _adaptivePowerTierController == null)
                return;

            AdaptiveTierDiagnostics diag = _adaptivePowerTierController.Diagnostics;
            if (diag.CurrentTier != _lastLoggedTier)
            {
                AppendLog(FormatTierEventLog("transition", diag));
                _lastLoggedTier = diag.CurrentTier;
            }
            if (diag.PendingTier != _lastLoggedPending)
            {
                AppendLog(FormatTierEventLog(diag.PendingTier.HasValue ? "pending_start" : "pending_cancel", diag));
                _lastLoggedPending = diag.PendingTier;
            }
        }

        private static string FormatTierEventLog(string eventName, AdaptiveTierDiagnostics d)
        {
            return string.Format(
                "AUTO_TIER {0} current={1} pending={2} effective={3} cooling={4} cpu_avg_8s={5:F1} gpu_avg_8s={6:F1} cpu_avg_30s={7:F1} gpu_avg_30s={8:F1} cpu_peak_15s={9:F1} gpu_peak_15s={10:F1} strong={11} elapsed={12:F1} required={13:F1} reason={14}",
                eventName,
                d.CurrentTier,
                d.PendingTier.HasValue ? d.PendingTier.Value.ToString() : "none",
                d.EffectiveTier,
                d.CoolingState,
                d.UpshiftAverageCpu,
                d.UpshiftAverageGpu,
                d.DownshiftAverageCpu,
                d.DownshiftAverageGpu,
                d.RecentPeakCpu,
                d.RecentPeakGpu,
                d.StrongUpshiftEvidence,
                d.DwellElapsedSeconds,
                d.DwellRequiredSeconds,
                d.Reason);
        }

        private void StartApplyPowerTierAsync(AdaptivePowerTier tier, DateTime nowUtc)
        {
            _adaptivePowerApplying = true;
            _adaptivePendingApplyTier = tier;
            _adaptiveNextApplyUtc = nowUtc.AddSeconds(120);
            int generation = ++_adaptiveApplyGeneration;
            _adaptiveApplyCts?.Cancel();
            _adaptiveApplyCts = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken cancellationToken = _adaptiveApplyCts.Token;
            _adaptiveApplyTask = Task.Run(async () =>
            {
                try
                {
                    Tuple<bool, bool> outcome = await ApplyAdaptiveTierAsync(tier, cancellationToken);
                    bool windowsApplied = outcome.Item1;
                    bool xtuApplied = outcome.Item2;
                    bool applied = windowsApplied || xtuApplied;
                    // 只有当前 generation 才能发布状态：旧请求完成后不得覆盖
                    // 新档位，也不得发布过期的 xtuConfirmed/backendName。
                    if (!AdaptivePowerApplyGuard.CanPublish(
                            generation,
                            _adaptiveApplyGeneration,
                            tier,
                            _adaptiveEffectiveTier))
                    {
                        AppendLog("AUTO_TIER power_apply_superseded tier=" + GetAdaptiveTierName(tier));
                        return;
                    }
                    _adaptiveXtuConfirmed = xtuApplied;
                    _adaptiveBackendName = xtuApplied
                        ? "Control Center DCHU（回读确认）"
                        : windowsApplied ? "Windows 性能兜底（DCHU未确认）" : "未应用";
                    if (applied)
                    {
                        _adaptiveAppliedTier = tier;
                        AppendLog("AUTO_TIER power_applied tier=" + GetAdaptiveTierName(tier) +
                                  " backend=" + (_adaptiveXtuConfirmed ? "dchu-confirmed" : "windows-fallback"));
                    }
                    else
                    {
                        AppendLog("AUTO_TIER power_apply_failed tier=" + GetAdaptiveTierName(tier) + " 稍后重试");
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    AppendLog("固定策略功耗应用失败：" + exception.Message);
                }
                finally
                {
                    if (generation == _adaptiveApplyGeneration)
                    {
                        _adaptivePowerApplying = false;
                        _adaptivePendingApplyTier = null;
                    }
                }
            }, cancellationToken);
        }

        private async Task<Tuple<bool, bool>> ApplyAdaptiveTierAsync(
            AdaptivePowerTier tier,
            System.Threading.CancellationToken token)
        {
            AdaptivePowerPreset preset = AdaptivePowerPreset.For(tier, _config?.AdaptivePower);
            // 记录 CPU 功耗 requested（供 CSV/日志）。
            _cpuPl1RequestedWatts = (double)preset.Pl1Watts;
            _cpuPl2RequestedWatts = (double)preset.Pl2Watts;
            _cpuTauRequestedSeconds = (double)preset.TimeSeconds;
            bool windowsApplied = false;
            bool xtuApplied = false;
            if (!_adaptivePolicyCaptured)
            {
                try
                {
                    _adaptiveProcessorPolicy = new WindowsProcessorPolicy();
                    _adaptivePolicySnapshot = await Task.Run(() => _adaptiveProcessorPolicy.Capture(), token);
                    _adaptivePolicyCaptured = true;
                    AppendLog("固定策略功耗：已保存原 Windows CPU 性能上限=" + _adaptivePolicySnapshot.OriginalAcMaximumPercent + "%");
                }
                catch (Exception exception)
                {
                    AppendLog("固定策略功耗：无法保存 Windows 性能方案，跳过兜底写入：" + exception.Message);
                }
            }

            if (_adaptivePolicyCaptured)
            {
                string policyError = null;
                windowsApplied = await Task.Run(() => _adaptiveProcessorPolicy.ApplyAcMaximumPercent(
                    _adaptivePolicySnapshot, preset.WindowsMaximumPerformancePercent, out policyError), token);
                if (!windowsApplied)
                    AppendLog("固定策略功耗：Windows 性能上限应用失败：" + policyError);
            }

            xtuApplied = await ApplyXtuPresetAsync(preset, token);
            // 状态发布（_adaptiveXtuConfirmed/_adaptiveBackendName/_adaptiveAppliedTier）
            // 由 StartApplyPowerTierAsync 在当前 generation 内完成。
            return Tuple.Create(windowsApplied, xtuApplied);
        }

        private async Task<bool> ApplyXtuPresetAsync(AdaptivePowerPreset preset, System.Threading.CancellationToken token)
        {
            string bridgePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "X15XtuBridge.exe");
            if (!File.Exists(bridgePath))
            {
                AppendLog("固定策略功耗：未部署 X15XtuBridge.exe");
                return false;
            }

            using (Process bridge = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = bridgePath,
                    Arguments = "--apply-cpu-power " +
                                 preset.Pl1Watts.ToString("0.##", CultureInfo.InvariantCulture) + " " +
                                 preset.Pl2Watts.ToString("0.##", CultureInfo.InvariantCulture) + " " +
                                 preset.TimeSeconds.ToString(CultureInfo.InvariantCulture),
                    WorkingDirectory = Path.GetDirectoryName(bridgePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            })
            {
                if (!bridge.Start())
                    return false;

                Task<string> outputTask = bridge.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = bridge.StandardError.ReadToEndAsync();
                Task waitTask = Task.Run(() => bridge.WaitForExit());
                if (await Task.WhenAny(waitTask, Task.Delay(12000, token)).ConfigureAwait(false) != waitTask)
                {
                    try { bridge.Kill(); } catch { }
                    AppendLog("固定策略功耗：Control Center 写入超过12秒，已终止");
                    return false;
                }

                string output = await outputTask.ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    AppendLog("固定策略 XTU：" + line);
                    ParseCpuPowerReadbackLine(line);
                }
                foreach (string line in error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    AppendLog("固定策略 XTU 错误：" + line);
                return bridge.ExitCode == 0 && output.IndexOf("APPLIED=True", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        // 解析 X15XtuBridge --apply-cpu-power 输出的读回值，供 CSV/日志记录
        // CPU PL1/PL2/Tau requested/readback。
        private void ParseCpuPowerReadbackLine(string line)
        {
            const string appliedPl1Prefix = "APPLIED_PL1_WATTS=";
            const string appliedPl2Prefix = "APPLIED_PL2_WATTS=";
            const string appliedTimePrefix = "APPLIED_TIME_SECONDS=";
            double value;
            if (line.StartsWith(appliedPl1Prefix, StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(line.Substring(appliedPl1Prefix.Length),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                _cpuPl1ReadbackWatts = value;
            else if (line.StartsWith(appliedPl2Prefix, StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(line.Substring(appliedPl2Prefix.Length),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                _cpuPl2ReadbackWatts = value;
            else if (line.StartsWith(appliedTimePrefix, StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(line.Substring(appliedTimePrefix.Length),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                _cpuTauReadbackSeconds = value;
        }

        private async Task<bool> RestoreOriginalDchuPowerAsync(
            string bridgePath,
            int pl1,
            int pl2,
            uint timeSeconds)
        {
            using (Process bridge = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = bridgePath,
                    Arguments = "--restore-cpu-power " +
                                 pl1.ToString(CultureInfo.InvariantCulture) + " " +
                                 pl2.ToString(CultureInfo.InvariantCulture) + " " +
                                 timeSeconds.ToString(CultureInfo.InvariantCulture),
                    WorkingDirectory = Path.GetDirectoryName(bridgePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                }
            })
            {
                if (!bridge.Start())
                    return false;

                Task<string> outputTask = bridge.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = bridge.StandardError.ReadToEndAsync();
                Task waitTask = Task.Run(() => bridge.WaitForExit());
                if (await Task.WhenAny(waitTask, Task.Delay(6000, System.Threading.CancellationToken.None)).ConfigureAwait(false) != waitTask)
                {
                    try { bridge.Kill(); } catch { }
                    AppendLog("恢复原始 DCHU：写入超过12秒，已终止");
                    return false;
                }

                string output = await outputTask.ConfigureAwait(false);
                string error = await errorTask.ConfigureAwait(false);
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    AppendLog("恢复原始 DCHU：" + line);
                foreach (string line in error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    AppendLog("恢复原始 DCHU 错误：" + line);
                return bridge.ExitCode == 0 && output.IndexOf("RESTORED=True", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        private static string GetAdaptiveTierName(AdaptivePowerTier tier)
        {
            switch (tier)
            {
                case AdaptivePowerTier.Quiet: return "安静";
                case AdaptivePowerTier.Code: return "代码";
                case AdaptivePowerTier.Heavy: return "重负载";
                default: return "日常";
            }
        }

        private async Task RestoreAdaptivePowerPolicyAsync()
        {
            _adaptiveApplyCts?.Cancel();
            Task applyTask = _adaptiveApplyTask;
            if (applyTask != null && !applyTask.IsCompleted)
            {
                try
                {
                    await Task.WhenAny(applyTask, Task.Delay(3000)).ConfigureAwait(false);
                }
                catch { }
            }
            if (_adaptivePolicyCaptured && _adaptiveProcessorPolicy != null && _adaptivePolicySnapshot != null)
            {
                string error = null;
                Task<bool> restoreTask = Task.Run(() => _adaptiveProcessorPolicy.Restore(_adaptivePolicySnapshot, out error));
                try
                {
                    if (await Task.WhenAny(restoreTask, Task.Delay(6000)).ConfigureAwait(false) == restoreTask)
                    {
                        bool restored = await restoreTask.ConfigureAwait(false);
                        AppendLog(restored
                            ? "固定策略功耗：已恢复原 Windows CPU 性能上限=" + _adaptivePolicySnapshot.OriginalAcMaximumPercent + "%"
                            : "固定策略功耗：恢复 Windows CPU 性能上限失败：" + error);
                    }
                    else
                    {
                        AppendLog("固定策略功耗：恢复 Windows CPU 性能上限超时，继续退出清理。");
                    }
                }
                catch (Exception exception)
                {
                    AppendLog("固定策略功耗：恢复 Windows CPU 性能上限异常：" + exception.Message);
                }
            }
            if (_adaptiveDchuOriginalCaptured)
            {
                string bridgePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "X15XtuBridge.exe");
                if (File.Exists(bridgePath))
                {
                    bool restored = await RestoreOriginalDchuPowerAsync(
                        bridgePath,
                        _adaptiveDchuOriginalPl1,
                        _adaptiveDchuOriginalPl2,
                        _adaptiveDchuOriginalTimeSeconds).ConfigureAwait(false);
                    AppendLog(restored
                        ? "固定策略功耗：已恢复启动前 DCHU 功耗。"
                        : "固定策略功耗：恢复启动前 DCHU 功耗失败，请检查日志。");
                }
            }
            _adaptivePolicyCaptured = false;
            _adaptiveAppliedTier = (AdaptivePowerTier)(-1);
            _adaptiveCurrentTier = AdaptivePowerTier.Daily;
            _adaptiveDesiredTier = AdaptivePowerTier.Daily;
            _adaptiveEffectiveTier = AdaptivePowerTier.Daily;
            _adaptiveFanAppliedTier = (AdaptivePowerTier)(-1);
            _sharedThermalBudget?.Reset();
            _sharedThermalFanFloorSet = false;
            _sharedThermalFanFloor = AdaptivePowerTier.Daily;
            _platformPowerCoordinator?.ResetCoordinatorState();
            _acousticGovernor?.Reset(AdaptivePowerTier.Daily);
            _adaptiveXtuConfirmed = false;
            _adaptiveBackendName = "未应用";
            _adaptiveLastReason = "当前未进入 Active，策略未写入";
        }

        private void RestoreAdaptivePowerPolicy()
        {
            try
            {
                RestoreAdaptivePowerPolicyAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                AppendLog("恢复自适应功耗方案失败：" + exception.Message);
            }
        }
    }
}
