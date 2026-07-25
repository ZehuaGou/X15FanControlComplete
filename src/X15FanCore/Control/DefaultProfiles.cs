using System.Collections.Generic;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    public static class DefaultProfiles
    {
        public static AppConfig CreateConfig()
        {
            AppConfig config = new AppConfig();
            config.Profiles.Add(CreateBalancedProfile());
            config.Profiles.Add(CreateLowNoiseProfile());
            config.Profiles.Add(CreateSilentProfile());
            config.Profiles.Add(CreateCurrentBrzProfile());
            config.Profiles.Add(CreatePerformanceProfile());
            return config;
        }

        public static FanProfile CreateBalancedProfile()
        {
            FanProfile profile = new FanProfile
            {
                Name = "静音稳定－平衡",
                Description = "默认推荐配置。70-80°C形成52-54%声学平台，53%保持点。适合日常使用。",
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
            profile.Cpu.EmergencyStage1TemperatureC = 87;
            profile.Cpu.EmergencyStage1Percent = 75;
            profile.Cpu.EmergencyStage2TemperatureC = 90;
            profile.Cpu.EmergencyStage2Percent = 90;
            profile.Cpu.EmergencyStage3TemperatureC = 93;
            profile.Cpu.EmergencyStage3Percent = 100;

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
            profile.Gpu.EmergencyStage1TemperatureC = 82;
            profile.Gpu.EmergencyStage1Percent = 70;
            profile.Gpu.EmergencyStage2TemperatureC = 85;
            profile.Gpu.EmergencyStage2Percent = 85;
            profile.Gpu.EmergencyStage3TemperatureC = 87;
            profile.Gpu.EmergencyStage3Percent = 100;
            return profile;
        }

        public static FanProfile CreateLowNoiseProfile()
        {
            FanProfile profile = new FanProfile
            {
                Name = "静音稳定－低噪",
                Description = "更低的稳定平台保持点(52%)，适合对风扇噪音更敏感的用户。CPU>=80C后允许正常升速。",
                CouplingEnabled = false,
                CouplingStartTemperatureC = 78,
                CouplingMaximumPercent = 3
            };

            profile.Cpu.Curve = Points(40, 5, 50, 15, 60, 35, 70, 52, 80, 54, 85, 66, 90, 90, 93, 100);
            profile.Cpu.StableZoneEnabled = true;
            profile.Cpu.StableZoneMinimumPercent = 49;
            profile.Cpu.StableZoneMaximumPercent = 54;
            profile.Cpu.StableZoneHoldPercent = 52;
            profile.Cpu.UpRatePercentPerSecond = 1.5;
            profile.Cpu.DownRatePercentPerSecond = 0.4;
            profile.Cpu.DownHoldSeconds = 15;
            profile.Cpu.HysteresisC = 3;
            profile.Cpu.TargetDeadbandPercent = 1.5;
            profile.Cpu.FilterWindowSamples = 4;
            profile.Cpu.FastEmaAlpha = 0.45;
            profile.Cpu.SlowEmaAlpha = 0.18;
            profile.Cpu.EmergencyStage1TemperatureC = 87;
            profile.Cpu.EmergencyStage1Percent = 75;
            profile.Cpu.EmergencyStage2TemperatureC = 90;
            profile.Cpu.EmergencyStage2Percent = 90;
            profile.Cpu.EmergencyStage3TemperatureC = 93;
            profile.Cpu.EmergencyStage3Percent = 100;

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
            profile.Gpu.EmergencyStage1TemperatureC = 82;
            profile.Gpu.EmergencyStage1Percent = 70;
            profile.Gpu.EmergencyStage2TemperatureC = 85;
            profile.Gpu.EmergencyStage2Percent = 85;
            profile.Gpu.EmergencyStage3TemperatureC = 87;
            profile.Gpu.EmergencyStage3Percent = 100;
            return profile;
        }

        public static FanProfile CreateSilentProfile()
        {
            FanProfile profile = new FanProfile
            {
                Name = "极致静音",
                Description = "60°C前风扇保持极低转速(≤35%)，优先安静。高温时缓慢升速，适合轻度使用。",
                CouplingEnabled = false,
                CouplingStartTemperatureC = 78,
                CouplingMaximumPercent = 3
            };

            profile.Cpu.Curve = Points(40, 10, 50, 15, 60, 25, 65, 35, 70, 45, 75, 55, 80, 60, 85, 75, 90, 85, 95, 100);
            profile.Cpu.StableZoneEnabled = false;
            profile.Cpu.UpRatePercentPerSecond = 1.0;
            profile.Cpu.DownRatePercentPerSecond = 0.3;
            profile.Cpu.DownHoldSeconds = 20;
            profile.Cpu.HysteresisC = 4;
            profile.Cpu.TargetDeadbandPercent = 2.0;
            profile.Cpu.FilterWindowSamples = 4;
            profile.Cpu.FastEmaAlpha = 0.45;
            profile.Cpu.SlowEmaAlpha = 0.18;
            profile.Cpu.EmergencyStage1TemperatureC = 90;
            profile.Cpu.EmergencyStage1Percent = 70;
            profile.Cpu.EmergencyStage2TemperatureC = 93;
            profile.Cpu.EmergencyStage2Percent = 85;
            profile.Cpu.EmergencyStage3TemperatureC = 95;
            profile.Cpu.EmergencyStage3Percent = 100;

            profile.Gpu.Curve = Points(40, 8, 50, 12, 60, 22, 65, 30, 70, 38, 75, 48, 80, 52, 85, 65, 90, 80, 95, 100);
            profile.Gpu.StableZoneEnabled = false;
            profile.Gpu.UpRatePercentPerSecond = 0.8;
            profile.Gpu.DownRatePercentPerSecond = 0.25;
            profile.Gpu.DownHoldSeconds = 20;
            profile.Gpu.HysteresisC = 4;
            profile.Gpu.TargetDeadbandPercent = 2.0;
            profile.Gpu.FilterWindowSamples = 4;
            profile.Gpu.FastEmaAlpha = 0.4;
            profile.Gpu.SlowEmaAlpha = 0.15;
            profile.Gpu.EmergencyStage1TemperatureC = 85;
            profile.Gpu.EmergencyStage1Percent = 65;
            profile.Gpu.EmergencyStage2TemperatureC = 88;
            profile.Gpu.EmergencyStage2Percent = 80;
            profile.Gpu.EmergencyStage3TemperatureC = 90;
            profile.Gpu.EmergencyStage3Percent = 100;
            return profile;
        }

        public static FanProfile CreateCurrentBrzProfile()
        {
            FanProfile profile = new FanProfile
            {
                Name = "当前 Brz 曲线",
                Description = "你当前的 Brz 温度/功率曲线，并增加了平滑处理和安全保护。",
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
            profile.Gpu.EmergencyStage1TemperatureC = 82;
            profile.Gpu.EmergencyStage2TemperatureC = 85;
            return profile;
        }

        public static FanProfile CreatePerformanceProfile()
        {
            FanProfile profile = new FanProfile
            {
                Name = "性能模式",
                Description = "更早的冷却和更高的稳定气流，适合持续高负载。",
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
            profile.Gpu.EmergencyStage1TemperatureC = 82;
            profile.Gpu.EmergencyStage2TemperatureC = 85;
            return profile;
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
