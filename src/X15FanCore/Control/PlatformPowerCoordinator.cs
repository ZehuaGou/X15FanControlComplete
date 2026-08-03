using System;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    /// <summary>
    /// 协调器热/声学输入：由调用方（AcousticGovernor 每通道结果）提供。
    /// 共享热影响证据（GpuTemperatureNearLimit / GpuFanAtSoftMaximum /
    /// GpuTemperatureNotFalling / CpuTemperatureWorsening）只在 GPU 热饱和
    /// 时判定是否允许降低 CPU 有效功耗档——单纯 GPU 利用率高不构成降低
    /// CPU 功耗的证据（架构收束 2026-08-02）。
    /// </summary>
    public sealed class CoolingStateInput
    {
        public bool CpuSaturated;      // CPU 通道热饱和（AcousticGovernor 判定）
        public bool GpuSaturated;      // GPU 通道热饱和
        public bool Emergency;         // 整机 Emergency（任一通道紧急散热）
        // 共享热影响证据（全部满足才允许因 GPU 热饱和降低 CPU 档位）：
        public bool GpuTemperatureNearLimit;   // GPU 温度接近上限
        public bool GpuFanAtSoftMaximum;       // GPU 主风扇达到软上限
        public bool GpuTemperatureNotFalling;  // GPU 温度持续不下降
        public bool CpuTemperatureWorsening;   // CPU 温度同步恶化
        // 共享热预算让出（SharedThermalBudgetController 判定，仅 Auto 模式）：
        // GPU 接近热上限 + GPU 风扇接近全速 + CPU 同步高热持续 20s → CPU 有效
        // 功耗至多 Quiet（25/35W），为 GPU 与共享热管让出热预算。
        public bool SharedThermalSheddingActive;
    }

    public sealed class CoordinatorDecision
    {
        // 输出只有 CPU 有效功耗档：GPU 没有有效功率档（TelemetryOnly）。
        public AdaptivePowerTier CpuEffective;
        // 风扇 profile 档位：与 CPU 功耗档解耦——共享热预算让出把功耗压到
        // Quiet 时，风扇曲线必须保持让出前档位（不套用 Quiet 曲线）。
        public AdaptivePowerTier CpuFanProfileTier;
        public string Reason;
    }

    public interface IPlatformPowerCoordinator
    {
        // 原子解析：一次输入 CPU 请求 + 热状态，返回 CPU effective；
        // 结果与调用顺序无关。
        CoordinatorDecision Resolve(
            AdaptivePowerTier cpuRequested,
            CoolingStateInput thermal);

        // 仅重置协调器枚举状态；不是硬件恢复。
        void ResetCoordinatorState();
    }

    /// <summary>
    /// CPU 功耗协调器（架构收束 2026-08-02 + 共享热预算让出 2026-08-03）。
    ///
    /// - 删除了整机瓦数预算仲裁：测试专用 GPU 瓦数估算表不得进入运行时。
    /// - 删除了 GpuEffective：GPU 无有效功率档。
    /// - CPU 有效功耗在以下情况被治理器主动降低：
    ///   1) CPU 自身热饱和；2) 明确的整机 Emergency（CPU 不得升高）；
    ///   3) GPU 热饱和且共享热影响证据全部成立；
    ///   4) 共享热预算让出（SharedThermalSheddingActive，仅 Auto 模式）：CPU
    ///      有效功耗至多 Quiet，为 GPU 与共享热管让出热预算；风扇 profile
    ///      档位（CpuFanProfileTier）保持让出前结果，与功耗档解耦。
    /// - 单纯 GPU 利用率高不构成降低 CPU 功耗的证据。
    /// </summary>
    public sealed class PlatformPowerCoordinator : IPlatformPowerCoordinator
    {
        private AdaptivePowerTier _lastCpu = AdaptivePowerTier.Daily;

        public CoordinatorDecision Resolve(
            AdaptivePowerTier cpuRequested,
            CoolingStateInput thermal)
        {
            if (thermal == null)
                thermal = new CoolingStateInput();

            // 先计算"非让出"基线（Emergency / 热饱和 / 共享热影响证据）。
            AdaptivePowerTier baseTier = cpuRequested;
            string reason = "无热限制";
            bool emergency = thermal.Emergency;

            // Emergency：CPU 不得升档（只能保持或降）。
            if (emergency)
            {
                baseTier = TierPower.MinPowerTier(baseTier, _lastCpu);
                reason = "紧急状态：CPU 功耗不得升高";
            }
            else
            {
                // 1) CPU 自身热饱和：只降 CPU。
                if (thermal.CpuSaturated && TierPower.IsHigherPowerTier(baseTier, AdaptivePowerTier.Quiet))
                {
                    baseTier = TierPower.LowerTier(baseTier);
                    reason = "CPU 通道热饱和，CPU 有效档降低";
                }

                // 2) GPU 热饱和 + 共享热影响证据全部成立：CPU 有效档降低。
                if (thermal.GpuSaturated &&
                    thermal.GpuTemperatureNearLimit &&
                    thermal.GpuFanAtSoftMaximum &&
                    thermal.GpuTemperatureNotFalling &&
                    thermal.CpuTemperatureWorsening &&
                    TierPower.IsHigherPowerTier(baseTier, AdaptivePowerTier.Quiet))
                {
                    baseTier = TierPower.LowerTier(baseTier);
                    reason = reason.StartsWith("CPU", StringComparison.Ordinal)
                        ? reason + "；GPU 热饱和且共享热影响证据成立，CPU 有效档再降"
                        : "GPU 热饱和且共享热影响证据成立（GPU 温度接近上限/主风扇到软上限/温度不降/CPU 同步恶化），CPU 有效档降低";
                }
            }

            // 风扇 profile 档位 = 非让出基线（让出只压功耗，不压风扇曲线）。
            AdaptivePowerTier fanProfileTier = baseTier;

            // 3) 共享热预算让出：CPU 有效功耗至多 Quiet（安全预设 25/35/28W）。
            //    在 Emergency/热饱和结果之上叠加（Emergency 语义不回归：CPU
            //    仍不得升高；风扇仍可经紧急路径双侧突破 100%）。
            AdaptivePowerTier effective = baseTier;
            if (thermal.SharedThermalSheddingActive)
            {
                effective = TierPower.MinPowerTier(baseTier, AdaptivePowerTier.Quiet);
                reason = "共享热预算让出：CPU 有效功耗压至 Quiet（25/35W），风扇 profile 保持 " +
                    GetTierName(fanProfileTier) + " 档";
            }

            _lastCpu = effective;
            return new CoordinatorDecision
            {
                CpuEffective = effective,
                CpuFanProfileTier = fanProfileTier,
                Reason = reason
            };
        }

        // 仅重置协调器枚举状态；不是硬件恢复。
        public void ResetCoordinatorState()
        {
            _lastCpu = AdaptivePowerTier.Daily;
        }

        private static string GetTierName(AdaptivePowerTier tier)
        {
            switch (tier)
            {
                case AdaptivePowerTier.Quiet: return "安静";
                case AdaptivePowerTier.Code: return "代码";
                case AdaptivePowerTier.Heavy: return "重负载";
                default: return "日常";
            }
        }
    }
}
