using System.Collections.Generic;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    public static class DefaultProfiles
    {
        public static AppConfig CreateConfig()
        {
            AppConfig config = new AppConfig();
            config.ActiveProfileName = "自动";
            config.StrategyMode = StrategyMode.Auto;
            config.Profiles.Add(CreateAutoProfile());
            config.Profiles.Add(CreateQuietProfile());
            config.Profiles.Add(CreateDailyProfile());
            config.Profiles.Add(CreateBalancedProfile());
            config.Profiles.Add(CreatePerformanceProfile());
            return config;
        }

        public static FanProfile CreateAutoProfile()
        {
            FanProfile profile = CreateQuietProfile();
            profile.Name = "自动";
            profile.Description = "自动策略入口。运行时根据持续负载在安静、日常、代码和重负载四档之间切换。";
            return profile;
        }

        // Compatibility entry point used by the calibration/verification code.
        public static FanProfile CreateBalancedProfile()
        {
            FanProfile profile = new FanProfile
            {
                Name = "代码",
                Description = "代码固定策略：38W/55W，CPU性能上限95%，配套稳定风扇曲线。",
                CouplingEnabled = false,
                CouplingStartTemperatureC = 78,
                CouplingMaximumPercent = 3
            };

            profile.Cpu.Curve = Points(40, 5, 50, 15, 60, 35, 70, 52, 80, 54, 85, 66, 90, 90, 93, 100);
            profile.Cpu.StableZoneEnabled = true;
            profile.Cpu.StableZoneMinimumPercent = 50;
            profile.Cpu.StableZoneMaximumPercent = 55;
            profile.Cpu.StableZoneHoldPercent = 53;
            profile.Cpu.UpRatePercentPerSecond = 1.5;
            profile.Cpu.DownRatePercentPerSecond = 0.4;
            profile.Cpu.DownHoldSeconds = 15;
            profile.Cpu.HysteresisC = 3;
            profile.Cpu.TargetDeadbandPercent = 1.5;
            profile.Cpu.FilterWindowSamples = 4;
            profile.Cpu.FastEmaAlpha = 0.45;
            profile.Cpu.SlowEmaAlpha = 0.18;

            profile.Gpu.Curve = Points(40, 5, 50, 12, 60, 25, 70, 42, 80, 48, 85, 65, 90, 100);
            profile.Gpu.StableZoneEnabled = true;
            profile.Gpu.StableZoneMinimumPercent = 48;
            profile.Gpu.StableZoneMaximumPercent = 54;
            profile.Gpu.StableZoneHoldPercent = 50;
            profile.Gpu.UpRatePercentPerSecond = 1.2;
            profile.Gpu.DownRatePercentPerSecond = 0.3;
            profile.Gpu.DownHoldSeconds = 15;
            profile.Gpu.HysteresisC = 3;
            profile.Gpu.TargetDeadbandPercent = 1.5;
            profile.Gpu.FilterWindowSamples = 4;
            profile.Gpu.FastEmaAlpha = 0.4;
            profile.Gpu.SlowEmaAlpha = 0.15;

            ApplySafetyPolicy(profile);
            return profile;
        }

        public static FanProfile CreateDailyProfile()
        {
            FanProfile profile = new FanProfile
            {
                Name = "日常",
                Description = "日常固定策略：30W/45W，较代码档更低的稳定风扇曲线。",
                CouplingEnabled = false,
                CouplingStartTemperatureC = 78,
                CouplingMaximumPercent = 3
            };

            profile.Cpu.Curve = Points(40, 5, 50, 12, 60, 30, 70, 45, 75, 50, 80, 58, 85, 70, 90, 92, 93, 100);
            profile.Cpu.StableZoneEnabled = true;
            profile.Cpu.StableZoneMinimumPercent = 45;
            profile.Cpu.StableZoneMaximumPercent = 50;
            profile.Cpu.StableZoneHoldPercent = 48;
            profile.Cpu.UpRatePercentPerSecond = 1.3;
            profile.Cpu.DownRatePercentPerSecond = 0.35;
            profile.Cpu.DownHoldSeconds = 15;
            profile.Cpu.HysteresisC = 3;
            profile.Cpu.TargetDeadbandPercent = 1.5;
            profile.Cpu.FilterWindowSamples = 4;
            profile.Cpu.FastEmaAlpha = 0.45;
            profile.Cpu.SlowEmaAlpha = 0.18;

            profile.Gpu.Curve = Points(40, 5, 50, 10, 60, 22, 70, 35, 75, 45, 80, 58, 85, 78, 90, 100);
            profile.Gpu.StableZoneEnabled = true;
            profile.Gpu.StableZoneMinimumPercent = 43;
            profile.Gpu.StableZoneMaximumPercent = 50;
            profile.Gpu.StableZoneHoldPercent = 46;
            profile.Gpu.UpRatePercentPerSecond = 1.1;
            profile.Gpu.DownRatePercentPerSecond = 0.28;
            profile.Gpu.DownHoldSeconds = 15;
            profile.Gpu.HysteresisC = 3;
            profile.Gpu.TargetDeadbandPercent = 1.5;
            profile.Gpu.FilterWindowSamples = 4;
            profile.Gpu.FastEmaAlpha = 0.4;
            profile.Gpu.SlowEmaAlpha = 0.15;

            ApplySafetyPolicy(profile);
            return profile;
        }

        public static FanProfile CreateQuietProfile()
        {
            FanProfile profile = new FanProfile
            {
                Name = "安静",
                Description = "安静固定策略：25W/35W，CPU性能上限75%，高温仍保留安全爬升。",
                CouplingEnabled = false,
                CouplingStartTemperatureC = 78,
                CouplingMaximumPercent = 3
            };

            profile.Cpu.Curve = Points(40, 5, 50, 10, 60, 25, 65, 32, 70, 40, 75, 48, 80, 52, 85, 65, 87, 75, 90, 90, 93, 100);
            profile.Cpu.StableZoneEnabled = false;
            profile.Cpu.UpRatePercentPerSecond = 1.0;
            profile.Cpu.DownRatePercentPerSecond = 0.3;
            profile.Cpu.DownHoldSeconds = 20;
            profile.Cpu.HysteresisC = 4;
            profile.Cpu.TargetDeadbandPercent = 2.0;
            profile.Cpu.FilterWindowSamples = 4;
            profile.Cpu.FastEmaAlpha = 0.45;
            profile.Cpu.SlowEmaAlpha = 0.18;

            profile.Gpu.Curve = Points(40, 5, 50, 10, 60, 20, 65, 28, 70, 35, 75, 43, 80, 48, 81, 60, 82, 70, 85, 85, 87, 100);
            profile.Gpu.StableZoneEnabled = false;
            profile.Gpu.UpRatePercentPerSecond = 0.8;
            profile.Gpu.DownRatePercentPerSecond = 0.25;
            profile.Gpu.DownHoldSeconds = 20;
            profile.Gpu.HysteresisC = 4;
            profile.Gpu.TargetDeadbandPercent = 2.0;
            profile.Gpu.FilterWindowSamples = 4;
            profile.Gpu.FastEmaAlpha = 0.4;
            profile.Gpu.SlowEmaAlpha = 0.15;

            ApplySafetyPolicy(profile);
            return profile;
        }

        // Compatibility entry points for configurations created by older builds.
        public static FanProfile CreateLowNoiseProfile()
        {
            return CreateQuietProfile();
        }

        public static FanProfile CreateSilentProfile()
        {
            return CreateQuietProfile();
        }

        public static FanProfile CreateCurrentBrzProfile()
        {
            FanProfile profile = new FanProfile
            {
                Name = "Brz Legacy",
                Description = "Legacy BRZ fan curve retained as an explicit rollback reference.",
                CouplingEnabled = false
            };

            profile.Cpu.Curve = Points(40, 5, 50, 12, 60, 25, 70, 40, 80, 60, 85, 82, 90, 100);
            profile.Cpu.StableZoneEnabled = false;
            profile.Cpu.UpRatePercentPerSecond = 2.0;
            profile.Cpu.DownRatePercentPerSecond = 0.5;
            profile.Cpu.DownHoldSeconds = 12;
            profile.Cpu.HysteresisC = 3;

            profile.Gpu.Curve = Points(40, 5, 50, 10, 60, 20, 70, 35, 80, 55, 85, 75, 90, 100);
            profile.Gpu.StableZoneEnabled = false;
            profile.Gpu.UpRatePercentPerSecond = 1.5;
            profile.Gpu.DownRatePercentPerSecond = 0.4;
            profile.Gpu.DownHoldSeconds = 12;
            profile.Gpu.HysteresisC = 3;

            ApplySafetyPolicy(profile);
            return profile;
        }

        public static FanProfile CreatePerformanceProfile()
        {
            FanProfile profile = new FanProfile
            {
                Name = "重负载",
                Description = "重负载固定策略：55W/69W，CPU性能上限100%，配套更积极的散热曲线。",
                CouplingEnabled = true,
                CouplingStartTemperatureC = 75,
                CouplingMaximumPercent = 4
            };

            profile.Cpu.Curve = Points(40, 10, 50, 20, 60, 38, 70, 55, 80, 70, 85, 88, 90, 100);
            profile.Cpu.StableZoneEnabled = false;
            profile.Cpu.UpRatePercentPerSecond = 3.0;
            profile.Cpu.DownRatePercentPerSecond = 0.7;
            profile.Cpu.DownHoldSeconds = 10;
            profile.Cpu.HysteresisC = 2;

            profile.Gpu.Curve = Points(40, 10, 50, 18, 60, 30, 70, 48, 80, 65, 85, 85, 90, 100);
            profile.Gpu.StableZoneEnabled = false;
            profile.Gpu.UpRatePercentPerSecond = 2.5;
            profile.Gpu.DownRatePercentPerSecond = 0.6;
            profile.Gpu.DownHoldSeconds = 10;
            profile.Gpu.HysteresisC = 2;

            ApplySafetyPolicy(profile);
            return profile;
        }

        // A profile can be edited in the UI, but it must never weaken the fan safety floor.
        public static bool ApplySafetyPolicy(FanProfile profile)
        {
            if (profile == null)
                return false;

            bool changed = false;
            // Stage-1 thresholds sit above the temperatures this notebook
            // reaches during normal use (idle ~80C, heavy work 85-87C), so
            // the emergency floor no longer snaps the fans on routine load.
            // Stage 2/3 remain unchanged and still respond immediately.
            changed |= ApplySafetyPolicy(profile.Cpu, 89, 75, 90, 90, 93);
            changed |= ApplySafetyPolicy(profile.Gpu, 82, 75, 85, 85, 87);
            return changed;
        }

        private static bool ApplySafetyPolicy(FanChannelProfile channel, int stage1Temperature, double stage1Percent,
            int stage2Temperature, double stage2Percent, int stage3Temperature)
        {
            if (channel == null)
                return false;

            bool changed = false;
            int stage1TemperatureValue = channel.EmergencyStage1TemperatureC;
            int stage2TemperatureValue = channel.EmergencyStage2TemperatureC;
            int stage3TemperatureValue = channel.EmergencyStage3TemperatureC;
            double stage1PercentValue = channel.EmergencyStage1Percent;
            double stage2PercentValue = channel.EmergencyStage2Percent;
            double stage3PercentValue = channel.EmergencyStage3Percent;
            changed |= ClampMaximum(ref stage1TemperatureValue, stage1Temperature);
            changed |= ClampMinimum(ref stage1PercentValue, stage1Percent);
            changed |= ClampMaximum(ref stage2TemperatureValue, stage2Temperature);
            changed |= ClampMinimum(ref stage2PercentValue, stage2Percent);
            changed |= ClampMaximum(ref stage3TemperatureValue, stage3Temperature);
            changed |= ClampMinimum(ref stage3PercentValue, 100);
            channel.EmergencyStage1TemperatureC = stage1TemperatureValue;
            channel.EmergencyStage1Percent = stage1PercentValue;
            channel.EmergencyStage2TemperatureC = stage2TemperatureValue;
            channel.EmergencyStage2Percent = stage2PercentValue;
            channel.EmergencyStage3TemperatureC = stage3TemperatureValue;
            channel.EmergencyStage3Percent = stage3PercentValue;
            return changed;
        }

        private static bool ClampMaximum(ref int value, int maximum)
        {
            if (value <= 0)
            {
                value = maximum;
                return true;
            }
            if (value <= maximum)
                return false;
            value = maximum;
            return true;
        }

        private static bool ClampMinimum(ref double value, double minimum)
        {
            if (value >= minimum)
                return false;
            value = minimum;
            return true;
        }

        private static List<FanCurvePoint> Points(params double[] values)
        {
            List<FanCurvePoint> points = new List<FanCurvePoint>();
            for (int index = 0; index + 1 < values.Length; index += 2)
                points.Add(new FanCurvePoint(values[index], values[index + 1]));
            return points;
        }
    }
}
