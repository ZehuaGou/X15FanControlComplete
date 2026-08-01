using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;

namespace X15FanCore.Control
{
    /// <summary>
    /// Adapter for the power-limit methods exposed by the installed CLEVO
    /// Control Center background assembly.  The OEM assembly performs the
    /// actual DCHU transaction; this adapter only supplies bounded values and
    /// verifies the readback before reporting success.
    /// </summary>
    public sealed class ControlCenterDchuPowerBackend
    {
        private const string DchuTypeName = "ConsoleCPUOC_Background.DCHU";

        public ControlCenterDchuProbeResult ProbePowerLimits()
        {
            ControlCenterDchuProbeResult result = new ControlCenterDchuProbeResult();
            try
            {
                object dchu = LoadDchuObject(out string sdkDirectory);
                result.SdkDirectory = sdkDirectory;
                result.PowerMode = InvokeInt(dchu, "GetPowerMode");
                result.Pl1Watts = InvokeInt(dchu, "GetPowerLimit1CurrentValue");
                result.Pl2Watts = InvokeInt(dchu, "GetPowerLimit2CurrentValue");
                result.TimeSeconds = InvokeSingle(dchu, "GetPowerLimitTimeValue");
                result.Available = true;
            }
            catch (Exception exception)
            {
                result.Error = Unwrap(exception).Message;
            }
            return result;
        }

        public ControlCenterDchuPowerApplyResult ApplyCpuPowerLimits(
            decimal pl1Watts,
            decimal pl2Watts,
            uint timeSeconds)
        {
            ControlCenterDchuPowerApplyResult result = new ControlCenterDchuPowerApplyResult
            {
                RequestedPl1Watts = pl1Watts,
                RequestedPl2Watts = pl2Watts,
                RequestedTimeSeconds = timeSeconds
            };

            if (pl1Watts < 5m || pl2Watts < pl1Watts || pl2Watts > 125m || timeSeconds == 0 || timeSeconds > 256)
            {
                result.Error = "Control Center 功耗参数超出安全范围。";
                return result;
            }

            object dchu = null;
            int originalPl1 = 0;
            int originalPl2 = 0;
            float originalTime = 0;
            bool originalsRead = false;
            try
            {
                string sdkDirectory;
                dchu = LoadDchuObject(out sdkDirectory);
                result.SdkDirectory = sdkDirectory;
                originalPl1 = InvokeInt(dchu, "GetPowerLimit1CurrentValue");
                originalPl2 = InvokeInt(dchu, "GetPowerLimit2CurrentValue");
                originalTime = InvokeSingle(dchu, "GetPowerLimitTimeValue");
                originalsRead = true;

                InvokeVoid(dchu, "SetPowerLimit1CurrentValue", Convert.ToInt32(Math.Round(pl1Watts)));
                InvokeVoid(dchu, "SetPowerLimit2CurrentValue", Convert.ToInt32(Math.Round(pl2Watts)));
                InvokeVoid(dchu, "SetPowerLimitTimeCurrentValue", Convert.ToSingle(timeSeconds));

                int actualPl1 = InvokeInt(dchu, "GetPowerLimit1CurrentValue");
                int actualPl2 = InvokeInt(dchu, "GetPowerLimit2CurrentValue");
                float actualTime = InvokeSingle(dchu, "GetPowerLimitTimeValue");
                if (Math.Abs(actualPl1 - Math.Round(pl1Watts)) > 1 ||
                    Math.Abs(actualPl2 - Math.Round(pl2Watts)) > 1 ||
                    Math.Abs(actualTime - timeSeconds) > 0.5f)
                {
                    Rollback(dchu, originalPl1, originalPl2, originalTime);
                    result.Error = "Control Center DCHU 功耗写入回读不一致，已恢复原值。";
                    return result;
                }

                result.Applied = true;
                result.AppliedPl1Watts = actualPl1;
                result.AppliedPl2Watts = actualPl2;
                result.AppliedTimeSeconds = actualTime;
                return result;
            }
            catch (Exception exception)
            {
                if (originalsRead && dchu != null)
                    Rollback(dchu, originalPl1, originalPl2, originalTime);
                result.Error = Unwrap(exception).Message;
                return result;
            }
        }

        private static object LoadDchuObject(out string sdkDirectory)
        {
            EnsureDchuDriverReady();
            sdkDirectory = IntelXtuPowerBackend.FindSdkDirectory();
            if (string.IsNullOrEmpty(sdkDirectory))
                throw new FileNotFoundException("未找到当前 Control Center 的 CPUOC SDK 目录。");

            string cpuOcDirectory = Directory.GetParent(sdkDirectory).FullName;
            string backgroundPath = Path.Combine(cpuOcDirectory, "CC30_BG.exe");
            if (!File.Exists(backgroundPath))
                throw new FileNotFoundException("未找到 Control Center 的 CC30_BG.exe。", backgroundPath);

            string nativeSource = File.Exists(Path.Combine(sdkDirectory, "InsydeDCHU.dll"))
                ? Path.Combine(sdkDirectory, "InsydeDCHU.dll")
                : Path.Combine(cpuOcDirectory, "InsydeDCHU.dll");
            if (!File.Exists(nativeSource))
                throw new FileNotFoundException("未找到 Control Center 的 InsydeDCHU.dll。", nativeSource);

            // WindowsApps ACLs can deny LoadLibrary even to an elevated
            // unpackaged process. Stage only this signed OEM native DLL in a
            // private temp directory; the OEM code and exported ABI remain
            // unchanged, and no DLL is copied into the application folder.
            string nativeDirectory = Path.Combine(Path.GetTempPath(), "X15FanControl-DCHU");
            Directory.CreateDirectory(nativeDirectory);
            string stagedNative = Path.Combine(nativeDirectory, "InsydeDCHU.dll");
            File.Copy(nativeSource, stagedNative, true);
            SetDllDirectory(nativeDirectory);

            // Register the SDK dependency resolver exactly once.  The previous
            // per-call registration accumulated AppDomain-wide handlers that
            // were never removed, and Assembly.Load(File.ReadAllBytes(...))
            // created a duplicate assembly instance on every load.  The
            // resolver caches loaded dependencies and refreshes its search
            // directories on each probe so an updated Control Center install
            // is picked up without leaking handlers.
            lock (_resolveLock)
            {
                _resolvedSdkDirectory = sdkDirectory;
                _resolvedCpuOcDirectory = cpuOcDirectory;
                _resolvedAssemblies.Clear();
                if (!_resolverRegistered)
                {
                    AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
                    _resolverRegistered = true;
                }
            }

            Assembly assembly = Assembly.Load(File.ReadAllBytes(backgroundPath));
            Type dchuType = assembly.GetType(DchuTypeName, true);
            return Activator.CreateInstance(dchuType, true);
        }

        private static readonly object _resolveLock = new object();
        private static bool _resolverRegistered;
        private static string _resolvedSdkDirectory;
        private static string _resolvedCpuOcDirectory;
        private static readonly Dictionary<string, Assembly> _resolvedAssemblies =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name + ".dll";
            string[] candidates =
            {
                Path.Combine(_resolvedSdkDirectory ?? string.Empty, name),
                Path.Combine(_resolvedCpuOcDirectory ?? string.Empty, name)
            };
            string dependency = candidates.FirstOrDefault(File.Exists);
            if (dependency == null)
                return null;

            lock (_resolveLock)
            {
                Assembly cached;
                if (_resolvedAssemblies.TryGetValue(name, out cached))
                    return cached;
                Assembly loaded = Assembly.Load(File.ReadAllBytes(dependency));
                _resolvedAssemblies[name] = loaded;
                return loaded;
            }
        }

        private static void EnsureDchuDriverReady()
        {
            using (ServiceController service = new ServiceController("XTUComponent"))
            {
                if (service.Status == ServiceControllerStatus.Running)
                    return;
                service.Start();
                service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(8));
                if (service.Status != ServiceControllerStatus.Running)
                    throw new InvalidOperationException("Control Center 的 XTUComponent 驱动未能启动。");
            }
        }

        private static void Rollback(object dchu, int pl1, int pl2, float time)
        {
            try { InvokeVoid(dchu, "SetPowerLimit1CurrentValue", pl1); } catch { }
            try { InvokeVoid(dchu, "SetPowerLimit2CurrentValue", pl2); } catch { }
            try { InvokeVoid(dchu, "SetPowerLimitTimeCurrentValue", time); } catch { }
        }

        private static int InvokeInt(object instance, string methodName)
        {
            object value = FindMethod(instance, methodName).Invoke(instance, new object[0]);
            return Convert.ToInt32(value);
        }

        private static float InvokeSingle(object instance, string methodName)
        {
            object value = FindMethod(instance, methodName).Invoke(instance, new object[0]);
            return Convert.ToSingle(value);
        }

        private static void InvokeVoid(object instance, string methodName, object value)
        {
            FindMethod(instance, methodName).Invoke(instance, new[] { value });
        }

        private static MethodInfo FindMethod(object instance, string methodName)
        {
            MethodInfo method = instance.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, methodName, StringComparison.Ordinal));
            if (method == null)
                throw new MissingMethodException(instance.GetType().FullName, methodName);
            return method;
        }

        private static Exception Unwrap(Exception exception)
        {
            while (exception is TargetInvocationException && exception.InnerException != null)
                exception = exception.InnerException;
            return exception;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string pathName);
    }

    public sealed class ControlCenterDchuProbeResult
    {
        public bool Available { get; internal set; }
        public string SdkDirectory { get; internal set; }
        public int PowerMode { get; internal set; }
        public int Pl1Watts { get; internal set; }
        public int Pl2Watts { get; internal set; }
        public float TimeSeconds { get; internal set; }
        public string Error { get; internal set; }
    }

    public sealed class ControlCenterDchuPowerApplyResult
    {
        public bool Applied { get; internal set; }
        public string SdkDirectory { get; internal set; }
        public decimal RequestedPl1Watts { get; internal set; }
        public decimal RequestedPl2Watts { get; internal set; }
        public uint RequestedTimeSeconds { get; internal set; }
        public int AppliedPl1Watts { get; internal set; }
        public int AppliedPl2Watts { get; internal set; }
        public float AppliedTimeSeconds { get; internal set; }
        public string Error { get; internal set; }
    }
}
