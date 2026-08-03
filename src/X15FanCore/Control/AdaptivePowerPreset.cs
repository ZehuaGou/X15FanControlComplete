using X15FanCore.Models;

namespace X15FanCore.Control
{
    public sealed class AdaptivePowerPreset
    {
        public decimal Pl1Watts { get; private set; }
        public decimal Pl2Watts { get; private set; }
        public uint TimeSeconds { get; private set; }
        public int WindowsMaximumPerformancePercent { get; private set; }

        // 兼容入口：使用默认配置。生产路径必须传真实配置，禁止硬编码。
        public static AdaptivePowerPreset For(AdaptivePowerTier tier)
        {
            return For(tier, null);
        }

        // 功耗参数固定来自 AdaptivePowerPresets 安全预设（与桥接白名单共享
        // 同一定义），不允许用配置中的任意功耗值绕过桥接安全校验。
        public static AdaptivePowerPreset For(AdaptivePowerTier tier, AdaptivePowerSettings settings)
        {
            AdaptivePowerPresets.Preset preset;
            switch (tier)
            {
                case AdaptivePowerTier.Quiet:
                    preset = AdaptivePowerPresets.Quiet;
                    break;
                case AdaptivePowerTier.Code:
                    preset = AdaptivePowerPresets.Code;
                    break;
                case AdaptivePowerTier.Heavy:
                    preset = AdaptivePowerPresets.Heavy;
                    break;
                default:
                    preset = AdaptivePowerPresets.Daily;
                    break;
            }
            return new AdaptivePowerPreset
            {
                Pl1Watts = preset.Pl1Watts,
                Pl2Watts = preset.Pl2Watts,
                TimeSeconds = preset.TimeSeconds,
                WindowsMaximumPerformancePercent = preset.WindowsMaximumPerformancePercent
            };
        }
    }
}
