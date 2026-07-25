using System;

namespace X15FanCore.Models
{
    public sealed class GpuTelemetryData
    {
        public bool IsAvailable { get; set; }
        public int TemperatureC { get; set; }
        public int UtilizationPercent { get; set; }
        public double PowerWatts { get; set; }
        public string PState { get; set; }
        public string SourceName { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime LastUpdatedUtc { get; set; }
        public bool IsStale { get; set; }
    }
}
