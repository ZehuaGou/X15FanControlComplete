namespace X15FanCore.Control
{
    /// <summary>
    /// 四档安全功耗预设的唯一权威定义。主程序、启动档位识别、UI 显示与
    /// X15XtuBridge 白名单共享此定义（桥接项目以源码链接方式引用本文件）。
    /// 功耗值不允许通过配置任意修改：时间窗口与驻留参数可以配置，但
    /// PL1/PL2/TimeSeconds/性能上限必须命中这四组安全预设，防止绕过桥接
    /// 程序的安全白名单写入任意功耗。
    /// </summary>
    public static class AdaptivePowerPresets
    {
        public sealed class Preset
        {
            public decimal Pl1Watts { get; private set; }
            public decimal Pl2Watts { get; private set; }
            public uint TimeSeconds { get; private set; }
            public int WindowsMaximumPerformancePercent { get; private set; }

            internal Preset(decimal pl1, decimal pl2, uint timeSeconds, int maximumPerformancePercent)
            {
                Pl1Watts = pl1;
                Pl2Watts = pl2;
                TimeSeconds = timeSeconds;
                WindowsMaximumPerformancePercent = maximumPerformancePercent;
            }
        }

        public static readonly Preset Quiet = new Preset(25m, 35m, 28, 75);
        public static readonly Preset Daily = new Preset(30m, 45m, 28, 85);
        public static readonly Preset Code = new Preset(38m, 55m, 28, 95);
        public static readonly Preset Heavy = new Preset(55m, 69m, 28, 100);

        // 桥接程序安全白名单：只接受四组固定预设（PL1/PL2/TimeSeconds 全部匹配）。
        public static bool IsSafePreset(decimal pl1, decimal pl2, uint timeSeconds)
        {
            return (pl1 == Quiet.Pl1Watts && pl2 == Quiet.Pl2Watts && timeSeconds == Quiet.TimeSeconds) ||
                   (pl1 == Daily.Pl1Watts && pl2 == Daily.Pl2Watts && timeSeconds == Daily.TimeSeconds) ||
                   (pl1 == Code.Pl1Watts && pl2 == Code.Pl2Watts && timeSeconds == Code.TimeSeconds) ||
                   (pl1 == Heavy.Pl1Watts && pl2 == Heavy.Pl2Watts && timeSeconds == Heavy.TimeSeconds);
        }
    }
}
