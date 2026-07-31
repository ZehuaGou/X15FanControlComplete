using System;
using System.Globalization;
using System.ServiceProcess;
using System.Text;
using X15FanCore.Control;

namespace X15XtuBridge
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                // Keep diagnostic key/value output lossless when the parent
                // controller redirects this helper's stdout.
                Console.OutputEncoding = Encoding.UTF8;

                if (args.Length > 0 && string.Equals(args[0], "--apply-cpu-power", StringComparison.OrdinalIgnoreCase))
                {
                    if (args.Length != 4)
                    {
                        Write("ERROR", "用法：--apply-cpu-power <PL1瓦> <PL2瓦> <时间秒>");
                        return 1;
                    }

                    decimal pl1;
                    decimal pl2;
                    uint timeSeconds;
                    if (!decimal.TryParse(args[1], NumberStyles.Number, CultureInfo.InvariantCulture, out pl1) ||
                        !decimal.TryParse(args[2], NumberStyles.Number, CultureInfo.InvariantCulture, out pl2) ||
                        !uint.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out timeSeconds))
                    {
                        Write("ERROR", "功耗参数格式无效。");
                        return 1;
                    }

                    if (!IsFixedStrategyPreset(pl1, pl2, timeSeconds))
                    {
                        Write("ERROR", "power preset is not one of the fixed safe strategies");
                        return 3;
                    }

                    ControlCenterDchuPowerApplyResult dchu = new ControlCenterDchuPowerBackend()
                        .ApplyCpuPowerLimits(pl1, pl2, timeSeconds);
                    Write("BACKEND", "ControlCenter-DCHU");
                    Write("APPLIED", dchu.Applied);
                    Write("SDK_DIRECTORY", dchu.SdkDirectory);
                    Write("REQUESTED_PL1_WATTS", pl1.ToString(CultureInfo.InvariantCulture));
                    Write("REQUESTED_PL2_WATTS", pl2.ToString(CultureInfo.InvariantCulture));
                    Write("REQUESTED_TIME_SECONDS", timeSeconds);
                    if (dchu.Applied)
                    {
                        Write("APPLIED_PL1_WATTS", dchu.AppliedPl1Watts.ToString(CultureInfo.InvariantCulture));
                        Write("APPLIED_PL2_WATTS", dchu.AppliedPl2Watts.ToString(CultureInfo.InvariantCulture));
                        Write("APPLIED_TIME_SECONDS", dchu.AppliedTimeSeconds);
                    }
                    if (!string.IsNullOrEmpty(dchu.Error))
                        Write("DCHU_ERROR", dchu.Error);
                    if (dchu.Applied)
                        return 0;

                    // Keep the older XTU SDK as a bounded fallback for OEM
                    // packages that do not expose DCHU power methods.
                    if (!IsServiceRunning("XTU3SERVICE"))
                    {
                        Write("XTU_SKIPPED", "XTU3SERVICE 未运行，避免进入已知可能阻塞的旧 SDK 路径。");
                        return 2;
                    }
                    IntelXtuPowerApplyResult applied = new IntelXtuPowerBackend()
                        .ApplyCpuPowerLimits(pl1, pl2, timeSeconds);
                    Write("XTU_APPLIED", applied.Applied);
                    if (!string.IsNullOrEmpty(applied.Error))
                        Write("XTU_ERROR", applied.Error);
                    return applied.Applied ? 0 : 2;
                }

                ControlCenterDchuProbeResult dchuProbe = new ControlCenterDchuPowerBackend().ProbePowerLimits();
                Write("DCHU_AVAILABLE", dchuProbe.Available);
                Write("DCHU_SDK_DIRECTORY", dchuProbe.SdkDirectory);
                if (dchuProbe.Available)
                {
                    Write("DCHU_POWER_MODE", dchuProbe.PowerMode);
                    Write("DCHU_PL1_WATTS", dchuProbe.Pl1Watts);
                    Write("DCHU_PL2_WATTS", dchuProbe.Pl2Watts);
                    Write("DCHU_TIME_SECONDS", dchuProbe.TimeSeconds);
                    return 0;
                }
                if (!string.IsNullOrEmpty(dchuProbe.Error))
                    Write("DCHU_ERROR", dchuProbe.Error);

                // If the service is already running, the legacy SDK probe is
                // known to block on this machine. Do not enter it during GUI
                // startup; the DCHU result above is the safe probe.
                if (IsServiceRunning("XTU3SERVICE"))
                {
                    Write("ERROR", "Control Center DCHU 不可用；XTU 服务已运行但跳过可能阻塞的旧 SDK 探测。");
                    return 2;
                }

                bool startServiceForProbe = args.Length > 0 &&
                    string.Equals(args[0], "--probe-start-service", StringComparison.OrdinalIgnoreCase);
                // Startup probing remains side-effect free by default.  The
                // explicit --probe-start-service action only starts the OEM
                // service and enumerates controls; it never calls Tune.
                IntelXtuProbeResult result = new IntelXtuPowerBackend().Probe(startService: startServiceForProbe);
                Write("SDK_FOUND", result.SdkFound);
                Write("SDK_DIRECTORY", result.SdkDirectory);
                Write("SERVICE_INSTALLED", result.ServiceInstalled);
                Write("SERVICE_STATE", result.ServiceState.ToString());
                Write("INITIALIZED", result.Initialized);
                Write("CONTROL_COUNT", result.Controls.Count);
                Write("POWER_CONTROL_COUNT", result.PowerControls.Count);

                foreach (IntelXtuControlInfo control in result.PowerControls)
                {
                    Console.WriteLine(
                        "POWER_CONTROL id=" + control.Id +
                        " name=" + (control.Name ?? string.Empty) +
                        " units=" + (control.Units ?? string.Empty) +
                        " active=" + control.ActiveValue +
                        " default=" + control.DefaultValue +
                        " min=" + (control.MinValue ?? string.Empty) +
                        " max=" + (control.MaxValue ?? string.Empty) +
                        " readonly=" + control.ReadOnly +
                        " reboot=" + control.RequiresReboot);
                }

                if (startServiceForProbe)
                {
                    foreach (IntelXtuControlInfo control in result.Controls)
                    {
                        Console.WriteLine(
                            "CONTROL id=" + control.Id +
                            " name=" + (control.Name ?? string.Empty) +
                            " category=" + (control.Category ?? string.Empty) +
                            " units=" + (control.Units ?? string.Empty) +
                            " active=" + control.ActiveValue +
                            " default=" + control.DefaultValue +
                            " min=" + (control.MinValue ?? string.Empty) +
                            " max=" + (control.MaxValue ?? string.Empty) +
                            " readonly=" + control.ReadOnly);
                    }
                }

                if (!string.IsNullOrEmpty(result.Error))
                {
                    Write("ERROR", result.Error);
                }

                // A failed optional probe must not be interpreted as a hardware
                // write failure by the parent.  Exit code 2 means unavailable.
                return result.Initialized ? 0 : 2;
            }
            catch (Exception exception)
            {
                Write("ERROR", exception.Message);
                return 1;
            }
        }

        private static void Write(string key, object value)
        {
            Console.WriteLine(key + "=" + (value ?? string.Empty));
        }

        private static bool IsFixedStrategyPreset(decimal pl1, decimal pl2, uint timeSeconds)
        {
            return timeSeconds == 28 &&
                ((pl1 == 25m && pl2 == 35m) ||
                 (pl1 == 30m && pl2 == 45m) ||
                 (pl1 == 38m && pl2 == 55m) ||
                 (pl1 == 55m && pl2 == 69m));
        }

        private static bool IsServiceRunning(string name)
        {
            try
            {
                using (ServiceController service = new ServiceController(name))
                    return service.Status == ServiceControllerStatus.Running;
            }
            catch
            {
                return false;
            }
        }
    }
}
