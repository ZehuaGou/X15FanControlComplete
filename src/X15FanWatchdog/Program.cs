using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
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
                Log(options.LogPath, "看门狗已启动，父进程 PID " + options.ParentProcessId + "。");
                DateTime startUtc = DateTime.UtcNow;

                while (true)
                {
                    string heartbeat = ReadHeartbeat(options.HeartbeatPath);
                    if (heartbeat.StartsWith("STOP|", StringComparison.OrdinalIgnoreCase))
                    {
                        Log(options.LogPath, "收到停止心跳；看门狗退出。");
                        return 0;
                    }

                    bool parentAlive = IsProcessAlive(options.ParentProcessId);
                    DateTime heartbeatTime = File.Exists(options.HeartbeatPath) ? File.GetLastWriteTimeUtc(options.HeartbeatPath) : DateTime.MinValue;
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

                    Thread.Sleep(1000);
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
                return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
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

            public static Options Parse(string[] args)
            {
                string parent = GetArgument(args, "--parent");
                string heartbeat = GetArgument(args, "--heartbeat");
                string dll = GetArgument(args, "--dll");
                string log = GetArgument(args, "--log");
                if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(heartbeat) || string.IsNullOrEmpty(dll))
                {
                    throw new ArgumentException("必需参数：--parent <pid> --heartbeat <path> --dll <path> [--log <path>]");
                }

                return new Options
                {
                    ParentProcessId = int.Parse(parent),
                    HeartbeatPath = heartbeat,
                    DllPath = dll,
                    LogPath = string.IsNullOrEmpty(log) ? Path.Combine(Path.GetTempPath(), "X15FanWatchdog.log") : log
                };
            }
        }
    }
}
