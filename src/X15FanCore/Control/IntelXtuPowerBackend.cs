using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Microsoft.Win32;
using System.Reflection;
using System.ServiceProcess;
using System.Threading;

namespace X15FanCore.Control
{
    /// <summary>
    /// Read-only discovery bridge for the OEM Control Center Intel XTU path.
    ///
    /// The OEM package owns the actual hardware protocol.  This class deliberately
    /// does not expose Tune/ApplyChanges or any DCHU write method yet.  It only
    /// starts the already-installed XTU service (when requested) and enumerates the
    /// controls that the OEM Intel SDK reports.
    /// </summary>
    public sealed class IntelXtuPowerBackend
    {
        private const string ServiceName = "XTU3SERVICE";
        private const string SdkAssemblyName = "IntelOverclockingSDK.dll";
        private const string TuningTypeName = "Intel.Overclocking.SDK.Tuning.TuningLibrary";
        private const string TuningInterfaceName = "Intel.Overclocking.SDK.Tuning.ITuningLibrary";

        public IntelXtuProbeResult Probe(bool startService)
        {
            IntelXtuProbeResult result = new IntelXtuProbeResult();
            try
            {
                string sdkDirectory = FindSdkDirectory();
                result.SdkDirectory = sdkDirectory;
                if (string.IsNullOrEmpty(sdkDirectory))
                {
                    result.Error = "未找到 Control Center 的 IntelOverclockingSDK.dll。";
                    return result;
                }

                result.SdkFound = true;
                result.ServiceInstalled = ServiceExists();
                if (result.ServiceInstalled)
                {
                    result.ServiceState = GetServiceState();
                    if (startService && result.ServiceState != ServiceControllerStatus.Running)
                    {
                        StartServiceAndWait();
                        result.ServiceState = GetServiceState();
                    }

                    // The current Control Center SDK is a WCF client for the
                    // XTU service.  If that service is stopped, merely asking
                    // TuningLibrary.Instance/Initialize() can block while the
                    // named-pipe endpoint waits for the OEM driver.  A normal
                    // read-only startup probe must report the stopped service
                    // instead of entering that unbounded path.
                    if (!startService && result.ServiceState != ServiceControllerStatus.Running)
                    {
                        result.Error = "XTU 服务未运行，跳过可能阻塞的 Intel SDK 初始化。";
                        return result;
                    }
                }

                Assembly sdk = LoadSdkAssembly(sdkDirectory);
                Type tuningType = sdk.GetType(TuningTypeName, true);
                Type tuningInterface = sdk.GetType(TuningInterfaceName, true);
                object tuning = tuningType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    .GetValue(null, null);
                if (tuning == null)
                {
                    result.Error = "Intel SDK 返回了空的 TuningLibrary 实例。";
                    return result;
                }

                InterfaceMapping map = tuningType.GetInterfaceMap(tuningInterface);
                InvokeInterfaceMethod(map, tuning, "Initialize");
                result.Initialized = Convert.ToBoolean(InvokeInterfaceMethod(map, tuning, "InitializeCheck"));

                object controlsObject = InvokeInterfaceMethod(map, tuning, "GetAvailableControls");
                if (controlsObject is System.Collections.IEnumerable controls)
                {
                    foreach (object control in controls)
                    {
                        IntelXtuControlInfo info = ReadControl(control);
                        if (info != null)
                        {
                            result.Controls.Add(info);
                        }
                    }
                }

                result.PowerControls = result.Controls
                    .Where(IsPowerControl)
                    .ToList();
                if (!result.Initialized && string.IsNullOrEmpty(result.Error))
                {
                    result.Error = "Intel SDK 初始化检查未通过。";
                }
            }
            catch (Exception exception)
            {
                result.Error = Unwrap(exception).Message;
            }

            return result;
        }

        public IntelXtuPowerApplyResult ApplyCpuPowerLimits(decimal pl1Watts, decimal pl2Watts, uint timeSeconds)
        {
            IntelXtuPowerApplyResult result = new IntelXtuPowerApplyResult
            {
                RequestedPl1Watts = pl1Watts,
                RequestedPl2Watts = pl2Watts,
                RequestedTimeSeconds = timeSeconds
            };

            if (pl1Watts <= 0 || pl2Watts <= 0 || pl2Watts < pl1Watts || timeSeconds == 0)
            {
                result.Error = "CPU 功耗限制参数无效：PL2 必须不小于 PL1，时间必须大于 0。";
                return result;
            }

            try
            {
                string sdkDirectory = FindSdkDirectory();
                result.SdkDirectory = sdkDirectory;
                if (string.IsNullOrEmpty(sdkDirectory))
                {
                    result.Error = "未找到当前 Control Center 的 Intel XTU SDK。";
                    return result;
                }

                if (!ServiceExists())
                {
                    result.Error = "未安装 XTU3SERVICE，无法通过 Control Center 的 CPU 功耗接口写入。";
                    return result;
                }

                if (GetServiceState() != ServiceControllerStatus.Running)
                {
                    StartServiceAndWait();
                }

                if (GetServiceState() != ServiceControllerStatus.Running)
                {
                    result.Error = "XTU3SERVICE 未能启动，未执行任何功耗写入。";
                    return result;
                }

                Assembly sdk = LoadSdkAssembly(sdkDirectory);
                Type tuningType = sdk.GetType(TuningTypeName, true);
                Type tuningInterface = sdk.GetType(TuningInterfaceName, true);
                object tuning = tuningType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
                    .GetValue(null, null);
                InterfaceMapping map = tuningType.GetInterfaceMap(tuningInterface);
                InvokeInterfaceMethod(map, tuning, "Initialize");
                bool initialized = Convert.ToBoolean(InvokeInterfaceMethod(map, tuning, "InitializeCheck"));
                if (!initialized)
                {
                    result.Error = "Intel XTU SDK 初始化失败，未执行任何功耗写入。";
                    return result;
                }

                Dictionary<uint, IntelXtuControlInfo> controls = ReadControlMap(
                    InvokeInterfaceMethod(map, tuning, "GetAvailableControls"));
                uint[] ids = { 48u, 47u, 66u };
                foreach (uint id in ids)
                {
                    IntelXtuControlInfo control;
                    if (!controls.TryGetValue(id, out control) || control.ReadOnly || !control.Enabled)
                    {
                        result.Error = "当前 XTU 服务没有提供可写的 CPU 功耗控制 ID=" + id + "。";
                        return result;
                    }

                    decimal requested = id == 48u ? pl1Watts : id == 47u ? pl2Watts : timeSeconds;
                    if (!IsWithinControlRange(control, requested))
                    {
                        result.Error = string.Format(
                            CultureInfo.InvariantCulture,
                            "CPU 功耗参数超出 Control Center 报告的范围：ID={0}, 请求值={1}, 范围={2}..{3}。",
                            id, requested, control.MinValue, control.MaxValue);
                        return result;
                    }
                }

                Dictionary<uint, decimal> originals = new Dictionary<uint, decimal>();
                foreach (uint id in ids)
                {
                    originals[id] = controls[id].ActiveValue;
                }

                decimal[] requestedValues = { pl1Watts, pl2Watts, timeSeconds };
                for (int i = 0; i < ids.Length; i++)
                {
                    uint id = ids[i];
                    IntelXtuControlInfo control = controls[id];
                    object tuningResult = InvokeInterfaceMethod(
                        map,
                        tuning,
                        "Tune",
                        id,
                        requestedValues[i],
                        control.RequiresReboot);
                    if (!IsSuccessfulTuningResult(tuningResult))
                    {
                        RollbackPowerLimits(map, tuning, controls, originals, ids, i);
                        result.Error = "XTU 拒绝了 CPU 功耗限制写入，已尝试恢复原值。";
                        return result;
                    }

                    object activeControl = InvokeInterfaceMethod(map, tuning, "GetControl", id);
                    decimal activeValue = Read<decimal>(activeControl.GetType(), activeControl, "ActiveValue");
                    if (Math.Abs(activeValue - requestedValues[i]) > 0.1m)
                    {
                        RollbackPowerLimits(map, tuning, controls, originals, ids, i);
                        result.Error = string.Format(
                            CultureInfo.InvariantCulture,
                            "CPU 功耗限制回读不一致：ID={0}, 请求值={1}, 实际值={2}；已尝试恢复原值。",
                            id, requestedValues[i], activeValue);
                        return result;
                    }
                }

                result.Applied = true;
                result.AppliedPl1Watts = pl1Watts;
                result.AppliedPl2Watts = pl2Watts;
                result.AppliedTimeSeconds = timeSeconds;
                return result;
            }
            catch (Exception exception)
            {
                result.Error = Unwrap(exception).Message;
                return result;
            }
        }

        private static Dictionary<uint, IntelXtuControlInfo> ReadControlMap(object controlsObject)
        {
            Dictionary<uint, IntelXtuControlInfo> controls = new Dictionary<uint, IntelXtuControlInfo>();
            if (controlsObject is System.Collections.IEnumerable enumerable)
            {
                foreach (object control in enumerable)
                {
                    IntelXtuControlInfo info = ReadControl(control);
                    if (info != null)
                        controls[info.Id] = info;
                }
            }
            return controls;
        }

        private static bool IsWithinControlRange(IntelXtuControlInfo control, decimal value)
        {
            decimal min;
            decimal max;
            if (!decimal.TryParse(control.MinValue, NumberStyles.Float, CultureInfo.InvariantCulture, out min) ||
                !decimal.TryParse(control.MaxValue, NumberStyles.Float, CultureInfo.InvariantCulture, out max))
            {
                return false;
            }
            return value >= min && value <= max;
        }

        private static bool IsSuccessfulTuningResult(object tuningResult)
        {
            if (tuningResult == null)
                return false;
            if (tuningResult is bool booleanResult)
                return booleanResult;

            MethodInfo opTrue = tuningResult.GetType().GetMethod(
                "op_True",
                BindingFlags.Public | BindingFlags.Static);
            if (opTrue != null)
                return Convert.ToBoolean(opTrue.Invoke(null, new[] { tuningResult }));

            return string.Equals(Convert.ToString(tuningResult, CultureInfo.InvariantCulture), "Success", StringComparison.OrdinalIgnoreCase);
        }

        private static void RollbackPowerLimits(
            InterfaceMapping map,
            object tuning,
            Dictionary<uint, IntelXtuControlInfo> controls,
            Dictionary<uint, decimal> originals,
            uint[] ids,
            int lastChangedIndex)
        {
            for (int i = lastChangedIndex; i >= 0; i--)
            {
                uint id = ids[i];
                try
                {
                    InvokeInterfaceMethod(
                        map,
                        tuning,
                        "Tune",
                        id,
                        originals[id],
                        controls[id].RequiresReboot);
                }
                catch
                {
                    // Preserve the original failure.  The caller reports that
                    // rollback was attempted; no second exception is allowed to
                    // hide the first one.
                }
            }
        }

        private static bool IsPowerControl(IntelXtuControlInfo control)
        {
            string text = (control.Name + " " + control.Category + " " + control.Description + " " + control.Units)
                .ToLowerInvariant();
            return text.Contains("pl1") || text.Contains("pl2") ||
                   text.Contains("power limit") || text.Contains("package power");
        }

        private static IntelXtuControlInfo ReadControl(object control)
        {
            if (control == null)
            {
                return null;
            }

            Type type = control.GetType();
            return new IntelXtuControlInfo
            {
                Id = Read<uint>(type, control, "Id"),
                Name = Read<string>(type, control, "Name"),
                Category = Read<string>(type, control, "Category"),
                Description = Read<string>(type, control, "Description"),
                Units = Read<string>(type, control, "Units"),
                Enabled = Read<bool>(type, control, "Enabled"),
                ReadOnly = Read<bool>(type, control, "ReadOnly"),
                RequiresReboot = Read<bool>(type, control, "RequiresReboot"),
                ActiveValue = Read<decimal>(type, control, "ActiveValue"),
                DefaultValue = Read<decimal>(type, control, "DefaultValue"),
                MinValue = ReadMinMax(control, "GetMinPossibleValue"),
                MaxValue = ReadMinMax(control, "GetMaxPossibleValue")
            };
        }

        private static T Read<T>(Type type, object instance, string propertyName)
        {
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null)
            {
                return default(T);
            }

            object value = property.GetValue(instance, null);
            if (value == null)
            {
                return default(T);
            }

            return (T)Convert.ChangeType(value, typeof(T));
        }

        private static string ReadMinMax(object control, string methodName)
        {
            try
            {
                MethodInfo method = control.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
                return method == null ? string.Empty : Convert.ToString(method.Invoke(control, null));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static object InvokeInterfaceMethod(InterfaceMapping map, object instance, string name)
        {
            return InvokeInterfaceMethod(map, instance, name, new object[0]);
        }

        private static object InvokeInterfaceMethod(InterfaceMapping map, object instance, string name, params object[] arguments)
        {
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (string.Equals(map.InterfaceMethods[i].Name, name, StringComparison.Ordinal))
                {
                    return map.TargetMethods[i].Invoke(instance, arguments);
                }
            }

            throw new MissingMethodException(map.InterfaceType.FullName, name);
        }

        private static Assembly LoadSdkAssembly(string directory)
        {
            string sdkPath = Path.Combine(directory, SdkAssemblyName);
            Assembly alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .FirstOrDefault(assembly =>
                {
                    try
                    {
                        return string.Equals(assembly.Location, sdkPath, StringComparison.OrdinalIgnoreCase);
                    }
                    catch (NotSupportedException)
                    {
                        return false;
                    }
                });
            if (alreadyLoaded != null)
            {
                return alreadyLoaded;
            }

            ResolveEventHandler resolver = (sender, args) =>
            {
                string dependencyName = new AssemblyName(args.Name).Name + ".dll";
                string dependencyPath = Path.Combine(directory, dependencyName);
                return File.Exists(dependencyPath) ? Assembly.LoadFrom(dependencyPath) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolver;
            return Assembly.LoadFrom(sdkPath);
            // Keep the resolver alive for the duration of the process.  The SDK
            // loads XtuCommon/ProfileHelperModel lazily when Initialize runs.
            // Removing it after Assembly.LoadFrom would make later read-only
            // initialization fail even though the SDK itself loaded successfully.
        }

        public static string FindSdkDirectory()
        {
            List<string> candidates = new List<string>();
            string configured = Environment.GetEnvironmentVariable("X15_CONTROL_CENTER_CPUOC_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                candidates.Add(configured);
            }

            candidates.Add(AppDomain.CurrentDomain.BaseDirectory);
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"));

            // Recent Control Center releases keep the real CPUOC backend inside
            // the FnKey package instead of installing a separate
            // CLEVOCO.CPUOverclocking package.  The old package scan below can
            // therefore find a stale 6.x extraction while the live FnKey app is
            // using the 7.x SDK.  Prefer the versioned directories used by the
            // running OEM package, with XTU_V10 first because it is the backend
            // that matches the current Intel SDK.
            string windowsAppsRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsApps");
            try
            {
                AddRegisteredFnKeyPackageCandidates(candidates);
                if (Directory.Exists(windowsAppsRoot))
                {
                    foreach (string package in Directory.GetDirectories(
                        windowsAppsRoot,
                        "CLEVOCO.FnhotkeysandOSD_*"))
                    {
                        candidates.Add(Path.Combine(package, "FnKey", "CPUOC", "XTU_V10"));
                        candidates.Add(Path.Combine(package, "FnKey", "CPUOC"));
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // The elevated parent normally has access.  Discovery is
                // optional and must never affect fan control if WindowsApps is
                // protected on a particular installation.
            }
            catch (IOException)
            {
            }

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (File.Exists(Path.Combine(candidate, SdkAssemblyName)))
                {
                    return candidate;
                }
            }

            string windowsApps = windowsAppsRoot;
            try
            {
                if (Directory.Exists(windowsApps))
                {
                    foreach (string directory in Directory.GetDirectories(windowsApps, "CLEVOCO.CPUOverclocking_*"))
                    {
                        if (File.Exists(Path.Combine(directory, SdkAssemblyName)))
                        {
                            return directory;
                        }
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // The elevated controller normally has access; a failed optional
                // probe must never affect EC fan control.
            }
            catch (IOException)
            {
            }

            return ExtractBundledCpuOcPackage();
        }

        private static void AddRegisteredFnKeyPackageCandidates(List<string> candidates)
        {
            const string applicationsKeyPath =
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Applications";
            try
            {
                using (RegistryKey applications = Registry.LocalMachine.OpenSubKey(applicationsKeyPath))
                {
                    if (applications == null)
                        return;

                    foreach (string packageKeyName in applications.GetSubKeyNames())
                    {
                        if (!packageKeyName.StartsWith("CLEVOCO.FnhotkeysandOSD_", StringComparison.OrdinalIgnoreCase))
                            continue;

                        using (RegistryKey package = applications.OpenSubKey(packageKeyName))
                        {
                            string manifestPath = package == null ? null : package.GetValue("Path") as string;
                            string neutralRoot = string.IsNullOrEmpty(manifestPath)
                                ? null
                                : Directory.GetParent(Path.GetDirectoryName(manifestPath)).FullName;
                            AddFnKeyCandidates(candidates, neutralRoot);

                            if (!string.IsNullOrEmpty(neutralRoot))
                            {
                                string parent = Path.GetDirectoryName(neutralRoot);
                                string leaf = Path.GetFileName(neutralRoot);
                                string x64Leaf = leaf.Replace("_neutral_~_", "_x64__");
                                if (!string.Equals(leaf, x64Leaf, StringComparison.Ordinal))
                                    AddFnKeyCandidates(candidates, Path.Combine(parent, x64Leaf));
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Package registration is only a discovery hint.  Keep the
                // existing WindowsApps and bundled-package fallbacks intact.
            }
        }

        private static void AddFnKeyCandidates(List<string> candidates, string packageRoot)
        {
            if (string.IsNullOrEmpty(packageRoot))
                return;

            candidates.Add(Path.Combine(packageRoot, "FnKey", "CPUOC", "XTU_V10"));
            candidates.Add(Path.Combine(packageRoot, "FnKey", "CPUOC"));
        }

        private static string ExtractBundledCpuOcPackage()
        {
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string bundlePath = Path.Combine(programFilesX86, "ControlCenter", "AppInstall", "CPU_OC", "CPU_OC.appxbundle");
            if (!File.Exists(bundlePath))
            {
                return null;
            }

            string destination = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "X15FanControl",
                "ControlCenterCpuOc");
            string sdkPath = Path.Combine(destination, SdkAssemblyName);
            if (File.Exists(sdkPath))
            {
                return destination;
            }

            string loadingDirectory = Path.Combine(destination, "CPUOC_Loading");
            if (File.Exists(Path.Combine(loadingDirectory, SdkAssemblyName)))
            {
                return loadingDirectory;
            }

            try
            {
                Directory.CreateDirectory(destination);
                using (ZipArchive bundle = ZipFile.OpenRead(bundlePath))
                {
                    ZipArchiveEntry x64Appx = bundle.Entries.FirstOrDefault(entry =>
                        entry.FullName.EndsWith("_x64.appx", StringComparison.OrdinalIgnoreCase));
                    if (x64Appx == null)
                    {
                        return null;
                    }

                    using (Stream appxStream = x64Appx.Open())
                    using (ZipArchive appx = new ZipArchive(appxStream, ZipArchiveMode.Read))
                    {
                        foreach (ZipArchiveEntry entry in appx.Entries)
                        {
                            if (string.IsNullOrEmpty(entry.Name))
                            {
                                continue;
                            }

                            string targetPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));
                            string destinationRoot = Path.GetFullPath(destination + Path.DirectorySeparatorChar);
                            if (!targetPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidDataException("Control Center 包含越界路径。" + entry.FullName);
                            }

                            string parent = Path.GetDirectoryName(targetPath);
                            if (!string.IsNullOrEmpty(parent))
                            {
                                Directory.CreateDirectory(parent);
                            }

                            using (Stream input = entry.Open())
                            using (FileStream output = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                input.CopyTo(output);
                            }
                        }
                    }
                }

                if (File.Exists(sdkPath))
                {
                    return destination;
                }

                return File.Exists(Path.Combine(loadingDirectory, SdkAssemblyName))
                    ? loadingDirectory
                    : null;
            }
            catch (InvalidDataException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static bool ServiceExists()
        {
            try
            {
                using (ServiceController service = new ServiceController(ServiceName))
                {
                    string ignored = service.Status.ToString();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ServiceControllerStatus GetServiceState()
        {
            using (ServiceController service = new ServiceController(ServiceName))
            {
                return service.Status;
            }
        }

        private static void StartServiceAndWait()
        {
            Process startProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "sc.exe",
                    Arguments = "start " + ServiceName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            try
            {
                if (startProcess.Start() && !startProcess.WaitForExit(5000))
                {
                    try { startProcess.Kill(); } catch { }
                }
            }
            finally
            {
                startProcess.Dispose();
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(8);
            using (ServiceController service = new ServiceController(ServiceName))
            {
                while (DateTime.UtcNow < deadline)
                {
                    service.Refresh();
                    if (service.Status == ServiceControllerStatus.Running)
                        return;
                    Thread.Sleep(250);
                }
            }
        }

        private static Exception Unwrap(Exception exception)
        {
            while (exception is TargetInvocationException && exception.InnerException != null)
            {
                exception = exception.InnerException;
            }

            return exception;
        }
    }

    public sealed class IntelXtuProbeResult
    {
        public bool ServiceInstalled { get; internal set; }
        public ServiceControllerStatus ServiceState { get; internal set; }
        public bool SdkFound { get; internal set; }
        public string SdkDirectory { get; internal set; }
        public bool Initialized { get; internal set; }
        public string Error { get; internal set; }
        public List<IntelXtuControlInfo> Controls { get; } = new List<IntelXtuControlInfo>();
        public List<IntelXtuControlInfo> PowerControls { get; internal set; } = new List<IntelXtuControlInfo>();
    }

    public sealed class IntelXtuPowerApplyResult
    {
        public bool Applied { get; internal set; }
        public string Error { get; internal set; }
        public string SdkDirectory { get; internal set; }
        public decimal RequestedPl1Watts { get; internal set; }
        public decimal RequestedPl2Watts { get; internal set; }
        public uint RequestedTimeSeconds { get; internal set; }
        public decimal AppliedPl1Watts { get; internal set; }
        public decimal AppliedPl2Watts { get; internal set; }
        public uint AppliedTimeSeconds { get; internal set; }
    }

    public sealed class IntelXtuControlInfo
    {
        public uint Id { get; internal set; }
        public string Name { get; internal set; }
        public string Category { get; internal set; }
        public string Description { get; internal set; }
        public string Units { get; internal set; }
        public bool Enabled { get; internal set; }
        public bool ReadOnly { get; internal set; }
        public bool RequiresReboot { get; internal set; }
        public decimal ActiveValue { get; internal set; }
        public decimal DefaultValue { get; internal set; }
        public string MinValue { get; internal set; }
        public string MaxValue { get; internal set; }
    }
}
