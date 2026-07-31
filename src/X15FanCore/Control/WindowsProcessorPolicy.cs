using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace X15FanCore.Control
{
    /// <summary>
    /// Safe, reversible fallback for CPU frequency-like control. Windows exposes
    /// a maximum processor performance state rather than a fixed MHz value.
    /// This is deliberately limited to the AC setting used by the laptop while
    /// plugged in; it does not touch BIOS or undocumented ACPI registers.
    /// </summary>
    public sealed class WindowsProcessorPolicy
    {
        private const string ProcessorSubgroup = "SUB_PROCESSOR";
        private const string MaximumProcessorState = "PROCTHROTTLEMAX";

        public WindowsProcessorPolicySnapshot Capture()
        {
            string schemeOutput = RunPowerCfg("/getactivescheme", out string schemeError);
            Match schemeMatch = Regex.Match(schemeOutput ?? string.Empty, "([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
            if (!schemeMatch.Success)
                throw new InvalidOperationException("无法读取当前 Windows 电源方案：" + FirstNonEmpty(schemeError, schemeOutput));

            string schemeGuid = schemeMatch.Groups[1].Value;
            string query = RunPowerCfg("/query " + schemeGuid + " " + ProcessorSubgroup + " " + MaximumProcessorState, out string queryError);
            Match acMatch = Regex.Match(
                query ?? string.Empty,
                "当前交流电源设置索引:\\s*0x([0-9a-fA-F]+)|Current AC Power Setting Index:\\s*0x([0-9a-fA-F]+)",
                RegexOptions.IgnoreCase);
            if (!acMatch.Success)
                throw new InvalidOperationException("无法读取 CPU 最大性能状态：" + FirstNonEmpty(queryError, query));

            string hex = acMatch.Groups[1].Success ? acMatch.Groups[1].Value : acMatch.Groups[2].Value;
            return new WindowsProcessorPolicySnapshot
            {
                SchemeGuid = schemeGuid,
                OriginalAcMaximumPercent = Convert.ToInt32(hex, 16)
            };
        }

        public bool ApplyAcMaximumPercent(WindowsProcessorPolicySnapshot snapshot, int percent, out string error)
        {
            error = null;
            if (snapshot == null || string.IsNullOrEmpty(snapshot.SchemeGuid))
            {
                error = "缺少可恢复的 Windows 电源方案快照。";
                return false;
            }
            if (percent < 5 || percent > 100)
            {
                error = "CPU 最大性能状态必须在 5% 到 100% 之间。";
                return false;
            }

            string arguments = "/setacvalueindex " + snapshot.SchemeGuid + " " + ProcessorSubgroup + " " + MaximumProcessorState + " " + percent.ToString(CultureInfo.InvariantCulture);
            RunPowerCfg(arguments, out error);
            if (!string.IsNullOrEmpty(error))
                return false;

            RunPowerCfg("/S " + snapshot.SchemeGuid, out error);
            if (!string.IsNullOrEmpty(error))
            {
                Restore(snapshot, out string ignored);
                return false;
            }

            return true;
        }

        public bool Restore(WindowsProcessorPolicySnapshot snapshot, out string error)
        {
            error = null;
            if (snapshot == null || string.IsNullOrEmpty(snapshot.SchemeGuid))
            {
                error = "缺少可恢复的 Windows 电源方案快照。";
                return false;
            }

            string arguments = "/setacvalueindex " + snapshot.SchemeGuid + " " + ProcessorSubgroup + " " + MaximumProcessorState + " " + snapshot.OriginalAcMaximumPercent.ToString(CultureInfo.InvariantCulture);
            RunPowerCfg(arguments, out error);
            if (!string.IsNullOrEmpty(error))
                return false;

            RunPowerCfg("/S " + snapshot.SchemeGuid, out error);
            return string.IsNullOrEmpty(error);
        }

        private static string RunPowerCfg(string arguments, out string error)
        {
            error = null;
            try
            {
                using (Process process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powercfg.exe",
                        Arguments = arguments,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                })
                {
                    if (!process.Start())
                    {
                        error = "powercfg.exe 启动失败。";
                        return string.Empty;
                    }

                    // Drain both redirected streams asynchronously before waiting
                    // so a stuck powercfg process cannot block the UI indefinitely
                    // on a full stdout/stderr pipe.
                    System.Threading.Tasks.Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> errorTask = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000))
                    {
                        try { process.Kill(); } catch { }
                        try { process.WaitForExit(1000); } catch { }
                        error = "powercfg.exe 超时。";
                        return string.Empty;
                    }

                    string output = outputTask.GetAwaiter().GetResult();
                    string stderr = errorTask.GetAwaiter().GetResult();

                    if (process.ExitCode != 0)
                        error = FirstNonEmpty(stderr, output, "powercfg.exe 返回码=" + process.ExitCode);
                    return output;
                }
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return string.Empty;
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return "未知错误";
        }
    }

    public sealed class WindowsProcessorPolicySnapshot
    {
        public string SchemeGuid { get; set; }
        public int OriginalAcMaximumPercent { get; set; }
    }
}
