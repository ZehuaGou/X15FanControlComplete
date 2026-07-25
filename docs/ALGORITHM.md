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

Default Stable Noise profile:

- CPU ≥ 88 °C: immediately at least 75%.
- CPU ≥ 92 °C: immediately 100%.
- GPU ≥ 83 °C: immediately at least 75%.
- GPU ≥ 87 °C: immediately 100%.

These values are initial safeguards, not a claim that they are ideal for every workload.

## Acoustic stable zone

The CPU default maps raw targets from 50% through 57% to a fixed 53%. This is intended to stop the command from wandering across the range where the user reports a large perceived sound change. Calibration can replace these values with observed points.

## Fail-safe behavior

The main process restores both known channels to Auto on:

- leaving Active mode;
- normal application exit;
- system suspend;
- control-loop exception;
- repeated invalid temperature readings.

When Active mode or calibration is running, the watchdog monitors a heartbeat. If the process exits or heartbeat becomes stale, the watchdog initializes the same native DLL and restores channel 1 and 2 to Auto.
