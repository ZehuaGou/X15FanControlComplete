using System;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    internal sealed class ChannelController
    {
        private readonly FanKind _fanKind;
        private readonly TemperatureFilter _filter;
        private FanChannelProfile _profile;
        // 声学软上限的快速升温突破阈值（°C/s）：温升率超过它时曲线目标
        // 不受软上限约束，保证快速升温安全。由 MainForm 按配置注入。
        private double _acousticFastRiseCPerSecond = 1.0;
        // 跨风扇 Emergency 共享散热下限余量（%）：另一侧进入 Emergency 时，
        // 本通道目标不低于 软上限 + 该余量（候选值，未硬件标定）。
        private const double EmergencySharedBreakthroughMarginPercent = 5;
        // 快速温升结束后不让较高的历史目标继续占用降速保持。
        // 2%/s 从约 77% 回到 69% 需约 4 秒，避免噪声长时间滞留；
        // Emergency/快速温升期间不使用该回归速率。
        private const double AcousticCeilingReturnRatePercentPerSecond = 2.0;
        private bool _initialized;
        private double _acceptedTarget;
        private double _applied;
        private double _lastWritten;
        private DateTime _lastWriteUtc;
        private DateTime? _pendingDownSinceUtc;
        private int _invalidSamples;

        // Rate-of-rise tracking
        private double _lastTemperatureForRise;
        private DateTime _lastRiseSampleUtc;
        private double _temperatureRiseRateCPerSec;

        // Write verification
        private double _ecLastReadbackPercent;
        private int _consecutiveMismatchCount;
        private double _lastWriteVerificationTarget;
        private bool _pendingWriteVerification;
        private bool _lastWriteVerified;
        private DateTime? _externalMismatchSinceUtc;
        private DateTime _lastExternalMismatchSampleUtc;
        private const double ExternalOverrideRequiredSeconds = 5.0;
        private const double ExternalOverrideMaximumSampleGapSeconds = 3.0;

        // Stable zone hysteresis
        private bool _inStableZone;
        private double _stableZoneEntryPercent;
        private double _stableZoneExitPercent;

        public ChannelController(FanKind fanKind, FanChannelProfile profile)
        {
            _fanKind = fanKind;
            _profile = profile ?? throw new ArgumentNullException("profile");
            _filter = new TemperatureFilter(profile.FilterWindowSamples, profile.FastEmaAlpha, profile.SlowEmaAlpha);
        }

        public void SetProfile(FanChannelProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException("profile");
            _filter.Configure(profile.FilterWindowSamples, profile.FastEmaAlpha, profile.SlowEmaAlpha);
            UpdateStableZoneThresholds();
        }

        // 换档保留状态版本：更新曲线/速率/稳定区/紧急阈值，但保留
        // _applied、_acceptedTarget、滤波 EMA 与窗口、_lastWritten、
        // _lastWriteUtc 和外部覆盖计数。新曲线目标通过正常 ramp 爬升，
        // 不会因换档直接跳到新曲线值。
        public void SetProfilePreservingState(FanChannelProfile profile)
        {
            _profile = profile ?? throw new ArgumentNullException("profile");
            _filter.Configure(profile.FilterWindowSamples, profile.FastEmaAlpha, profile.SlowEmaAlpha);
            UpdateStableZoneThresholds();
        }

        // 注入声学软上限的快速升温突破阈值（来自 AdaptivePowerSettings）。
        public void SetAcousticFastRiseBreakthrough(double cPerSecond)
        {
            if (cPerSecond > 0)
                _acousticFastRiseCPerSecond = cPerSecond;
        }

        public void Reset()
        {
            _filter.Reset();
            _initialized = false;
            _acceptedTarget = 0;
            _applied = 0;
            _lastWritten = -100;
            _lastWriteUtc = DateTime.MinValue;
            _pendingDownSinceUtc = null;
            _invalidSamples = 0;
            _lastTemperatureForRise = -1;
            _lastRiseSampleUtc = DateTime.MinValue;
            _temperatureRiseRateCPerSec = 0;
            _ecLastReadbackPercent = 0;
            _consecutiveMismatchCount = 0;
            _lastWriteVerificationTarget = -1;
            _pendingWriteVerification = false;
            _lastWriteVerified = false;
            _externalMismatchSinceUtc = null;
            _lastExternalMismatchSampleUtc = DateTime.MinValue;
            _inStableZone = false;
            UpdateStableZoneThresholds();
        }

        // The stable zone hysteresis band must be derived from the profile's
        // zone, not hardcoded.  The previous fixed 51%/49% thresholds silently
        // disabled the stable zone for every profile whose zone maximum was
        // below 51% (e.g. the Daily profile's 45-50% CPU / 43-50% GPU zones).
        // For the Code profile (50-55%) this reproduces the historical 51/49
        // band exactly, keeping existing behavior unchanged.
        private void UpdateStableZoneThresholds()
        {
            double zoneMinimum = Math.Min(_profile.StableZoneMinimumPercent, _profile.StableZoneMaximumPercent);
            double zoneMaximum = Math.Max(_profile.StableZoneMinimumPercent, _profile.StableZoneMaximumPercent);
            _stableZoneEntryPercent = Math.Max(0, Math.Min(zoneMinimum + 1, zoneMaximum));
            _stableZoneExitPercent = Math.Max(0, zoneMinimum - 1);
        }

        public ChannelDecision Update(
            int instantTemperatureC,
            int currentDutyPercent,
            double assistAdditionPercent,
            DateTime timestampUtc,
            double elapsedSeconds,
            bool emergencyOverride = false)
        {
            ChannelDecision decision = new ChannelDecision
            {
                Fan = _fanKind,
                InstantTemperatureC = instantTemperatureC,
                Detail = string.Empty,
                Reason = DecisionReason.Normal,
                State = ControlState.Normal,
                TemperatureRiseRateCPerSec = _temperatureRiseRateCPerSec
            };

            if (!IsTemperatureValid(instantTemperatureC))
            {
                _invalidSamples++;
                decision.RequestAutoFallback = _invalidSamples >= Math.Max(1, _profile.ConsecutiveInvalidSamplesBeforeAuto);
                decision.Reason = DecisionReason.InvalidSensor;
                decision.State = ControlState.InvalidSensor;
                decision.Detail = "Invalid temperature " + instantTemperatureC + " °C (" + _invalidSamples + ")";
                decision.AppliedPercent = _initialized ? _applied : currentDutyPercent;
                decision.AcceptedTargetPercent = decision.AppliedPercent;
                decision.RawTargetPercent = decision.AppliedPercent;
                return decision;
            }

            _invalidSamples = 0;

            // Calculate temperature rate-of-rise
            UpdateTemperatureRiseRate(instantTemperatureC, timestampUtc);
            decision.TemperatureRiseRateCPerSec = _temperatureRiseRateCPerSec;

            double controlTemperature = _filter.Add(instantTemperatureC);
            decision.FastTemperatureC = _filter.FastEma;
            decision.SlowTemperatureC = _filter.SlowEma;
            decision.ControlTemperatureC = controlTemperature;

            bool firstValidUpdate = !_initialized;
            if (firstValidUpdate)
            {
                _applied = Clamp(currentDutyPercent, 0, 100);
                _acceptedTarget = _applied;
                _lastWritten = _applied;
                _lastWriteUtc = timestampUtc;
                _initialized = true;
                decision.Reason = DecisionReason.Initializing;
                decision.State = ControlState.Normal;
            }

            double rawTarget = FanCurve.Interpolate(_profile.Curve, controlTemperature);
            rawTarget = Clamp(rawTarget + Math.Max(0, assistAdditionPercent), 0, 100);
            rawTarget = ApplyStableZone(rawTarget);
            // 声学软上限：曲线目标受 SoftMaximumFanDutyPercent 约束。这是
            // 软上限而非安全上限——紧急档（直接设置 applied）、快速升温
            // 与 RPM 保护可以立即突破；emergencyOverride（跨风扇共同散热
            // 的 Emergency/快速温升保护）同样突破。
            bool acousticCeilingActive = _profile.SoftMaximumFanDutyPercent > 0 &&
                _profile.SoftMaximumFanDutyPercent < 100 &&
                _temperatureRiseRateCPerSec <= _acousticFastRiseCPerSecond &&
                !emergencyOverride;
            if (acousticCeilingActive)
            {
                rawTarget = Math.Min(rawTarget, _profile.SoftMaximumFanDutyPercent);
            }

            // 共享散热增强（跨风扇 Emergency，当前周期生效）：本通道未达自身
            // 紧急阈值但另一通道进入 Emergency 时，目标不得低于软上限 + 余量
            // ——两个风扇在同一周期内共同散热、突破软上限（不依赖上一周期
            // ChannelDecision）。应用值仍按正常 ramp 爬升，不跳变。
            if (emergencyOverride && !IsOwnEmergency(instantTemperatureC))
            {
                rawTarget = Math.Max(rawTarget, _profile.SoftMaximumFanDutyPercent + EmergencySharedBreakthroughMarginPercent);
            }
            decision.RawTargetPercent = rawTarget;

            // Determine effective ramp rates based on temperature velocity
            double upRate = _profile.UpRatePercentPerSecond;
            double emergencyBoostRate = upRate;

            // Boost ramp rate when temperature is rising fast
            if (_temperatureRiseRateCPerSec > 2.0)
            {
                emergencyBoostRate = Math.Max(upRate, 8.0);
            }
            else if (_temperatureRiseRateCPerSec > 1.0)
            {
                emergencyBoostRate = Math.Max(upRate, 4.0);
            }

            bool emergencyStage3 = instantTemperatureC >= _profile.EmergencyStage3TemperatureC && _profile.EmergencyStage3TemperatureC > 0;
            bool emergencyStage2 = instantTemperatureC >= _profile.EmergencyStage2TemperatureC && _profile.EmergencyStage2TemperatureC > 0;
            bool emergencyStage1 = instantTemperatureC >= _profile.EmergencyStage1TemperatureC && _profile.EmergencyStage1TemperatureC > 0;

            if (emergencyStage3)
            {
                _acceptedTarget = Math.Max(_acceptedTarget, _profile.EmergencyStage3Percent);
                _applied = Math.Max(_applied, _profile.EmergencyStage3Percent);
                decision.Reason = DecisionReason.EmergencyStage3;
                decision.State = ControlState.Emergency;
                decision.Detail = "Stage 3 emergency";
                _pendingDownSinceUtc = null;
            }
            else if (emergencyStage2)
            {
                _acceptedTarget = Math.Max(_acceptedTarget, _profile.EmergencyStage2Percent);
                _applied = Math.Max(_applied, _profile.EmergencyStage2Percent);
                decision.Reason = DecisionReason.EmergencyStage2;
                decision.State = ControlState.Emergency;
                decision.Detail = "Stage 2 emergency";
                _pendingDownSinceUtc = null;
            }
            else if (emergencyStage1)
            {
                _acceptedTarget = Math.Max(Math.Max(_acceptedTarget, rawTarget), _profile.EmergencyStage1Percent);
                // Stage 1 ramps quickly instead of snapping: the target jumps
                // to the floor immediately, but the applied duty climbs at a
                // bounded rate capped by the ACCEPTED target, not by the floor
                // itself.  Capping at the 75% floor would strand the fan below
                // a higher curve demand (e.g. 85% at 89C) indefinitely.  Stage
                // 2/3 stay immediate because those are real thermal emergencies.
                double stage1RampRate = Math.Max(4.0, _profile.UpRatePercentPerSecond * 4.0);
                double stage1Limit = Math.Min(_acceptedTarget, _applied + stage1RampRate * Math.Max(0.01, elapsedSeconds));
                _applied = Math.Max(_applied, stage1Limit);
                decision.Reason = DecisionReason.EmergencyStage1;
                decision.State = ControlState.Emergency;
                decision.Detail = "Stage 1 emergency";
                _pendingDownSinceUtc = null;
            }
            else
            {
                if (firstValidUpdate)
                {
                    // The duty observed while EC automatic control was active is
                    // not a target previously accepted by this controller. Using
                    // it as one applies hysteresis to an unrelated starting value
                    // and can pin a high startup duty indefinitely.
                    _acceptedTarget = rawTarget;
                    _pendingDownSinceUtc = null;
                }
                else
                {
                    UpdateAcceptedTarget(rawTarget, controlTemperature, timestampUtc);
                }
                RampAppliedTarget(Math.Max(0.01, elapsedSeconds), emergencyBoostRate);

                // 声学上限恢复后，立即废弃快速温升期间留下的超上限
                // accepted target，不让 DownHold 把 76%~80% 的噪声再保持
                // 15 秒或更久。Applied 以受限速率回归，不瞬间降扇。
                if (acousticCeilingActive &&
                    (_acceptedTarget > _profile.SoftMaximumFanDutyPercent ||
                     _applied > _profile.SoftMaximumFanDutyPercent))
                {
                    _acceptedTarget = Math.Min(_acceptedTarget, _profile.SoftMaximumFanDutyPercent);
                    _applied = Math.Max(
                        _profile.SoftMaximumFanDutyPercent,
                        _applied - AcousticCeilingReturnRatePercentPerSecond * Math.Max(0.01, elapsedSeconds));
                    _pendingDownSinceUtc = null;
                }
            }

            _acceptedTarget = Clamp(_acceptedTarget, 0, 100);
            _applied = Clamp(_applied, 0, 100);

            decision.AcceptedTargetPercent = _acceptedTarget;
            decision.AppliedPercent = _applied;

            // Determine control state for UI
            if (decision.State == ControlState.Normal || decision.State == ControlState.Emergency)
            {
                if (_pendingDownSinceUtc.HasValue && _applied >= _acceptedTarget - 0.5)
                {
                    double remaining = Math.Max(0, _profile.DownHoldSeconds - (timestampUtc - _pendingDownSinceUtc.Value).TotalSeconds);
                    decision.DownHoldRemainingSeconds = remaining;
                    decision.State = remaining > 0 ? ControlState.DownHold : ControlState.RampingDown;
                }
                else if (_applied < _acceptedTarget - 0.5)
                {
                    decision.State = ControlState.RampingUp;
                }
                else if (_applied > _acceptedTarget + 0.5)
                {
                    decision.State = ControlState.RampingDown;
                }
                else if (_profile.StableZoneEnabled &&
                         _applied >= _profile.StableZoneMinimumPercent &&
                         _applied <= _profile.StableZoneMaximumPercent)
                {
                    decision.State = ControlState.StableZone;
                }
            }

            // Determine whether to write
            double writeDelta = Math.Abs(_applied - _lastWritten);
            bool writeIntervalElapsed = (timestampUtc - _lastWriteUtc).TotalSeconds >= Math.Max(0.5, _profile.MaximumWriteIntervalSeconds);
            bool emergency = decision.Reason == DecisionReason.EmergencyStage1 || decision.Reason == DecisionReason.EmergencyStage2;
            decision.ShouldWrite = emergency || writeDelta >= Math.Max(0.1, _profile.MinimumWriteDeltaPercent) || writeIntervalElapsed;
            decision.WritePercent = Clamp((int)Math.Round(_applied), 0, 100);

            return decision;
        }

        public void MarkWritten(int writtenPercent, DateTime timestampUtc)
        {
            // A ramping controller can issue a different target every sample.
            // Mismatches against different targets are not continuous evidence
            // that another controller is overriding one specific write.
            if (_lastWriteVerificationTarget < 0 ||
                Math.Abs(writtenPercent - _lastWriteVerificationTarget) > 0.5)
            {
                ResetExternalOverrideEvidence();
            }
            _lastWritten = writtenPercent;
            _lastWriteUtc = timestampUtc;
            _pendingWriteVerification = true;
            _lastWriteVerificationTarget = writtenPercent;
            _lastWriteVerified = false;
        }

        public FanWriteReadbackStatus ObserveEcReadback(double readbackPercent, DateTime timestampUtc)
        {
            _ecLastReadbackPercent = readbackPercent;
            bool hasExpectedWrite = _initialized && _lastWritten >= 0;
            bool verified = hasExpectedWrite && Math.Abs(readbackPercent - _lastWritten) <= 2.0;
            bool overridden = hasExpectedWrite && CheckExternalOverride(readbackPercent, timestampUtc);

            if (verified)
                _pendingWriteVerification = false;
            _lastWriteVerified = verified;

            return new FanWriteReadbackStatus
            {
                HasExpectedWrite = hasExpectedWrite,
                ExpectedPercent = hasExpectedWrite ? _lastWritten : 0,
                ObservedPercent = readbackPercent,
                Verified = _lastWriteVerified,
                ExternalOverrideDetected = overridden
            };
        }

        public FanWriteReadbackStatus ObserveEcReadback(double readbackPercent)
        {
            return ObserveEcReadback(readbackPercent, DateTime.UtcNow);
        }

        public int ConsecutiveMismatchCount => _consecutiveMismatchCount;

        public bool CheckExternalOverride(double currentReadbackPercent, DateTime timestampUtc)
        {
            if (!_initialized || _lastWritten < 0)
                return false;

            double diff = Math.Abs(currentReadbackPercent - _lastWritten);
            if (diff > 3.0)
            {
                bool discontinuous = !_externalMismatchSinceUtc.HasValue ||
                    _lastExternalMismatchSampleUtc == DateTime.MinValue ||
                    timestampUtc <= _lastExternalMismatchSampleUtc ||
                    (timestampUtc - _lastExternalMismatchSampleUtc).TotalSeconds >
                        ExternalOverrideMaximumSampleGapSeconds;

                if (discontinuous)
                {
                    _externalMismatchSinceUtc = timestampUtc;
                    _consecutiveMismatchCount = 1;
                }
                else
                {
                    _consecutiveMismatchCount++;
                }
                _lastExternalMismatchSampleUtc = timestampUtc;

                // Require both repeated samples and a real-time dwell against
                // one stable write target.  Three 500ms startup frames only
                // prove EC response latency, not an external controller.
                return _consecutiveMismatchCount >= 3 &&
                    (timestampUtc - _externalMismatchSinceUtc.Value).TotalSeconds >=
                        ExternalOverrideRequiredSeconds;
            }

            // A matching ordinary control snapshot breaks continuity.
            ResetExternalOverrideEvidence();

            return false;
        }

        private void ResetExternalOverrideEvidence()
        {
            _consecutiveMismatchCount = 0;
            _externalMismatchSinceUtc = null;
            _lastExternalMismatchSampleUtc = DateTime.MinValue;
        }

        public bool PendingWriteVerification => _pendingWriteVerification;
        public double LastWriteVerificationTarget => _lastWriteVerificationTarget;

        private void UpdateTemperatureRiseRate(int instantTemperatureC, DateTime timestampUtc)
        {
            if (_lastTemperatureForRise < 0 || _lastRiseSampleUtc == DateTime.MinValue)
            {
                _lastTemperatureForRise = instantTemperatureC;
                _lastRiseSampleUtc = timestampUtc;
                _temperatureRiseRateCPerSec = 0;
                return;
            }

            double elapsed = Math.Max(0.1, (timestampUtc - _lastRiseSampleUtc).TotalSeconds);
            double rise = instantTemperatureC - _lastTemperatureForRise;

            // Exponential moving average for rate
            double instantRate = rise / elapsed;
            _temperatureRiseRateCPerSec = _temperatureRiseRateCPerSec * 0.7 + instantRate * 0.3;

            _lastTemperatureForRise = instantTemperatureC;
            _lastRiseSampleUtc = timestampUtc;
        }

        private void UpdateAcceptedTarget(double rawTarget, double controlTemperature, DateTime timestampUtc)
        {
            double deadband = Math.Max(0, _profile.TargetDeadbandPercent);
            if (rawTarget >= _acceptedTarget + deadband)
            {
                _acceptedTarget = rawTarget;
                _pendingDownSinceUtc = null;
                return;
            }

            if (rawTarget > _acceptedTarget - deadband)
            {
                _pendingDownSinceUtc = null;
                return;
            }

            double releaseTemperature = FanCurve.ApproximateTemperatureForPower(_profile.Curve, _acceptedTarget) - Math.Max(0, _profile.HysteresisC);
            if (controlTemperature > releaseTemperature)
            {
                _pendingDownSinceUtc = null;
                return;
            }

            if (!_pendingDownSinceUtc.HasValue)
            {
                _pendingDownSinceUtc = timestampUtc;
                return;
            }

            if ((timestampUtc - _pendingDownSinceUtc.Value).TotalSeconds >= Math.Max(0, _profile.DownHoldSeconds))
            {
                _acceptedTarget = rawTarget;
                _pendingDownSinceUtc = null;
            }
        }

        private void RampAppliedTarget(double elapsedSeconds, double boostUpRate)
        {
            if (_acceptedTarget > _applied)
            {
                double maximumIncrease = Math.Max(0.1, boostUpRate) * elapsedSeconds;
                _applied = Math.Min(_acceptedTarget, _applied + maximumIncrease);
            }
            else if (_acceptedTarget < _applied)
            {
                double maximumDecrease = Math.Max(0.05, _profile.DownRatePercentPerSecond) * elapsedSeconds;
                _applied = Math.Max(_acceptedTarget, _applied - maximumDecrease);
            }
        }

        private double ApplyStableZone(double target)
        {
            if (!_profile.StableZoneEnabled)
            {
                _inStableZone = false;
                return target;
            }

            double holdPct = Clamp(_profile.StableZoneHoldPercent,
                Math.Min(_profile.StableZoneMinimumPercent, _profile.StableZoneMaximumPercent),
                Math.Max(_profile.StableZoneMinimumPercent, _profile.StableZoneMaximumPercent));

            double entryThreshold = _stableZoneEntryPercent; // 51% to enter
            double exitThreshold = _stableZoneExitPercent;   // 49% to exit
            double zoneMax = Math.Max(_profile.StableZoneMinimumPercent, _profile.StableZoneMaximumPercent);

            if (_inStableZone)
            {
                // Already inside: stay until target drops below exit threshold
                if (target >= exitThreshold && target <= zoneMax + 2)
                {
                    return holdPct;
                }
                _inStableZone = false;
            }
            else
            {
                // Outside: enter only when target crosses entry threshold
                if (target >= entryThreshold && target <= zoneMax)
                {
                    _inStableZone = true;
                    return holdPct;
                }
            }

            return target;
        }

        private bool IsTemperatureValid(int temperatureC)
        {
            return temperatureC >= _profile.MinimumValidTemperatureC && temperatureC <= _profile.MaximumValidTemperatureC;
        }

        // 本通道自身是否达到紧急阈值（用于区分「自己紧急」与「另一侧紧急」）。
        private bool IsOwnEmergency(int instantTemperatureC)
        {
            return _profile.EmergencyStage1TemperatureC > 0 &&
                   instantTemperatureC >= _profile.EmergencyStage1TemperatureC;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
