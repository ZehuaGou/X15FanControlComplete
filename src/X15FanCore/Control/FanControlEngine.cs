using System;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    public sealed class FanControlEngine
    {
        private FanProfile _profile;
        private ChannelController _cpu;
        private ChannelController _gpu;
        private DateTime _lastUpdateUtc;
        // 跨风扇辅助控制器（架构收束 2026-08-02）：替代旧直接耦合
        // CalculateCoupling —— 旧耦合会因一侧短暂升温立即同时提高两侧
        // 风扇；新合同为主扇优先、辅助延迟介入（20s 持续证据 + 25% 候选
        // 辅助量 + 60s 退出滞回）。FanChannelProfile 的 CouplingEnabled /
        // CouplingStartTemperatureC / CouplingMaximumPercent 字段保留作配置
        // 兼容，引擎不再读取。
        private readonly CrossFanAssistController _assist;
        private double _gpuDemandBiasPercent;
        private ControlDecision _lastDecision;
        private FanSnapshot _lastSnapshot;

        public FanControlEngine(FanProfile profile)
        {
            _assist = new CrossFanAssistController();
            SetProfile(profile);
            Reset();
        }

        public FanControlEngine(FanProfile profile, CrossFanAssistSettings assistSettings)
        {
            _assist = new CrossFanAssistController(assistSettings);
            SetProfile(profile);
            Reset();
        }

        public FanProfile Profile
        {
            get { return _profile; }
        }

        /// <summary>GPU 热需求 → GPU 风扇曲线偏置（%）：由 MainForm 在调用
        /// Update 前设置；候选值未标定，仅作 GPU 风扇响应性增强。</summary>
        public double GpuDemandBiasPercent
        {
            get { return _gpuDemandBiasPercent; }
            set { _gpuDemandBiasPercent = Math.Max(0, Math.Min(20, value)); }
        }

        public CrossFanAssistController AssistController { get { return _assist; } }

        public void SetProfile(FanProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException("profile");
            if (_cpu == null)
            {
                _cpu = new ChannelController(FanKind.Cpu, profile.Cpu);
                _gpu = new ChannelController(FanKind.Gpu, profile.Gpu);
            }
            else
            {
                _cpu.SetProfile(profile.Cpu);
                _gpu.SetProfile(profile.Gpu);
            }
        }

        // 自动档位切换用：更换风扇曲线/速率/稳定区配置，但保留当前占空比、
        // 接受目标、温度滤波历史、写入时间和外部覆盖检测状态。换档不得
        // 清空控制状态，否则同样温度下曲线目标跳变会直接造成风扇突响。
        public void SetProfilePreservingState(FanProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException("profile");
            if (_cpu == null)
            {
                _cpu = new ChannelController(FanKind.Cpu, profile.Cpu);
                _gpu = new ChannelController(FanKind.Gpu, profile.Gpu);
            }
            else
            {
                _cpu.SetProfilePreservingState(profile.Cpu);
                _gpu.SetProfilePreservingState(profile.Gpu);
            }
        }

        public void Reset()
        {
            _cpu.Reset();
            _gpu.Reset();
            _assist.Reset();
            _lastUpdateUtc = DateTime.MinValue;
            _lastDecision = null;
            _lastSnapshot = null;
        }

        // 注入声学软上限的快速升温突破阈值（来自 AdaptivePowerSettings）。
        public void SetAcousticFastRiseBreakthrough(double cPerSecond)
        {
            _cpu.SetAcousticFastRiseBreakthrough(cPerSecond);
            _gpu.SetAcousticFastRiseBreakthrough(cPerSecond);
        }

        public ControlDecision Update(FanSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException("snapshot");
            }

            DateTime now = snapshot.TimestampUtc == default(DateTime) ? DateTime.UtcNow : snapshot.TimestampUtc;
            double elapsedSeconds = _lastUpdateUtc == DateTime.MinValue ? 0.5 : Math.Max(0.05, Math.Min(5.0, (now - _lastUpdateUtc).TotalSeconds));
            _lastUpdateUtc = now;

            // 当前周期 Emergency 预判（任务：不得只依赖上一周期 ChannelDecision）：
            // 用本周期 CPU/GPU 原始温度与各自 EmergencyStage1 阈值判断。任一通道
            // 达到阈值 → 本周期双侧风扇共同散热（emergencyOverride 突破软上限）。
            bool cpuEmergencyNow = IsEmergencyTemperature(snapshot.CpuTemperatureC, _profile.Cpu);
            bool gpuEmergencyNow = snapshot.GpuTelemetryAvailable &&
                                   IsEmergencyTemperature(snapshot.GpuTemperatureC, _profile.Gpu);

            // 跨风扇辅助：基于上一控制周期的决策与快照（辅助量滞后 1 周期可接受），
            // 但有效性门禁与 Emergency 用本周期数据。
            // - CPU 通道 TelemetryValid 由 EC 快照温度有效性决定（与 GPU 遥测无关）；
            // - GPU 通道 TelemetryValid 由 GPU 遥测决定；
            // - isCpuChannel 参数已删除（原实现把 GPU 遥测可用性误用于 CPU 通道）。
            AssistChannelInput cpuInput = BuildAssistInput(
                _lastDecision != null && _lastDecision.Cpu != null ? _lastDecision.Cpu : null,
                _lastSnapshot != null ? _lastSnapshot.CpuTemperatureC : snapshot.CpuTemperatureC,
                IsCpuSnapshotTemperatureValid(snapshot),
                cpuEmergencyNow);
            AssistChannelInput gpuInput = BuildAssistInput(
                _lastDecision != null && _lastDecision.Gpu != null ? _lastDecision.Gpu : null,
                _lastSnapshot != null ? _lastSnapshot.GpuTemperatureC : snapshot.GpuTemperatureC,
                snapshot.GpuTelemetryAvailable && snapshot.GpuTemperatureC > 0,
                gpuEmergencyNow);
            _assist.Update(cpuInput, gpuInput, now);

            // CPU 风扇辅助 GPU 的量（GPU 为主通道）；GPU 风扇辅助 CPU 的量
            // （CPU 为主通道）再加 GPU 热需求偏置（需求 -> GPU 风扇曲线）。
            double cpuAssist = _assist.CpuFanAssistPercent;
            double gpuAssist = _assist.GpuFanAssistPercent + _gpuDemandBiasPercent;

            // Emergency / 快速温升保护：本周期任一通道原始温度达紧急阈值 →
            // 双侧风扇共同散热，突破声学软上限（ChannelController 的
            // emergencyOverride 跳过软上限钳制并施加共享散热下限）。
            bool emergencyOverride = cpuEmergencyNow || gpuEmergencyNow || _assist.EmergencyAssistActive;

            ControlDecision decision = new ControlDecision
            {
                Cpu = _cpu.Update(snapshot.CpuTemperatureC, snapshot.CpuDutyPercent, cpuAssist, now, elapsedSeconds, emergencyOverride),
                Gpu = _gpu.Update(snapshot.GpuTemperatureC, snapshot.GpuDutyPercent, gpuAssist, now, elapsedSeconds, emergencyOverride)
            };

            _lastDecision = decision;
            _lastSnapshot = snapshot;
            return decision;
        }

        private static bool IsEmergencyTemperature(int temperatureC, FanChannelProfile profile)
        {
            // Emergency must never be inferred from an invalid sensor value. EC can
            // occasionally return an out-of-range byte (for example 144 C); the
            // channel controller correctly treats that as InvalidSensor, so the
            // same sample must not simultaneously force the other fan through the
            // shared-emergency path.
            return profile != null &&
                   temperatureC >= profile.MinimumValidTemperatureC &&
                   temperatureC <= profile.MaximumValidTemperatureC &&
                   profile.EmergencyStage1TemperatureC > 0 &&
                   temperatureC >= profile.EmergencyStage1TemperatureC;
        }

        private bool IsCpuSnapshotTemperatureValid(FanSnapshot snapshot)
        {
            return snapshot != null && _profile != null && _profile.Cpu != null &&
                   snapshot.CpuTemperatureC >= _profile.Cpu.MinimumValidTemperatureC &&
                   snapshot.CpuTemperatureC <= _profile.Cpu.MaximumValidTemperatureC;
        }

        private static AssistChannelInput BuildAssistInput(
            ChannelDecision decision,
            double temperatureC,
            bool telemetryValid,
            bool emergencyNow)
        {
            AssistChannelInput input = new AssistChannelInput();
            if (decision != null)
            {
                input.TemperatureC = decision.InstantTemperatureC > 0 ? decision.InstantTemperatureC : temperatureC;
                input.FanDutyPercent = decision.AppliedPercent;
                input.RiseRateCPerSec = decision.TemperatureRiseRateCPerSec;
                input.Emergency = decision.State == ControlState.Emergency || emergencyNow;
            }
            else
            {
                input.TemperatureC = temperatureC;
                input.RiseRateCPerSec = 0;
                input.Emergency = emergencyNow;
            }
            // 软上限/目标温度由 AssistController 默认设置注入；引擎不持有
            // 档位声学参数（MainForm 通过 SetAssistChannelLimits 提供）。
            input.TelemetryValid = telemetryValid;
            return input;
        }

        /// <summary>注入每通道声学参数（软上限/目标温度）供辅助判定使用；
        /// 由 MainForm 在档位变化时调用。</summary>
        public void SetAssistChannelLimits(ChannelAcousticLimits cpuLimits, ChannelAcousticLimits gpuLimits)
        {
            _assist.SetChannelLimits(cpuLimits, gpuLimits);
        }

        public void MarkCpuWritten(int percent, DateTime timestampUtc)
        {
            _cpu.MarkWritten(percent, timestampUtc);
        }

        public void MarkGpuWritten(int percent, DateTime timestampUtc)
        {
            _gpu.MarkWritten(percent, timestampUtc);
        }

        // 外部覆盖检测由每个普通控制快照的 ReadRaw 占空回读驱动。
        // 不再为每次写入额外发起 50ms/1000ms EC 交易，避免高频控制
        // 将单通道 EC 队列压垮。确认的连续失配仍会触发原有故障保护。
        public FanWriteReadbackStatus ObserveCpuReadback(double readbackPercent)
        {
            return _cpu.ObserveEcReadback(readbackPercent);
        }

        public FanWriteReadbackStatus ObserveGpuReadback(double readbackPercent)
        {
            return _gpu.ObserveEcReadback(readbackPercent);
        }
    }

    public sealed class FanWriteReadbackStatus
    {
        public bool HasExpectedWrite { get; set; }
        public double ExpectedPercent { get; set; }
        public double ObservedPercent { get; set; }
        public bool Verified { get; set; }
        public bool ExternalOverrideDetected { get; set; }
    }
}
