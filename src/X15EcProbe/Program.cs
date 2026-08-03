using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using X15FanCore.Native;
using X15FanCore.Probe;

namespace X15EcProbe
{
    /// <summary>
    /// Independent READ-ONLY EC sampler for the Control Center mechanism audit.
    ///
    /// Whitelist calls only (all read-only):
    ///   - constructor (loads ClevoEcInfo.dll + InitIo)
    ///   - GetFanCount()
    ///   - ReadRaw(channel)  -> EcData { Remote temp, Local temp, FanDuty }
    ///   - GetCpuRpm()
    ///   - GetGpuRpm()
    ///   - Dispose()
    ///
    /// This tool never modifies the EC: it only calls the read-only members
    /// listed above. It does not perform any duty writes, DCHU writes,
    /// app-settings writes, apply/restore operations, channel control,
    /// lease acquisition, watchdog, or Active-mode logic. The linked
    /// ClevoEcInfo.cs class contains additional write-capable members that
    /// this tool deliberately never calls (verified by static scan).
    ///
    /// E0 采集合同（2026-08-03）：
    ///   --abort-cpu-temp &lt;T&gt;：每样本读取完成后先写入并 flush 当前样本；
    ///     若 CPU 温度 &gt;= T 立即退出（exit 3，输出 ABORT_CPU_TEMP），不得继续
    ///     sleep 或读取第二个样本；默认不传保持旧行为。E0 必须使用 70。
    ///   CSV 增加 cpu_rpm_plausible/gpu_rpm_plausible 列（RPM 合理性诊断，仅
    ///     E0 数据质量检查，不进入生产风扇算法；原始 RPM 值不被修改）。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            int seconds = 10;
            string dllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ClevoEcInfo.dll");
            string outFile = null;
            bool continuous = false;
            string statusFile = null;
            int abortCpuTemp = 0;

            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--seconds", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds);
                else if (string.Equals(args[i], "--dll", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    dllPath = args[i + 1];
                else if (string.Equals(args[i], "--out", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    outFile = args[i + 1];
                else if (string.Equals(args[i], "--continuous", StringComparison.OrdinalIgnoreCase))
                    continuous = true;
                else if (string.Equals(args[i], "--status-file", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    statusFile = args[i + 1];
                else if (string.Equals(args[i], "--abort-cpu-temp", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out abortCpuTemp);
                else if (string.Equals(args[i], "--help", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("X15EcProbe --seconds <n> --dll <path> --out <csv> [--continuous] [--status-file <path>] [--abort-cpu-temp <C>]");
                    Console.WriteLine("Read-only EC sampler: temp/RPM/duty. Never writes EC.");
                    Console.WriteLine("--continuous streams one line/second until killed (for load-test supervisor).");
                    Console.WriteLine("--status-file overwrites a timestamped status file every second (supervisor reads the last record; implies continuous).");
                    Console.WriteLine("--abort-cpu-temp <C>: abort in-sample when CPU temp >= C (exit 3, ABORT_CPU_TEMP). E0 uses 70.");
                    return 0;
                }
            }

            if (seconds <= 0) seconds = 10;
            if (statusFile != null)
                continuous = true;   // 状态文件模式隐含持续采集

            try
            {
                if (!File.Exists(dllPath))
                {
                    Console.WriteLine("ERROR dll_not_found path=" + dllPath);
                    return 2;
                }

                using (ClevoEcInfo ec = new ClevoEcInfo(dllPath))
                {
                    int fanCount = ec.GetFanCount();
                    Console.WriteLine("INIT_OK fan_count=" + fanCount);

                    // 持续采集模式（供 load-test 监督器使用）：每秒输出一行到
                    // stdout，直到被监督器终止（supervisor 与 worker 分离的
                    // 前提是 EC 读取本身由独立持续进程承担，不在主采样循环
                    // 内反复启动外部程序）。Ctrl+C / 父进程 kill 即退出。
                    // --status-file 模式：同时把带时间戳的单行状态覆盖写入
                    // 状态文件（监督器每秒读取最后一条完整记录，按时间戳
                    // 新鲜度判定 stale），避免监督器对永久运行进程做
                    // EndOfStream 阻塞式读取。
                    if (continuous)
                    {
                        Console.WriteLine("CONTINUOUS_READY");
                        int tick = 0;
                        while (true)
                        {
                            tick++;
                            DateTime t0 = DateTime.UtcNow;
                            string status = "OK";
                            int cpuTemp = -1, gpuTemp = -1, cpuRpm = -1, gpuRpm = -1, cpuDuty = -1, gpuDuty = -1;
                            try
                            {
                                EcData cpu = ec.ReadRaw(1);
                                EcData gpu = ec.ReadRaw(2);
                                cpuTemp = cpu.Remote;
                                gpuTemp = gpu.Remote;
                                cpuRpm = ec.GetCpuRpm();
                                gpuRpm = ec.GetGpuRpm();
                                cpuDuty = (int)Math.Round(cpu.FanDuty * 100.0 / 255.0);
                                gpuDuty = (int)Math.Round(gpu.FanDuty * 100.0 / 255.0);
                            }
                            catch (Exception ex)
                            {
                                status = "ERROR " + ex.GetType().Name;
                                Console.WriteLine("READ_ERROR " + ex.GetType().Name + " " + ex.Message);
                            }
                            string line = "ts=" + t0.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") +
                                " t=" + tick + "s cpuTemp=" + cpuTemp + " gpuTemp=" + gpuTemp +
                                " cpuRpm=" + cpuRpm + " gpuRpm=" + gpuRpm +
                                " cpuDuty=" + cpuDuty + " gpuDuty=" + gpuDuty + " status=" + status;
                            Console.WriteLine(line);
                            Console.Out.Flush();
                            if (statusFile != null)
                            {
                                // 覆盖写入：状态文件始终存在（不删除-改名，避免
                                // 监督器读到"文件缺失"窗口）；监督器每秒读取最后
                                // 一条完整记录并按时间戳新鲜度判定 stale。
                                string temp = statusFile + ".tmp";
                                try
                                {
                                    File.WriteAllText(temp, line + "\n", new UTF8Encoding(false));
                                    File.Copy(temp, statusFile, true);
                                    try { File.Delete(temp); } catch { }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine("STATUS_WRITE_ERROR " + ex.GetType().Name + " " + ex.Message);
                                }
                            }
                            int remain = 1000 - (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                            if (remain > 0) System.Threading.Thread.Sleep(remain);
                        }
                    }

                    // 采样内中止启用时流式写入（每样本写入后立即 flush），
                    // 保证中止样本不会丢失；未启用时保持旧的缓冲写行为。
                    bool streaming = abortCpuTemp > 0;
                    const string header =
                        "timestamp_utc,cpu_temp_c,gpu_temp_c,cpu_fan_rpm,gpu_fan_rpm,cpu_fan_duty_pct,gpu_fan_duty_pct," +
                        "fan_count,status,cpu_rpm_plausible,gpu_rpm_plausible";
                    StringBuilder csv = new StringBuilder();
                    StreamWriter streamWriter = null;
                    if (streaming)
                    {
                        streamWriter = new StreamWriter(outFile, false, new UTF8Encoding(true));
                        streamWriter.WriteLine(header);
                        streamWriter.Flush();
                    }
                    else
                    {
                        csv.AppendLine(header);
                    }

                    DateTime lastReadUtc = DateTime.MinValue;
                    Func<int, EcProbeSample> readSample = delegate (int index)
                    {
                        // 样本间等待放在读取之前：中止触发后不会执行（RunSamples
                        // 在中止时立即返回，不会进入下一次读取）。
                        if (lastReadUtc != DateTime.MinValue)
                        {
                            int remain = 1000 - (int)(DateTime.UtcNow - lastReadUtc).TotalMilliseconds;
                            if (remain > 0) System.Threading.Thread.Sleep(remain);
                        }
                        DateTime t0 = DateTime.UtcNow;
                        EcProbeSample sample = new EcProbeSample { ReadOk = true };
                        try
                        {
                            EcData cpu = ec.ReadRaw(1);   // X15 AT 23: channel 1 = CPU
                            EcData gpu = ec.ReadRaw(2);   // channel 2 = GPU
                            sample.CpuTemperatureC = cpu.Remote;
                            sample.GpuTemperatureC = gpu.Remote;
                            sample.CpuRpm = ec.GetCpuRpm();
                            sample.GpuRpm = ec.GetGpuRpm();
                            sample.CpuDutyPercent = (int)Math.Round(cpu.FanDuty * 100.0 / 255.0);
                            sample.GpuDutyPercent = (int)Math.Round(gpu.FanDuty * 100.0 / 255.0);
                        }
                        catch (Exception ex)
                        {
                            sample.ReadOk = false;
                            sample.ReadError = ex.GetType().Name + ": " + ex.Message;
                            Console.WriteLine("READ_ERROR " + sample.ReadError);
                        }
                        lastReadUtc = t0;
                        return sample;
                    };

                    Action<int, EcProbeSample> writeSample = delegate (int index, EcProbeSample sample)
                    {
                        string status = sample.ReadOk ? "OK" : "ERROR " + sample.ReadError;
                        RpmPlausibilityResult plaus = sample.ReadOk
                            ? EcProbeContract.EvaluateRpmPlausibility(
                                sample.CpuRpm, sample.GpuRpm, sample.CpuDutyPercent, sample.GpuDutyPercent)
                            : new RpmPlausibilityResult { CpuPlausible = false, GpuPlausible = false, CpuNote = "READ_FAIL", GpuNote = "READ_FAIL" };
                        string cpuPlaus = plaus.CpuPlausible ? "OK" : "IMPLAUSIBLE|" + plaus.CpuNote;
                        string gpuPlaus = plaus.GpuPlausible ? "OK" : "IMPLAUSIBLE|" + plaus.GpuNote;
                        string line = string.Format(CultureInfo.InvariantCulture,
                            "{0},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10}",
                            lastReadUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                            sample.CpuTemperatureC, sample.GpuTemperatureC,
                            sample.CpuRpm, sample.GpuRpm,
                            sample.CpuDutyPercent, sample.GpuDutyPercent,
                            fanCount, status, cpuPlaus, gpuPlaus);
                        if (streaming)
                        {
                            streamWriter.WriteLine(line);
                            streamWriter.Flush();
                        }
                        else
                        {
                            csv.AppendLine(line);
                        }
                        Console.WriteLine("t=" + (index + 1) + "s cpuTemp=" + sample.CpuTemperatureC +
                            " gpuTemp=" + sample.GpuTemperatureC +
                            " cpuRpm=" + sample.CpuRpm + " gpuRpm=" + sample.GpuRpm +
                            " cpuDuty=" + sample.CpuDutyPercent + " gpuDuty=" + sample.GpuDutyPercent +
                            " status=" + status +
                            " cpuRpmPlausible=" + cpuPlaus + " gpuRpmPlausible=" + gpuPlaus);
                    };

                    EcProbeSamplingResult sampling = EcProbeContract.RunSamples(
                        seconds, abortCpuTemp, readSample, writeSample);

                    if (streaming)
                    {
                        streamWriter.Dispose();
                    }
                    else if (!string.IsNullOrEmpty(outFile))
                    {
                        File.WriteAllText(outFile, csv.ToString(), new UTF8Encoding(false));
                        Console.WriteLine("CSV_WRITTEN " + outFile);
                    }

                    if (sampling.AbortedByCpuTemp)
                    {
                        Console.WriteLine("ABORT_CPU_TEMP observed=" + sampling.AbortObservedC +
                            " threshold=" + sampling.AbortThresholdC);
                        Console.WriteLine("ABORTED samples_written=" + sampling.SamplesWritten);
                        return sampling.ExitCode;
                    }
                    Console.WriteLine("DONE samples_written=" + sampling.SamplesWritten);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }
    }
}
