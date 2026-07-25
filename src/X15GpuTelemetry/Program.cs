using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace X15GpuTelemetry
{
    internal static class Program
    {
        // --- Windows Job Object P/Invoke ---
        private const int JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
        private const uint JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, uint JobObjectInfoClass,
            IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // --- Main ---
        private static int Main(string[] args)
        {
            // x64 helper: runs nvidia-smi in a loop, outputs JSON Lines to stdout.
            // Invocation: X15GpuTelemetry.exe [--interval-ms 1000] [--smi-path <path>] [--parent-pid <PID>]
            int intervalMs = 1000;
            int parentPid = 0;
            string smiPath = FindNvidiaSmi();
            int myPid = Process.GetCurrentProcess().Id;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--interval-ms" && i + 1 < args.Length)
                    int.TryParse(args[++i], out intervalMs);
                if (args[i] == "--smi-path" && i + 1 < args.Length)
                    smiPath = args[++i];
                if (args[i] == "--parent-pid" && i + 1 < args.Length)
                    int.TryParse(args[++i], out parentPid);
            }

            if (smiPath == null || !File.Exists(smiPath))
            {
                WriteError("nvidia-smi not found at any known location.");
                return 1;
            }

            intervalMs = Math.Max(200, Math.Min(10000, intervalMs));

            // Write header so the parent knows we're alive
            WriteTelemetry("info", new { message = "X15GpuTelemetry starting", smiPath, intervalMs, parentPid, myPid });

            // --- Create Job Object with KILL_ON_JOB_CLOSE ---
            IntPtr jobHandle = IntPtr.Zero;
            try
            {
                jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (jobHandle == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    WriteError("CreateJobObject failed: " + new Win32Exception(err).Message);
                    return 1;
                }

                JOBOBJECT_EXTENDED_LIMIT_INFORMATION jobInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                jobInfo.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

                IntPtr jobInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf(jobInfo));
                try
                {
                    Marshal.StructureToPtr(jobInfo, jobInfoPtr, false);
                    if (!SetInformationJobObject(jobHandle, JOB_OBJECT_EXTENDED_LIMIT_INFORMATION,
                        jobInfoPtr, (uint)Marshal.SizeOf(jobInfo)))
                    {
                        int err = Marshal.GetLastWin32Error();
                        WriteError("SetInformationJobObject failed: " + new Win32Exception(err).Message);
                        JobObjectCleanup(jobHandle);
                        jobHandle = IntPtr.Zero;
                        return 1;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(jobInfoPtr);
                }

                WriteTelemetry("info", new { message = "Job Object created with KILL_ON_JOB_CLOSE", handle = jobHandle.ToInt64() });
            }
            catch (Exception ex)
            {
                WriteError("Job Object setup failed: " + ex.Message);
                if (jobHandle != IntPtr.Zero)
                {
                    JobObjectCleanup(jobHandle);
                    jobHandle = IntPtr.Zero;
                }
                return 1;
            }

            // --- Start nvidia-smi ---
            Process smiProcess = null;
            var cts = new CancellationTokenSource();
            int exitCode = 0;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = smiPath,
                    Arguments = "--query-gpu=name,temperature.gpu,utilization.gpu,power.draw,pstate --format=csv,noheader,nounits --loop-ms=" + intervalMs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                smiProcess = Process.Start(psi);
                if (smiProcess == null)
                {
                    WriteError("nvidia-smi process start returned null.");
                    exitCode = 1;
                    return 1;
                }

                WriteTelemetry("info", new { message = "nvidia-smi started", nvidiaSmiPid = smiProcess.Id });

                // --- Assign nvidia-smi to Job Object ---
                if (jobHandle != IntPtr.Zero)
                {
                    try
                    {
                        if (!smiProcess.HasExited)
                        {
                            if (AssignProcessToJobObject(jobHandle, smiProcess.Handle))
                            {
                                WriteTelemetry("info", new { message = "nvidia-smi assigned to Job Object" });
                            }
                            else
                            {
                                int err = Marshal.GetLastWin32Error();
                                WriteError("AssignProcessToJobObject failed: " + new Win32Exception(err).Message);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteError("AssignProcessToJobObject exception: " + ex.Message);
                    }
                }

                // --- Async stdout reading ---
                var outputDone = new ManualResetEvent(false);
                var outputErrors = new StringBuilder();

                smiProcess.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data == null)
                    {
                        outputDone.Set();
                        return;
                    }
                    if (string.IsNullOrWhiteSpace(e.Data))
                        return;

                    var parsed = ParseSmiLine(e.Data);
                    if (parsed != null)
                    {
                        WriteTelemetry("smi", parsed);
                    }
                    else
                    {
                        WriteTelemetry("smi_raw", new { raw = e.Data });
                    }
                };

                smiProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (outputErrors)
                            outputErrors.AppendLine(e.Data);
                    }
                };

                smiProcess.BeginOutputReadLine();
                smiProcess.BeginErrorReadLine();

                // --- File and parent monitoring loop ---
                CancellationToken token = cts.Token;
                while (!token.IsCancellationRequested)
                {
                    // Check if parent process still exists
                    if (parentPid > 0)
                    {
                        try
                        {
                            Process parentProc = Process.GetProcessById(parentPid);
                            parentProc.Dispose();
                        }
                        catch (ArgumentException)
                        {
                            WriteTelemetry("info", new { message = "Parent process exited", parentPid });
                            break;
                        }
                    }

                    // Check if nvidia-smi exited
                    if (smiProcess.HasExited)
                    {
                        string stderr;
                        lock (outputErrors)
                            stderr = outputErrors.ToString();
                        WriteError("nvidia-smi exited: " + (string.IsNullOrEmpty(stderr) ? "unknown" : stderr));
                        exitCode = 2;
                        break;
                    }

                    // Use event-based wait instead of Sleep to allow cancellation
                    if (token.WaitHandle.WaitOne(1000))
                        break; // cancellation requested
                }

                // Ensure nvidia-smi is terminated
                if (smiProcess != null && !smiProcess.HasExited)
                {
                    try { smiProcess.Kill(); } catch { }
                }

                // Wait for the async read to finish
                outputDone.WaitOne(2000);
            }
            catch (Exception ex)
            {
                WriteError("X15GpuTelemetry error: " + ex.Message);
                exitCode = 3;
            }
            finally
            {
                // Stop async reading
                try
                {
                    if (smiProcess != null)
                    {
                        if (!smiProcess.HasExited)
                        {
                            try { smiProcess.Kill(); } catch { }
                            smiProcess.WaitForExit(2000);
                        }
                        smiProcess.Dispose();
                    }
                }
                catch { }

                // Close the Job Object — with KILL_ON_JOB_CLOSE, this guarantees nvidia-smi exits
                if (jobHandle != IntPtr.Zero)
                {
                    JobObjectCleanup(jobHandle);
                    WriteTelemetry("info", new { message = "Job Object closed", nvidiaSmiExited = smiProcess?.HasExited ?? true });
                }
            }

            return exitCode;
        }

        private static void JobObjectCleanup(IntPtr jobHandle)
        {
            try { CloseHandle(jobHandle); } catch { }
        }

        private static object ParseSmiLine(string line)
        {
            // Format: name, temperature.gpu, utilization.gpu, power.draw, pstate
            // Example: "NVIDIA GeForce RTX 4060 Laptop GPU, 67, 1, 24.56, P0"
            try
            {
                string[] parts = line.Split(',');
                if (parts.Length < 5)
                    return null;

                string name = parts[0].Trim();

                if (!int.TryParse(parts[1].Trim(), out int tempGpu))
                    tempGpu = -1;

                if (!int.TryParse(parts[2].Trim(), out int utilGpu))
                    utilGpu = -1;

                if (!double.TryParse(parts[3].Trim(), out double powerDraw))
                    powerDraw = -1;

                string pstate = parts[4].Trim();

                return new
                {
                    name,
                    temperature_gpu = tempGpu,
                    utilization_gpu = utilGpu,
                    power_draw = powerDraw,
                    pstate,
                    timestamp = DateTime.UtcNow.ToString("O")
                };
            }
            catch
            {
                return null;
            }
        }

        private static void WriteTelemetry(string type, object data)
        {
            string json = SimpleJsonSerialize(new { type, data });
            Console.WriteLine(json);
        }

        private static void WriteError(string message)
        {
            string json = SimpleJsonSerialize(new { type = "error", data = new { message, timestamp = DateTime.UtcNow.ToString("O") } });
            Console.Error.WriteLine(json);
        }

        private static string SimpleJsonSerialize(object obj)
        {
            var sb = new StringBuilder();
            SerializeObject(sb, obj);
            return sb.ToString();
        }

        private static void SerializeObject(StringBuilder sb, object obj)
        {
            if (obj == null)
            {
                sb.Append("null");
                return;
            }

            var type = obj.GetType();
            if (type == typeof(string))
            {
                sb.Append('"');
                sb.Append(((string)obj).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t"));
                sb.Append('"');
                return;
            }

            if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte))
            {
                sb.Append(obj.ToString());
                return;
            }

            if (type == typeof(double) || type == typeof(float))
            {
                sb.Append(((double)obj).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }

            if (type == typeof(bool))
            {
                sb.Append(((bool)obj) ? "true" : "false");
                return;
            }

            // Anonymous type or other object - serialize properties
            sb.Append('{');
            bool first = true;
            foreach (var prop in type.GetProperties())
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"');
                sb.Append(prop.Name);
                sb.Append("\":");
                SerializeObject(sb, prop.GetValue(obj, null));
            }
            sb.Append('}');
        }

        private static string FindNvidiaSmi()
        {
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
                Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
                "nvidia-smi.exe"
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }

            return null;
        }
    }
}
