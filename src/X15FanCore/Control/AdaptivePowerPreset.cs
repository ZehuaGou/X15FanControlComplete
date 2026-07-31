namespace X15FanCore.Control
{
    public sealed class AdaptivePowerPreset
    {
        public decimal Pl1Watts { get; private set; }
        public decimal Pl2Watts { get; private set; }
        public uint TimeSeconds { get; private set; }
        public int WindowsMaximumPerformancePercent { get; private set; }

        public static AdaptivePowerPreset For(AdaptivePowerTier tier)
        {
            switch (tier)
            {
                case AdaptivePowerTier.Quiet:
                    return new AdaptivePowerPreset { Pl1Watts = 25, Pl2Watts = 35, TimeSeconds = 28, WindowsMaximumPerformancePercent = 75 };
                case AdaptivePowerTier.Code:
                    return new AdaptivePowerPreset { Pl1Watts = 38, Pl2Watts = 55, TimeSeconds = 28, WindowsMaximumPerformancePercent = 95 };
                case AdaptivePowerTier.Heavy:
                    return new AdaptivePowerPreset { Pl1Watts = 55, Pl2Watts = 69, TimeSeconds = 28, WindowsMaximumPerformancePercent = 100 };
                default:
                    return new AdaptivePowerPreset { Pl1Watts = 30, Pl2Watts = 45, TimeSeconds = 28, WindowsMaximumPerformancePercent = 85 };
            }
        }
    }
}
