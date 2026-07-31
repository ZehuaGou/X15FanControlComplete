namespace X15FanCore.Models
{
    public sealed class FanSnapshot
    {
        public int CpuTemperatureC { get; set; }
        public int GpuTemperatureC { get; set; }
        public int CpuTemperatureLocalC { get; set; }
        public int GpuTemperatureLocalC { get; set; }
        public int CpuDutyPercent { get; set; }
        public int GpuDutyPercent { get; set; }
        public double CpuUtilizationPercent { get; set; }
        public double CpuPerformancePercent { get; set; }
        public int CpuRpm { get; set; }
        public int GpuRpm { get; set; }
        public bool GpuTelemetryAvailable { get; set; }
        public int GpuTelemetryUtilization { get; set; }
        public double GpuTelemetryPowerWatts { get; set; }
        public string GpuTelemetryPState { get; set; }
        public string GpuTelemetrySource { get; set; }
        public System.DateTime TimestampUtc { get; set; }
    }

    public enum ControlState
    {
        Normal = 0,
        RampingUp,
        RampingDown,
        DownHold,
        StableZone,
        Emergency,
        InvalidSensor,
        WriteFailed,
        ExternalOverride,
        RestoredAuto
    }

    public sealed class ChannelDecision
    {
        public FanKind Fan { get; set; }
        public int InstantTemperatureC { get; set; }
        public double FastTemperatureC { get; set; }
        public double SlowTemperatureC { get; set; }
        public double ControlTemperatureC { get; set; }
        public double RawTargetPercent { get; set; }
        public double AcceptedTargetPercent { get; set; }
        public double AppliedPercent { get; set; }
        public bool ShouldWrite { get; set; }
        public int WritePercent { get; set; }
        public bool RequestAutoFallback { get; set; }
        public DecisionReason Reason { get; set; }
        public string Detail { get; set; }
        public ControlState State { get; set; }
        public double TemperatureRiseRateCPerSec { get; set; }
        public double DownHoldRemainingSeconds { get; set; }
        public double EcReadbackPercent { get; set; }
        public int EcReadbackDuty { get; set; }
        public bool WriteVerified { get; set; }
        public bool ExternalOverrideDetected { get; set; }
    }

    public sealed class ControlDecision
    {
        public ChannelDecision Cpu { get; set; }
        public ChannelDecision Gpu { get; set; }
        public bool RequestAutoFallback
        {
            get
            {
                return (Cpu != null && Cpu.RequestAutoFallback) || (Gpu != null && Gpu.RequestAutoFallback);
            }
        }
    }
}
