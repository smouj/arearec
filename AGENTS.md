# Agent instructions

## Product contract
AreaRec does one thing: select a rectangle and record it to MP4.

## v0.1 freeze
Do not add audio, webcam, editing, accounts, cloud, telemetry, updater, database, web server, Electron or Tauri during v0.1 work.

## Engineering rules
- Keep the standard-library-only Python UI unless a concrete blocker is demonstrated.
- Keep process invocation isolated from UI code where practical.
- Never silently upload or transmit captures.
- Prefer fixes that reduce code and dependencies.
- Run `python -m pytest` before merging.
