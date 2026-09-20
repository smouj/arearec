# Agent instructions

## Product contract
AreaRec does one thing: select a rectangle and record it to MP4.

## v0.1 freeze
Do not add audio, webcam, editing, accounts, cloud, telemetry, updater, database, web server, Electron or Tauri during v0.1 work.

## Engineering rules
- Treat the native .NET solution as the only product path.
- Do not add FFmpeg, Python, external encoder processes, network services or runtime telemetry to the native path.
- Keep process invocation isolated from UI code where practical; native recording must not invoke a child process.
- Never silently upload or transmit captures.
- Prefer fixes that reduce code and dependencies.
- Run `dotnet build AreaRec.sln --configuration Release` and the relevant native smoke tests before merging.
