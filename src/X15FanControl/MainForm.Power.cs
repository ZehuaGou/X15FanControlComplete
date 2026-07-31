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
        private void UpdateAdaptivePowerPolicy(FanSnapshot snapshot, DateTime nowUtc)
        {
            if (_config == null || _runMode != RunMode.Active ||
                _adaptivePowerTierController == null || snapshot == null)
                return;

            StrategyMode mode = _config.StrategyMode;
            AdaptivePowerTier tier;
            if (mode == StrategyMode.Auto)
            {
                tier = _adaptivePowerTierController.Update(new AdaptivePowerSample
                {
                    TimestampUtc = snapshot.TimestampUtc,
                    CpuUtilizationPercent = snapshot.CpuUtilizationPercent,
                    CpuPerformancePercent = snapshot.CpuPerformancePercent,
                    GpuUtilizationPercent = snapshot.GpuTelemetryAvailable ? snapshot.GpuTelemetryUtilization : 0,
                    CpuTemperatureC = snapshot.CpuTemperatureC,
                    GpuTemperatureC = snapshot.GpuTemperatureC
                });
                _adaptiveLastReason = _adaptivePowerTierController.LastReason;
            }
            else
            {
                tier = GetTierForMode(mode);
                _adaptivePowerTierController.ForceTier(tier, "固定策略：" + StrategyModeInfo.GetName(mode));
                _adaptiveLastReason = "固定策略：" + StrategyModeInfo.GetName(mode) + "，不会自动升降档";
            }
            _adaptiveCurrentTier = tier;
            UpdateTrayStrategyStatus();

            bool tierChanged = tier != _adaptiveAppliedTier;
            bool retryExternalBackend = !_adaptiveXtuConfirmed && nowUtc >= _adaptiveNextApplyUtc;
            if (_adaptiveFanAppliedTier != tier)
            {
                ApplyFixedFanProfile(tier);
                _adaptiveFanAppliedTier = tier;
            }

            if ((!tierChanged && !retryExternalBackend) || _adaptivePowerApplying)
                return;

            _adaptivePowerApplying = true;
            _adaptiveNextApplyUtc = nowUtc.AddSeconds(120);
            _adaptiveApplyCts = new System.Threading.CancellationTokenSource();
            System.Threading.CancellationToken cancellationToken = _adaptiveApplyCts.Token;
            _adaptiveApplyTask = Task.Run(async () =>
            {
                try
                {
                    bool applied = await ApplyAdaptiveTierAsync(tier, cancellationToken);
                    if (applied)
                    {
                        _adaptiveAppliedTier = tier;
                        AppendLog("固定策略功耗：当前档位=" + GetAdaptiveTierName(tier) +
                                  "；" + (_adaptiveXtuConfirmed ? "Control Center 已回读确认" : "仅 Windows 兜底已应用"));
                    }
                    else
                    {
                        AppendLog("固定策略功耗：" + GetAdaptiveTierName(tier) + " 未获得后端确认，保持风扇安全控制并稍后重试");
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception exception)
                {
                    AppendLog("固定策略功耗应用失败：" + exception.Message);
                }
                finally
                {
                    _adaptivePowerApplying = false;
                }
            }, cancellationToken);
        }

        private async Task<bool> ApplyAdaptiveTierAsync(AdaptivePowerTier tier, System.Threading.CancellationToken token)
        {
            AdaptivePowerPreset preset = AdaptivePowerPreset.For(tier);
            bool windowsApplied = false;
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

            bool xtuApplied = await ApplyXtuPresetAsync(preset, token);
            _adaptiveXtuConfirmed = xtuApplied;
            _adaptiveBackendName = xtuApplied
                ? "Control Center DCHU（回读确认）"
                : windowsApplied ? "Windows 性能兜底（DCHU未确认）" : "未应用";
            return windowsApplied || xtuApplied;
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
                if (await Task.WhenAny(waitTask, Task.Delay(12000, token)) != waitTask)
                {
                    try { bridge.Kill(); } catch { }
                    AppendLog("固定策略功耗：Control Center 写入超过12秒，已终止");
                    return false;
                }

                string output = await outputTask;
                string error = await errorTask;
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    AppendLog("固定策略 XTU：" + line);
                foreach (string line in error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    AppendLog("固定策略 XTU 错误：" + line);
                return bridge.ExitCode == 0 && output.IndexOf("APPLIED=True", StringComparison.OrdinalIgnoreCase) >= 0;
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

        private void RestoreAdaptivePowerPolicy()
        {
            _adaptiveApplyCts?.Cancel();
            Task applyTask = _adaptiveApplyTask;
            if (applyTask != null && !applyTask.IsCompleted)
            {
                try { applyTask.Wait(3000); } catch { }
            }
            if (_adaptivePolicyCaptured && _adaptiveProcessorPolicy != null && _adaptivePolicySnapshot != null)
            {
                string error = null;
                bool restored = Task.Run(() => _adaptiveProcessorPolicy.Restore(_adaptivePolicySnapshot, out error)).GetAwaiter().GetResult();
                AppendLog(restored
                    ? "固定策略功耗：已恢复原 Windows CPU 性能上限=" + _adaptivePolicySnapshot.OriginalAcMaximumPercent + "%"
                    : "固定策略功耗：恢复 Windows CPU 性能上限失败：" + error);
            }
            _adaptivePolicyCaptured = false;
            _adaptiveAppliedTier = (AdaptivePowerTier)(-1);
            _adaptiveCurrentTier = AdaptivePowerTier.Daily;
            _adaptiveFanAppliedTier = (AdaptivePowerTier)(-1);
            _adaptiveXtuConfirmed = false;
            _adaptiveBackendName = "未应用";
            _adaptiveLastReason = "当前未进入 Active，策略未写入";
        }
    }
}
