using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.Serialization;

namespace X15FanCore.Models
{
    [DataContract]
    public sealed class FanChannelProfile
    {
        public FanChannelProfile()
        {
            Curve = new List<FanCurvePoint>();
            FilterWindowSamples = 8;
            FastEmaAlpha = 0.45;
            SlowEmaAlpha = 0.16;
            HysteresisC = 3.0;
            TargetDeadbandPercent = 1.5;
            UpRatePercentPerSecond = 2.0;
            DownRatePercentPerSecond = 0.5;
            DownHoldSeconds = 12;
            MinimumWriteDeltaPercent = 1.0;
            MaximumWriteIntervalSeconds = 2.0;
            ConsecutiveInvalidSamplesBeforeAuto = 4;
            MinimumValidTemperatureC = 5;
            MaximumValidTemperatureC = 115;
            StableZoneEnabled = false;
            StableZoneMinimumPercent = 50;
            StableZoneMaximumPercent = 57;
            StableZoneHoldPercent = 53;
            EmergencyStage1TemperatureC = 88;
            EmergencyStage1Percent = 75;
            EmergencyStage2TemperatureC = 92;
            EmergencyStage2Percent = 100;
        }

        [DataMember(Order = 1)]
        [Browsable(false)]
        public List<FanCurvePoint> Curve { get; set; }

        [DataMember(Order = 2)]
        public int FilterWindowSamples { get; set; }

        [DataMember(Order = 3)]
        public double FastEmaAlpha { get; set; }

        [DataMember(Order = 4)]
        public double SlowEmaAlpha { get; set; }

        [DataMember(Order = 5)]
        public double HysteresisC { get; set; }

        [DataMember(Order = 6)]
        public double TargetDeadbandPercent { get; set; }

        [DataMember(Order = 7)]
        public double UpRatePercentPerSecond { get; set; }

        [DataMember(Order = 8)]
        public double DownRatePercentPerSecond { get; set; }

        [DataMember(Order = 9)]
        public int DownHoldSeconds { get; set; }

        [DataMember(Order = 10)]
        public double MinimumWriteDeltaPercent { get; set; }

        [DataMember(Order = 11)]
        public double MaximumWriteIntervalSeconds { get; set; }

        [DataMember(Order = 12)]
        public int ConsecutiveInvalidSamplesBeforeAuto { get; set; }

        [DataMember(Order = 13)]
        public int MinimumValidTemperatureC { get; set; }

        [DataMember(Order = 14)]
        public int MaximumValidTemperatureC { get; set; }

        [DataMember(Order = 15)]
        public bool StableZoneEnabled { get; set; }

        [DataMember(Order = 16)]
        public double StableZoneMinimumPercent { get; set; }

        [DataMember(Order = 17)]
        public double StableZoneMaximumPercent { get; set; }

        [DataMember(Order = 18)]
        public double StableZoneHoldPercent { get; set; }

        [DataMember(Order = 19)]
        public int EmergencyStage1TemperatureC { get; set; }

        [DataMember(Order = 20)]
        public double EmergencyStage1Percent { get; set; }

        [DataMember(Order = 21)]
        public int EmergencyStage2TemperatureC { get; set; }

        [DataMember(Order = 22)]
        public double EmergencyStage2Percent { get; set; }

        [DataMember(Order = 23)]
        public int EmergencyStage3TemperatureC { get; set; }

        [DataMember(Order = 24)]
        public double EmergencyStage3Percent { get; set; }
    }
}
