using System;
using System.Collections.Generic;

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
        public bool GpuTelemetryAvailable { get; set; }
        public double CpuTemperatureC { get; set; }
        public double GpuTemperatureC { get; set; }
    }

    /// <summary>
    /// Four-level automatic classifier. Each transition moves only one tier,
    /// uses rolling load windows and has its own dwell time. Downshifting power
    /// is allowed while the machine is warm because lower power helps it cool;
    /// fan emergency floors remain independent from this classifier.
    /// </summary>
    public sealed class AdaptivePowerTierController
    {
        // Load thresholds calibrated against a 23-hour real usage trace
        // (84,139 one-second samples, 2026-07-31/08-01): CPU utilization sits
        // in 0-5% (7%), 5-10% (22%), 10-15% (22%), 15-20% (20%), 20-35%
        // (25%), 35-50% (4%), 50%+ (0.6%).  The previous thresholds (35/70
        // upshift) made the Code tier the de-facto ceiling and the Heavy
        // tier unreachable in automatic mode.  GPU utilization is almost
        // never below 5% (desktop compositing holds it near 10%), so quiet
        // tier gating must treat that as idle.
        public const double DailyToCodeCpuPercent = 25;
        public const double CodeToHeavyCpuPercent = 50;
        public const double DailyToCodeGpuPercent = 20;
        public const double CodeToHeavyGpuPercent = 40;
        public const double QuietToDailyCpuPercent = 12;
        public const double QuietToDailyGpuPercent = 10;
        public const double DailyToQuietCpuAveragePercent = 8;
        public const double DailyToQuietGpuAveragePercent = 15;

        // Downshift uses averages with hysteresis instead of the instantaneous
        // 15%/10% test used by the previous implementation.  Hysteresis bands
        // relative to the upshift gates above: quiet 4%, daily 10%, code 25%.
        public const double CodeToDailyCpuAveragePercent = 15;
        public const double CodeToDailyGpuAveragePercent = 12;
        public const double HeavyToCodeCpuAveragePercent = 25;
        public const double HeavyToCodeGpuAveragePercent = 20;

        // Recent-peak gates for downshift: a load spike inside the 15-second
        // peak window cancels an otherwise eligible downshift.  Scaled to the
        // upshift gates above (a tier may only drop once sustained load is
        // clearly below the upshift evidence).
        public const int QuietDownshiftMaxCpuPeakPercent = 30;
        public const int QuietDownshiftMaxGpuPeakPercent = 30;
        public const int CodeDownshiftMaxCpuPeakPercent = 40;
        public const int CodeDownshiftMaxGpuPeakPercent = 30;
        public const int HeavyDownshiftMaxCpuPeakPercent = 60;
        public const int HeavyDownshiftMaxGpuPeakPercent = 50;

        public const int QuietToDailyDwellSeconds = 10;
        public const int DailyToCodeDwellSeconds = 15;
        public const int CodeToHeavyDwellSeconds = 10;
        public const int HeavyToCodeDwellSeconds = 45;
        public const int CodeToDailyDwellSeconds = 60;
        public const int DailyToQuietDwellSeconds = 90;
        public const int MinimumTierHoldSeconds = 20;
        public const int UpshiftEvidenceWindowSeconds = 8;
        public const int DownshiftAverageWindowSeconds = 30;
        public const int RecentPeakWindowSeconds = 15;

        // Game fast-track: strong evidence (sustained GPU >= 70% or CPU >= 80%)
        // means a game is opening and the machine needs performance headroom
        // immediately.  The normal evidence window (8s) plus dwell (10-15s)
        // plus the 20s minimum tier hold used to leave a game stuck on the
        // Daily tier (30W PL1 / 85% CPU ceiling) for 30-45 seconds, which
        // reads as stutter at load time.  Strong evidence pierces the minimum
        // hold and compresses the upshift dwell to 3 seconds; downshifts keep
        // their long dwells so the tier cannot flap after the game exits.
        public const double StrongUpshiftEvidenceCpuPercent = 80;
        public const double StrongUpshiftEvidenceGpuPercent = 70;
        public const int StrongUpshiftDwellSeconds = 3;

        private const double QuietMaximumCpuTemperatureC = 85;
        private const double QuietMaximumGpuTemperatureC = 75;
        private const double QuietMaximumTemperatureRiseCPerSecond = 0.50;
        private const int HistoryRetentionSeconds = 180;

        private sealed class LoadSample
        {
            public DateTime TimestampUtc;
            public double CpuLoad;
            public double GpuLoad;
            public bool GpuKnown;
            public double CpuTemperatureC;
            public double GpuTemperatureC;
        }

        private sealed class WindowStats
        {
            public int Count;
            public bool GpuKnown;
            public bool TemperaturesKnown;
            public double AverageCpu;
            public double AverageGpu;
            public double MaximumCpu;
            public double MaximumGpu;
            public double MaximumCpuTemperature;
            public double MaximumGpuTemperature;
            public double CpuTemperatureRise;
            public double GpuTemperatureRise;
        }

        private readonly Queue<LoadSample> _history = new Queue<LoadSample>();
        private AdaptivePowerTier _currentTier;
        private DateTime? _upshiftSinceUtc;
        private DateTime? _downshiftSinceUtc;
        private DateTime? _tierSinceUtc;
        private AdaptivePowerTier? _pendingTier;
        private double _dwellRemainingSeconds;
        private string _lastReason;

        public AdaptivePowerTierController()
        {
            _currentTier = AdaptivePowerTier.Daily;
            _lastReason = "启动后保持日常档";
        }

        public AdaptivePowerTier CurrentTier { get { return _currentTier; } }
        public AdaptivePowerTier? PendingTier { get { return _pendingTier; } }
        public double DwellRemainingSeconds { get { return _dwellRemainingSeconds; } }
        public string LastReason { get { return _lastReason ?? string.Empty; } }

        public void ForceTier(AdaptivePowerTier tier, string reason)
        {
            _currentTier = tier;
            _tierSinceUtc = DateTime.UtcNow;
            ClearTransition();
            _lastReason = reason ?? "固定策略";
        }

        public AdaptivePowerTier Update(AdaptivePowerSample sample)
        {
            if (sample == null)
                return _currentTier;

            DateTime now = sample.TimestampUtc == default(DateTime)
                ? DateTime.UtcNow
                : sample.TimestampUtc;
            if (_history.Count > 0 && now < _history.Peek().TimestampUtc)
                now = _history.Peek().TimestampUtc;
            if (_tierSinceUtc.HasValue && now < _tierSinceUtc.Value)
                _tierSinceUtc = now;

            // CPU performance percentage is frequency relative to nominal, not
            // utilization. Falling back to it turned a legitimate 0% idle
            // sample into 100% load and could pin the policy in Heavy forever.
            double cpuLoad = Clamp(sample.CpuUtilizationPercent, 0, 100);
            double gpuLoad = Clamp(sample.GpuUtilizationPercent, 0, 100);
            bool gpuKnown = sample.GpuTelemetryAvailable || sample.GpuUtilizationPercent > 0;

            _history.Enqueue(new LoadSample
            {
                TimestampUtc = now,
                CpuLoad = cpuLoad,
                GpuLoad = gpuLoad,
                GpuKnown = gpuKnown,
                CpuTemperatureC = sample.CpuTemperatureC,
                GpuTemperatureC = sample.GpuTemperatureC
            });
            TrimHistory(now);

            WindowStats upshift = Summarize(now, UpshiftEvidenceWindowSeconds);
            WindowStats average = Summarize(now, DownshiftAverageWindowSeconds);
            WindowStats recent = Summarize(now, RecentPeakWindowSeconds);
            bool quietThermalSafe = IsQuietThermallySafe(average);
            bool codeEvidence = upshift.AverageCpu >= DailyToCodeCpuPercent ||
                                (upshift.GpuKnown && upshift.AverageGpu >= DailyToCodeGpuPercent);
            bool heavyEvidence = upshift.AverageCpu >= CodeToHeavyCpuPercent ||
                                  (upshift.GpuKnown && upshift.AverageGpu >= CodeToHeavyGpuPercent);
            bool dailyEvidence = upshift.AverageCpu >= QuietToDailyCpuPercent ||
                                 (upshift.GpuKnown && upshift.AverageGpu >= QuietToDailyGpuPercent);
            bool strongEvidence = upshift.AverageCpu >= StrongUpshiftEvidenceCpuPercent ||
                                  (upshift.GpuKnown && upshift.AverageGpu >= StrongUpshiftEvidenceGpuPercent);
            bool dailyToQuiet = average.TemperaturesKnown && quietThermalSafe &&
                                average.AverageCpu <= DailyToQuietCpuAveragePercent &&
                                average.GpuKnown && average.AverageGpu <= DailyToQuietGpuAveragePercent &&
                                recent.MaximumCpu < QuietDownshiftMaxCpuPeakPercent &&
                                recent.GpuKnown && recent.MaximumGpu < QuietDownshiftMaxGpuPeakPercent;
            bool codeToDaily =
                               average.AverageCpu <= CodeToDailyCpuAveragePercent &&
                                average.GpuKnown && average.AverageGpu <= CodeToDailyGpuAveragePercent &&
                               recent.MaximumCpu < CodeDownshiftMaxCpuPeakPercent &&
                               recent.GpuKnown && recent.MaximumGpu < CodeDownshiftMaxGpuPeakPercent;
            bool heavyToCode =
                               average.AverageCpu <= HeavyToCodeCpuAveragePercent &&
                               average.GpuKnown && average.AverageGpu <= HeavyToCodeGpuAveragePercent &&
                               recent.MaximumCpu < HeavyDownshiftMaxCpuPeakPercent &&
                               recent.MaximumGpu < HeavyDownshiftMaxGpuPeakPercent;

            if (_currentTier == AdaptivePowerTier.Quiet)
            {
                if (dailyEvidence)
                    return ConsiderTransition(AdaptivePowerTier.Daily, now, true, "负载持续升高，准备进入日常档", upshift, strongEvidence);

                ClearTransition();
                _lastReason = FormatStatus("保持安静档，等待日常负载", upshift);
                return _currentTier;
            }

            if (_currentTier == AdaptivePowerTier.Daily)
            {
                if (codeEvidence)
                    return ConsiderTransition(AdaptivePowerTier.Code, now, true, "中等负载持续，准备进入代码档", upshift, strongEvidence);

                if (dailyToQuiet)
                    return ConsiderTransition(AdaptivePowerTier.Quiet, now, false, "极低负载和温度持续稳定，准备进入安静档", average, false);

                ClearTransition();
                _lastReason = FormatStatus("保持日常档，负载未达到代码档条件", upshift);
                return _currentTier;
            }

            if (_currentTier == AdaptivePowerTier.Code)
            {
                if (heavyEvidence)
                    return ConsiderTransition(AdaptivePowerTier.Heavy, now, true, "高负载持续，准备进入重负载档", upshift, strongEvidence);

                if (codeToDaily)
                    return ConsiderTransition(AdaptivePowerTier.Daily, now, false, "低负载和温度持续稳定，准备回到日常档", average, false);

                ClearTransition();
                _lastReason = FormatStatus("保持代码档，等待负载持续降低", average);
                return _currentTier;
            }

            if (_currentTier == AdaptivePowerTier.Heavy)
            {
                if (heavyToCode)
                    return ConsiderTransition(AdaptivePowerTier.Code, now, false, "负载和温度持续回落，准备进入代码档", average, false);

                ClearTransition();
                _lastReason = FormatStatus("保持重负载档，等待负载持续降低", average);
                return _currentTier;
            }

            ClearTransition();
            _lastReason = "未知档位，保持当前状态";
            return _currentTier;
        }

        private AdaptivePowerTier ConsiderTransition(
            AdaptivePowerTier target,
            DateTime now,
            bool upshift,
            string reason,
            WindowStats stats,
            bool strongEvidence)
        {
            // The minimum tier hold prevents flapping, but strong evidence
            // (game-level load) pierces it: a game opening needs performance
            // headroom immediately and downshifts stay slow regardless, so
            // piercing the hold cannot cause a fast flap.
            if (_tierSinceUtc.HasValue && !(upshift && strongEvidence) &&
                (now - _tierSinceUtc.Value).TotalSeconds < MinimumTierHoldSeconds)
            {
                ClearTransition();
                _lastReason = "刚切换档位，保持" + GetTierName(_currentTier) + "至少" + MinimumTierHoldSeconds + "秒";
                return _currentTier;
            }

            DateTime? marker = upshift ? _upshiftSinceUtc : _downshiftSinceUtc;
            if (!marker.HasValue || _pendingTier != target)
            {
                marker = now;
                if (upshift)
                    _upshiftSinceUtc = now;
                else
                    _downshiftSinceUtc = now;
            }

            _pendingTier = target;
            double dwell = GetTransitionDwellSeconds(_currentTier, target);
            if (upshift && strongEvidence)
                dwell = Math.Min(dwell, StrongUpshiftDwellSeconds);
            double elapsed = Math.Max(0, (now - marker.Value).TotalSeconds);
            _dwellRemainingSeconds = Math.Max(0, dwell - elapsed);
            _lastReason = reason + "，还需" + Math.Ceiling(_dwellRemainingSeconds) + "秒" + FormatStats(stats);

            if (elapsed < dwell)
                return _currentTier;

            _currentTier = target;
            _tierSinceUtc = now;
            ClearTransition();
            _lastReason = "已切换到" + GetTierName(target) + "档";
            return _currentTier;
        }

        private WindowStats Summarize(DateTime now, int seconds)
        {
            DateTime cutoff = now.AddSeconds(-seconds);
            WindowStats result = new WindowStats();
            double cpuTotal = 0;
            double gpuTotal = 0;
            LoadSample first = null;
            LoadSample last = null;

            foreach (LoadSample item in _history)
            {
                if (item.TimestampUtc < cutoff)
                    continue;
                result.Count++;
                cpuTotal += item.CpuLoad;
                result.MaximumCpu = Math.Max(result.MaximumCpu, item.CpuLoad);
                if (item.GpuKnown)
                {
                    result.GpuKnown = true;
                    gpuTotal += item.GpuLoad;
                    result.MaximumGpu = Math.Max(result.MaximumGpu, item.GpuLoad);
                }
                if (item.CpuTemperatureC > 0 && item.GpuTemperatureC > 0)
                {
                    result.TemperaturesKnown = true;
                    result.MaximumCpuTemperature = Math.Max(result.MaximumCpuTemperature, item.CpuTemperatureC);
                    result.MaximumGpuTemperature = Math.Max(result.MaximumGpuTemperature, item.GpuTemperatureC);
                    if (first == null)
                        first = item;
                    last = item;
                }
            }

            if (result.Count > 0)
                result.AverageCpu = cpuTotal / result.Count;
            if (result.GpuKnown)
            {
                int gpuCount = 0;
                foreach (LoadSample item in _history)
                {
                    if (item.TimestampUtc >= cutoff && item.GpuKnown)
                        gpuCount++;
                }
                result.AverageGpu = gpuCount == 0 ? 0 : gpuTotal / gpuCount;
            }

            if (first != null && last != null)
            {
                double elapsed = (last.TimestampUtc - first.TimestampUtc).TotalSeconds;
                if (elapsed > 0)
                {
                    result.CpuTemperatureRise = (last.CpuTemperatureC - first.CpuTemperatureC) / elapsed;
                    result.GpuTemperatureRise = (last.GpuTemperatureC - first.GpuTemperatureC) / elapsed;
                }
            }
            return result;
        }

        private bool IsQuietThermallySafe(WindowStats stats)
        {
            return stats.TemperaturesKnown &&
                   stats.MaximumCpuTemperature < QuietMaximumCpuTemperatureC &&
                   stats.MaximumGpuTemperature < QuietMaximumGpuTemperatureC &&
                   stats.CpuTemperatureRise <= QuietMaximumTemperatureRiseCPerSecond &&
                   stats.GpuTemperatureRise <= QuietMaximumTemperatureRiseCPerSecond;
        }

        private static int GetTransitionDwellSeconds(AdaptivePowerTier current, AdaptivePowerTier target)
        {
            if (current == AdaptivePowerTier.Quiet && target == AdaptivePowerTier.Daily)
                return QuietToDailyDwellSeconds;
            if (current == AdaptivePowerTier.Daily && target == AdaptivePowerTier.Code)
                return DailyToCodeDwellSeconds;
            if (current == AdaptivePowerTier.Code && target == AdaptivePowerTier.Heavy)
                return CodeToHeavyDwellSeconds;
            if (current == AdaptivePowerTier.Heavy && target == AdaptivePowerTier.Code)
                return HeavyToCodeDwellSeconds;
            if (current == AdaptivePowerTier.Code && target == AdaptivePowerTier.Daily)
                return CodeToDailyDwellSeconds;
            if (current == AdaptivePowerTier.Daily && target == AdaptivePowerTier.Quiet)
                return DailyToQuietDwellSeconds;
            return 30;
        }

        private void TrimHistory(DateTime now)
        {
            DateTime cutoff = now.AddSeconds(-HistoryRetentionSeconds);
            while (_history.Count > 0 && _history.Peek().TimestampUtc < cutoff)
                _history.Dequeue();
        }

        private void ClearTransition()
        {
            _upshiftSinceUtc = null;
            _downshiftSinceUtc = null;
            _pendingTier = null;
            _dwellRemainingSeconds = 0;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static string FormatStatus(string text, WindowStats stats)
        {
            return text + FormatStats(stats);
        }

        private static string FormatStats(WindowStats stats)
        {
            string gpu = stats.GpuKnown ? stats.AverageGpu.ToString("0") + "%" : "未知";
            return "（均值 CPU " + stats.AverageCpu.ToString("0") + "% / GPU " + gpu + "）";
        }

        private static string GetTierName(AdaptivePowerTier tier)
        {
            switch (tier)
            {
                case AdaptivePowerTier.Code: return "代码";
                case AdaptivePowerTier.Heavy: return "重负载";
                case AdaptivePowerTier.Quiet: return "安静";
                default: return "日常";
            }
        }
    }
}
