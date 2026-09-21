# Privacy

AreaRec makes no network requests, at all.

- No telemetry, analytics, crash reporting or usage statistics.
- No account, sign-in, licence server or cloud storage.
- No FFmpeg, Python, Node or any other external process or runtime dependency —
  capture, encoding and container writing all happen in-process through Windows
  APIs.
- The repository contains a CI guard that fails the build if a native source
  file references an HTTP client, a socket, or a process launch.

## Where your data lives

| Data | Location |
| --- | --- |
| Recordings | The output folder you choose |
| Settings | `%LOCALAPPDATA%\AreaRec` — versioned local JSON |

Recordings are written to a temporary file next to the target path and moved
into place atomically once finalization succeeds. Nothing is uploaded, and
nothing leaves the machine.

The one thing AreaRec cannot promise is where your recordings end up: it saves
to the folder you pick, so anything you record and then share is your decision,
not the app's.
