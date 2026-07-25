# Security and hardware safety

- Never run two EC fan controllers simultaneously.
- Keep Active mode disabled until ReadOnly and Simulation values are verified.
- Do not disable Windows driver security solely to run an unknown NTPort driver.
- Do not replace `ClevoEcInfo.dll` with a download from an untrusted site.
- The program intentionally touches only channel 1 and channel 2 because those channels were observed in the supplied application.
- Active mode requires administrator privileges.
- Configuration corruption falls back to generated defaults and preserves a backup.
- Sensor failures request Auto rather than holding a low fixed fan command.
