using System;
using X15FanCore.Models;

namespace X15FanCore.Control
{
    internal sealed class ChannelController
    {
        private readonly FanKind _fanKind;
        private readonly TemperatureFilter _filter;
        private FanChannelProfile _profile;
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
        private int _externalOverrideCount;
        private int _consecutiveMismatchCount;
        private double _lastWriteVerificationTarget;
        private bool _pendingWriteVerification;

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
            _externalOverrideCount = 0;
            _consecutiveMismatchCount = 0;
            _lastWriteVerificationTarget = -1;
            _pendingWriteVerification = false;
            _inStableZone = false;
            _stableZoneEntryPercent = 51;
            _stableZoneExitPercent = 49;
        }

        public ChannelDecision Update(
            int instantTemperatureC,
            int currentDutyPercent,
            double couplingAdditionPercent,
            DateTime timestampUtc,
            double elapsedSeconds)
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
            rawTarget = Clamp(rawTarget + Math.Max(0, couplingAdditionPercent), 0, 100);
            rawTarget = ApplyStableZone(rawTarget);
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
            bool emergencyStage2 = instantTemperatureC >= _profile.EmergencyStage2TemperatureC;
            bool emergencyStage1 = instantTemperatureC >= _profile.EmergencyStage1TemperatureC;

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
                _applied = Math.Max(_applied, _profile.EmergencyStage1Percent);
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
            _lastWritten = writtenPercent;
            _lastWriteUtc = timestampUtc;
            _pendingWriteVerification = true;
            _lastWriteVerificationTarget = writtenPercent;
        }

        public void SetEcReadback(double readbackPercent)
        {
            _ecLastReadbackPercent = readbackPercent;

            if (!_initialized || _lastWritten < 0)
            {
                _consecutiveMismatchCount = 0;
                return;
            }

            double diff = Math.Abs(readbackPercent - _lastWritten);

            // Only count as mismatch when there's a significant discrepancy
            if (diff > 3.0)
            {
                _consecutiveMismatchCount++;
            }
            else
            {
                // Decrement counter when consistent, but don't reset immediately
                if (_consecutiveMismatchCount > 0)
                    _consecutiveMismatchCount--;
            }
        }

        public int ConsecutiveMismatchCount => _consecutiveMismatchCount;

        public bool CheckExternalOverride(double currentReadbackPercent)
        {
            if (!_initialized || _lastWritten < 0)
                return false;

            double diff = Math.Abs(currentReadbackPercent - _lastWritten);
            if (diff > 3.0)
            {
                _consecutiveMismatchCount++;
                _externalOverrideCount++;

                // 3 consecutive mismatches or total overrides > 10 = confirm override
                return _consecutiveMismatchCount >= 3 || _externalOverrideCount > 10;
            }

            if (_consecutiveMismatchCount > 0)
                _consecutiveMismatchCount--;

            return false;
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
