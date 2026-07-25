using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace X15GpuTelemetry
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // x64 helper: runs nvidia-smi in a loop, outputs JSON Lines to stdout.
            // Invocation: X15GpuTelemetry.exe [--interval-ms 1000] [--smi-path <path>]
            int intervalMs = 1000;
            string smiPath = FindNvidiaSmi();

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--interval-ms" && i + 1 < args.Length)
                    int.TryParse(args[++i], out intervalMs);
                if (args[i] == "--smi-path" && i + 1 < args.Length)
                    smiPath = args[++i];
            }

            if (smiPath == null || !File.Exists(smiPath))
            {
                WriteError("nvidia-smi not found at any known location.");
                return 1;
            }

            intervalMs = Math.Max(200, Math.Min(10000, intervalMs));

            // Write header so the parent knows we're alive
            WriteTelemetry("info", new { message = "X15GpuTelemetry starting", smiPath, intervalMs });

            Process smiProcess = null;
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
                    StandardOutputEncoding = Encoding.UTF8
                };

                smiProcess = Process.Start(psi);

                // Read stdout line by line
                string line;
                while ((line = smiProcess.StandardOutput.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var parsed = ParseSmiLine(line);
                    if (parsed != null)
                    {
                        WriteTelemetry("smi", parsed);
                    }
                    else
                    {
                        WriteTelemetry("smi_raw", new { raw = line });
                    }
                }

                // nvidia-smi exited
                string stderr = smiProcess.StandardError.ReadToEnd();
                WriteError("nvidia-smi exited: " + (string.IsNullOrEmpty(stderr) ? "unknown" : stderr));
                return 2;
            }
            catch (Exception ex)
            {
                WriteError("X15GpuTelemetry error: " + ex.Message);
                return 3;
            }
            finally
            {
                if (smiProcess != null && !smiProcess.HasExited)
                {
                    try { smiProcess.Kill(); } catch { }
                    smiProcess.Dispose();
                }
            }
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
            // Minimal JSON serializer without external dependencies
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
