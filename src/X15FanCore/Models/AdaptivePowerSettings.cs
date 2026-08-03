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
            HeavyPl2Watts = 69m;
            HeavyWindowsMaximumPerformancePercent = 100;
            TimeSeconds = 28;
            // v2 遗留字段：仅用于 v2->v3 配置迁移，运行时不再读取。
            UpshiftDwellSeconds = 30;
            DownshiftDwellSeconds = 120;
            // v3 显式字段：运行时状态机的唯一参数来源。
            QuietToDailyDwellSeconds = 15;
            DailyToCodeDwellSeconds = 30;
            CodeToHeavyDwellSeconds = 30;
            HeavyToCodeDwellSeconds = 60;
            CodeToDailyDwellSeconds = 120;
            DailyToQuietDwellSeconds = 120;
            StrongUpshiftDwellSeconds = 12;
            MinimumTierHoldSeconds = 20;
            UpshiftEvidenceWindowSeconds = 8;
            DownshiftAverageWindowSeconds = 30;
            RecentPeakWindowSeconds = 15;
            // 声学/热治理参数（候选值待硬件标定）：
            // 温升率超过该值(°C/s)时风扇可突破声学软上限（快速升温安全）。
            FastRiseBreakthroughCPerSecond = 1.0;
            // 风扇顶住软上限且温度持续上升达到该时长 → 热饱和，禁止提高有效功耗。
            ThermalSaturationDwellSeconds = 20;
            // 热饱和/受限后，温度稳定下降且风扇低于软上限 RecoveryMarginPercent
            // 并持续该时长 → 恢复正常。
            RecoveryDwellSeconds = 90;
            RecoveryMarginPercent = 5;
            // 跨风扇辅助（架构收束 2026-08-02，主扇优先、辅助延迟介入）：
            // 辅助量 = 主风扇目标 × AssistRatio（候选 20%~30%，未标定）。
            CrossFanAssistEnabled = true;
            CrossFanAssistRatioPercent = 25;
            CrossFanAssistEngageSeconds = 20;
            CrossFanAssistExitStableSeconds = 60;
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
         Description("v2 遗留字段，仅用于配置迁移。v3 起由各档位独立驻留时间字段取代。")]
        public int UpshiftDwellSeconds { get; set; }

        [DataMember(Order = 12), DisplayName("降档持续时间 (秒)"), Category("自动策略"),
         Description("v2 遗留字段，仅用于配置迁移。v3 起由各档位独立驻留时间字段取代。")]
        public int DownshiftDwellSeconds { get; set; }

        [DataMember(Order = 13), DisplayName("安静→日常 驻留 (秒)"), Category("自动策略")]
        public int QuietToDailyDwellSeconds { get; set; }

        [DataMember(Order = 14), DisplayName("日常→代码 驻留 (秒)"), Category("自动策略")]
        public int DailyToCodeDwellSeconds { get; set; }

        [DataMember(Order = 15), DisplayName("代码→重负载 驻留 (秒)"), Category("自动策略")]
        public int CodeToHeavyDwellSeconds { get; set; }

        [DataMember(Order = 16), DisplayName("重负载→代码 驻留 (秒)"), Category("自动策略")]
        public int HeavyToCodeDwellSeconds { get; set; }

        [DataMember(Order = 17), DisplayName("代码→日常 驻留 (秒)"), Category("自动策略")]
        public int CodeToDailyDwellSeconds { get; set; }

        [DataMember(Order = 18), DisplayName("日常→安静 驻留 (秒)"), Category("自动策略")]
        public int DailyToQuietDwellSeconds { get; set; }

        [DataMember(Order = 19), DisplayName("强升档驻留 (秒)"), Category("自动策略"),
         Description("CPU≥80% 或 GPU≥70% 的强负载升档所需持续时间。")]
        public int StrongUpshiftDwellSeconds { get; set; }

        [DataMember(Order = 20), DisplayName("档位最短保持 (秒)"), Category("自动策略")]
        public int MinimumTierHoldSeconds { get; set; }

        [DataMember(Order = 21), DisplayName("升档证据窗口 (秒)"), Category("自动策略")]
        public int UpshiftEvidenceWindowSeconds { get; set; }

        [DataMember(Order = 22), DisplayName("降档均值窗口 (秒)"), Category("自动策略")]
        public int DownshiftAverageWindowSeconds { get; set; }

        [DataMember(Order = 23), DisplayName("近期峰值窗口 (秒)"), Category("自动策略")]
        public int RecentPeakWindowSeconds { get; set; }

        [DataMember(Order = 24), DisplayName("快速升温突破温升率 (°C/s)"), Category("声学预算"),
         Description("温升率超过该值时风扇可突破声学软上限，保证快速升温安全。")]
        public double FastRiseBreakthroughCPerSecond { get; set; }

        [DataMember(Order = 25), DisplayName("热饱和判定时长 (秒)"), Category("声学预算"),
         Description("风扇顶住软上限且温度持续上升达到该时长后，禁止提高有效功耗档位。")]
        public int ThermalSaturationDwellSeconds { get; set; }

        [DataMember(Order = 26), DisplayName("热恢复判定时长 (秒)"), Category("声学预算"),
         Description("温度稳定下降且风扇低于软上限足够余量并持续该时长后，恢复正常。")]
        public int RecoveryDwellSeconds { get; set; }

        [DataMember(Order = 27), DisplayName("恢复余量 (%)"), Category("声学预算"),
         Description("风扇低于软上限至少该余量才允许判定热恢复。")]
        public int RecoveryMarginPercent { get; set; }

        [DataMember(Order = 28), DisplayName("跨风扇辅助启用"), Category("跨风扇辅助"),
         Description("主扇优先、辅助延迟介入：一侧热源先由其主风扇负责，另一侧风扇仅在满足持续证据后提供 20%~30% 辅助量（候选值未标定）。")]
        public bool CrossFanAssistEnabled { get; set; }

        [DataMember(Order = 29), DisplayName("辅助量比例 (%)"), Category("跨风扇辅助"),
         Description("辅助量 = 主风扇目标 × 该比例（候选 20%~30%，未硬件标定前不得宣称最终值）。")]
        public int CrossFanAssistRatioPercent { get; set; }

        [DataMember(Order = 30), DisplayName("辅助介入持续时长 (秒)"), Category("跨风扇辅助"),
         Description("主通道温度接近目标、主风扇接近软上限且温度持续不明显下降达到该时长才允许辅助介入（排除短暂尖峰）。")]
        public int CrossFanAssistEngageSeconds { get; set; }

        [DataMember(Order = 31), DisplayName("辅助退出稳定时长 (秒)"), Category("跨风扇辅助"),
         Description("温度恢复余量且温升率 <= 0 并持续该时长后辅助退出（滞回避免反复开关）。")]
        public int CrossFanAssistExitStableSeconds { get; set; }

        public void Normalize()
        {
            // 功耗参数锁定为安全预设（与桥接白名单共享定义）：不允许通过
            // 配置把任意 PL1/PL2/TimeSeconds 传给桥接程序。
            DailyPl1Watts = X15FanCore.Control.AdaptivePowerPresets.Daily.Pl1Watts;
            DailyPl2Watts = X15FanCore.Control.AdaptivePowerPresets.Daily.Pl2Watts;
            DailyWindowsMaximumPerformancePercent = X15FanCore.Control.AdaptivePowerPresets.Daily.WindowsMaximumPerformancePercent;
            CodePl1Watts = X15FanCore.Control.AdaptivePowerPresets.Code.Pl1Watts;
            CodePl2Watts = X15FanCore.Control.AdaptivePowerPresets.Code.Pl2Watts;
            CodeWindowsMaximumPerformancePercent = X15FanCore.Control.AdaptivePowerPresets.Code.WindowsMaximumPerformancePercent;
            HeavyPl1Watts = X15FanCore.Control.AdaptivePowerPresets.Heavy.Pl1Watts;
            HeavyPl2Watts = X15FanCore.Control.AdaptivePowerPresets.Heavy.Pl2Watts;
            HeavyWindowsMaximumPerformancePercent = X15FanCore.Control.AdaptivePowerPresets.Heavy.WindowsMaximumPerformancePercent;
            TimeSeconds = X15FanCore.Control.AdaptivePowerPresets.Daily.TimeSeconds;
            UpshiftDwellSeconds = Clamp(UpshiftDwellSeconds, 10, 300);
            DownshiftDwellSeconds = Clamp(DownshiftDwellSeconds, 30, 900);
            // v3 字段：缺失(0)时必须填默认值，不能只 Clamp 后保留 0。
            QuietToDailyDwellSeconds = Clamp(QuietToDailyDwellSeconds <= 0 ? 15 : QuietToDailyDwellSeconds, 5, 600);
            DailyToCodeDwellSeconds = Clamp(DailyToCodeDwellSeconds <= 0 ? 30 : DailyToCodeDwellSeconds, 5, 600);
            CodeToHeavyDwellSeconds = Clamp(CodeToHeavyDwellSeconds <= 0 ? 30 : CodeToHeavyDwellSeconds, 5, 600);
            HeavyToCodeDwellSeconds = Clamp(HeavyToCodeDwellSeconds <= 0 ? 60 : HeavyToCodeDwellSeconds, 5, 900);
            CodeToDailyDwellSeconds = Clamp(CodeToDailyDwellSeconds <= 0 ? 120 : CodeToDailyDwellSeconds, 5, 900);
            DailyToQuietDwellSeconds = Clamp(DailyToQuietDwellSeconds <= 0 ? 120 : DailyToQuietDwellSeconds, 5, 900);
            StrongUpshiftDwellSeconds = Clamp(StrongUpshiftDwellSeconds <= 0 ? 12 : StrongUpshiftDwellSeconds, 3, 120);
            MinimumTierHoldSeconds = Clamp(MinimumTierHoldSeconds <= 0 ? 20 : MinimumTierHoldSeconds, 1, 120);
            UpshiftEvidenceWindowSeconds = Clamp(UpshiftEvidenceWindowSeconds <= 0 ? 8 : UpshiftEvidenceWindowSeconds, 2, 60);
            DownshiftAverageWindowSeconds = Clamp(DownshiftAverageWindowSeconds <= 0 ? 30 : DownshiftAverageWindowSeconds, 5, 300);
            RecentPeakWindowSeconds = Clamp(RecentPeakWindowSeconds <= 0 ? 15 : RecentPeakWindowSeconds, 3, 120);
            if (FastRiseBreakthroughCPerSecond <= 0) FastRiseBreakthroughCPerSecond = 1.0;
            ThermalSaturationDwellSeconds = Clamp(ThermalSaturationDwellSeconds <= 0 ? 20 : ThermalSaturationDwellSeconds, 5, 300);
            RecoveryDwellSeconds = Clamp(RecoveryDwellSeconds <= 0 ? 90 : RecoveryDwellSeconds, 10, 900);
            RecoveryMarginPercent = Clamp(RecoveryMarginPercent <= 0 ? 5 : RecoveryMarginPercent, 1, 30);
            CrossFanAssistRatioPercent = Clamp(CrossFanAssistRatioPercent <= 0 ? 25 : CrossFanAssistRatioPercent, 5, 50);
            CrossFanAssistEngageSeconds = Clamp(CrossFanAssistEngageSeconds <= 0 ? 20 : CrossFanAssistEngageSeconds, 5, 300);
            CrossFanAssistExitStableSeconds = Clamp(CrossFanAssistExitStableSeconds <= 0 ? 60 : CrossFanAssistExitStableSeconds, 10, 900);
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
