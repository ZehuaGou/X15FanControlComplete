using System.ComponentModel;
using System.Runtime.Serialization;

namespace X15FanCore.Models
{
    [DataContract]
    public sealed class AdaptivePowerSettings
    {
        public AdaptivePowerSettings()
        {
            Enabled = true;
            DailyPl1Watts = 30m;
            DailyPl2Watts = 45m;
            DailyWindowsMaximumPerformancePercent = 85;
            CodePl1Watts = 38m;
            CodePl2Watts = 55m;
            CodeWindowsMaximumPerformancePercent = 95;
            HeavyPl1Watts = 55m;
            HeavyPl2Watts = 68.75m;
            HeavyWindowsMaximumPerformancePercent = 100;
            TimeSeconds = 28;
            UpshiftDwellSeconds = 30;
            DownshiftDwellSeconds = 120;
        }

        [DataMember(Order = 0), DisplayName("启用自动功耗档位"), Category("自动策略"),
         Description("活动模式下根据持续负载自动切换日常、代码、重负载三档。")]
        public bool Enabled { get; set; }

        [DataMember(Order = 1), DisplayName("日常 PL1 长时功耗 (W)"), Category("日常")]
        public decimal DailyPl1Watts { get; set; }

        [DataMember(Order = 2), DisplayName("日常 PL2 短时功耗 (W)"), Category("日常")]
        public decimal DailyPl2Watts { get; set; }

        [DataMember(Order = 3), DisplayName("日常 CPU 性能上限 (%)"), Category("日常")]
        public int DailyWindowsMaximumPerformancePercent { get; set; }

        [DataMember(Order = 4), DisplayName("代码 PL1 长时功耗 (W)"), Category("代码")]
        public decimal CodePl1Watts { get; set; }

        [DataMember(Order = 5), DisplayName("代码 PL2 短时功耗 (W)"), Category("代码")]
        public decimal CodePl2Watts { get; set; }

        [DataMember(Order = 6), DisplayName("代码 CPU 性能上限 (%)"), Category("代码")]
        public int CodeWindowsMaximumPerformancePercent { get; set; }

        [DataMember(Order = 7), DisplayName("重负载 PL1 长时功耗 (W)"), Category("重负载")]
        public decimal HeavyPl1Watts { get; set; }

        [DataMember(Order = 8), DisplayName("重负载 PL2 短时功耗 (W)"), Category("重负载")]
        public decimal HeavyPl2Watts { get; set; }

        [DataMember(Order = 9), DisplayName("重负载 CPU 性能上限 (%)"), Category("重负载")]
        public int HeavyWindowsMaximumPerformancePercent { get; set; }

        [DataMember(Order = 10), DisplayName("PL2 时间窗口 (秒)"), Category("自动策略")]
        public uint TimeSeconds { get; set; }

        [DataMember(Order = 11), DisplayName("升档持续时间 (秒)"), Category("自动策略"),
         Description("负载超过阈值持续这么久才升档，防止瞬时尖峰触发。")]
        public int UpshiftDwellSeconds { get; set; }

        [DataMember(Order = 12), DisplayName("降档持续时间 (秒)"), Category("自动策略"),
         Description("负载降低持续这么久才降档，防止功耗和风扇来回跳变。")]
        public int DownshiftDwellSeconds { get; set; }

        public void Normalize()
        {
            DailyPl1Watts = Clamp(DailyPl1Watts, 5m, 100m);
            DailyPl2Watts = Clamp(DailyPl2Watts, DailyPl1Watts, 125m);
            CodePl1Watts = Clamp(CodePl1Watts, DailyPl1Watts, 100m);
            CodePl2Watts = Clamp(CodePl2Watts, CodePl1Watts, 125m);
            HeavyPl1Watts = Clamp(HeavyPl1Watts, CodePl1Watts, 100m);
            HeavyPl2Watts = Clamp(HeavyPl2Watts, HeavyPl1Watts, 125m);
            DailyWindowsMaximumPerformancePercent = Clamp(DailyWindowsMaximumPerformancePercent, 5, 100);
            CodeWindowsMaximumPerformancePercent = Clamp(CodeWindowsMaximumPerformancePercent, DailyWindowsMaximumPerformancePercent, 100);
            HeavyWindowsMaximumPerformancePercent = Clamp(HeavyWindowsMaximumPerformancePercent, CodeWindowsMaximumPerformancePercent, 100);
            TimeSeconds = (uint)Clamp((int)TimeSeconds, 1, 256);
            UpshiftDwellSeconds = Clamp(UpshiftDwellSeconds, 10, 300);
            DownshiftDwellSeconds = Clamp(DownshiftDwellSeconds, 30, 900);
        }

        private static decimal Clamp(decimal value, decimal minimum, decimal maximum)
        {
            return value < minimum ? minimum : value > maximum ? maximum : value;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return value < minimum ? minimum : value > maximum ? maximum : value;
        }
    }
}
