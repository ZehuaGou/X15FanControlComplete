using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using X15FanCore.Control;
using X15FanCore.Native;

namespace X15FanWatchdog
{
    internal static class Program
    {
        private const double HeartbeatTimeoutSeconds = 12;

        private static int Main(string[] args)
        {
            try
            {
                Options options = Options.Parse(args);
                if (options.LeaseOnly)
                    return RunLeaseOnly(options);
                Log(options.LogPath, "看门狗已启动，父进程 PID " + options.ParentProcessId + "。");
                DateTime startUtc = DateTime.UtcNow;
                EventWaitHandle pulseEvent = Heartbeat.OpenExistingPulseEvent(options.HeartbeatPath);
                DateTime lastPulseUtc = DateTime.UtcNow;

                while (true)
                {
                    string heartbeat = ReadHeartbeat(options.HeartbeatPath);
                    if (heartbeat.StartsWith("STOP|", StringComparison.OrdinalIgnoreCase))
                    {
                        Log(options.LogPath, "收到停止心跳；看门狗退出。");
                        return 0;
                    }

                    bool parentAlive = IsProcessAlive(options.ParentProcessId);
                    DateTime heartbeatTime;
                    if (pulseEvent != null)
                    {
                        if (pulseEvent.WaitOne(1000))
                        {
                            lastPulseUtc = DateTime.UtcNow;
                        }
                        heartbeatTime = lastPulseUtc;
                    }
                    else
                    {
                        heartbeatTime = GetHeartbeatTime(options.HeartbeatPath, heartbeat);
                        Thread.Sleep(1000);
                    }
                    bool startupGrace = (DateTime.UtcNow - startUtc).TotalSeconds < 10;
                    // EC verification can legitimately hold the control loop for
                    // a little over five seconds. Keep enough margin to avoid a
                    // false fail-safe while still recovering quickly from a hang.
                    bool stale = heartbeatTime == DateTime.MinValue ||
                        (DateTime.UtcNow - heartbeatTime).TotalSeconds > HeartbeatTimeoutSeconds;

                    if (!startupGrace && (!parentAlive || stale))
                    {
                        string reason = !parentAlive ? "父进程已退出" : "心跳过期";
                        Log(options.LogPath, "故障保护触发：" + reason + "。");
                        RestoreAuto(options.DllPath, options.LogPath);
                        return 2;
                    }

                }
            }
            catch (Exception exception)
            {
                try
                {
                    string logPath = GetArgument(args, "--log") ?? Path.Combine(Path.GetTempPath(), "X15FanWatchdog.log");
                    Log(logPath, "看门狗致命错误：" + exception);
                }
                catch
                {
                }
                return 1;
            }
        }

        private static int RunLeaseOnly(Options options)
        {
            Log(options.LogPath, "Control Center 接管看门狗已启动，父进程 PID " + options.ParentProcessId + "。");
            while (IsProcessAlive(options.ParentProcessId))
                Thread.Sleep(1000);

            string diagnostic;
            bool restored = ControlCenterLease.RestorePersisted(options.LeasePath, out diagnostic);
            Log(options.LogPath, (restored ? "父进程退出，Control Center 已恢复：" : "父进程退出，Control Center 恢复失败：") + diagnostic);
            return restored ? 0 : 2;
        }

        private static void RestoreAuto(string dllPath, string logPath)
        {
            try
            {
                using (ClevoEcInfo ec = new ClevoEcInfo(dllPath))
                {
                    ec.RestoreAllAuto();
                }
                Log(logPath, "两个风扇通道已恢复自动。");
            }
            catch (Exception exception)
            {
                Log(logPath, "恢复自动失败：" + exception);
            }
        }

        private static bool IsProcessAlive(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    return !process.HasExited;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ReadHeartbeat(string path)
        {
            try
            {
                if (!File.Exists(path)) return string.Empty;

                // File.Replace needs delete sharing on the destination. The
                // watchdog only needs a short diagnostic read, so do not
                // block an atomic heartbeat publication while reading it.
                using (FileStream stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    return reader.ReadToEnd();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static DateTime GetHeartbeatTime(string path, string heartbeat)
        {
            string[] fields = (heartbeat ?? string.Empty).Split('|');
            DateTime payloadTime;
            if (fields.Length >= 3 &&
                DateTime.TryParse(fields[2], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out payloadTime))
            {
                return payloadTime.ToUniversalTime();
            }

            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }

        private static void Log(string path, string message)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(path, DateTime.Now.ToString("O") + "  " + message + Environment.NewLine);
        }

        private static string GetArgument(string[] args, string key)
        {
            for (int index = 0; index + 1 < args.Length; index++)
            {
                if (string.Equals(args[index], key, StringComparison.OrdinalIgnoreCase)) return args[index + 1];
            }
            return null;
        }

        private sealed class Options
        {
            public int ParentProcessId { get; private set; }
            public string HeartbeatPath { get; private set; }
            public string DllPath { get; private set; }
            public string LogPath { get; private set; }
            public string LeasePath { get; private set; }
            public bool LeaseOnly { get; private set; }

            public static Options Parse(string[] args)
            {
                string parent = GetArgument(args, "--parent");
                string heartbeat = GetArgument(args, "--heartbeat");
                string dll = GetArgument(args, "--dll");
                string log = GetArgument(args, "--log");
                bool leaseOnly = args.Any(value => string.Equals(value, "--lease-only", StringComparison.OrdinalIgnoreCase));
                string lease = GetArgument(args, "--lease");
                if (string.IsNullOrEmpty(parent) ||
                    (leaseOnly && string.IsNullOrEmpty(lease)) ||
                    (!leaseOnly && (string.IsNullOrEmpty(heartbeat) || string.IsNullOrEmpty(dll))))
                {
                    throw new ArgumentException("必需参数：--parent <pid> --heartbeat <path> --dll <path> [--log <path>]");
                }

                return new Options
                {
                    ParentProcessId = int.Parse(parent),
                    HeartbeatPath = heartbeat,
                    DllPath = dll,
                    LogPath = string.IsNullOrEmpty(log) ? Path.Combine(Path.GetTempPath(), "X15FanWatchdog.log") : log,
                    LeasePath = lease,
                    LeaseOnly = leaseOnly
                };
            }
        }
    }
}
