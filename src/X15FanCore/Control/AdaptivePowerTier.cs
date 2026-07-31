using System;

namespace X15FanCore.Control
{
    public enum AdaptivePowerTier
    {
        Daily = 0,
        Code = 1,
        Heavy = 2,
        Quiet = 3
    }

    public sealed class AdaptivePowerSample
    {
        public DateTime TimestampUtc { get; set; }
        public double CpuUtilizationPercent { get; set; }
        public double CpuPerformancePercent { get; set; }
        public double GpuUtilizationPercent { get; set; }
        public double CpuTemperatureC { get; set; }
        public double GpuTemperatureC { get; set; }
    }

    /// <summary>
    /// Three-level load classifier.  A single spike never changes the tier:
    /// escalation and de-escalation both need a continuous dwell interval.
    /// Cooling remains governed by the fan controller's independent safety
    /// stages, so a failed power operation cannot suppress the emergency ramp.
    /// </summary>
    public sealed class AdaptivePowerTierController
    {
        public const double DailyToCodeCpuPercent = 35;
        public const double CodeToHeavyCpuPercent = 75;
        public const double DailyToCodeGpuPercent = 25;
        public const double CodeToHeavyGpuPercent = 55;
        public const int UpshiftDwellSeconds = 30;
        public const int DownshiftDwellSeconds = 120;

        private AdaptivePowerTier _currentTier;
        private DateTime? _upshiftSinceUtc;
        private DateTime? _downshiftSinceUtc;
        private AdaptivePowerTier? _pendingTier;
        private double _dwellRemainingSeconds;
        private string _lastReason;

        public AdaptivePowerTierController()
        {
            _currentTier = AdaptivePowerTier.Daily;
            _lastReason = "启动后保持日常档";
        }

        public AdaptivePowerTier CurrentTier
        {
            get { return _currentTier; }
        }

        public AdaptivePowerTier? PendingTier { get { return _pendingTier; } }
        public double DwellRemainingSeconds { get { return _dwellRemainingSeconds; } }
        public string LastReason { get { return _lastReason ?? string.Empty; } }

        public void ForceTier(AdaptivePowerTier tier, string reason)
        {
            _currentTier = tier;
            ClearDwellMarkers();
            _pendingTier = null;
            _dwellRemainingSeconds = 0;
            _lastReason = reason ?? "固定策略";
        }

        public AdaptivePowerTier Update(AdaptivePowerSample sample)
        {
            if (sample == null)
                return _currentTier;

            DateTime now = sample.TimestampUtc == default(DateTime)
                ? DateTime.UtcNow
                : sample.TimestampUtc;
            // Utilization is the primary load signal. Performance percentage
            // is only a fallback when the utilization counter is unavailable;
            // otherwise a brief turbo-frequency rise while idle could trigger
            // an unnecessary power-tier jump.
            double cpuLoad = sample.CpuUtilizationPercent > 0
                ? sample.CpuUtilizationPercent
                : sample.CpuPerformancePercent;
            double gpuLoad = sample.GpuUtilizationPercent;

            bool heavyLoad = cpuLoad >= CodeToHeavyCpuPercent || gpuLoad >= CodeToHeavyGpuPercent;
            bool codeLoad = cpuLoad >= DailyToCodeCpuPercent || gpuLoad >= DailyToCodeGpuPercent;
            bool quietLoad = cpuLoad <= 15 && gpuLoad <= 10;
            _pendingTier = null;
            _dwellRemainingSeconds = 0;

            if (_currentTier == AdaptivePowerTier.Daily)
            {
                if (codeLoad)
                {
                    if (!_upshiftSinceUtc.HasValue)
                        _upshiftSinceUtc = now;
                    double elapsed = (now - _upshiftSinceUtc.Value).TotalSeconds;
                    _pendingTier = AdaptivePowerTier.Code;
                    _dwellRemainingSeconds = Math.Max(0, UpshiftDwellSeconds - elapsed);
                    _lastReason = "负载持续升高，代码档还需 " + Math.Ceiling(_dwellRemainingSeconds) + " 秒";
                    if (elapsed >= UpshiftDwellSeconds)
                    {
                        _currentTier = AdaptivePowerTier.Code;
                        ClearDwellMarkers();
                        _lastReason = "负载持续达到代码档阈值";
                    }
                }
                else
                {
                    _upshiftSinceUtc = null;
                    _lastReason = quietLoad ? "低负载，保持日常档" : "负载未达到代码档阈值，保持日常档";
                }
                return _currentTier;
            }

            if (_currentTier == AdaptivePowerTier.Code)
            {
                if (heavyLoad)
                {
                    if (!_upshiftSinceUtc.HasValue)
                        _upshiftSinceUtc = now;
                    double elapsed = (now - _upshiftSinceUtc.Value).TotalSeconds;
                    _pendingTier = AdaptivePowerTier.Heavy;
                    _dwellRemainingSeconds = Math.Max(0, UpshiftDwellSeconds - elapsed);
                    _lastReason = "高负载持续中，重负载档还需 " + Math.Ceiling(_dwellRemainingSeconds) + " 秒";
                    if (elapsed >= UpshiftDwellSeconds)
                    {
                        _currentTier = AdaptivePowerTier.Heavy;
                        ClearDwellMarkers();
                        _lastReason = "高负载持续达到重负载档阈值";
                    }
                }
                else
                {
                    _upshiftSinceUtc = null;
                }

                if (_currentTier == AdaptivePowerTier.Code)
                {
                    if (quietLoad)
                    {
                        if (!_downshiftSinceUtc.HasValue)
                            _downshiftSinceUtc = now;
                        double elapsed = (now - _downshiftSinceUtc.Value).TotalSeconds;
                        _pendingTier = AdaptivePowerTier.Daily;
                        _dwellRemainingSeconds = Math.Max(0, DownshiftDwellSeconds - elapsed);
                        _lastReason = "负载已降低，日常档还需 " + Math.Ceiling(_dwellRemainingSeconds) + " 秒";
                        if (elapsed >= DownshiftDwellSeconds)
                        {
                            _currentTier = AdaptivePowerTier.Daily;
                            ClearDwellMarkers();
                            _lastReason = "低负载持续达到日常档回落条件";
                        }
                    }
                    else
                    {
                        _downshiftSinceUtc = null;
                        _lastReason = "保持代码档，等待更高负载或持续低负载";
                    }
                }
                return _currentTier;
            }

            // Heavy -> Code has a little hysteresis: load must be below the
            // code threshold, not merely below the heavy threshold.
            bool belowHeavy = cpuLoad < DailyToCodeCpuPercent && gpuLoad < DailyToCodeGpuPercent;
            if (belowHeavy)
            {
                if (!_downshiftSinceUtc.HasValue)
                    _downshiftSinceUtc = now;
                double elapsed = (now - _downshiftSinceUtc.Value).TotalSeconds;
                _pendingTier = AdaptivePowerTier.Code;
                _dwellRemainingSeconds = Math.Max(0, DownshiftDwellSeconds - elapsed);
                _lastReason = "负载已降低，代码档还需 " + Math.Ceiling(_dwellRemainingSeconds) + " 秒";
                if (elapsed >= DownshiftDwellSeconds)
                {
                    _currentTier = AdaptivePowerTier.Code;
                    ClearDwellMarkers();
                    _lastReason = "负载持续低于代码档阈值";
                }
            }
            else
            {
                _downshiftSinceUtc = null;
                _lastReason = "保持重负载档，等待负载持续降低";
            }
            return _currentTier;
        }

        private void ClearDwellMarkers()
        {
            _upshiftSinceUtc = null;
            _downshiftSinceUtc = null;
        }
    }
}
