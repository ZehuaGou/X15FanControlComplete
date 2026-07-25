using System.Collections.Generic;
using System.Runtime.Serialization;

namespace X15FanCore.Models
{
    [DataContract]
    public sealed class AppConfig
    {
        public AppConfig()
        {
            ActiveProfileName = "静音稳定－平衡";
            StartupMode = RunMode.ReadOnly;
            PollIntervalMs = 500;
            StartMinimized = false;
            LaunchWatchdogInActiveMode = true;
            EnableCsvLogging = true;
            Profiles = new List<FanProfile>();
            CalibrationNoisyPointsCpu = new List<int>();
            CalibrationNoisyPointsGpu = new List<int>();
        }

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
    }
}
