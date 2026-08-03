# Control algorithm

## Why Brz changes sound abruptly

The supplied Brz program reads CPU channel 1 and GPU channel 2 and immediately writes the interpolated curve result. With a curve such as 70 °C → 40% and 80 °C → 60%, a four-degree sensor jump changes the requested duty by roughly eight percentage points in one control cycle.

## Processing path

For each fan channel:

1. Validate the instantaneous temperature.
2. Check emergency thresholds against the instantaneous value.
3. Update a fast EMA, slow EMA, and moving average.
4. Use a rise-sensitive but decay-resistant control temperature.
5. Interpolate the selected curve.
6. Add optional cross-fan assistance.
7. Map the result into an acoustic stable zone when enabled.
8. Accept upward target changes immediately after the target deadband.
9. Accept downward changes only after both temperature hysteresis and the down-hold time are satisfied.
10. Ramp the applied duty toward the accepted target with different rise/fall rates.
11. Avoid unnecessary EC writes until the duty delta or maximum write interval is reached.

## Emergency path

Emergency temperature checks use the instantaneous sensor value, not a filtered value.

Built-in profile safety floor (enforced by `DefaultProfiles.ApplySafetyPolicy`):

- CPU ≥ 89 °C: at least 75%, applied via a bounded fast ramp (4× normal up-rate, ≥4 %/s) capped at the accepted curve target; the target jumps to the floor immediately.
- CPU ≥ 90 °C: immediately 100%.
- GPU ≥ 82 °C: at least 75%, immediate.
- GPU ≥ 85 °C: immediately 100%.

Stage 1 ramps instead of snapping because 87–89 °C is reached routinely during
normal use on the target notebook (idle sits near 80 °C); an instant duty snap
at that boundary produced audible fan jumps several times an hour. Stages 2/3
remain immediate — those are real thermal emergencies. The CPU stage-1
threshold was calibrated from 87 °C to 89 °C (config migration rewrites the old
built-in default combination on load).

## Acoustic stable zone

The CPU default maps raw targets from 50% through 57% to a fixed 53%. This is intended to stop the command from wandering across the range where the user reports a large perceived sound change. Calibration can replace these values with observed points.

## Fail-safe behavior

The main process restores both known channels to Auto on:

- leaving Active mode;
- normal application exit;
- system suspend;
- control-loop exception;
- repeated invalid temperature readings.

## Acoustic budget governor

The requested performance tier (load demand) is decoupled from the effective
power tier (what the acoustic/thermal budget actually allows):

- `RequestedPerformanceTier`: produced by the load state machine.
- `EffectivePowerTier`: produced by `AcousticGovernor` from the requested tier,
  fan duty, temperatures, temperature slope and the tier's acoustic limits.
- `CoolingState`: Normal / NearAcousticLimit / ThermalSaturation /
  TemporaryCoolingBoost / Emergency.

Rules:

- Fans at the tier soft maximum + temperature at/above target + rising for the
  saturation dwell (default 20 s) → ThermalSaturation: the effective power tier
  is never raised; if saturation persists it is lowered one tier (power down =
  cooling), while fan safety responses keep working independently.
- Emergency (Stage 1/2/3, fast rise, RPM guard) may break the fan soft maximum
  immediately but never raises the effective power tier.
- Recovery requires stable/falling temperature and fan duty below the soft
  maximum by the recovery margin for 60–120 s.
- Soft maximums live in `FanChannelProfile` (comfort/soft-max/target per
  channel); initial candidate values are offline-only and not hardware-calibrated.
- Power tiers remain fixed safe presets shared with the XtuBridge whitelist.

Fan curve targets are clamped by the tier's soft maximum in `ChannelController`
except during fast temperature rise (rate above the breakthrough threshold);
emergency stages set the applied duty directly and are never clamped.

When Active mode is running, the watchdog monitors a heartbeat. If the process exits or heartbeat becomes stale, the watchdog initializes the same native DLL and restores channel 1 and 2 to Auto.
