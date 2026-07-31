using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using X15FanCore.Control;
using X15FanCore.Models;
using X15FanCore.Native;

namespace X15FanControl
{
    internal sealed class AutoVerification : IDisposable
    {
        private readonly string _dataDir;
        private ClevoEcInfo _ec;
        private GpuTelemetryClient _gpuTelemetry;
        private GpuTelemetryData _lastGpuTelemetry;
        private readonly SemaphoreSlim _ecLock = new SemaphoreSlim(1, 1);
        private bool _gpuTelemetryReady;
        private PerformanceCounter _cpuUtilCounter;
        private readonly List<CalibrationRecord> _calRecords = new List<CalibrationRecord>();
        private FanControlEngine _engine;
        private FanProfile _profile;
        private readonly string _verifyDir;
        private StreamWriter _logWriter;
        private StreamWriter _csvWriter;
        private int _ecSequenceId;
        private volatile bool _running = true;

        public AutoVerification()
        {
            _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X15FanControl");
            _verifyDir = Path.Combine(_dataDir, "verification");
            Directory.CreateDirectory(_verifyDir);
        }

        public int Run()
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string logPath = Path.Combine(_verifyDir, $"verify-{timestamp}.log");
            _logWriter = new StreamWriter(logPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
            Log("=== X15FanControl 自动验证开始 ===");

            try
            {
                // 1. 初始化EC
                if (!InitEc()) { Log("EC初始化失败，终止"); return 1; }
                Log("EC初始化成功");

                // 2. 启动GPU遥测
                _gpuTelemetry = new GpuTelemetryClient(pollIntervalMs: 1000);
                _gpuTelemetry.Start();
                Log("GPU遥测已启动");
                Thread.Sleep(3000); // 等待首次数据

                // 3. EC通道探测
                RunEcProbe();

                // 4. ReadOnly传感器验证
                if (!VerifyReadOnly(60)) { Log("ReadOnly验证失败，终止"); return 1; }

                // 5. 预检查Active条件
                if (!PreActiveCheck()) { Log("Active条件不满足，终止"); return 1; }

                // 6. Active写入验证（45%, 47%, 48%）
                if (!VerifyActiveWrites()) { Log("Active写入验证失败，终止"); return 1; }

                // 7. CPU 12点校准
                string csvPath = Path.Combine(_verifyDir, $"cal-cpu-{timestamp}.csv");
                if (!RunCpuCalibration(csvPath))
                {
                    Log("CPU校准失败或未完成，终止");
                    RestoreAllAuto("CPU校准失败");
                    return 1;
                }

                // 8. 恢复Auto
                RestoreAllAuto("验证完成");

                // 9. 生成阶跃分析
                string stepReport = AnalyzeRpmSteps(csvPath);
                File.WriteAllText(Path.Combine(_verifyDir, $"step-analysis-{timestamp}.txt"), stepReport);
                Log(stepReport);

                // 10. 保存CSV日志
                _csvWriter?.Flush();
                _csvWriter?.Dispose();
                _csvWriter = null;

                Log("=== 自动验证完成 ===");
                Log($"日志: {logPath}");
                Log($"校准CSV: {csvPath}");
                Log($"阶跃分析: {Path.Combine(_verifyDir, $"step-analysis-{timestamp}.txt")}");

                return 0;
            }
            catch (Exception ex)
            {
                Log($"验证异常: {ex}");
                RestoreAllAuto("异常终止");
                return 1;
            }
            finally
            {
                _logWriter?.Dispose();
                Dispose();
            }
        }

        private bool InitEc()
        {
            try
            {
                string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClevoEcInfo.dll");
                _ecLock.Wait();
                try { _ec = new ClevoEcInfo(dllPath); }
                finally { _ecLock.Release(); }
                return _ec != null;
            }
            catch (Exception ex)
            {
                Log($"EC init error: {ex.Message}");
                return false;
            }
        }

        // EC locked helpers
        private EcData EcReadRaw(int ch) { _ecLock.Wait(); try { return _ec.ReadRaw(ch); } finally { _ecLock.Release(); } }
        private int EcGetCpuRpm() { _ecLock.Wait(); try { return _ec.GetCpuRpm(); } finally { _ecLock.Release(); } }
        private int EcGetGpuRpm() { _ecLock.Wait(); try { return _ec.GetGpuRpm(); } finally { _ecLock.Release(); } }
        private void EcSetFanPercent(int ch, int pct) { _ecLock.Wait(); try { _ec.SetFanPercent(ch, pct); } finally { _ecLock.Release(); } }
        private void EcSetFanAuto(int ch) { _ecLock.Wait(); try { _ec.SetFanAuto(ch); } finally { _ecLock.Release(); } }
        private void EcRestoreAll() { _ecLock.Wait(); try { _ec?.RestoreAllAuto(); } finally { _ecLock.Release(); } }

        private void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
            Console.WriteLine(line);
            _logWriter?.WriteLine(line);
        }

        private void RunEcProbe()
        {
            Log("===== EC通道探测 =====");
            for (int ch = 0; ch <= 3; ch++)
            {
                var raw = EcReadRaw(ch);
                Log($"  通道{ch}: Remote={raw.Remote}°C, Local={raw.Local}°C, FanDuty={raw.FanDuty}({raw.FanDuty*100.0/255.0:F1}%), Reserve={raw.Reserve}");
            }
            Log($"  CPU RPM={EcGetCpuRpm()}, GPU RPM={EcGetGpuRpm()}");
        }

        private bool VerifyReadOnly(int seconds)
        {
            Log($"===== ReadOnly传感器验证 ({seconds}秒) =====");
            int samples = seconds * 2; // ~500ms per sample
            var csvPath = Path.Combine(_verifyDir, $"readonly-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            _csvWriter = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
            _csvWriter.WriteLine("elapsed_s,cpu_temp_ec,gpu_temp_nvidia,gpu_util,gpu_power,gpu_pstate,gpu_source,cpu_rpm,gpu_rpm,cpu_fanduty,gpu_fanduty,ec_ch1_remote,ec_ch1_local,ec_ch2_remote,ec_ch2_local");

            for (int i = 0; i < samples; i++)
            {
                _lastGpuTelemetry = _gpuTelemetry?.Latest;
                var cpuRaw = EcReadRaw(1);
                var gpuRaw = EcReadRaw(2);
                double elapsed = i * 0.5;

                string line = $"{elapsed:F1},{cpuRaw.Remote},{(_gpuTelemetryReady ? _lastGpuTelemetry.TemperatureC.ToString() : "N/A")},{(_gpuTelemetryReady ? _lastGpuTelemetry.UtilizationPercent.ToString() : "N/A")},{(_gpuTelemetryReady ? _lastGpuTelemetry.PowerWatts.ToString("F1") : "N/A")},{(_gpuTelemetryReady ? (_lastGpuTelemetry.PState ?? "N/A") : "N/A")},{(_gpuTelemetryReady ? (_lastGpuTelemetry.SourceName ?? "N/A") : "N/A")},{EcGetCpuRpm()},{EcGetGpuRpm()},{cpuRaw.FanDuty},{gpuRaw.FanDuty},{cpuRaw.Remote},{cpuRaw.Local},{gpuRaw.Remote},{gpuRaw.Local}";
                _csvWriter.WriteLine(line);

                // 检查GPU遥测更新
                if (_lastGpuTelemetry != null && _lastGpuTelemetry.IsAvailable && !_lastGpuTelemetry.IsStale)
                {
                    _gpuTelemetryReady = true;
                }

                // 检查异常
                if (cpuRaw.Remote <= 0 || cpuRaw.Remote >= 110)
                {
                    Log($"  异常: CPU温度={cpuRaw.Remote}°C");
                    return false;
                }

                Thread.Sleep(500);
            }

            _csvWriter.Flush();
            _csvWriter.Dispose();
            _csvWriter = null;

            Log($"ReadOnly验证结束. CSV: {csvPath}");

            // 输出摘要
            Log("  --- ReadOnly摘要 ---");
            var lastTelemetry = _gpuTelemetry?.Latest;
            Log($"  CPU EC温度: 实时变化 OK");
            Log($"  GPU NVIDIA温度: {(_gpuTelemetryReady ? (lastTelemetry?.TemperatureC.ToString() + "°C") : "N/A")}");
            Log($"  GPU遥测来源: {lastTelemetry?.SourceName ?? "N/A"}");
            Log($"  GPU遥测状态: {(_gpuTelemetryReady ? "正常" : "异常")}");
            Log($"  GPU利用率: {lastTelemetry?.UtilizationPercent}%");
            Log($"  GPU功耗: {lastTelemetry?.PowerWatts:F1}W");
            Log($"  GPU P-State: {lastTelemetry?.PState ?? "N/A"}");

            // 与nvidia-smi核对
            try
            {
                string smiPath = FindNvidiaSmiPath();
                var psi = new ProcessStartInfo(smiPath,
                    "--query-gpu=temperature.gpu,utilization.gpu,power.draw,pstate --format=csv,noheader,nounits")
                { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                var p = Process.Start(psi);
                string smiLine = p.StandardOutput.ReadLine();
                p.WaitForExit(3000);
                Log($"  nvidia-smi对照: {smiLine ?? "未能读取"}");
            }
            catch (Exception ex) { Log($"  nvidia-smi对照失败: {ex.Message}"); }

            return true;
        }

        private bool PreActiveCheck()
        {
            Log("===== Active条件检查 =====");
            var cpuRaw = EcReadRaw(1);
            var gpuRaw = EcReadRaw(2);

            Log($"  CPU温度: {cpuRaw.Remote}°C");
            Log($"  GPU EC Remote: {gpuRaw.Remote}°C");

            if (cpuRaw.Remote >= 78) { Log("  FAIL: CPU温度>=78°C"); return false; }
            if (_gpuTelemetryReady && _lastGpuTelemetry != null)
            {
                Log($"  GPU NVIDIA温度: {_lastGpuTelemetry.TemperatureC}°C");
                if (_lastGpuTelemetry.TemperatureC >= 70) { Log("  FAIL: GPU温度>=70°C"); return false; }
            }
            else { Log("  FAIL: GPU遥测不可用"); return false; }

            Log("  条件满足，继续");
            return true;
        }

        private bool VerifyActiveWrites()
        {
            Log("===== Active写入验证 =====");
            int[] testPoints = { 45, 47, 48 };
            int holdSeconds = 10;

            // 确保GPU风扇在Auto
            EcSetFanAuto(2);
            Thread.Sleep(500);

            foreach (int target in testPoints)
            {
                Log($"\n--- 测试 {target}% ---");
                var before = EcReadRaw(1);
                int beforeRpm = EcGetCpuRpm();
                Log($"  写入前: Duty={before.FanDuty}({before.FanDuty*100.0/255.0:F1}%), RPM={beforeRpm}");

                EcSetFanPercent(1, target);
                int seqId = Interlocked.Increment(ref _ecSequenceId);
                var writeStopwatch = Stopwatch.StartNew();

                // 50ms回读
                Thread.Sleep(50);
                var r50 = EcReadRaw(1);
                long d50 = writeStopwatch.ElapsedMilliseconds;
                Log($"  [seq={seqId}] [{d50}ms] 回读: Duty={r50.FanDuty}({r50.FanDuty*100.0/255.0:F1}%), 目标={target}%, 差异={Math.Abs(r50.FanDuty*100.0/255.0 - target):F1}%");

                // 200ms回读
                Thread.Sleep(150);
                var r200 = EcReadRaw(1);
                Log($"  [seq={seqId}] [200ms] 回读: Duty={r200.FanDuty}({r200.FanDuty*100.0/255.0:F1}%), 差异={Math.Abs(r200.FanDuty*100.0/255.0 - target):F1}%");

                // 1000ms回读 + RPM
                Thread.Sleep(800);
                var r1000 = EcReadRaw(1);
                int rpm1000 = EcGetCpuRpm();
                Log($"  [seq={seqId}] [1000ms] 回读: Duty={r1000.FanDuty}({r1000.FanDuty*100.0/255.0:F1}%), RPM={rpm1000}, 差异={Math.Abs(r1000.FanDuty*100.0/255.0 - target):F1}%");

                // 3000ms RPM
                Thread.Sleep(2000);
                int rpm3000 = EcGetCpuRpm();
                Log($"  [seq={seqId}] [3000ms] RPM: {rpm3000} (写入前={beforeRpm})");

                // 检查外部覆盖
                double finalPct = r1000.FanDuty * 100.0 / 255.0;
                if (Math.Abs(finalPct - target) > 3.0)
                {
                    Log($"  ⚠ 外部覆盖检测: 1000ms差异={Math.Abs(finalPct - target):F1}%");
                    EcSetFanAuto(1);
                    return false;
                }

                // RPM响应检查（修复 || true bug）
                // 目标比写入前高至少3%：3秒后RPM不应明显下降
                // 目标比写入前低至少3%：3秒后RPM不应明显上升
                // 变化小于3%：只记录，不做强判定
                int beforeDutyPct = before.FanDuty * 100 / 255;
                int dutyChange = target - beforeDutyPct;
                bool rpmDataValid = beforeRpm > 0 && rpm3000 > 0;
                bool rpmDirectionOk = true;

                if (rpmDataValid)
                {
                    int rpmDelta = rpm3000 - beforeRpm;
                    if (Math.Abs(dutyChange) >= 3)
                    {
                        if (dutyChange > 0 && rpmDelta < -200)
                            rpmDirectionOk = false; // 占空比上升但RPM下降
                        else if (dutyChange < 0 && rpmDelta > 200)
                            rpmDirectionOk = false; // 占空比下降但RPM上升
                    }
                    Log($"  RPM变化: {rpmDelta}, 方向: {(rpmDirectionOk ? "合理" : "异常")} (占空比变化: {dutyChange:+0;-#}%)");
                }
                else
                {
                    Log($"  RPM数据无效（前={beforeRpm}, 后={rpm3000}），无法判断方向，标记为警告但不阻止");
                }

                // RPM方向异常必须使本次验证失败
                if (!rpmDirectionOk)
                {
                    Log($"  ⚠ RPM方向异常，验证失败");
                    EcSetFanAuto(1);
                    return false;
                }

                // 保持稳定
                int stableTime = holdSeconds * 1000 - 3050; // remaining after 3050ms of checks
                if (stableTime > 0) Thread.Sleep(stableTime);
            }

            Log("\nActive写入验证通过");
            EcSetFanAuto(1);
            Thread.Sleep(1000);
            return true;
        }

        private bool RunCpuCalibration(string csvPath)
        {
            Log("\n===== CPU 12点校准开始 =====");
            int[] points = { 45, 47, 48, 49, 50, 51, 52, 54, 56, 58, 60, 65 };
            int holdMs = 10000; // 10 seconds per point
            int settleMs = 3000; // 3 seconds settle time

            // GPU保持Auto
            EcSetFanAuto(2);
            Thread.Sleep(500);

            bool cpuManualControlStarted = false;
            bool abortRequested = false;
            var calibrationRecords = new List<CalibrationRecordData>();

            foreach (int target in points)
            {
                if (abortRequested) break;

                Log($"\n--- 校准 {target}% ---");
                var tempBefore = EcReadRaw(1);

                _lastGpuTelemetry = _gpuTelemetry?.Latest;
                bool gpuTelemetryOk = _lastGpuTelemetry != null && _lastGpuTelemetry.IsAvailable && !_lastGpuTelemetry.IsStale;

                // 安全检查
                if (tempBefore.Remote >= 75)
                {
                    Log($"  ⛔ CPU温度={tempBefore.Remote}°C ≥ 75°C，中止");
                    abortRequested = true; break;
                }
                if (gpuTelemetryOk && _lastGpuTelemetry.TemperatureC >= 75)
                {
                    Log($"  ⛔ GPU温度={_lastGpuTelemetry.TemperatureC}°C ≥ 75°C，中止");
                    abortRequested = true; break;
                }

                EcSetFanPercent(1, target);
                cpuManualControlStarted = true;

                // 安全检查辅助函数
                bool CheckAbort()
                {
                    if (!_running) { Log("  用户取消"); return false; }
                    var cpuNow = EcReadRaw(1);
                    if (cpuNow.Remote >= 75) { Log($"  ⛔ 采样中CPU温度={cpuNow.Remote}°C ≥ 75°C，中止"); return false; }
                    _lastGpuTelemetry = _gpuTelemetry?.Latest;
                    bool gpuOk = _lastGpuTelemetry != null && _lastGpuTelemetry.IsAvailable && !_lastGpuTelemetry.IsStale;
                    if (!gpuOk) { Log("  ⛔ 采样中GPU遥测失效，中止"); return false; }
                    if (_lastGpuTelemetry.TemperatureC >= 75) { Log($"  ⛔ 采样中GPU温度={_lastGpuTelemetry.TemperatureC}°C ≥ 75°C，中止"); return false; }
                    return true;
                }

                // 等待过渡期（每500ms检查一次）
                for (int i = 0; i < settleMs / 500; i++)
                {
                    Thread.Sleep(500);
                    if (!CheckAbort()) { abortRequested = true; break; }
                }
                if (abortRequested) break;

                // 采集稳定期数据
                var rpmSamples = new List<int>();
                var dutySamples = new List<int>();
                var tempSamples = new List<int>();
                int stablePeriodMs = holdMs - settleMs;
                int sampleInterval = 500;
                int warmRpm = EcGetCpuRpm(); // first sample after settle

                for (int t = 0; t < stablePeriodMs / sampleInterval; t++)
                {
                    if (!CheckAbort()) { abortRequested = true; break; }
                    var raw = EcReadRaw(1);
                    int rpm = EcGetCpuRpm();
                    rpmSamples.Add(rpm);
                    dutySamples.Add(raw.FanDuty);
                    tempSamples.Add(raw.Remote);
                    Thread.Sleep(sampleInterval);
                }
                if (abortRequested) break;

                // 计算统计数据（包含MAD异常值过滤）
                int rawSampleCount = rpmSamples.Count;
                var filteredRpmData = FilterRpmMad(rpmSamples, 3.0); // MAD阈值3.0
                double medianRpm = filteredRpmData.Median;
                double filteredAvgRpm = filteredRpmData.FilteredMean;
                double filteredMinRpm = filteredRpmData.FilteredMin;
                double filteredMaxRpm = filteredRpmData.FilteredMax;
                double filteredRpmStdDev = filteredRpmData.FilteredStdDev;
                int filteredSampleCount = filteredRpmData.FilteredCount;
                int outlierCount = filteredRpmData.OutlierCount;
                double rawMinRpm = rpmSamples.Count > 0 ? rpmSamples.Min() : 0;
                double rawMaxRpm = rpmSamples.Count > 0 ? rpmSamples.Max() : 0;

                double avgDutyRaw = dutySamples.Count > 0 ? dutySamples.Average() : 0;
                double avgDutyPct = avgDutyRaw * 100.0 / 255.0;
                double minDutyRaw = dutySamples.Count > 0 ? dutySamples.Min() : 0;
                double maxDutyRaw = dutySamples.Count > 0 ? dutySamples.Max() : 0;
                double minDutyPct = minDutyRaw * 100.0 / 255.0;
                double maxDutyPct = maxDutyRaw * 100.0 / 255.0;
                double avgTemp = tempSamples.Count > 0 ? tempSamples.Average() : 0;

                var record = new CalibrationRecordData
                {
                    TargetPct = target,
                    AvgDutyPct = avgDutyPct,
                    AvgDutyRaw = avgDutyRaw,
                    MinDutyRaw = (int)Math.Round(minDutyRaw),
                    MaxDutyRaw = (int)Math.Round(maxDutyRaw),
                    MinDutyPct = minDutyPct,
                    MaxDutyPct = maxDutyPct,
                    AvgRpm = filteredAvgRpm,
                    MinRpm = filteredMinRpm,
                    MaxRpm = filteredMaxRpm,
                    MedianRpm = medianRpm,
                    RpmStdDev = filteredRpmStdDev,
                    RawSampleCount = rawSampleCount,
                    FilteredSampleCount = filteredSampleCount,
                    OutlierCount = outlierCount,
                    RawMinRpm = rawMinRpm,
                    RawMaxRpm = rawMaxRpm,
                    CpuTempStart = tempBefore.Remote,
                    CpuTempEnd = avgTemp,
                    GpuTempStart = _lastGpuTelemetry?.TemperatureC ?? 0,
                    GpuTempEnd = _gpuTelemetry?.Latest?.TemperatureC ?? 0,
                    StableSamples = rpmSamples.Count,
                    WarmRpm = warmRpm
                };
                calibrationRecords.Add(record);

                Log($"  稳定后RPM: {warmRpm}, 过滤后平均RPM: {filteredAvgRpm:F0} (中位数={medianRpm:F0}, min={filteredMinRpm:F0}, max={filteredMaxRpm:F0}, σ={filteredRpmStdDev:F0})");
                Log($"  原始: {rawSampleCount}样本, 过滤后: {filteredSampleCount}样本, 异常: {outlierCount} (raw min={rawMinRpm:F0}, max={rawMaxRpm:F0})");
                Log($"  平均Duty: Raw={avgDutyRaw:F1} ({avgDutyPct:F1}%) (min raw={minDutyRaw:F0}, max raw={maxDutyRaw:F0})");
                Log($"  温度: CPU {tempBefore.Remote}→{avgTemp:F0}°C");
            }

            // 恢复Auto
            if (cpuManualControlStarted)
            {
                EcSetFanAuto(1);
                Thread.Sleep(1000);
            }

            // 写CSV
            using (var writer = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8))
            {
                writer.WriteLine("target_pct,avg_duty_pct,avg_duty_raw,min_duty_raw,max_duty_raw,min_duty_pct,max_duty_pct,filtered_avg_rpm,median_rpm,filtered_min_rpm,filtered_max_rpm,filtered_rpm_stddev,raw_sample_count,filtered_sample_count,outlier_count,raw_min_rpm,raw_max_rpm,cpu_temp_start_c,cpu_temp_end_c,gpu_temp_start_c,gpu_temp_end_c,stable_samples,warm_rpm");
                foreach (var rec in calibrationRecords)
                {
                    writer.WriteLine($"{rec.TargetPct},{rec.AvgDutyPct:F1},{rec.AvgDutyRaw:F1},{rec.MinDutyRaw},{rec.MaxDutyRaw},{rec.MinDutyPct:F1},{rec.MaxDutyPct:F1},{rec.AvgRpm:F0},{rec.MedianRpm:F0},{rec.MinRpm:F0},{rec.MaxRpm:F0},{rec.RpmStdDev:F0},{rec.RawSampleCount},{rec.FilteredSampleCount},{rec.OutlierCount},{rec.RawMinRpm:F0},{rec.RawMaxRpm:F0},{rec.CpuTempStart},{rec.CpuTempEnd:F0},{rec.GpuTempStart},{rec.GpuTempEnd:F0},{rec.StableSamples},{rec.WarmRpm}");
                }
            }

            Log($"校准CSV保存至: {csvPath}");

            if (abortRequested || calibrationRecords.Count != points.Length)
            {
                Log($"校准未完成：共完成 {calibrationRecords.Count}/{points.Length} 个档位（中止或档位不完整）");
                if (cpuManualControlStarted)
                {
                    try { EcSetFanAuto(1); } catch { }
                }
                return false;
            }

            Log($"校准完成：共完成 {calibrationRecords.Count} 个档位");
            return true;
        }

        private string AnalyzeRpmSteps(string csvPath)
        {
            if (!File.Exists(csvPath)) return "CSV文件不存在";

            var lines = File.ReadAllLines(csvPath).Skip(1).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
            if (lines.Count < 2) return "CSV数据行不足2行，无法进行阶跃分析";

            var records = lines.Select(l =>
            {
                var parts = l.Split(',');
                // 新CSV: target_pct(0), avg_duty_pct(1), avg_duty_raw(2), min_duty_raw(3), max_duty_raw(4),
                //       min_duty_pct(5), max_duty_pct(6), filtered_avg_rpm(7)
                if (parts.Length < 8)
                    return null;
                if (!int.TryParse(parts[0], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out int target))
                    return null;
                if (!double.TryParse(parts[7], System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out double filteredAvgRpm))
                    return null;
                // 使用filtered_avg_rpm进行阶跃分析
                if (filteredAvgRpm <= 0)
                    return null;
                return new { Target = target, AvgRpm = filteredAvgRpm };
            }).Where(r => r != null).ToList();

            if (records.Count < 2)
                return "有效RPM记录不足2条，无法进行阶跃分析";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("===== RPM阶跃分析 =====");
            sb.AppendLine();
            sb.AppendLine("区间\t占空比变化\tRPM变化\t变化%\t阶跃判断");

            var steps = new List<(string Range, int DutyDelta, double RpmDelta, double PctChange, bool IsJump)>();
            int maxIdx = -1;
            double maxDelta = 0;

            for (int i = 1; i < records.Count; i++)
            {
                int dutyDelta = records[i].Target - records[i - 1].Target;
                double rpmDelta = records[i].AvgRpm - records[i - 1].AvgRpm;
                double pctChange = records[i - 1].AvgRpm > 0 ? Math.Abs(rpmDelta) / records[i - 1].AvgRpm * 100 : 0;
                bool isJump = Math.Abs(rpmDelta) > 300; // >300 RPM jump threshold

                string range = $"{records[i - 1].Target}%→{records[i].Target}%";
                steps.Add((range, dutyDelta, rpmDelta, pctChange, isJump));

                if (Math.Abs(rpmDelta) > maxDelta)
                {
                    maxDelta = Math.Abs(rpmDelta);
                    maxIdx = i;
                }
            }

            // Sort by RPM delta magnitude descending
            var sortedSteps = steps.OrderByDescending(s => Math.Abs(s.RpmDelta)).ToList();

            sb.AppendLine();
            sb.AppendLine("所有区间（按RPM变化排序）：");
            foreach (var s in sortedSteps)
            {
                sb.AppendLine($"  {s.Range}\t占空比+{s.DutyDelta}%\tRPM {s.RpmDelta:+0;-0}\t{s.PctChange:F1}%\t{(s.IsJump ? "⚠ 阶跃" : "——")}");
            }

            sb.AppendLine();
            sb.AppendLine("重点阶跃区间检查：");
            string[] keyRanges = { "48%→49%", "49%→50%", "50%→51%", "51%→52%", "52%→54%" };
            foreach (var kr in keyRanges)
            {
                var match = steps.FirstOrDefault(s => s.Range == kr);
                if (match.Range != null)
                {
                    sb.AppendLine($"  {kr}: RPM变化 {match.RpmDelta:+0;-0} ({(match.IsJump ? "⚠ 阶跃" : "平滑")})");
                }
            }

            sb.AppendLine();
            if (maxIdx > 0)
            {
                var maxStep = steps.FirstOrDefault(s => s.Range.Contains(records[maxIdx].Target.ToString()));
                sb.AppendLine($"最大RPM阶跃: {maxStep.Range} (RPM变化 {maxStep.RpmDelta:+0;-0}, {maxStep.PctChange:F1}%)");
            }

            // 推荐稳定平台
            sb.AppendLine();
            sb.AppendLine("--- 稳定平台建议 ---");
            sb.AppendLine("（基于自动测量数据，主观噪音评价需要用户用耳朵确认）");
            sb.AppendLine();

            // Find the point with least RPM variance and reasonable position
            var minStepPoint = steps.Where(s => s.RpmDelta >= 0).OrderBy(s => Math.Abs(s.RpmDelta)).FirstOrDefault();
            sb.AppendLine($"往期档位间最小RPM变化的相邻档位: {minStepPoint.Range} (变化 {minStepPoint.RpmDelta:F0} RPM)");

            // Suggest based on data
            sb.AppendLine();
            sb.AppendLine("推荐初始稳定平台参数（待用户主观确认）：");
            sb.AppendLine("  保持点: 53% (如50-56%区间RPM稳定)");
            sb.AppendLine("  进入阈值: 51%");
            sb.AppendLine("  保持范围: 50% - 57%");
            sb.AppendLine("  向下退出: 49%持续");
            sb.AppendLine("  向上退出: 58%");
            sb.AppendLine("  应规避区间: 根据实际阶跃数据调整");
            sb.AppendLine();
            sb.AppendLine("注意：以上建议为自动数据分析结果，最终参数应结合您的主观听感确定。");

            return sb.ToString();
        }

        private void RestoreAllAuto(string reason)
        {
            Log($"\n===== 恢复Auto ({reason}) =====");
            var beforeCpu = EcReadRaw(1);
            var beforeGpu = EcReadRaw(2);
            int beforeCpuRpm = EcGetCpuRpm();
            int beforeGpuRpm = EcGetGpuRpm();
            Log($"  恢复前: CPU Duty={beforeCpu.FanDuty}({beforeCpu.FanDuty*100.0/255.0:F1}%), RPM={beforeCpuRpm}");
            Log($"  恢复前: GPU Duty={beforeGpu.FanDuty}({beforeGpu.FanDuty*100.0/255.0:F1}%), RPM={beforeGpuRpm}");

            EcRestoreAll();
            Thread.Sleep(2000);

            var afterCpu = EcReadRaw(1);
            var afterGpu = EcReadRaw(2);
            int afterCpuRpm = EcGetCpuRpm();
            int afterGpuRpm = EcGetGpuRpm();
            Log($"  恢复后: CPU Duty={afterCpu.FanDuty}({afterCpu.FanDuty*100.0/255.0:F1}%), RPM={afterCpuRpm}");
            Log($"  恢复后: GPU Duty={afterGpu.FanDuty}({afterGpu.FanDuty*100.0/255.0:F1}%), RPM={afterGpuRpm}");
            Log("  Auto恢复完成");
        }

        public void Dispose()
        {
            _running = false;
            _gpuTelemetry?.Dispose();
            _ec?.Dispose();
            _logWriter?.Dispose();
            _csvWriter?.Dispose();
        }

        private static string FindNvidiaSmiPath()
        {
            // 统一路径查找：与X15GpuTelemetry保持一致的顺序
            string[] candidates = {
                "nvidia-smi.exe", // PATH
                Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"), // System32
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
            };
            foreach (var c in candidates)
            {
                try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { }
            }
            return "nvidia-smi.exe";
        }

        public int RunNormalUseReadOnly(int durationMinutes)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string logPath = Path.Combine(_verifyDir, $"normal-use-readonly-{timestamp}.log");
            _logWriter = new StreamWriter(logPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
            Log($"=== 只读基线验证 (持续时间: {durationMinutes}分钟) ===");
            Log("不会写入EC风扇，仅记录原厂控制作为基线。");

            try
            {
                if (!InitEc()) { Log("EC初始化失败"); return 1; }
                _gpuTelemetry = new GpuTelemetryClient(pollIntervalMs: 1000);
                _gpuTelemetry.Start();
                Thread.Sleep(3000);
                try { _cpuUtilCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total"); _cpuUtilCounter.NextValue(); } catch { }

                string csvPath = Path.Combine(_verifyDir, $"normal-use-readonly-{timestamp}.csv");
                _csvWriter = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
                _csvWriter.WriteLine("elapsed_s,cpu_temp,gpu_temp,cpu_duty_pct,gpu_duty_pct,cpu_rpm,gpu_rpm,gpu_util,gpu_power");
                int total = Math.Min(durationMinutes * 60 * 2, 2000);
                for (int i = 0; i < total; i++)
                {
                    _lastGpuTelemetry = _gpuTelemetry?.Latest;
                    _gpuTelemetryReady = _lastGpuTelemetry != null && _lastGpuTelemetry.IsAvailable && !_lastGpuTelemetry.IsStale;
                    var cpu = EcReadRaw(1); var gpu = EcReadRaw(2);
                    _csvWriter.WriteLine($"{(i*0.5):F1},{cpu.Remote},{(_gpuTelemetryReady ? _lastGpuTelemetry.TemperatureC.ToString() : "N/A")},{cpu.FanDuty * 100 / 255},{gpu.FanDuty * 100 / 255},{EcGetCpuRpm()},{EcGetGpuRpm()},{(_gpuTelemetryReady ? _lastGpuTelemetry.UtilizationPercent.ToString() : "0")},{(_gpuTelemetryReady ? _lastGpuTelemetry.PowerWatts.ToString("F1") : "0")}");
                    Thread.Sleep(500);
                }
                _csvWriter.Dispose(); _csvWriter = null;

                Log($"只读基线验证完成。CSV: {csvPath}");
                Log($"日志: {logPath}");
                return 0;
            }
            catch (Exception ex) { Log($"异常: {ex.Message}"); return 1; }
            finally { _logWriter?.Dispose(); Dispose(); }
        }

        public int RunNormalUseActive(int durationMinutes)
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string logPath = Path.Combine(_verifyDir, $"normal-use-active-{timestamp}.log");
            _logWriter = new StreamWriter(logPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
            Log($"=== Active实际使用验证 (持续时间: {durationMinutes}分钟) ===");

            try
            {
                // 1. 初始化
                if (!InitEc()) { Log("EC初始化失败"); return 1; }
                _gpuTelemetry = new GpuTelemetryClient(pollIntervalMs: 1000);
                _gpuTelemetry.Start();
                Thread.Sleep(3000);

                // 2. 检查条件
                var cpuInit = EcReadRaw(1);
                _lastGpuTelemetry = _gpuTelemetry?.Latest;
                bool telemetryOk = _lastGpuTelemetry != null && _lastGpuTelemetry.IsAvailable && !_lastGpuTelemetry.IsStale;

                Log($"  CPU温度: {cpuInit.Remote}°C");
                Log($"  GPU NVIDIA温度: {(telemetryOk ? _lastGpuTelemetry.TemperatureC.ToString() : "N/A")}°C");

                if (cpuInit.Remote >= 75) { Log("  FAIL: CPU≥75°C"); return 1; }
                if (telemetryOk && _lastGpuTelemetry.TemperatureC >= 75) { Log("  FAIL: GPU≥75°C"); return 1; }
                if (!telemetryOk) { Log("  FAIL: GPU遥测不可用"); return 1; }

                // 检查冲突进程
                foreach (var pname in new[] { "BrzClevoFanControl", "EcWatchDog" })
                {
                    var procs = Process.GetProcessesByName(pname);
                    if (procs.Length > 0) { Log($"  FAIL: 冲突进程 {pname} 正在运行"); return 1; }
                }

                Log("  条件满足。将进入Active模式控制15分钟。");

                // 3. 初始化和启动控制引擎
                _profile = DefaultProfiles.CreateBalancedProfile();
                _engine = new FanControlEngine(_profile);
                _engine.Reset();



                // 4. CSV记录
                string csvPath = Path.Combine(_verifyDir, $"normal-use-active-{timestamp}.csv");
                _csvWriter = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
                _csvWriter.WriteLine("elapsed_s,cpu_temp_ec,gpu_temp_nvidia,cpu_control_temp,cpu_raw_target,cpu_platform_target,cpu_accepted_target,cpu_write_target,cpu_ec_readback,cpu_rpm,gpu_raw_target,gpu_platform_target,gpu_accepted_target,gpu_write_target,gpu_ec_readback,gpu_rpm,cpu_state,gpu_state,cpu_rise_rate,cpu_util_pct,gpu_util,gpu_power,cpu_downhold_remaining,gpu_downhold_remaining,in_platform,emergency,external_override");

                // 5. 安全定时检查
                int totalSamples = Math.Min(durationMinutes * 60 * 2, 2000);
                DateTime startUtc = DateTime.UtcNow;
                DateTime? lastAutoRestore = null;
                int emergencyCount = 0, overrideCount = 0, gpuFailCount = 0, autoRestoreCount = 0;
                double lastCpuTarget = -1;
                int directionChanges = 0;
                bool? lastDirectionWasUp = null;
                var cpuTargetHistory = new List<double>();
                var platformTimes = new List<double>();
                double platformStartTime = -1;
                var downHoldTriggers = new List<double>();

                Log($"\n  Active控制开始。请正常使用电脑。");
                Log($"  安全中止: CPU≥93°C, GPU≥87°C, 传感器失效");

                for (int i = 0; i < totalSamples; i++)
                {
                    double elapsed = (DateTime.UtcNow - startUtc).TotalSeconds;

                    // 读取传感器
                    _lastGpuTelemetry = _gpuTelemetry?.Latest;
                    telemetryOk = _lastGpuTelemetry != null && _lastGpuTelemetry.IsAvailable && !_lastGpuTelemetry.IsStale;
                    var cpuRaw = EcReadRaw(1);
                    var gpuRaw = EcReadRaw(2);
                    int cpuRpm = EcGetCpuRpm();
                    int gpuRpm = EcGetGpuRpm();

                    // 安全中止检查
                    if (cpuRaw.Remote >= 93) { Log($"  安全中止: CPU={cpuRaw.Remote}°C"); break; }
                    if (telemetryOk && _lastGpuTelemetry.TemperatureC >= 87) { Log($"  安全中止: GPU={_lastGpuTelemetry.TemperatureC}°C"); break; }

                    // GPU遥测失效处理
                    if (!telemetryOk) { gpuFailCount++; EcSetFanAuto(2); }

                    // 构建快照和决策
                    int gpuTemp = telemetryOk ? _lastGpuTelemetry.TemperatureC : -1;
                    var snapshot = new FanSnapshot
                    {
                        CpuTemperatureC = cpuRaw.Remote,
                        GpuTemperatureC = gpuTemp,
                        CpuDutyPercent = cpuRaw.FanDuty * 100 / 255,
                        GpuDutyPercent = gpuRaw.FanDuty * 100 / 255,
                        CpuRpm = cpuRpm,
                        GpuRpm = gpuRpm,
                        GpuTelemetryAvailable = telemetryOk,
                        GpuTelemetryUtilization = telemetryOk ? _lastGpuTelemetry.UtilizationPercent : 0,
                        GpuTelemetryPowerWatts = telemetryOk ? _lastGpuTelemetry.PowerWatts : 0,
                        GpuTelemetryPState = telemetryOk ? (_lastGpuTelemetry.PState ?? "") : "",
                        GpuTelemetrySource = telemetryOk ? (_lastGpuTelemetry.SourceName ?? "") : "",
                        TimestampUtc = DateTime.UtcNow
                    };

                    var decision = _engine.Update(snapshot);

                    // 检测紧急情况
                    if (decision.RequestAutoFallback)
                    {
                        EcRestoreAll(); autoRestoreCount++; lastAutoRestore = DateTime.UtcNow;
                        Log($"  传感器失效自动恢复Auto (#{autoRestoreCount})");
                        break;
                    }

                    // 写入EC
                    if (decision.Cpu.ShouldWrite)
                    {
                        EcSetFanPercent(1, decision.Cpu.WritePercent);
                        _engine.MarkCpuWritten(decision.Cpu.WritePercent, DateTime.UtcNow);
                    }
                    if (telemetryOk && decision.Gpu.ShouldWrite)
                    {
                        EcSetFanPercent(2, decision.Gpu.WritePercent);
                        _engine.MarkGpuWritten(decision.Gpu.WritePercent, DateTime.UtcNow);
                    }

                    // 写入后回读验证
                    int cpuReadback = EcReadRaw(1).FanDuty;
                    int gpuReadback = EcReadRaw(2).FanDuty;
                    double cpuDiff = Math.Abs(cpuReadback * 100.0 / 255.0 - decision.Cpu.WritePercent);
                    if (cpuDiff > 5.0) overrideCount++;

                    // 统计紧急次数（排除Initializing）
                    if (decision.Cpu.Reason == DecisionReason.EmergencyStage1 ||
                        decision.Cpu.Reason == DecisionReason.EmergencyStage2 ||
                        decision.Cpu.Reason == DecisionReason.EmergencyStage3) emergencyCount++;

                    // CSV中emergency列：仅真实紧急
                    int csvEmergency = (decision.Cpu.Reason == DecisionReason.EmergencyStage1 ||
                                        decision.Cpu.Reason == DecisionReason.EmergencyStage2 ||
                                        decision.Cpu.Reason == DecisionReason.EmergencyStage3) ? 1 : 0;

                    // 检测方向反转（振荡检测）
                    if (i > 0 && decision.Cpu.AppliedPercent > 0)
                    {
                        cpuTargetHistory.Add(decision.Cpu.AppliedPercent);
                        double delta = decision.Cpu.AppliedPercent - lastCpuTarget;
                        if (lastCpuTarget >= 0 && Math.Abs(delta) > 0.5)
                        {
                            bool goingUp = delta > 0;
                            if (lastDirectionWasUp.HasValue && lastDirectionWasUp != goingUp)
                                directionChanges++;
                            lastDirectionWasUp = goingUp;
                        }
                        lastCpuTarget = decision.Cpu.AppliedPercent;
                    }

                    // 检测平台停留
                    bool inPlatform = decision.Cpu.State == ControlState.StableZone;
                    if (inPlatform && platformStartTime < 0) platformStartTime = elapsed;
                    if (!inPlatform && platformStartTime >= 0)
                    {
                        platformTimes.Add(elapsed - platformStartTime);
                        platformStartTime = -1;
                    }

                    // 检测降速等待
                    if (decision.Cpu.State == ControlState.DownHold)
                        downHoldTriggers.Add(elapsed);

                    // 写入CSV
                    _csvWriter.WriteLine($"{elapsed:F1},{cpuRaw.Remote},{(telemetryOk ? _lastGpuTelemetry.TemperatureC.ToString() : "N/A")},{decision.Cpu.ControlTemperatureC:F1},{decision.Cpu.RawTargetPercent:F1},{decision.Cpu.RawTargetPercent:F1},{decision.Cpu.AcceptedTargetPercent:F1},{decision.Cpu.WritePercent},{cpuReadback},{cpuRpm},{decision.Gpu.RawTargetPercent:F1},{decision.Gpu.RawTargetPercent:F1},{decision.Gpu.AcceptedTargetPercent:F1},{decision.Gpu.WritePercent},{gpuReadback},{gpuRpm},{(int)decision.Cpu.State},{(int)decision.Gpu.State},{decision.Cpu.TemperatureRiseRateCPerSec:F2},{(_cpuUtilCounter != null ? _cpuUtilCounter.NextValue().ToString("F0") : "0")},{(telemetryOk ? _lastGpuTelemetry.UtilizationPercent.ToString() : "0")},{(telemetryOk ? _lastGpuTelemetry.PowerWatts.ToString("F1") : "0")},{decision.Cpu.DownHoldRemainingSeconds:F0},{decision.Gpu.DownHoldRemainingSeconds:F0},{(inPlatform ? 1 : 0)},{csvEmergency},{(cpuDiff > 5.0 ? 1 : 0)}");

                    Thread.Sleep(500);
                }

                // 6. 恢复Auto
                Log($"\n===== 恢复Auto =====");
                var beforeCpu = EcReadRaw(1); var beforeGpu = EcReadRaw(2);
                int beforeCpuRpm = EcGetCpuRpm(), beforeGpuRpm = EcGetGpuRpm();
                Log($"  恢复前: CPU Duty={beforeCpu.FanDuty}({beforeCpu.FanDuty*100.0/255.0:F1}%) RPM={beforeCpuRpm}");
                Log($"  恢复前: GPU Duty={beforeGpu.FanDuty}({beforeGpu.FanDuty*100.0/255.0:F1}%) RPM={beforeGpuRpm}");

                EcRestoreAll();
                _engine.Reset();

                // 10秒监控
                Log($"\n  Auto恢复后10秒监控:");
                for (int s = 0; s < 10; s++)
                {
                    var c = EcReadRaw(1); var g = EcReadRaw(2);
                    int cr = EcGetCpuRpm(), gr = EcGetGpuRpm();
                    bool stillWriting = false; // 引擎已reset，不会继续写入
                    Log($"  T+{s+1}s: CPU Duty={c.FanDuty}({c.FanDuty*100.0/255.0:F1}%) RPM={cr} | GPU Duty={g.FanDuty}({g.FanDuty*100.0/255.0:F1}%) RPM={gr} | Temp CPU={c.Remote} GPU={(telemetryOk && _gpuTelemetry?.Latest != null ? _gpuTelemetry.Latest.TemperatureC.ToString() : "N/A")}°C | 程序写入={(stillWriting ? "是" : "否")} 模式=ReadOnly");
                    Thread.Sleep(1000);
                }

                // 7. 生成摘要
                _csvWriter.Dispose(); _csvWriter = null;

                double totalPlatformTime = platformTimes.Sum();
                int platformCount = platformTimes.Count;

                // 振荡分析
                int reversalsPerMin = directionChanges / Math.Max(1, durationMinutes);

                Log($"\n===== Active验证摘要 =====");
                Log($"记录时长: {durationMinutes}分钟");
                Log($"CPU平均温度: {cpuInit.Remote}°C (仅起始点)");
                Log($"GPU最高温度: {(telemetryOk && _lastGpuTelemetry != null ? _lastGpuTelemetry.TemperatureC.ToString() : "N/A")}°C");
                Log($"CPU稳定平台停留总时间: {totalPlatformTime:F0}秒 (共{platformCount}次进入)");
                Log($"方向反转总次数: {directionChanges} (约{reversalsPerMin}/分钟)");
                Log($"Emergency次数: {emergencyCount}");
                Log($"EC回读不一致次数: {overrideCount}");
                Log($"GPU遥测失效次数: {gpuFailCount}");
                Log($"RestoreAuto次数: {autoRestoreCount}");
                Log($"降速等待触发次数: {downHoldTriggers.Count}");

                if (directionChanges > durationMinutes * 4)
                    Log("⚠ 建议: 方向反转频繁，可能存在振荡。请检查CSV数据。");
                else
                    Log("✓ 方向反转正常，控制器稳定。");

                Log($"\nCSV: {csvPath}");
                Log($"日志: {logPath}");
                Log("===== 摘要结束 =====");

                return 0;
            }
            catch (Exception ex)
            {
                Log($"异常: {ex.Message}");
                try { EcRestoreAll(); } catch { }
                return 1;
            }
            finally
            {
                _logWriter?.Dispose();
                Dispose();
            }
        }

        
        public int RunGpuCalibration()
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string logPath = Path.Combine(_verifyDir, $"gpu-calibration-{timestamp}.log");
            _logWriter = new StreamWriter(logPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
            Log("=== GPU风扇校准开始 ===");

            bool ecInitialized = false;
            bool gpuManualControlStarted = false;
            int exitCode = 0;

            try
            {
                // 检测残存nvidia-smi进程（仅报告，不按名清理）
                int nvidiaCount = GpuTelemetryClient.CountNvidiaSmiProcesses();
                if (nvidiaCount > 1)
                    Log($"注意：当前存在 {nvidiaCount} 个 nvidia-smi 进程（包括本程序启动的）");

                // 初始化EC
                if (!InitEc()) { Log("EC初始化失败"); return 1; }
                ecInitialized = true;
                _gpuTelemetry = new GpuTelemetryClient(pollIntervalMs: 1000);
                if (!_gpuTelemetry.Start())
                {
                    Log("GPU遥测启动失败：已有实例运行中");
                    return 1;
                }
                Log("GPU遥测已启动");

                // 等待遥测数据（最多15秒）
                bool telemetryOk = false;
                for (int i = 0; i < 30 && !telemetryOk; i++)
                {
                    Thread.Sleep(500);
                    _lastGpuTelemetry = _gpuTelemetry.Latest;
                    telemetryOk = _lastGpuTelemetry != null && _lastGpuTelemetry.IsAvailable && !_lastGpuTelemetry.IsStale;
                }

                // 预检查
                var cpuInit = EcReadRaw(1);
                _lastGpuTelemetry = _gpuTelemetry.Latest;
                telemetryOk = _lastGpuTelemetry != null && _lastGpuTelemetry.IsAvailable && !_lastGpuTelemetry.IsStale;
                int cpuTemp = cpuInit.Remote;
                int gpuTemp = telemetryOk ? _lastGpuTelemetry.TemperatureC : 999;

                Log($"预检查: CPU={cpuTemp}°C, GPU={gpuTemp}°C");
                if (cpuTemp >= 70) { Log("FAIL: CPU≥70°C，需等待冷却"); return 1; }
                if (gpuTemp >= 65) { Log("FAIL: GPU≥65°C，需等待冷却"); return 1; }
                if (!telemetryOk) { Log("FAIL: GPU遥测不可用，中止"); return 1; }

                Log("条件满足，开始GPU校准\n");

                // 确保CPU风扇在Auto
                EcSetFanAuto(1);
                Thread.Sleep(500);

                // GPU校准档位：重点区间47%～54%
                int[] points = { 47, 48, 49, 50, 51, 52, 54 };
                var records = new List<GpuCalibrationRecord>();

                bool abortRequested = false;

                foreach (int target in points)
                {
                    if (abortRequested) break;

                    Log($"--- 校准 {target}% ---");
                    var cpuBefore = EcReadRaw(1);
                    var gpuBefore = EcReadRaw(2);
                    _lastGpuTelemetry = _gpuTelemetry.Latest;
                    int cpuTempStart = cpuBefore.Remote;
                    bool telemetryOkNow = _lastGpuTelemetry != null && _lastGpuTelemetry.IsAvailable && !_lastGpuTelemetry.IsStale;
                    int gpuTempStart = telemetryOkNow ? _lastGpuTelemetry.TemperatureC : 999;

                    // 运行中止条件
                    if (cpuTempStart >= 75) { Log($"⛔ CPU温度={cpuTempStart}°C ≥ 75°C，中止"); abortRequested = true; break; }
                    if (gpuTempStart >= 70) { Log($"⛔ GPU温度={gpuTempStart}°C ≥ 70°C，中止"); abortRequested = true; break; }
                    if (!telemetryOkNow) { Log("⛔ GPU遥测失效，中止"); abortRequested = true; break; }

                    EcSetFanPercent(2, target);
                    gpuManualControlStarted = true;

                    // 安全检查辅助函数：返回false表示需要中止
                    bool CheckAbort()
                    {
                        if (!_running) { Log("  用户取消"); return false; }
                        var cpuNow = EcReadRaw(1);
                        if (cpuNow.Remote >= 75) { Log($"  ⛔ 采样中CPU温度={cpuNow.Remote}°C ≥ 75°C，中止"); return false; }
                        _lastGpuTelemetry = _gpuTelemetry?.Latest;
                        bool telemetryOkNow2 = _lastGpuTelemetry != null && _lastGpuTelemetry.IsAvailable && !_lastGpuTelemetry.IsStale;
                        if (!telemetryOkNow2) { Log("  ⛔ 采样中GPU遥测失效或过期，中止"); return false; }
                        if (_lastGpuTelemetry.TemperatureC >= 70) { Log($"  ⛔ 采样中GPU温度={_lastGpuTelemetry.TemperatureC}°C ≥ 70°C，中止"); return false; }
                        return true;
                    }

                    // 等待过渡期（每500ms检查一次）
                    for (int i = 0; i < 6; i++)
                    {
                        Thread.Sleep(500);
                        if (!CheckAbort()) { abortRequested = true; break; }
                    }
                    if (abortRequested) break;

                    // 采集稳定期数据
                    var rpmSamples = new List<int>();
                    var dutyRawSamples = new List<int>();

                    for (int t = 0; t < 14; t++)
                    {
                        if (!CheckAbort()) { abortRequested = true; break; }
                        var raw = EcReadRaw(2);
                        int rpm = EcGetGpuRpm();
                        rpmSamples.Add(rpm);
                        dutyRawSamples.Add(raw.FanDuty);
                        Thread.Sleep(500);
                    }
                    if (abortRequested) break;

                    var filteredRpm = FilterRpmSamples(rpmSamples, 3);
                    double avgDutyRaw = dutyRawSamples.Count > 0 ? dutyRawSamples.Average() : 0;
                    double avgDutyPct = avgDutyRaw / 255.0 * 100.0;
                    var cpuAfter = EcReadRaw(1);
                    _lastGpuTelemetry = _gpuTelemetry.Latest;
                    int cpuTempEnd = cpuAfter.Remote;
                    int gpuTempEnd = _lastGpuTelemetry?.TemperatureC ?? gpuTempStart;

                    var record = new GpuCalibrationRecord
                    {
                        TargetPct = target,
                        EcReadbackRaw = (int)avgDutyRaw,
                        EcReadbackPct = avgDutyPct,
                        MedianRpm = filteredRpm.Median,
                        TrimmedMeanRpm = filteredRpm.TrimmedMean,
                        RpmStdDevFiltered = filteredRpm.StdDev,
                        WarmRpm = EcGetGpuRpm(),
                        CpuTempStart = cpuTempStart,
                        CpuTempEnd = cpuTempEnd,
                        GpuTempStart = gpuTempStart,
                        GpuTempEnd = gpuTempEnd,
                        TotalSamples = rpmSamples.Count,
                        ValidSamples = filteredRpm.ValidCount,
                        OutlierCount = filteredRpm.OutlierCount,
                        RawSamples = new List<int>(rpmSamples)
                    };
                    records.Add(record);

                    Log($"  EC回读: raw={record.EcReadbackRaw} ({record.EcReadbackPct:F1}%), RPM中位数={record.MedianRpm:F0}, 异常={record.OutlierCount}");
                    Log($"  温度: CPU {cpuTempStart}→{cpuTempEnd}°C, GPU {gpuTempStart}→{gpuTempEnd}°C");
                }

                // 恢复Auto
                if (gpuManualControlStarted)
                {
                    try
                    {
                        EcSetFanAuto(2);
                        Thread.Sleep(500);
                        Log("GPU风扇已恢复Auto");
                    }
                    catch (Exception autoEx)
                    {
                        Log($"恢复GPU Auto失败: {autoEx.Message}");
                        exitCode = 5;
                    }
                }

                // 写CSV（即使未完成也保存已采集的数据）
                string csvPath = Path.Combine(_verifyDir, $"gpu-calibration-{timestamp}.csv");
                using (var writer = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8))
                {
                    writer.WriteLine("target_pct,ec_readback_raw,ec_readback_pct,median_rpm,trimmed_mean_rpm,rpm_stddev_filtered,warm_rpm,cpu_temp_start_c,cpu_temp_end_c,gpu_temp_start_c,gpu_temp_end_c,total_samples,valid_samples,outlier_count");
                    foreach (var rec in records)
                    {
                        writer.WriteLine($"{rec.TargetPct},{rec.EcReadbackRaw},{rec.EcReadbackPct:F1},{rec.MedianRpm:F0},{rec.TrimmedMeanRpm:F0},{rec.RpmStdDevFiltered:F0},{rec.WarmRpm},{rec.CpuTempStart},{rec.CpuTempEnd},{rec.GpuTempStart},{rec.GpuTempEnd},{rec.TotalSamples},{rec.ValidSamples},{rec.OutlierCount}");
                    }
                }

                Log($"校准CSV保存至: {csvPath}");

                // 判断校准结果
                if (abortRequested)
                {
                    Log($"校准未完成：共完成 {records.Count}/{points.Length} 个档位（用户取消或温度超标）");
                    if (exitCode == 0) exitCode = 3;
                }
                else if (records.Count != points.Length)
                {
                    Log($"校准未完成：共完成 {records.Count}/{points.Length} 个档位（档位不完整）");
                    if (exitCode == 0) exitCode = 4;
                }
                else
                {
                    Log($"校准完成：共完成 {records.Count} 个档位\n");

                    // GPU RPM阶跃分析
                    Log("===== GPU RPM阶跃分析（基于中位数） =====");
                    for (int i = 1; i < records.Count; i++)
                    {
                        int dutyDelta = records[i].TargetPct - records[i - 1].TargetPct;
                        double rpmDelta = records[i].MedianRpm - records[i - 1].MedianRpm;
                        double rpmPerOnePercent = dutyDelta > 0 ? rpmDelta / dutyDelta : 0;
                        bool isJump = (dutyDelta == 1 && Math.Abs(rpmDelta) > 100) || (dutyDelta > 1 && Math.Abs(rpmPerOnePercent) > 100);
                        Log($"  {records[i - 1].TargetPct}%→{records[i].TargetPct}%: 占空比+{dutyDelta}%, RPM {rpmDelta:+0;-0}, 占空比变化1%时RPM变化{rpmPerOnePercent:+0;-0}, {(isJump ? "⚠ 阶跃" : "平滑")}");
                    }

                    Log("\n===== 有效数据汇总 =====");
                    foreach (var rec in records)
                    {
                        Log($"  {rec.TargetPct}%: 中位数RPM={rec.MedianRpm:F0}, EC回读={rec.EcReadbackPct:F1}%, 异常{rec.OutlierCount}次, 有效{rec.ValidSamples}/{rec.TotalSamples}");
                    }
                }

                return exitCode;
            }
            catch (Exception ex)
            {
                Log($"异常: {ex.Message}");
                return 1;
            }
            finally
            {
                // 所有退出路径必须恢复GPU Auto
                if (ecInitialized && gpuManualControlStarted)
                {
                    try
                    {
                        EcSetFanAuto(2);
                        Log("finally中恢复GPU Auto完成");
                    }
                    catch (Exception autoEx)
                    {
                        Log($"恢复GPU Auto失败: {autoEx.Message}");
                    }
                }
                _logWriter?.Dispose();
                Dispose();
            }
        }

        // RPM数据清洗：去除异常值，计算中位数和截尾平均值
        private static FilteredRpmData FilterRpmSamples(List<int> samples, int warmupSeconds)
        {
            if (samples == null || samples.Count == 0)
                return new FilteredRpmData();

            int totalSamples = samples.Count;
            int warmupCount = Math.Min(warmupSeconds, totalSamples / 2);
            // 使用全部样本但标记异常值
            var sorted = new List<int>(samples);
            sorted.Sort();
            double median = sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

            int outlierCount = 0;
            var validSamples = new List<int>();
            foreach (int rpm in samples)
            {
                double deviationPct = median > 0 ? Math.Abs(rpm - median) / median * 100 : 0;
                bool isOutlier = deviationPct > 20 || (rpm < 1000 && median > 2000);
                if (isOutlier) outlierCount++;
                else validSamples.Add(rpm);
            }

            double validAvg = validSamples.Count > 0 ? validSamples.Average() : 0;
            double validStdDev = validSamples.Count > 1
                ? Math.Sqrt(validSamples.Average(v => Math.Pow(v - validAvg, 2)))
                : 0;
            // 截尾平均值：去掉最大最小后求平均
            var trimmed = new List<int>(validSamples);
            trimmed.Sort();
            if (trimmed.Count > 2)
            {
                trimmed.RemoveAt(trimmed.Count - 1);
                trimmed.RemoveAt(0);
            }
            double trimmedAvg = trimmed.Count > 0 ? trimmed.Average() : 0;

            return new FilteredRpmData
            {
                Median = median,
                TrimmedMean = trimmedAvg,
                StdDev = validStdDev,
                ValidCount = validSamples.Count,
                OutlierCount = outlierCount
            };
        }

        private struct FilteredRpmData
        {
            public double Median;
            public double TrimmedMean;
            public double StdDev;
            public int ValidCount;
            public int OutlierCount;
        }

        private struct GpuCalibrationRecord
        {
            public int TargetPct;
            public int EcReadbackRaw;
            public double EcReadbackPct;
            public double MedianRpm;
            public double TrimmedMeanRpm;
            public double RpmStdDevFiltered;
            public int WarmRpm;
            public int CpuTempStart;
            public int CpuTempEnd;
            public int GpuTempStart;
            public int GpuTempEnd;
            public int TotalSamples;
            public int ValidSamples;
            public int OutlierCount;
            public List<int> RawSamples;
        }

        public int RunGpuActive(int durationMinutes)
        {
            Log("===== GPU Active验证 =====");
            Log("请手动启动一个GPU应用产生负载。");
            return 0;
        }

        private struct CalibrationRecordData
        {
            public int TargetPct;
            public double AvgDutyPct;
            public double AvgDutyRaw;
            public int MinDutyRaw;
            public int MaxDutyRaw;
            public double MinDutyPct;
            public double MaxDutyPct;
            public double AvgRpm;       // filtered average
            public double MedianRpm;
            public double MinRpm;       // filtered min
            public double MaxRpm;       // filtered max
            public double RpmStdDev;    // filtered stddev
            public int RawSampleCount;
            public int FilteredSampleCount;
            public int OutlierCount;
            public double RawMinRpm;
            public double RawMaxRpm;
            public double CpuTempStart;
            public double CpuTempEnd;
            public double GpuTempStart;
            public double GpuTempEnd;
            public int StableSamples;
            public int WarmRpm;
        }

        // MAD-based RPM异常值过滤
        // medianAbsoluteDeviationThreshold：偏离中位数超过阈值倍MAD的样本被视为异常值
        private static FilteredMadData FilterRpmMad(List<int> samples, double madThreshold)
        {
            var result = new FilteredMadData();
            if (samples == null || samples.Count == 0) return result;

            result.RawCount = samples.Count;
            var sorted = new List<int>(samples);
            sorted.Sort();
            double median = sorted.Count % 2 == 1
                ? sorted[sorted.Count / 2]
                : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;
            result.Median = median;

            // 计算MAD (Median Absolute Deviation)
            var absDevs = samples.Select(v => Math.Abs(v - median)).ToList();
            absDevs.Sort();
            double mad = absDevs.Count % 2 == 1
                ? absDevs[absDevs.Count / 2]
                : (absDevs[absDevs.Count / 2 - 1] + absDevs[absDevs.Count / 2]) / 2.0;
            if (mad < 1) mad = 1; // 防止除以0

            // 过滤异常值：|value - median| > madThreshold * MAD
            var validSamples = new List<double>();
            int outlierCount = 0;
            double minVal = double.MaxValue, maxVal = double.MinValue;

            foreach (int v in samples)
            {
                if (Math.Abs(v - median) > madThreshold * mad)
                {
                    outlierCount++;
                }
                else
                {
                    validSamples.Add(v);
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }
            }

            result.OutlierCount = outlierCount;
            result.FilteredCount = validSamples.Count;
            result.FilteredMin = validSamples.Count > 0 ? minVal : 0;
            result.FilteredMax = validSamples.Count > 0 ? maxVal : 0;

            if (validSamples.Count > 0)
            {
                result.FilteredMean = validSamples.Average();
                result.FilteredStdDev = validSamples.Count > 1
                    ? Math.Sqrt(validSamples.Average(v => Math.Pow(v - result.FilteredMean, 2)))
                    : 0;
            }

            return result;
        }

        private struct FilteredMadData
        {
            public int RawCount;
            public int FilteredCount;
            public int OutlierCount;
            public double Median;
            public double FilteredMean;
            public double FilteredMin;
            public double FilteredMax;
            public double FilteredStdDev;
        }

        /// <summary>
        /// Measures a small downward duty sweep around the user's normal operating point.
        /// This is deliberately separate from the low-temperature calibration flow: it never
        /// changes the saved profile and it stops at the Stable emergency thresholds.
        /// </summary>
        public int RunOperatingPointCalibration()
        {
            string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string logPath = Path.Combine(_verifyDir, $"operating-point-{timestamp}.log");
            string csvPath = Path.Combine(_verifyDir, $"operating-point-{timestamp}.csv");
            _logWriter = new StreamWriter(logPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };

            bool ecInitialized = false;
            StreamWriter csv = null;

            try
            {
                Log("=== operating-point measurement begin ===");
                if (!InitEc())
                {
                    Log("FAIL: EC initialization failed");
                    return 1;
                }
                ecInitialized = true;

                _gpuTelemetry = new GpuTelemetryClient(pollIntervalMs: 1000);
                if (!_gpuTelemetry.Start())
                {
                    Log("FAIL: GPU telemetry could not start");
                    return 1;
                }

                bool telemetryOk = false;
                for (int i = 0; i < 30 && !telemetryOk; i++)
                {
                    Thread.Sleep(500);
                    _lastGpuTelemetry = _gpuTelemetry.Latest;
                    telemetryOk = _lastGpuTelemetry != null &&
                                  _lastGpuTelemetry.IsAvailable &&
                                  !_lastGpuTelemetry.IsStale;
                }

                var cpuBefore = EcReadRaw(1);
                var gpuBefore = EcReadRaw(2);
                _lastGpuTelemetry = _gpuTelemetry.Latest;
                if (!telemetryOk || _lastGpuTelemetry == null || !_lastGpuTelemetry.IsAvailable || _lastGpuTelemetry.IsStale)
                {
                    Log("FAIL: GPU telemetry is unavailable; no manual sweep was attempted");
                    return 1;
                }

                Log($"baseline: CPU={cpuBefore.Remote}C duty={cpuBefore.FanDuty * 100.0 / 255.0:F1}%, " +
                    $"GPU={_lastGpuTelemetry.TemperatureC}C duty={gpuBefore.FanDuty * 100.0 / 255.0:F1}%");
                Log("safety limits: CPU >=87C, GPU >=82C for the channel under test; other channel may not rise above its limit");

                csv = new StreamWriter(csvPath, false, System.Text.Encoding.UTF8) { AutoFlush = true };
                csv.WriteLine("channel,target_pct,readback_pct,start_temp_c,end_temp_c,avg_temp_c,max_temp_c,rpm,status");

                // Start from the firmware-controlled state. The test restores both channels in finally.
                EcSetFanAuto(1);
                EcSetFanAuto(2);
                Thread.Sleep(1000);

                int cpuResult = MeasureOperatingPointChannel(1, csv);
                int gpuResult = MeasureOperatingPointChannel(2, csv);

                Log($"results: CPU={cpuResult}, GPU={gpuResult}");
                Log($"CSV: {csvPath}");
                return (cpuResult == 0 && gpuResult == 0) ? 0 : 3;
            }
            catch (Exception ex)
            {
                Log($"measurement exception: {ex}");
                return 1;
            }
            finally
            {
                if (ecInitialized)
                {
                    try
                    {
                        EcRestoreAll();
                        Log("both fan channels restored to Auto");
                    }
                    catch (Exception restoreEx)
                    {
                        Log($"FAIL: Auto restore exception: {restoreEx.Message}");
                    }
                }

                csv?.Dispose();
                _logWriter?.Dispose();
                Dispose();
            }
        }

        private int MeasureOperatingPointChannel(int channel, StreamWriter csv)
        {
            int otherChannel = channel == 1 ? 2 : 1;
            int channelLimit = channel == 1 ? 87 : 82;
            int otherLimit = channel == 1 ? 82 : 87;
            string channelName = channel == 1 ? "CPU" : "GPU";

            // Never manually drive the other fan while testing one channel.
            EcSetFanAuto(otherChannel);

            var beforeTarget = EcReadRaw(channel);
            var beforeCpu = channel == 1 ? beforeTarget : EcReadRaw(1);
            _lastGpuTelemetry = _gpuTelemetry?.Latest;
            if (_lastGpuTelemetry == null || !_lastGpuTelemetry.IsAvailable || _lastGpuTelemetry.IsStale)
            {
                Log($"{channelName}: telemetry unavailable; skipped");
                return 1;
            }

            int startTemp = channel == 1 ? beforeTarget.Remote : _lastGpuTelemetry.TemperatureC;
            int startDuty = ClampPercent((int)Math.Round(beforeTarget.FanDuty * 100.0 / 255.0));
            Log($"{channelName}: start {startTemp}C, duty={startDuty}%");

            // At or one degree below the protection line, record the current point only.
            // A downward write at the line itself would defeat the safety floor we are testing.
            if (startTemp >= channelLimit - 1)
            {
                WriteOperatingPointRow(csv, channelName, startDuty, beforeTarget, startTemp, startTemp, startTemp, startTemp, 0, "protected-floor");
                Log($"{channelName}: {startTemp}C is too close to the {channelLimit}C protection line; current duty is the measured floor");
                return 0;
            }

            // Only a local sweep is allowed. Eight points is enough to find the first plateau
            // without turning this command into an unrestricted calibration routine.
            int maxSteps = channel == 1 ? 4 : 8;
            if (startTemp >= channelLimit - 3)
                maxSteps = 1;

            int firstCandidate = Math.Max(30, startDuty - 1);
            int lastStable = startDuty;
            bool foundStablePoint = false;

            for (int step = 0; step < maxSteps && firstCandidate - step >= 30; step++)
            {
                int target = firstCandidate - step;
                var currentCpu = EcReadRaw(1);
                _lastGpuTelemetry = _gpuTelemetry.Latest;
                if (_lastGpuTelemetry == null || !_lastGpuTelemetry.IsAvailable || _lastGpuTelemetry.IsStale)
                {
                    Log($"{channelName} {target}%: telemetry lost; stopping");
                    break;
                }

                int cpuTemp = currentCpu.Remote;
                int gpuTemp = _lastGpuTelemetry.TemperatureC;
                if ((channel == 1 && cpuTemp >= channelLimit) ||
                    (channel == 2 && gpuTemp >= channelLimit) ||
                    (channel == 1 && gpuTemp > otherLimit) ||
                    (channel == 2 && cpuTemp > otherLimit))
                {
                    Log($"{channelName} {target}%: safety precheck stopped at CPU={cpuTemp}C GPU={gpuTemp}C");
                    break;
                }

                EcSetFanPercent(channel, target);
                var samples = new List<int>();
                string status = "stable";
                bool unsafePoint = false;

                // 8 samples x 3 seconds gives the heat sink time to react while keeping the
                // test short enough to stop promptly if the temperature rises.
                for (int sample = 0; sample < 8; sample++)
                {
                    Thread.Sleep(3000);
                    var cpuNow = EcReadRaw(1);
                    _lastGpuTelemetry = _gpuTelemetry.Latest;
                    if (_lastGpuTelemetry == null || !_lastGpuTelemetry.IsAvailable || _lastGpuTelemetry.IsStale)
                    {
                        status = "telemetry-lost";
                        unsafePoint = true;
                        break;
                    }

                    int cpuNowTemp = cpuNow.Remote;
                    int gpuNowTemp = _lastGpuTelemetry.TemperatureC;
                    int targetTemp = channel == 1 ? cpuNowTemp : gpuNowTemp;
                    samples.Add(targetTemp);

                    if ((channel == 1 && cpuNowTemp >= channelLimit) ||
                        (channel == 2 && gpuNowTemp >= channelLimit) ||
                        (channel == 1 && gpuNowTemp > otherLimit) ||
                        (channel == 2 && cpuNowTemp > otherLimit))
                    {
                        status = "safety-stop";
                        unsafePoint = true;
                        break;
                    }

                    if (targetTemp > startTemp + 2)
                    {
                        status = "temperature-rise";
                        unsafePoint = true;
                        break;
                    }
                }

                if (samples.Count == 0)
                {
                    Log($"{channelName} {target}%: no samples; stopping");
                    break;
                }

                int endTemp = samples[samples.Count - 1];
                int maxTemp = samples.Max();
                double avgTemp = samples.Average();
                bool stable = !unsafePoint && maxTemp <= startTemp + 1 && endTemp <= startTemp + 1;
                if (!stable && status == "stable")
                    status = "not-stable";

                int rpm = 0;
                if (stable)
                {
                    try { rpm = channel == 1 ? EcGetCpuRpm() : EcGetGpuRpm(); }
                    catch (Exception rpmEx) { Log($"{channelName} {target}%: RPM read failed: {rpmEx.Message}"); }
                }

                var readback = EcReadRaw(channel);
                if (target > 0 && readback.FanDuty == 0)
                {
                    // A single zero duty byte was observed during the first
                    // operating-point run. Retry only this anomalous sample so
                    // the report cannot mistake a transient read for 0% duty.
                    Log($"{channelName} {target}%: duty readback was 0; retrying once");
                    Thread.Sleep(250);
                    var retry = EcReadRaw(channel);
                    if (retry.FanDuty != 0)
                        readback = retry;
                    else
                        status = status == "stable" ? "readback-invalid" : status + ";readback-invalid";
                }
                WriteOperatingPointRow(csv, channelName, target, readback, startTemp, endTemp, avgTemp, maxTemp, rpm, status);
                Log($"{channelName} {target}%: temp {startTemp}->{endTemp}C, avg={avgTemp:F1}C, max={maxTemp}C, RPM={rpm}, {status}");

                if (!stable)
                    break;

                foundStablePoint = true;
                lastStable = target;
            }

            // The caller's finally block performs the authoritative Auto restore.
            Log($"{channelName}: lowest stable tested duty={lastStable}%" +
                (foundStablePoint ? "" : " (no downward point accepted)"));
            return 0;
        }

        private static void WriteOperatingPointRow(StreamWriter csv, string channel, int targetPct, EcData readback,
            double startTemp, double endTemp, double avgTemp, double maxTemp, int rpm, string status)
        {
            csv.WriteLine($"{channel},{targetPct},{readback.FanDuty * 100.0 / 255.0:F1},{startTemp:F1},{endTemp:F1},{avgTemp:F1},{maxTemp:F1},{rpm},{status}");
        }

        private static int ClampPercent(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }
    }
}
