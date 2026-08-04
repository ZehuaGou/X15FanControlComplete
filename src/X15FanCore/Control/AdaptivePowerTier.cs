using System;
using System.Collections.Generic;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    public enum AdaptivePowerTier
    {
        Daily = 0,
        Code = 1,
        Heavy = 2,
        Quiet = 3
    }

    /// <summary>
    /// 功耗等级工具：所有"功耗高低"比较必须经由此处，禁止直接使用
    /// tier &gt; tier / tier &lt; tier（枚举数值与功耗等级无关：Quiet=3 数值
    /// 最大但功耗最低）。
    /// </summary>
    public static class TierPower
    {
        public static int Rank(AdaptivePowerTier tier)
        {
            switch (tier)
            {
                case AdaptivePowerTier.Quiet: return 0;
                case AdaptivePowerTier.Daily: return 1;
                case AdaptivePowerTier.Code: return 2;
                case AdaptivePowerTier.Heavy: return 3;
                default: return 1;
            }
        }

        public static bool IsHigherPowerTier(AdaptivePowerTier candidate, AdaptivePowerTier reference)
        {
            return Rank(candidate) > Rank(reference);
        }

        public static bool IsLowerPowerTier(AdaptivePowerTier candidate, AdaptivePowerTier reference)
        {
            return Rank(candidate) < Rank(reference);
        }

        public static bool IsLowerOrEqualPowerTier(AdaptivePowerTier candidate, AdaptivePowerTier reference)
        {
            return Rank(candidate) <= Rank(reference);
        }

        public static bool IsHigherOrEqualPowerTier(AdaptivePowerTier candidate, AdaptivePowerTier reference)
        {
            return Rank(candidate) >= Rank(reference);
        }

        public static AdaptivePowerTier MinPowerTier(AdaptivePowerTier a, AdaptivePowerTier b)
        {
            return Rank(a) <= Rank(b) ? a : b;
        }

        // 降一个功耗档位（Heavy→Code→Daily→Quiet；Quiet 为最低）。
        public static AdaptivePowerTier LowerTier(AdaptivePowerTier tier)
        {
            switch (tier)
            {
                case AdaptivePowerTier.Heavy: return AdaptivePowerTier.Code;
                case AdaptivePowerTier.Code: return AdaptivePowerTier.Daily;
                case AdaptivePowerTier.Daily: return AdaptivePowerTier.Quiet;
                default: return AdaptivePowerTier.Quiet;
            }
        }
    }

    public sealed class AdaptivePowerSample
    {
        public DateTime TimestampUtc { get; set; }
        public double CpuUtilizationPercent { get; set; }
        public double CpuPerformancePercent { get; set; }
        // CPU 实际功耗（W）：当前无安全只读来源，默认 0（不可用）。
        // 有安全来源后接入判定；在接入前不得用估算值代替。
        public double CpuPowerWatts { get; set; }
        // GPU 字段仅作 CSV/日志诊断，不参与 CPU requested tier 判定
        // （架构收束 2026-08-02：GPU 高负载不得直接提升 CPU 功耗档位）。
        public double GpuUtilizationPercent { get; set; }
        public bool GpuTelemetryAvailable { get; set; }
        public double GpuPowerWatts { get; set; }
        public string GpuPState { get; set; }
        public double CpuTemperatureC { get; set; }
        public double GpuTemperatureC { get; set; }
    }

    // 声学/热治理状态：正常、接近声学上限、热饱和、临时散热增强、紧急。
    public enum CoolingState
    {
        Normal = 0,
        NearAcousticLimit = 1,
        ThermalSaturation = 2,
        TemporaryCoolingBoost = 3,
        Emergency = 4
    }

    /// <summary>
    /// 档位状态机的可观测诊断快照。供 CSV 和应用日志记录升档/降档瞬间的
    /// 原始证据，用于回答"为什么切换档位"。
    /// </summary>
    public sealed class AdaptiveTierDiagnostics
    {
        public AdaptivePowerTier CurrentTier;
        public AdaptivePowerTier? PendingTier;
        public double DwellElapsedSeconds;
        public double DwellRequiredSeconds;

        public double UpshiftAverageCpu;
        public double UpshiftAverageGpu;
        public double DownshiftAverageCpu;
        public double DownshiftAverageGpu;
        public double RecentPeakCpu;
        public double RecentPeakGpu;

        public bool GpuKnown;
        public bool NormalUpshiftEvidence;
        public bool StrongUpshiftEvidence;
        public bool DownshiftEvidence;
        public string Reason;

        // 声学治理输出（由 MainForm 填充，供 CSV/日志记录）。
        public AdaptivePowerTier EffectiveTier;
        public CoolingState CoolingState;
    }

    /// <summary>
    /// Four-level automatic classifier. Each transition moves only one tier,
    /// uses rolling load windows and has its own dwell time. Downshifting power
    /// is allowed while the machine is warm because lower power helps it cool;
    /// fan emergency floors remain independent from this classifier.
    ///
    /// 运行时参数（驻留时间、窗口、强升档、最短保持）全部来自
    /// AdaptivePowerSettings，阈值常量仅作默认标定参考。
    /// </summary>
    public sealed class AdaptivePowerTierController
    {
        // Load thresholds calibrated against a 23-hour real usage trace
        // (84,139 one-second samples, 2026-07-31/08-01): CPU utilization sits
        // in 0-5% (7%), 5-10% (22%), 10-15% (22%), 15-20% (20%), 20-35%
        // (25%), 35-50% (4%), 50%+ (0.6%).
        // A later overnight trace (2026-08-04) showed a stable resident-load
        // baseline of 15.0-15.7%. Replaying the 15/16/17/18/19/20 candidates
        // selected 17% as the smallest robust Code -> Daily threshold: 16%
        // was marginal, while 18% admitted earlier interactive quiet windows.
        //
        // 架构收束 (2026-08-02): CPU requested tier 只由 CPU 利用率、CPU
        // 实际功耗(有安全只读来源后接入)、CPU 温度、持续时间决定。GPU
        // 负载/温度不再参与 CPU 档位判定 (GPU 数据仅作 CSV/日志诊断)。
        public const double DailyToCodeCpuPercent = 25;
        public const double CodeToHeavyCpuPercent = 50;
        public const double QuietToDailyCpuPercent = 12;
        public const double DailyToQuietCpuAveragePercent = 8;

        // Downshift uses averages with hysteresis instead of the instantaneous
        // thresholds used by the previous implementation.
        public const double CodeToDailyCpuAveragePercent = 17;
        public const double HeavyToCodeCpuAveragePercent = 25;

        // Recent-peak gates for downshift: a load spike inside the peak window
        // cancels an otherwise eligible downshift.
        public const int QuietDownshiftMaxCpuPeakPercent = 30;
        public const int CodeDownshiftMaxCpuPeakPercent = 40;
        public const int HeavyDownshiftMaxCpuPeakPercent = 60;

        // 与 AdaptivePowerSettings 默认值保持一致；运行时以配置为准。
        public const int QuietToDailyDwellSeconds = 15;
        public const int DailyToCodeDwellSeconds = 30;
        public const int CodeToHeavyDwellSeconds = 30;
        public const int HeavyToCodeDwellSeconds = 60;
        public const int CodeToDailyDwellSeconds = 120;
        public const int DailyToQuietDwellSeconds = 120;
        public const int MinimumTierHoldSeconds = 20;
        public const int UpshiftEvidenceWindowSeconds = 8;
        public const int DownshiftAverageWindowSeconds = 30;
        public const int RecentPeakWindowSeconds = 15;

        // Strong evidence (sustained CPU >= 80%) means a compile/build load is
        // opening. It pierces the minimum tier hold but must still hold for
        // StrongUpshiftDwellSeconds (12s by default), only moves one adjacent
        // tier per completed upshift, and its evidence is discarded after each
        // completed upshift so the same history window cannot double-jump.
        // (GPU 不再参与强证据判定 - 架构收束 2026-08-02)
        public const double StrongUpshiftEvidenceCpuPercent = 80;
        public const int StrongUpshiftDwellSeconds = 12;

        // 施密特滞回：升档证据进入/取消阈值分离。证据在 [cancel, enter) 区间
        // 时信用缓慢衰减（一帧跌破不抖动）；低于 cancel 时快速衰减（间隔
        // 很远的短脉冲不能拼成一次持续负载）。
        public const double CodeEvidenceCancelCpuPercent = 20;
        public const double HeavyEvidenceCancelCpuPercent = 45;
        public const double StrongEvidenceCancelCpuPercent = 70;

        private const double QuietMaximumCpuTemperatureC = 85;
        private const double QuietMaximumTemperatureRiseCPerSecond = 0.50;
        private const int HistoryRetentionSeconds = 180;

        private sealed class LoadSample
        {
            public DateTime TimestampUtc;
            public double CpuLoad;
            // GPU 负载仅作 CSV/日志诊断，不参与 CPU 档位判定（架构收束）。
            public double GpuLoad;
            public bool GpuKnown;
            // CPU 实际功耗（W）：有安全只读来源后接入；当前不可用时为 0。
            public double CpuPowerWatts;
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

        private readonly AdaptivePowerSettings _settings;
        private readonly Queue<LoadSample> _history = new Queue<LoadSample>();
        private readonly AdaptiveTierDiagnostics _diagnostics = new AdaptiveTierDiagnostics();
        private AdaptivePowerTier _currentTier;
        private DateTime? _tierSinceUtc;
        private AdaptivePowerTier? _pendingTier;
        private double _dwellRemainingSeconds;
        private string _lastReason;
        private DateTime _lastSampleUtc = DateTime.MinValue;

        // 升档证据积分（施密特滞回）：证据达到进入阈值时 +1/s；在进入与
        // 取消阈值之间 -0.5/s（一帧跌破不抖动）；低于取消阈值 -2/s（间隔
        // 很远的短脉冲不能拼成一次持续负载）。起始帧计 0，满 required 秒
        // 才切换。强升档（含游戏证据）使用独立积分。
        private double _upshiftCreditSeconds;
        private double _strongCreditSeconds;
        private bool _lastStrongEnter;

        // 降档使用证据积分：满足条件时积分，中等负载时缓慢衰减，明显高
        // 负载时快速衰减。单帧不满足不再把已累计时间全部清零。
        private AdaptivePowerTier? _downshiftTarget;
        private double _downshiftCreditSeconds;

        public AdaptivePowerTierController()
            : this(new AdaptivePowerSettings())
        {
        }

        public AdaptivePowerTierController(AdaptivePowerSettings settings)
        {
            _settings = settings ?? new AdaptivePowerSettings();
            _currentTier = AdaptivePowerTier.Daily;
            _lastReason = "启动后保持日常档";
        }

        public AdaptivePowerTier CurrentTier { get { return _currentTier; } }
        public AdaptivePowerTier? PendingTier { get { return _downshiftTarget ?? _pendingTier; } }
        public double DwellRemainingSeconds { get { return _dwellRemainingSeconds; } }
        public string LastReason { get { return _lastReason ?? string.Empty; } }

        public AdaptiveTierDiagnostics Diagnostics
        {
            get { return _diagnostics; }
        }

        private double EffectiveCodeToDailyCpuAveragePercent
        {
            get
            {
                double configured = _settings.CodeToDailyCpuAveragePercent;
                return configured >= 15.0 && configured <= 20.0
                    ? configured
                    : CodeToDailyCpuAveragePercent;
            }
        }

        public void ForceTier(AdaptivePowerTier tier, string reason)
        {
            _currentTier = tier;
            _tierSinceUtc = DateTime.UtcNow;
            _upshiftCreditSeconds = 0;
            _strongCreditSeconds = 0;
            _pendingTier = null;
            _dwellRemainingSeconds = 0;
            ResetDownshift();
            _lastReason = reason ?? "固定策略";
        }

        // 启动档位识别：根据 DCHU 当前 PL1/PL2/TimeSeconds 反推档位。功耗
        // 值必须命中安全预设（与桥接白名单一致），自定义功耗不会被识别。
        public static bool TryGetTierForPower(
            int pl1,
            int pl2,
            uint timeSeconds,
            out AdaptivePowerTier tier)
        {
            tier = AdaptivePowerTier.Daily;
            if (!AdaptivePowerPresets.IsSafePreset(pl1, pl2, timeSeconds))
                return false;

            if (pl1 == (int)AdaptivePowerPresets.Quiet.Pl1Watts)
                tier = AdaptivePowerTier.Quiet;
            else if (pl1 == (int)AdaptivePowerPresets.Daily.Pl1Watts)
                tier = AdaptivePowerTier.Daily;
            else if (pl1 == (int)AdaptivePowerPresets.Code.Pl1Watts)
                tier = AdaptivePowerTier.Code;
            else
                tier = AdaptivePowerTier.Heavy;
            return true;
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

            double elapsedSeconds = _lastSampleUtc == DateTime.MinValue
                ? 1.0
                : Math.Max(0.05, Math.Min(10.0, (now - _lastSampleUtc).TotalSeconds));
            _lastSampleUtc = now;

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
                CpuPowerWatts = sample.CpuPowerWatts,
                CpuTemperatureC = sample.CpuTemperatureC,
                GpuTemperatureC = sample.GpuTemperatureC
            });
            TrimHistory(now);

            WindowStats upshift = Summarize(now, _settings.UpshiftEvidenceWindowSeconds);
            WindowStats average = Summarize(now, _settings.DownshiftAverageWindowSeconds);
            WindowStats recent = Summarize(now, _settings.RecentPeakWindowSeconds);
            bool quietThermalSafe = IsQuietThermallySafe(average);

            // CPU-only 判定（架构收束 2026-08-02）：所有升档/降档门禁只使用
            // CPU 利用率与温度；GPU 数据仅保留在诊断中。施密特滞回：enter
            // 阈值触发累计，cancel 阈值决定衰减快慢。
            bool codeEnter = upshift.AverageCpu >= DailyToCodeCpuPercent;
            bool codeCancel = upshift.AverageCpu >= CodeEvidenceCancelCpuPercent;
            bool heavyEnter = upshift.AverageCpu >= CodeToHeavyCpuPercent;
            bool heavyCancel = upshift.AverageCpu >= HeavyEvidenceCancelCpuPercent;
            bool dailyEnter = upshift.AverageCpu >= QuietToDailyCpuPercent;
            bool dailyCancel = upshift.AverageCpu >= 8;

            // 强证据：仅 CPU 高负载（编译等场景）可触发；GPU 利用率/功耗
            // 不再构成 CPU 档位强证据。
            bool strongCpu = upshift.AverageCpu >= StrongUpshiftEvidenceCpuPercent;
            bool strongEnter = strongCpu;
            bool strongCancel = upshift.AverageCpu >= StrongEvidenceCancelCpuPercent;

            // 强升档积分：只有强证据成立才累计；首次成立的帧不计（与 dwell
            // 语义一致），否则按滞回衰减。独立于普通升档积分。
            if (strongEnter)
            {
                if (_lastStrongEnter)
                    _strongCreditSeconds += elapsedSeconds;
                else
                    _strongCreditSeconds = 0;
                _lastStrongEnter = true;
            }
            else
            {
                DecayStrongUpshift(strongCancel, elapsedSeconds);
                _lastStrongEnter = false;
            }
            bool dailyToQuiet = average.TemperaturesKnown && quietThermalSafe &&
                                average.AverageCpu <= DailyToQuietCpuAveragePercent &&
                                 recent.MaximumCpu < QuietDownshiftMaxCpuPeakPercent;
            bool codeToDaily =
                               average.AverageCpu <= EffectiveCodeToDailyCpuAveragePercent &&
                               recent.MaximumCpu < CodeDownshiftMaxCpuPeakPercent;
            bool heavyToCode =
                               average.AverageCpu <= HeavyToCodeCpuAveragePercent &&
                               recent.MaximumCpu < HeavyDownshiftMaxCpuPeakPercent;

            if (_currentTier == AdaptivePowerTier.Quiet)
            {
                if (dailyEnter)
                {
                    ResetDownshift();
                    return ConsiderUpshiftCredit(AdaptivePowerTier.Daily, now, elapsedSeconds, "负载持续升高，准备进入日常档", upshift, recent, strongEnter, strongCancel);
                }

                DecayNormalUpshift(dailyCancel, elapsedSeconds);
                _lastReason = FormatStatus("保持安静档，等待日常负载", upshift);
            }
            else if (_currentTier == AdaptivePowerTier.Daily)
            {
                if (codeEnter)
                {
                    // 升档与降档互斥：出现升档证据立即清除反向降档积分，
                    // PendingTier 不得被旧的 Quiet 目标遮挡。
                    ResetDownshift();
                    return ConsiderUpshiftCredit(AdaptivePowerTier.Code, now, elapsedSeconds, "中等负载持续，准备进入代码档", upshift, recent, strongEnter, strongCancel);
                }

                // 升档证据未达进入阈值：按滞回衰减（介于进入/取消阈值之间
                // 慢衰减，低于取消阈值快衰减），不影响降档积分。
                DecayNormalUpshift(codeCancel, elapsedSeconds);
                if (dailyToQuiet)
                    return AccumulateDownshift(AdaptivePowerTier.Quiet, now, elapsedSeconds,
                        _settings.DailyToQuietDwellSeconds, "极低负载和温度持续稳定，准备进入安静档", average, recent);

                DecayDownshift(elapsedSeconds, codeEnter);
                _lastReason = FormatStatus("保持日常档，负载未达到代码档条件", upshift);
            }
            else if (_currentTier == AdaptivePowerTier.Code)
            {
                if (heavyEnter)
                {
                    ResetDownshift();
                    return ConsiderUpshiftCredit(AdaptivePowerTier.Heavy, now, elapsedSeconds, "高负载持续，准备进入重负载档", upshift, recent, strongEnter, strongCancel);
                }

                DecayNormalUpshift(heavyCancel, elapsedSeconds);
                if (codeToDaily)
                    return AccumulateDownshift(AdaptivePowerTier.Daily, now, elapsedSeconds,
                        _settings.CodeToDailyDwellSeconds, "低负载和温度持续稳定，准备回到日常档", average, recent);

                DecayDownshift(elapsedSeconds, heavyEnter);
                _lastReason = FormatStatus("保持代码档，等待负载持续降低", average);
            }
            else if (_currentTier == AdaptivePowerTier.Heavy)
            {
                DecayNormalUpshift(false, elapsedSeconds);
                if (heavyToCode)
                    return AccumulateDownshift(AdaptivePowerTier.Code, now, elapsedSeconds,
                        _settings.HeavyToCodeDwellSeconds, "负载和温度持续回落，准备进入代码档", average, recent);

                DecayDownshift(elapsedSeconds, heavyEnter);
                _lastReason = FormatStatus("保持重负载档，等待负载持续降低", average);
            }
            else
            {
                ClearTransition();
                _lastReason = "未知档位，保持当前状态";
            }

            UpdateDiagnostics(upshift, average, recent, codeEnter, strongEnter, heavyToCode || codeToDaily || dailyToQuiet);
            return _currentTier;
        }

        // 升档（证据积分版）：只有相邻档位；普通/强证据独立积分；强证据
        // 可穿透最短保持，但驻留时间不得低于 StrongUpshiftDwellSeconds。
        // 完成后清空历史与积分（同一证据窗口不得连续跳两档）。
        private AdaptivePowerTier ConsiderUpshiftCredit(
            AdaptivePowerTier target,
            DateTime now,
            double elapsedSeconds,
            string reason,
            WindowStats stats,
            WindowStats recent,
            bool strongEnter,
            bool strongCancel)
        {
            bool firstFrame = _pendingTier != target;
            _pendingTier = target;
            if (firstFrame)
            {
                // 起始帧不计积分（进入后满 required 秒才切换）。
                _upshiftCreditSeconds = 0;
            }
            else
            {
                _upshiftCreditSeconds += elapsedSeconds;
            }
            // _strongCreditSeconds 已由 Update 顶部统一累计/衰减。

            // 最短保持：普通升档受限于 MinimumTierHoldSeconds，强升档穿透。
            if (_tierSinceUtc.HasValue && !strongEnter &&
                (now - _tierSinceUtc.Value).TotalSeconds < _settings.MinimumTierHoldSeconds)
            {
                _upshiftCreditSeconds = 0;
                _strongCreditSeconds = 0;
                _pendingTier = null;
                ResetDownshift();
                _lastReason = "刚切换档位，保持" + GetTierName(_currentTier) + "至少" + _settings.MinimumTierHoldSeconds + "秒";
                UpdateDiagnostics(stats, stats, recent, true, strongEnter, false);
                return _currentTier;
            }

            double dwell = GetTransitionDwellSeconds(_currentTier, target);
            double credit = strongEnter ? _strongCreditSeconds : _upshiftCreditSeconds;
            double required = strongEnter
                ? Math.Min(dwell, _settings.StrongUpshiftDwellSeconds)
                : dwell;
            _dwellRemainingSeconds = Math.Max(0, required - credit);
            _lastReason = reason + "，还需" + Math.Ceiling(_dwellRemainingSeconds) + "秒" + FormatStats(stats);

            if (credit < required)
            {
                UpdateDiagnostics(stats, stats, recent, true, strongEnter, false);
                return _currentTier;
            }

            _currentTier = target;
            _tierSinceUtc = now;
            _upshiftCreditSeconds = 0;
            _strongCreditSeconds = 0;
            _pendingTier = null;
            // 升档完成后强证据标记重置：同一证据窗口不得连续跳两档，
            // 新证据需要重新确认（起始帧不计）。
            _lastStrongEnter = false;
            ResetDownshift();
            // 升档完成：丢弃历史证据窗口，同一强负载证据不能连续跳两档。
            _history.Clear();
            _lastReason = "已切换到" + GetTierName(target) + "档";
            UpdateDiagnostics(stats, stats, recent, true, strongEnter, false);
            return _currentTier;
        }

        // 普通升档证据滞回衰减：aboveCancel=true 时（进入与取消阈值之间）
        // 慢衰减，一帧跌破不抖动；false 时（低于取消阈值）快衰减，间隔
        // 很远的短脉冲不能拼成一次持续负载。
        private void DecayNormalUpshift(bool aboveCancel, double elapsedSeconds)
        {
            if (_upshiftCreditSeconds <= 0)
            {
                _pendingTier = null;
                return;
            }

            _upshiftCreditSeconds = Math.Max(0,
                _upshiftCreditSeconds - (aboveCancel ? 0.5 : 2.0) * elapsedSeconds);
            if (_upshiftCreditSeconds <= 0)
            {
                _pendingTier = null;
                _dwellRemainingSeconds = 0;
            }
        }

        // 强升档证据滞回衰减（独立积分）。
        private void DecayStrongUpshift(bool aboveCancel, double elapsedSeconds)
        {
            if (_strongCreditSeconds <= 0)
                return;

            _strongCreditSeconds = Math.Max(0,
                _strongCreditSeconds - (aboveCancel ? 0.5 : 2.0) * elapsedSeconds);
            if (_strongCreditSeconds <= 0)
                _dwellRemainingSeconds = 0;
        }

        // 降档积分：满足条件每秒 +1；不满足但未达升档阈值每秒 -1；明显
        // 高负载每秒 -2。单帧波动不再清空全部证据。
        private AdaptivePowerTier AccumulateDownshift(
            AdaptivePowerTier target,
            DateTime now,
            double elapsedSeconds,
            double requiredSeconds,
            string reason,
            WindowStats stats,
            WindowStats recent)
        {
            if (_downshiftTarget != target)
            {
                _downshiftTarget = target;
                _downshiftCreditSeconds = 0;
            }

            _downshiftCreditSeconds += elapsedSeconds;
            _dwellRemainingSeconds = Math.Max(0, requiredSeconds - _downshiftCreditSeconds);
            _lastReason = reason + "，还需" + Math.Ceiling(_dwellRemainingSeconds) + "秒" + FormatStats(stats);

            if (_downshiftCreditSeconds < requiredSeconds)
            {
                UpdateDiagnostics(stats, stats, recent, false, false, true);
                return _currentTier;
            }

            _currentTier = target;
            _tierSinceUtc = now;
            ResetDownshift();
            _dwellRemainingSeconds = 0;
            ClearTransition();
            _lastReason = "已切换到" + GetTierName(target) + "档";
            UpdateDiagnostics(stats, stats, recent, false, false, true);
            return _currentTier;
        }

        private void DecayDownshift(double elapsedSeconds, bool strongHighLoad)
        {
            if (!_downshiftTarget.HasValue)
                return;

            _downshiftCreditSeconds = Math.Max(0,
                _downshiftCreditSeconds - (strongHighLoad ? 2.0 : 1.0) * elapsedSeconds);
            if (_downshiftCreditSeconds <= 0)
            {
                ResetDownshift();
                _dwellRemainingSeconds = 0;
            }
        }

        private void ResetDownshift()
        {
            _downshiftTarget = null;
            _downshiftCreditSeconds = 0;
        }

        private void UpdateDiagnostics(
            WindowStats upshift,
            WindowStats average,
            WindowStats recent,
            bool normalEvidence,
            bool strongEvidence,
            bool downshiftEvidence)
        {
            _diagnostics.CurrentTier = _currentTier;
            _diagnostics.PendingTier = _downshiftTarget ?? _pendingTier;
            _diagnostics.DwellElapsedSeconds = _downshiftTarget.HasValue
                ? _downshiftCreditSeconds
                : Math.Max(_upshiftCreditSeconds, _strongCreditSeconds);
            _diagnostics.DwellRequiredSeconds = _dwellRemainingSeconds + _diagnostics.DwellElapsedSeconds;
            _diagnostics.UpshiftAverageCpu = upshift.AverageCpu;
            _diagnostics.UpshiftAverageGpu = upshift.GpuKnown ? upshift.AverageGpu : -1;
            _diagnostics.DownshiftAverageCpu = average.AverageCpu;
            _diagnostics.DownshiftAverageGpu = average.GpuKnown ? average.AverageGpu : -1;
            _diagnostics.RecentPeakCpu = recent.MaximumCpu;
            _diagnostics.RecentPeakGpu = recent.GpuKnown ? recent.MaximumGpu : -1;
            _diagnostics.GpuKnown = upshift.GpuKnown;
            _diagnostics.NormalUpshiftEvidence = normalEvidence;
            _diagnostics.StrongUpshiftEvidence = strongEvidence;
            _diagnostics.DownshiftEvidence = downshiftEvidence;
            _diagnostics.Reason = _lastReason ?? string.Empty;
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
                if (item.CpuTemperatureC > 0)
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
            // 仅 CPU 温度与温升率参与（架构收束 2026-08-02）：GPU 温度不再
            // 门禁 CPU 档位。
            return stats.TemperaturesKnown &&
                   stats.MaximumCpuTemperature < QuietMaximumCpuTemperatureC &&
                   stats.CpuTemperatureRise <= QuietMaximumTemperatureRiseCPerSecond;
        }

        private int GetTransitionDwellSeconds(AdaptivePowerTier current, AdaptivePowerTier target)
        {
            if (current == AdaptivePowerTier.Quiet && target == AdaptivePowerTier.Daily)
                return _settings.QuietToDailyDwellSeconds;
            if (current == AdaptivePowerTier.Daily && target == AdaptivePowerTier.Code)
                return _settings.DailyToCodeDwellSeconds;
            if (current == AdaptivePowerTier.Code && target == AdaptivePowerTier.Heavy)
                return _settings.CodeToHeavyDwellSeconds;
            if (current == AdaptivePowerTier.Heavy && target == AdaptivePowerTier.Code)
                return _settings.HeavyToCodeDwellSeconds;
            if (current == AdaptivePowerTier.Code && target == AdaptivePowerTier.Daily)
                return _settings.CodeToDailyDwellSeconds;
            if (current == AdaptivePowerTier.Daily && target == AdaptivePowerTier.Quiet)
                return _settings.DailyToQuietDwellSeconds;
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
            _upshiftCreditSeconds = 0;
            _strongCreditSeconds = 0;
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
