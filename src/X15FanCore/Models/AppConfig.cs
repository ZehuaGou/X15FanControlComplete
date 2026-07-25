using System.Collections.Generic;
using System.Runtime.Serialization;

namespace X15FanCore.Models
{
    [DataContract]
    public sealed class AppConfig
    {
        public AppConfig()
        {
            ConfigVersion = 2;
            ActiveProfileName = "静音稳定－平衡";
            StartupMode = RunMode.ReadOnly;
            PollIntervalMs = 500;
            StartMinimized = false;
            LaunchWatchdogInActiveMode = true;
            EnableCsvLogging = true;
            Profiles = new List<FanProfile>();
            CalibrationNoisyPointsCpu = new List<int>();
            CalibrationNoisyPointsGpu = new List<int>();
            StartWithWindows = false;
            StartMinimizedToTray = true;
            AutoEnterActiveOnStartup = false;
            DetailedVerificationLogging = false;
            UiRefreshIntervalMs = 500;
            ChartSampleIntervalMs = 1000;
            MaxUiLogLines = 1500;
        }

        [DataMember(Order = 0)]
        public int ConfigVersion { get; set; }

        [DataMember(Order = 1)]
        public string ActiveProfileName { get; set; }

        [DataMember(Order = 2)]
        public RunMode StartupMode { get; set; }

        [DataMember(Order = 3)]
        public int PollIntervalMs { get; set; }

        [DataMember(Order = 4)]
        public bool StartMinimized { get; set; }

        [DataMember(Order = 5)]
        public bool LaunchWatchdogInActiveMode { get; set; }

        [DataMember(Order = 6)]
        public bool EnableCsvLogging { get; set; }

        [DataMember(Order = 7)]
        public List<FanProfile> Profiles { get; set; }

        [DataMember(Order = 8)]
        public List<int> CalibrationNoisyPointsCpu { get; set; }

        [DataMember(Order = 9)]
        public List<int> CalibrationNoisyPointsGpu { get; set; }

        // --- Desktop experience settings ---
        [DataMember(Order = 10)]
        public bool StartWithWindows { get; set; }

        [DataMember(Order = 11)]
        public bool StartMinimizedToTray { get; set; }

        [DataMember(Order = 12)]
        public bool AutoEnterActiveOnStartup { get; set; }

        [DataMember(Order = 13)]
        public bool DetailedVerificationLogging { get; set; }

        [DataMember(Order = 14)]
        public int UiRefreshIntervalMs { get; set; }

        [DataMember(Order = 15)]
        public int ChartSampleIntervalMs { get; set; }

        [DataMember(Order = 16)]
        public int MaxUiLogLines { get; set; }
    }
}
