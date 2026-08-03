using System;
using System.Globalization;
using System.IO;
using System.Text;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    /// <summary>
    /// CPU/GPU 联合运行时诊断快照（CSV 追加列；命名遵循架构收束
    /// 2026-08-02：不再使用 gpu_effective_power_tier 等暗示 GPU 瓦数被
    /// 控制的名称）。由 MainForm 每个控制周期填充后交给 CsvLogger 记录。
    /// </summary>
    public sealed class JointRuntimeDiagnostics
    {
        // CPU 档位（requested/effective；existing current_tier/effective_tier
        // 列同时保留，这里显式冗余用于状态流对照）。
        public AdaptivePowerTier CpuRequestedPowerTier;
        public AdaptivePowerTier CpuEffectivePowerTier;
        // GPU 热需求等级（不是功率档位）。
        public GpuThermalDemand GpuThermalDemand;
        // 每通道冷却状态。
        public CoolingState CpuCoolingState;
        public CoolingState GpuCoolingState;
        public bool EmergencyOverride;
        // 跨风扇辅助量（%）。
        public double CpuFanAssistPercent;
        public double GpuFanAssistPercent;
        // 共享热预算让出（2026-08-03，仅 Auto 模式；诊断字段，不暗示 GPU
        // 瓦数被控制）：
        public bool SharedThermalSheddingActive;
        public double SharedThermalEnterCreditSeconds;
        public double SharedThermalRecoveryCreditSeconds;
        // 风扇 profile 档位（与 CPU 功耗档解耦：让出期间保持进入前档位）。
        public AdaptivePowerTier CpuFanProfileTier;
        public string SharedThermalReason;
        // GPU 功耗后端标识（生产路径恒 "TelemetryOnly"）。
        public string GpuPowerBackend;
        // OEM mode 只读观测（-1 = 未观测；只记录，不写回）。
        public int OemModeObserved;
        // CPU 预设 requested / readback（W / 秒）。
        public double CpuPl1RequestedWatts;
        public double CpuPl2RequestedWatts;
        public double CpuTauRequestedSeconds;
        public double CpuPl1ReadbackWatts;
        public double CpuPl2ReadbackWatts;
        public double CpuTauReadbackSeconds;
        // GPU 功耗事件（TelemetryOnly 说明/降级记录）。
        public string GpuPowerEvent;
    }

    public sealed class CsvLogger : IDisposable
    {
        public const int DefaultFlushIntervalSeconds = 5;
        public const int DefaultRetentionDays = 7;

        private const string Header =
            "timestamp_utc,cpu_temp_ec,gpu_temp_nvidia,cpu_ec_local,gpu_ec_remote,gpu_telemetry_source,gpu_utilization,gpu_power_watts,gpu_pstate,cpu_utilization,cpu_performance,cpu_duty,gpu_duty,cpu_rpm,gpu_rpm,cpu_fast,cpu_slow,cpu_control,cpu_raw,cpu_target,cpu_applied,cpu_written,cpu_readback,cpu_readback_duty,cpu_write_verified,cpu_external_override,cpu_control_state,cpu_rise_rate,cpu_reason,gpu_fast,gpu_slow,gpu_control,gpu_raw,gpu_target,gpu_applied,gpu_written,gpu_readback,gpu_readback_duty,gpu_write_verified,gpu_external_override,gpu_control_state,gpu_reason," +
            "strategy_mode,current_tier,pending_tier,effective_tier,cooling_state,tier_reason,tier_dwell_elapsed,tier_dwell_required,tier_cpu_avg,tier_gpu_avg,tier_cpu_peak,tier_gpu_peak," +
            "cpu_requested_power_tier,cpu_effective_power_tier,gpu_thermal_demand,cpu_cooling_state,gpu_cooling_state,emergency_override,cpu_fan_assist,gpu_fan_assist,gpu_power_backend,oem_mode_observed,cpu_pl1_requested_w,cpu_pl2_requested_w,cpu_tau_requested_s,cpu_pl1_readback_w,cpu_pl2_readback_w,cpu_tau_readback_s,gpu_power_event," +
            "shared_thermal_shedding_active,shared_thermal_enter_credit_s,shared_thermal_recovery_credit_s,cpu_fan_profile_tier,shared_thermal_reason";

        private readonly string _directory;
        private readonly int _flushIntervalSeconds;
        private readonly int _retentionDays;
        private readonly object _sync = new object();
        private StreamWriter _writer;
        private DateTime _fileDate;
        private DateTime _lastFlushUtc;
        private long _rowsWritten;

        public CsvLogger(
            string directory,
            int flushIntervalSeconds = DefaultFlushIntervalSeconds,
            int retentionDays = DefaultRetentionDays)
        {
            _directory = directory;
            _flushIntervalSeconds = Math.Max(1, flushIntervalSeconds);
            _retentionDays = Math.Max(1, retentionDays);
            Directory.CreateDirectory(directory);
            OpenNewFile(DateTime.Now);
        }

        public string FilePath { get; private set; }

        // 写入失败不会被永久静默吞掉：保存最近一次错误，由调用方限频记录。
        public string LastWriteError { get; private set; }
        public DateTime? LastWriteErrorUtc { get; private set; }

        public string ConsumeLastWriteError()
        {
            lock (_sync)
            {
                string error = LastWriteError;
                LastWriteError = null;
                LastWriteErrorUtc = null;
                return error;
            }
        }

        public void Write(FanSnapshot snapshot, ControlDecision decision)
        {
            Write(snapshot, decision, null, StrategyMode.Auto);
        }

        public void Write(
            FanSnapshot snapshot,
            ControlDecision decision,
            AdaptiveTierDiagnostics diagnostics,
            StrategyMode strategyMode)
        {
            Write(snapshot, decision, diagnostics, strategyMode, null);
        }

        public void Write(
            FanSnapshot snapshot,
            ControlDecision decision,
            AdaptiveTierDiagnostics diagnostics,
            StrategyMode strategyMode,
            JointRuntimeDiagnostics joint)
        {
            if (_writer == null || snapshot == null || decision == null || decision.Cpu == null || decision.Gpu == null)
            {
                return;
            }

            lock (_sync)
            {
                DateTime now = snapshot.TimestampUtc == default(DateTime)
                    ? DateTime.Now
                    : snapshot.TimestampUtc.ToLocalTime();

                // 按天滚动：单个 CSV 文件不会无限增长。
                if (now.Date != _fileDate)
                {
                    OpenNewFile(now);
                }

                try
                {
                    _writer.WriteLine(string.Join(",", new[]
                    {
                        snapshot.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                        snapshot.CpuTemperatureC.ToString(CultureInfo.InvariantCulture),
                        snapshot.GpuTemperatureC.ToString(CultureInfo.InvariantCulture),
                        snapshot.CpuTemperatureLocalC.ToString(CultureInfo.InvariantCulture),
                        snapshot.GpuTemperatureLocalC.ToString(CultureInfo.InvariantCulture),
                        CsvEscape(snapshot.GpuTelemetrySource),
                        snapshot.GpuTelemetryUtilization.ToString(CultureInfo.InvariantCulture),
                        F(snapshot.GpuTelemetryPowerWatts),
                        CsvEscape(snapshot.GpuTelemetryPState),
                        F(snapshot.CpuUtilizationPercent),
                        F(snapshot.CpuPerformancePercent),
                        snapshot.CpuDutyPercent.ToString(CultureInfo.InvariantCulture),
                        snapshot.GpuDutyPercent.ToString(CultureInfo.InvariantCulture),
                        snapshot.CpuRpm.ToString(CultureInfo.InvariantCulture),
                        snapshot.GpuRpm.ToString(CultureInfo.InvariantCulture),
                        F(decision.Cpu.FastTemperatureC),
                        F(decision.Cpu.SlowTemperatureC),
                        F(decision.Cpu.ControlTemperatureC),
                        F(decision.Cpu.RawTargetPercent),
                        F(decision.Cpu.AcceptedTargetPercent),
                        F(decision.Cpu.AppliedPercent),
                        decision.Cpu.WritePercent.ToString(CultureInfo.InvariantCulture),
                        F(decision.Cpu.EcReadbackPercent),
                        decision.Cpu.EcReadbackDuty.ToString(CultureInfo.InvariantCulture),
                        decision.Cpu.WriteVerified ? "1" : "0",
                        decision.Cpu.ExternalOverrideDetected ? "1" : "0",
                        decision.Cpu.State.ToString(),
                        F(decision.Cpu.TemperatureRiseRateCPerSec),
                        decision.Cpu.Reason.ToString(),
                        F(decision.Gpu.FastTemperatureC),
                        F(decision.Gpu.SlowTemperatureC),
                        F(decision.Gpu.ControlTemperatureC),
                        F(decision.Gpu.RawTargetPercent),
                        F(decision.Gpu.AcceptedTargetPercent),
                        F(decision.Gpu.AppliedPercent),
                        decision.Gpu.WritePercent.ToString(CultureInfo.InvariantCulture),
                        F(decision.Gpu.EcReadbackPercent),
                        decision.Gpu.EcReadbackDuty.ToString(CultureInfo.InvariantCulture),
                        decision.Gpu.WriteVerified ? "1" : "0",
                        decision.Gpu.ExternalOverrideDetected ? "1" : "0",
                        decision.Gpu.State.ToString(),
                        decision.Gpu.Reason.ToString(),
                        // 自动策略诊断字段
                        strategyMode.ToString(),
                        diagnostics == null ? string.Empty : diagnostics.CurrentTier.ToString(),
                        diagnostics == null || !diagnostics.PendingTier.HasValue ? string.Empty : diagnostics.PendingTier.Value.ToString(),
                        diagnostics == null ? string.Empty : diagnostics.EffectiveTier.ToString(),
                        diagnostics == null ? string.Empty : diagnostics.CoolingState.ToString(),
                        diagnostics == null ? string.Empty : CsvEscape(diagnostics.Reason),
                        diagnostics == null ? string.Empty : F(diagnostics.DwellElapsedSeconds),
                        diagnostics == null ? string.Empty : F(diagnostics.DwellRequiredSeconds),
                        diagnostics == null ? string.Empty : F(diagnostics.UpshiftAverageCpu),
                        diagnostics == null ? string.Empty : F(diagnostics.UpshiftAverageGpu),
                        diagnostics == null ? string.Empty : F(diagnostics.RecentPeakCpu),
                        diagnostics == null ? string.Empty : F(diagnostics.RecentPeakGpu),
                        // CPU/GPU 联合运行时诊断字段（架构收束命名）
                        joint == null ? string.Empty : joint.CpuRequestedPowerTier.ToString(),
                        joint == null ? string.Empty : joint.CpuEffectivePowerTier.ToString(),
                        joint == null ? string.Empty : joint.GpuThermalDemand.ToString(),
                        joint == null ? string.Empty : joint.CpuCoolingState.ToString(),
                        joint == null ? string.Empty : joint.GpuCoolingState.ToString(),
                        joint == null ? string.Empty : (joint.EmergencyOverride ? "1" : "0"),
                        joint == null ? string.Empty : F(joint.CpuFanAssistPercent),
                        joint == null ? string.Empty : F(joint.GpuFanAssistPercent),
                        joint == null ? string.Empty : CsvEscape(joint.GpuPowerBackend),
                        joint == null ? string.Empty : joint.OemModeObserved.ToString(CultureInfo.InvariantCulture),
                        joint == null ? string.Empty : F(joint.CpuPl1RequestedWatts),
                        joint == null ? string.Empty : F(joint.CpuPl2RequestedWatts),
                        joint == null ? string.Empty : F(joint.CpuTauRequestedSeconds),
                        joint == null ? string.Empty : F(joint.CpuPl1ReadbackWatts),
                        joint == null ? string.Empty : F(joint.CpuPl2ReadbackWatts),
                        joint == null ? string.Empty : F(joint.CpuTauReadbackSeconds),
                        joint == null ? string.Empty : CsvEscape(joint.GpuPowerEvent),
                        // 共享热预算让出诊断字段（2026-08-03）
                        joint == null ? string.Empty : (joint.SharedThermalSheddingActive ? "1" : "0"),
                        joint == null ? string.Empty : F(joint.SharedThermalEnterCreditSeconds),
                        joint == null ? string.Empty : F(joint.SharedThermalRecoveryCreditSeconds),
                        joint == null ? string.Empty : joint.CpuFanProfileTier.ToString(),
                        joint == null ? string.Empty : CsvEscape(joint.SharedThermalReason)
                    }));

                    _rowsWritten++;
                    // 表头已立即刷盘；首行数据也立即刷新，之后按周期批量 flush。
                    bool firstRow = _rowsWritten == 1;
                    if (firstRow || (DateTime.UtcNow - _lastFlushUtc).TotalSeconds >= _flushIntervalSeconds)
                    {
                        _writer.Flush();
                        _lastFlushUtc = DateTime.UtcNow;
                    }
                }
                catch (Exception exception)
                {
                    LastWriteError = exception.Message;
                    LastWriteErrorUtc = DateTime.UtcNow;
                }
            }
        }

        private void OpenNewFile(DateTime now)
        {
            if (_writer != null)
            {
                try { _writer.Dispose(); } catch { }
                _writer = null;
            }

            FilePath = Path.Combine(_directory, "fan-" + now.ToString("yyyyMMdd-HHmmss") + ".csv");
            _writer = new StreamWriter(FilePath, false, new UTF8Encoding(true));
            _writer.WriteLine(Header);
            // 表头必须立即刷盘：否则进程在首行数据写入前退出时，文件会
            // 以 0 字节存在且没有表头，故障分析完全失去字段定义。
            _writer.Flush();
            _fileDate = now.Date;
            _lastFlushUtc = DateTime.MinValue; // 首行数据立即刷新
            _rowsWritten = 0;

            // 只保留最近 N 天的 CSV，目录体积有界，无需手动清理。
            CleanupOldFiles();
        }

        private void CleanupOldFiles()
        {
            try
            {
                DateTime cutoff = DateTime.Now.AddDays(-_retentionDays);
                foreach (string file in Directory.GetFiles(_directory, "fan-*.csv"))
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < cutoff)
                        {
                            File.Delete(file);
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static string F(double value)
        {
            return value.ToString("0.00", CultureInfo.InvariantCulture);
        }

        private static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "\"\"";
            if (value.Contains(",") || value.Contains("\"") || value.Contains("\n"))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_writer != null)
                {
                    // StreamWriter.Dispose 会执行最终 Flush。
                    try { _writer.Dispose(); } catch { }
                    _writer = null;
                }
            }
        }
    }
}
