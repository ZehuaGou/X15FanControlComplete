using System;
using System.Globalization;
using System.IO;
using System.Text;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    public sealed class CsvLogger : IDisposable
    {
        public const int DefaultFlushIntervalSeconds = 5;
        public const int DefaultRetentionDays = 7;

        private const string Header =
            "timestamp_utc,cpu_temp_ec,gpu_temp_nvidia,cpu_ec_local,gpu_ec_remote,gpu_telemetry_source,gpu_utilization,gpu_power_watts,gpu_pstate,cpu_utilization,cpu_performance,cpu_duty,gpu_duty,cpu_rpm,gpu_rpm,cpu_fast,cpu_slow,cpu_control,cpu_raw,cpu_target,cpu_applied,cpu_written,cpu_readback,cpu_readback_duty,cpu_write_verified,cpu_external_override,cpu_control_state,cpu_rise_rate,cpu_reason,gpu_fast,gpu_slow,gpu_control,gpu_raw,gpu_target,gpu_applied,gpu_written,gpu_readback,gpu_readback_duty,gpu_write_verified,gpu_external_override,gpu_control_state,gpu_reason";

        private readonly string _directory;
        private readonly int _flushIntervalSeconds;
        private readonly int _retentionDays;
        private readonly object _sync = new object();
        private StreamWriter _writer;
        private DateTime _fileDate;
        private DateTime _lastFlushUtc;

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

        // 每行同步刷盘等于每 500ms 一次磁盘写入，24/7 运行时会持续磨损
        // SSD。改为周期性批量 flush（默认 5s 一次），磁盘 I/O 大幅下降，
        // 最坏只丢失一个 flush 周期内的缓冲数据，退出时仍会最终刷写。
        public void Write(FanSnapshot snapshot, ControlDecision decision)
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
                    decision.Gpu.Reason.ToString()
                }));

                if ((DateTime.UtcNow - _lastFlushUtc).TotalSeconds >= _flushIntervalSeconds)
                {
                    try { _writer.Flush(); } catch { }
                    _lastFlushUtc = DateTime.UtcNow;
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
            _fileDate = now.Date;
            _lastFlushUtc = DateTime.UtcNow;

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
                    _writer.Dispose();
                    _writer = null;
                }
            }
        }
    }
}
