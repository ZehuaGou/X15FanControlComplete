using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.ServiceProcess;
using System.Text;

namespace X15FanCore.Control
{
    /// <summary>
    /// Temporarily owns the Control Center fan/power entry points. The exact
    /// previous service/process state is persisted so normal exit and the
    /// external watchdog can restore it instead of leaving OEM control off.
    /// </summary>
    public sealed class ControlCenterLease
    {
        private static readonly string[] ServiceNames = { "CCDCHUService", "XTU3SERVICE" };
        private const string FnKeyProcessName = "FnKey";
        private readonly string _statePath;
        private LeaseState _state;
        private bool _acquired;

        public ControlCenterLease(string statePath)
        {
            _statePath = statePath ?? throw new ArgumentNullException("statePath");
        }

        public bool IsAcquired { get { return _acquired; } }

        public bool Acquire(out string diagnostic)
        {
            diagnostic = null;
            try
            {
                if (File.Exists(_statePath))
                {
                    LeaseState previous = ReadState(_statePath);
                    if (previous != null && IsProcessAlive(previous.OwnerProcessId))
                    {
                        diagnostic = "检测到另一个 X15FanControl 仍持有 Control Center 接管租约。";
                        return false;
                    }

                    RestoreState(previous, out diagnostic);
                    TryDeleteStateFile();
                }

                _state = CaptureState();
                _state.OwnerProcessId = Process.GetCurrentProcess().Id;
                WriteState(_statePath, _state);

                StopCapturedServices(_state);
                StopCapturedFnKeyProcesses(_state);
                _acquired = true;
                diagnostic = "Control Center 接管已启用：CCDCHUService、XTU3SERVICE 和 FnKey 已暂时让出控制权。";
                return true;
            }
            catch (Exception exception)
            {
                try { RestoreState(_state, out diagnostic); } catch { }
                TryDeleteStateFile();
                _state = null;
                _acquired = false;
                diagnostic = "Control Center 接管失败：" + exception.Message;
                return false;
            }
        }

        public bool Release(out string diagnostic)
        {
            diagnostic = null;
            if (!_acquired && !File.Exists(_statePath))
                return true;

            LeaseState state = _state ?? ReadState(_statePath);
            bool restored = RestoreState(state, out diagnostic);
            if (restored)
                TryDeleteStateFile();
            _state = null;
            _acquired = false;
            return restored;
        }

        public static bool RestorePersisted(string statePath, out string diagnostic)
        {
            diagnostic = null;
            if (string.IsNullOrEmpty(statePath) || !File.Exists(statePath))
                return true;

            ControlCenterLease lease = new ControlCenterLease(statePath);
            LeaseState state = ReadState(statePath);
            bool restored = RestoreState(state, out diagnostic);
            if (restored)
                lease.TryDeleteStateFile();
            return restored;
        }

        private LeaseState CaptureState()
        {
            LeaseState state = new LeaseState { Services = new List<ServiceState>(), FnKeyExecutables = new List<string>() };
            foreach (string name in ServiceNames)
            {
                try
                {
                    using (ServiceController service = new ServiceController(name))
                    {
                        ServiceControllerStatus status = service.Status;
                        state.Services.Add(new ServiceState { Name = name, WasRunning = status == ServiceControllerStatus.Running || status == ServiceControllerStatus.StartPending });
                    }
                }
                catch (InvalidOperationException)
                {
                    // This Control Center component is not installed.
                }
            }

            foreach (Process process in Process.GetProcessesByName(FnKeyProcessName))
            {
                try
                {
                    string path = process.MainModule == null ? null : process.MainModule.FileName;
                    if (!string.IsNullOrEmpty(path) && path.IndexOf("FnhotkeysandOSD", StringComparison.OrdinalIgnoreCase) >= 0)
                        state.FnKeyExecutables.Add(path);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
            return state;
        }

        private static void StopCapturedServices(LeaseState state)
        {
            if (state == null || state.Services == null)
                return;
            foreach (ServiceState entry in state.Services)
            {
                if (!entry.WasRunning)
                    continue;
                using (ServiceController service = new ServiceController(entry.Name))
                {
                    if (service.Status != ServiceControllerStatus.Stopped)
                    {
                        service.Stop();
                        service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(8));
                    }
                    if (service.Status != ServiceControllerStatus.Stopped)
                        throw new InvalidOperationException("无法停止 Control Center 服务 " + entry.Name + "。");
                }
            }
        }

        private static void StopCapturedFnKeyProcesses(LeaseState state)
        {
            if (state == null || state.FnKeyExecutables == null)
                return;
            foreach (string executable in state.FnKeyExecutables)
            {
                foreach (Process process in Process.GetProcessesByName(FnKeyProcessName))
                {
                    try
                    {
                        string path = process.MainModule == null ? null : process.MainModule.FileName;
                        if (!string.Equals(path, executable, StringComparison.OrdinalIgnoreCase))
                            continue;
                        try { process.CloseMainWindow(); } catch { }
                        if (!process.WaitForExit(1500))
                            process.Kill();
                        process.WaitForExit(3000);
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
        }

        private static bool RestoreState(LeaseState state, out string diagnostic)
        {
            diagnostic = null;
            if (state == null)
                return true;

            List<string> errors = new List<string>();
            if (state.Services != null)
            {
                foreach (ServiceState entry in state.Services)
                {
                    if (!entry.WasRunning)
                        continue;
                    try
                    {
                        using (ServiceController service = new ServiceController(entry.Name))
                        {
                            if (service.Status == ServiceControllerStatus.Stopped)
                                service.Start();
                            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(8));
                        }
                    }
                    catch (Exception exception)
                    {
                        errors.Add(entry.Name + "恢复失败：" + exception.Message);
                    }
                }
            }

            if (state.FnKeyExecutables != null)
            {
                foreach (string executable in state.FnKeyExecutables)
                {
                    try
                    {
                        bool alreadyRunning = false;
                        foreach (Process process in Process.GetProcessesByName(FnKeyProcessName))
                        {
                            try
                            {
                                string path = process.MainModule == null ? null : process.MainModule.FileName;
                                if (string.Equals(path, executable, StringComparison.OrdinalIgnoreCase))
                                    alreadyRunning = true;
                            }
                            catch { }
                            finally { process.Dispose(); }
                        }
                        if (!alreadyRunning)
                            Process.Start(new ProcessStartInfo { FileName = executable, UseShellExecute = true });
                    }
                    catch (Exception exception)
                    {
                        errors.Add("FnKey 恢复失败：" + exception.Message);
                    }
                }
            }

            if (errors.Count > 0)
            {
                diagnostic = string.Join("；", errors.ToArray());
                return false;
            }
            diagnostic = "Control Center 已恢复原有服务和 FnKey 状态。";
            return true;
        }

        private static bool IsProcessAlive(int processId)
        {
            if (processId <= 0)
                return false;
            try
            {
                using (Process process = Process.GetProcessById(processId))
                    return !process.HasExited;
            }
            catch { return false; }
        }

        private void TryDeleteStateFile()
        {
            try { if (File.Exists(_statePath)) File.Delete(_statePath); } catch { }
        }

        private static void WriteState(string path, LeaseState state)
        {
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = path + ".tmp";
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(LeaseState));
            using (FileStream stream = File.Create(temporary))
            {
                serializer.WriteObject(stream, state);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, null, true);
            else File.Move(temporary, path);
        }

        private static LeaseState ReadState(string path)
        {
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(LeaseState));
                using (FileStream stream = File.OpenRead(path))
                    return serializer.ReadObject(stream) as LeaseState;
            }
            catch { return null; }
        }

        [DataContract]
        private sealed class LeaseState
        {
            [DataMember(Order = 0)] public int OwnerProcessId { get; set; }
            [DataMember(Order = 1)] public List<ServiceState> Services { get; set; }
            [DataMember(Order = 2)] public List<string> FnKeyExecutables { get; set; }
        }

        [DataContract]
        private sealed class ServiceState
        {
            [DataMember(Order = 0)] public string Name { get; set; }
            [DataMember(Order = 1)] public bool WasRunning { get; set; }
        }
    }
}
