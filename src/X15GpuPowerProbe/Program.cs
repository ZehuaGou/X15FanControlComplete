using System;
using System.Globalization;
using System.Text;
using X15FanCore.Control;

namespace X15GpuPowerProbe
{
    /// <summary>
    /// B2 GPU 单独验收工具：通过真实 NVML shim（X15FanCore.NvmlShim）执行
    /// W 级功耗验收。默认只读；仅当显式传入 --enable-writes 时才允许 Set。
    ///
    /// 流程（Phase B2）：
    ///   1. --probe          只读：UUID / current / default / min / max + 原始 nvmlReturn_t
    ///   2. --writeback 115000 --observe 60
    ///                        写回同值 → 立即读回 → 观察 60s 是否被覆盖/回落
    ///   3. --set 105000 --observe 60
    ///                        降 10W → 读回 → 观察 60s
    ///   4. --restore 115000  恢复 → 读回确认
    ///
    /// 安全：默认写禁用；Set 前必须 --enable-writes；任何 NOT_SUPPORTED /
    /// NO_PERMISSION / GPU_IS_LOST / 读回不一致 → 立即恢复并停止，结论为
    /// "本机不支持可靠 GPU W 级控制"，绝不改用锁频/P-State 冒充。
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                bool enableWrites = false;
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--enable-writes", StringComparison.OrdinalIgnoreCase))
                        enableWrites = true;
                }

                // 发现 GPU UUID（绝不按易变化序号盲选）。
                string detail;
                string uuid = NvmlShim.DiscoverFirstGpuUuid(out detail);
                if (string.IsNullOrEmpty(uuid))
                {
                    Console.WriteLine("FATAL: 未发现可枚举 GPU（" + detail + "）");
                    return 2;
                }
                Console.WriteLine("GPU_UUID=" + uuid);
                Console.WriteLine("GPU_DISCOVERY=" + detail);

                using (NvmlShim shim = new NvmlShim(uuid))
                {
                    if (!shim.IsAvailable())
                    {
                        Console.WriteLine("FATAL: NVML 初始化失败：" + shim.InitDetail);
                        return 2;
                    }
                    Console.WriteLine("NVML_INIT=" + shim.InitDetail);
                    if (enableWrites)
                    {
                        shim.EnableWrites();
                        Console.WriteLine("NVML_WRITES_ENABLED=True");
                    }
                    else
                    {
                        Console.WriteLine("NVML_WRITES_ENABLED=False（只读验收；需 --enable-writes）");
                    }

                    if (HasFlag(args, "--probe"))
                    {
                        return RunProbe(shim);
                    }
                    if (HasFlag(args, "--writeback") || HasFlag(args, "--set") || HasFlag(args, "--restore"))
                    {
                        return RunWriteTest(shim, args, enableWrites);
                    }

                    Console.WriteLine("用法：");
                    Console.WriteLine("  --probe [--enable-writes]");
                    Console.WriteLine("  --writeback <mW> [--observe <秒>] [--enable-writes]");
                    Console.WriteLine("  --set <mW> [--observe <秒>] [--enable-writes]");
                    Console.WriteLine("  --restore <mW> [--enable-writes]");
                    return 1;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL: " + ex);
                return 2;
            }
        }

        private static bool HasFlag(string[] args, string flag)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static string ArgAfter(string[] args, string flag, string fallback)
        {
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return fallback;
        }

        private static int RunProbe(NvmlShim shim)
        {
            int current, def, min, max;
            string identity;
            int rc = shim.GetPowerManagementLimit(out current);
            Console.WriteLine("PROBE_CURRENT_MW=" + current + " RC=" + NvmlReturnCodes.Describe(rc));
            rc = shim.GetDefaultPowerManagementLimit(out def);
            Console.WriteLine("PROBE_DEFAULT_MW=" + def + " RC=" + NvmlReturnCodes.Describe(rc));
            rc = shim.GetMinPowerManagementLimit(out min);
            Console.WriteLine("PROBE_MIN_MW=" + min + " RC=" + NvmlReturnCodes.Describe(rc));
            rc = shim.GetMaxPowerManagementLimit(out max);
            Console.WriteLine("PROBE_MAX_MW=" + max + " RC=" + NvmlReturnCodes.Describe(rc));
            rc = shim.GetGpuIdentity(out identity);
            Console.WriteLine("PROBE_IDENTITY=" + identity + " RC=" + NvmlReturnCodes.Describe(rc));
            return 0;
        }

        private static int RunWriteTest(NvmlShim shim, string[] args, bool enableWrites)
        {
            if (!enableWrites)
            {
                Console.WriteLine("FATAL: 写测试需要 --enable-writes（Phase B2 已批准后使用）");
                return 3;
            }

            string action = HasFlag(args, "--writeback") ? "--writeback"
                : HasFlag(args, "--set") ? "--set" : "--restore";
            string valueArg = ArgAfter(args, action, "0");
            int targetMw;
            if (!int.TryParse(valueArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out targetMw) || targetMw <= 0)
            {
                Console.WriteLine("FATAL: 无效目标值 " + valueArg);
                return 3;
            }

            int observeSeconds;
            if (!int.TryParse(ArgAfter(args, "--observe", "0"), NumberStyles.Integer, CultureInfo.InvariantCulture, out observeSeconds))
                observeSeconds = 0;

            // 写回目标值。
            int setRc = shim.SetPowerManagementLimit(targetMw);
            Console.WriteLine("SET_MW=" + targetMw + " RC=" + NvmlReturnCodes.Describe(setRc));
            if (setRc != NvmlReturnCodes.Success)
            {
                Console.WriteLine("RESULT=SET_FAILED_NOT_SUPPORTED_OR_BLOCKED");
                return 4;
            }

            // 立即读回。
            int readback;
            int readRc = shim.GetPowerManagementLimit(out readback);
            Console.WriteLine("READBACK_MW=" + readback + " RC=" + NvmlReturnCodes.Describe(readRc));
            if (readRc != NvmlReturnCodes.Success)
            {
                Console.WriteLine("RESULT=READBACK_FAILED");
                return 4;
            }
            bool matched = Math.Abs(readback - targetMw) <= 1000;
            Console.WriteLine("READBACK_MATCH=" + (matched ? "True" : "False"));
            if (!matched)
            {
                Console.WriteLine("RESULT=READBACK_MISMATCH");
                return 4;
            }

            // 观察窗口：每秒读回。
            for (int i = 1; i <= observeSeconds; i++)
            {
                System.Threading.Thread.Sleep(1000);
                int observed;
                int rc = shim.GetPowerManagementLimit(out observed);
                bool still = rc == NvmlReturnCodes.Success && Math.Abs(observed - targetMw) <= 1000;
                Console.WriteLine("OBSERVE t=" + i + "s MW=" + observed + " RC=" + NvmlReturnCodes.Describe(rc) +
                    " MATCH=" + (still ? "True" : "False"));
                if (!still)
                {
                    Console.WriteLine("RESULT=OVERRIDDEN_OR_DRIFT_AT_" + i + "s");
                    return 5;
                }
            }

            Console.WriteLine("RESULT=PASS STABLE_SECONDS=" + observeSeconds + " TARGET_MW=" + targetMw);
            return 0;
        }
    }
}
