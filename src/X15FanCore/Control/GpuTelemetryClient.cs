using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    public sealed class GpuTelemetryClient : IDisposable
    {
        private Process _helperProcess;
        private Thread _readerThread;
        private volatile bool _running;
        private readonly object _lock = new object();
        private GpuTelemetryData _latest;
        private int _restartCount;
        private readonly int _maxRestarts = 5;
        private readonly TimeSpan _restartBackoffBase = TimeSpan.FromSeconds(2);
        private readonly string _helperExePath;
        private readonly int _pollIntervalMs;
        private bool _disposed;
        private bool _ownsActiveSlot;

        // 全局单例跟踪：整个进程生命周期只允许一个GpuTelemetryClient实例
        private static int _activeCount;
        private static readonly object _staticLock = new object();

        public GpuTelemetryData Latest
        {
            get
            {
                lock (_lock)
                {
                    if (_latest == null)
                        return new GpuTelemetryData { IsAvailable = false, ErrorMessage = "No data yet", SourceName = "none" };

                    bool stale = (DateTime.UtcNow - _latest.LastUpdatedUtc).TotalSeconds > 3;
                    _latest.IsStale = stale;
                    return _latest;
                }
            }
        }

        public GpuTelemetryClient(string helperExePath = null, int pollIntervalMs = 1000)
        {
            _helperExePath = helperExePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "X15GpuTelemetry.exe");
            _pollIntervalMs = Math.Max(200, Math.Min(10000, pollIntervalMs));
            _latest = new GpuTelemetryData
            {
                IsAvailable = false,
                ErrorMessage = "Initializing",
                SourceName = "pending",
                LastUpdatedUtc = DateTime.MinValue
            };
        }

        public bool Start()
        {
            ThrowIfDisposed();
            if (_running)
                return true;

            lock (_staticLock)
            {
                if (_activeCount > 0)
                {
                    System.Diagnostics.Trace.WriteLine("[GpuTelemetry] 拒绝启动：已有遥测实例运行中");
                    return false;
                }
                _activeCount++;
                _ownsActiveSlot = true;
            }

            try
            {
                _running = true;
                _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "GpuTelemetryReader" };
                _readerThread.Start();
                return true;
            }
            catch
            {
                _running = false;
                ReleaseActiveSlot();
                throw;
            }
        }

        public void Stop()
        {
            _running = false;
            KillHelperTree();
            if (_readerThread != null && _readerThread.IsAlive)
            {
                _readerThread.Join(3000);
            }
            _readerThread = null;
        }

        private void ReaderLoop()
        {
            while (_running)
            {
                try
                {
                    EnsureHelperRunning();
                    if (_helperProcess == null || _helperProcess.HasExited)
                    {
                        Thread.Sleep(500);
                        continue;
                    }

                    string line = _helperProcess.StandardOutput.ReadLine();
                    if (line == null)
                    {
                        _restartCount++;
                        Thread.Sleep(200);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var parsed = ParseJsonLine(line);
                    if (parsed != null)
                    {
                        _restartCount = 0;
                        lock (_lock)
                        {
                            _latest = parsed;
                        }
                    }
                }
                catch (ThreadAbortException) { break; }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        _latest = new GpuTelemetryData
                        {
                            IsAvailable = false,
                            ErrorMessage = "Read error: " + ex.Message,
                            SourceName = "error",
                            LastUpdatedUtc = DateTime.UtcNow,
                            IsStale = true
                        };
                    }
                    Thread.Sleep(500);
                }
            }
        }

        private void EnsureHelperRunning()
        {
            if (_helperProcess != null)
            {
                try
                {
                    if (!_helperProcess.HasExited)
                        return;
                }
                catch { }

                try { _helperProcess.Dispose(); } catch { }
                _helperProcess = null;
            }

            if (_restartCount >= _maxRestarts)
            {
                lock (_lock)
                {
                    _latest = new GpuTelemetryData
                    {
                        IsAvailable = false,
                        ErrorMessage = "Helper process exceeded max restarts (" + _maxRestarts + ")",
                        SourceName = "error",
                        LastUpdatedUtc = DateTime.UtcNow,
                        IsStale = true
                    };
                }
                return;
            }

            if (_restartCount > 0)
            {
                int backoffMs = (int)(_restartBackoffBase.TotalMilliseconds * Math.Pow(2, _restartCount - 1));
                Thread.Sleep(Math.Min(backoffMs, 30000));
            }

            try
            {
                if (!File.Exists(_helperExePath))
                {
                    lock (_lock)
                    {
                        _latest = new GpuTelemetryData
                        {
                            IsAvailable = false,
                            ErrorMessage = "Helper not found: " + _helperExePath,
                            SourceName = "error",
                            LastUpdatedUtc = DateTime.UtcNow,
                            IsStale = true
                        };
                    }
                    _restartCount = _maxRestarts;
                    return;
                }

                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = _helperExePath,
                    Arguments = "--interval-ms " + _pollIntervalMs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                _helperProcess = Process.Start(psi);
                if (_helperProcess == null)
                    throw new InvalidOperationException("X15GpuTelemetry进程启动返回null。");

                System.Diagnostics.Trace.WriteLine($"[GpuTelemetry] 启动 X15GpuTelemetry PID={_helperProcess.Id}");

                lock (_lock)
                {
                    _latest = new GpuTelemetryData
                    {
                        IsAvailable = false,
                        ErrorMessage = "Waiting for first sample",
                        SourceName = "nvidia-smi",
                        LastUpdatedUtc = DateTime.UtcNow,
                        IsStale = false
                    };
                }
            }
            catch (Exception ex)
            {
                _restartCount++;
                lock (_lock)
                {
                    _latest = new GpuTelemetryData
                    {
                        IsAvailable = false,
                        ErrorMessage = "Start failed: " + ex.Message,
                        SourceName = "error",
                        LastUpdatedUtc = DateTime.UtcNow,
                        IsStale = true
                    };
                }
            }
        }

        // 只终止当前 helper 的进程树，不能按名称杀掉系统中所有 nvidia-smi。
        private void KillHelperTree()
        {
            Process helper = _helperProcess;
            _helperProcess = null;
            if (helper == null)
                return;

            int helperPid = 0;
            try { helperPid = helper.Id; } catch { }
            System.Diagnostics.Trace.WriteLine("[GpuTelemetry] 终止进程树 PID=" + helperPid);

            try
            {
                if (helperPid > 0 && !helper.HasExited)
                {
                    // .NET Framework 4.8 没有 Kill(entireProcessTree)，使用 Windows taskkill 精确终止该 PID 的子树。
                    var psi = new ProcessStartInfo
                    {
                        FileName = "taskkill.exe",
                        Arguments = "/PID " + helperPid + " /T /F",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (Process killer = Process.Start(psi))
                    {
                        killer?.WaitForExit(2000);
                    }
                }
            }
            catch
            {
                try
                {
                    if (!helper.HasExited)
                        helper.Kill();
                }
                catch { }
            }
            finally
            {
                try { helper.WaitForExit(2000); } catch { }
                try { helper.Dispose(); } catch { }
            }

            System.Diagnostics.Trace.WriteLine("[GpuTelemetry] 进程树终止完成");
        }

        private static GpuTelemetryData ParseJsonLine(string line)
        {
            try
            {
                if (!line.Contains("\"type\"") || !line.Contains("\"smi\""))
                    return null;

                int temp = ExtractInt(line, "temperature_gpu");
                int util = ExtractInt(line, "utilization_gpu");
                double power = ExtractDouble(line, "power_draw");
                string pstate = ExtractString(line, "pstate");

                if (temp < 0) return null;

                return new GpuTelemetryData
                {
                    IsAvailable = true,
                    TemperatureC = temp,
                    UtilizationPercent = util >= 0 ? util : 0,
                    PowerWatts = power >= 0 ? power : 0,
                    PState = pstate ?? "N/A",
                    SourceName = "nvidia-smi",
                    ErrorMessage = null,
                    LastUpdatedUtc = DateTime.UtcNow
                };
            }
            catch
            {
                return null;
            }
        }

        private static int ExtractInt(string json, string key)
        {
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search);
            if (idx < 0) return -1;
            idx += search.Length;
            int end = idx;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            if (int.TryParse(json.Substring(idx, end - idx), out int val))
                return val;
            return -1;
        }

        private static double ExtractDouble(string json, string key)
        {
            string search = "\"" + key + "\":";
            int idx = json.IndexOf(search);
            if (idx < 0) return -1;
            idx += search.Length;
            int end = idx;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '.' || json[end] == '-')) end++;
            if (double.TryParse(json.Substring(idx, end - idx),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double val))
                return val;
            return -1;
        }

        private static string ExtractString(string json, string key)
        {
            string search = "\"" + key + "\":\"";
            int idx = json.IndexOf(search);
            if (idx < 0) return null;
            idx += search.Length;
            int end = json.IndexOf('"', idx);
            if (end < 0) return null;
            return json.Substring(idx, end - idx);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            ReleaseActiveSlot();
        }

        private void ReleaseActiveSlot()
        {
            lock (_staticLock)
            {
                if (!_ownsActiveSlot)
                    return;

                _ownsActiveSlot = false;
                _activeCount = Math.Max(0, _activeCount - 1);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException("GpuTelemetryClient");
        }

        // 检测并清理泄漏的nvidia-smi进程（静态方法，供外部调用）
        public static int DetectLeakedProcesses()
        {
            int leakCount = 0;
            try
            {
                var nvidiaProcs = Process.GetProcessesByName("nvidia-smi");
                if (nvidiaProcs.Length > 1)
                {
                    leakCount = nvidiaProcs.Length - 1;
                    System.Diagnostics.Trace.WriteLine($"[GpuTelemetry] 检测到 {leakCount} 个泄漏的nvidia-smi进程");
                }
                foreach (var p in nvidiaProcs) p.Dispose();
            }
            catch { }
            return leakCount;
        }
    }
}
