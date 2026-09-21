# Testing

AreaRec does not use a test framework. The deterministic tests are small
self-contained executables and the media/capture tests are smokes: they exercise
real Windows components and report what they observed.

A result that cannot be produced in the current environment is recorded as
`NOT VERIFIED`. That is not a failure — it is the absence of evidence, and it is
never written up as a pass.

## Deterministic core tests

```powershell
dotnet run --project tests/AreaRec.Core.Tests/AreaRec.Core.Tests.csproj --configuration Release --no-build
```

Platform-independent, no GPU or desktop needed. Covers physical region
normalization and intersection, negative and off-screen origins, the recording
session state machine, frame pacing and drop/duplicate accounting, monotonically
increasing timestamps, the audio clock, PCM mixer saturation, resampling, and
settings validation including invalid quality values.

## Media Foundation smokes

```powershell
dotnet run --project tests/AreaRec.Media.Smoke/AreaRec.Media.Smoke.csproj -c Release --no-build -- <mode>
```

| Mode | What it proves |
| --- | --- |
| `playback` | Writes synthetic frames through the BGRA→NV12 conversion and H.264 sink, then decodes the result with the Media Foundation Source Reader |
| `audio` | WASAPI loopback capture produces timestamped PCM chunks |
| `audio-mp4` | AAC + H.264 mux into a single MP4 with both tracks decodable |
| `abort` | An aborted session leaves no temporary file behind |
| `performance` | Encoder/conversion throughput at 640×360, 1080p, 1440p and 4K, without desktop capture |

## Capture and end-to-end smokes

These need a real interactive desktop:

- `tests/AreaRec.Capture.Smoke` — WGC and DXGI backends each produce frames.
- `tests/AreaRec.EndToEnd.Smoke` — capture → D3D11 crop/readback → MP4 at 30 and
  60 FPS, with playback validation and a cursor on/off pixel probe.
- `tests/AreaRec.App.Smoke` — drives the published executable: global hotkey,
  selector overlay, `Esc` cancellation, drag selection, the enabled Record
  state, a real recording through the Save dialog, hotkey stop, and MP4
  metadata validation.

## What has been proven so far

The requirement-by-requirement matrix is in
[../history/verification-2026-09-20.md](../history/verification-2026-09-20.md),
and the measured numbers are in [performance.md](performance.md). The short
version, on a single Windows 10 Pro host with an NVIDIA GTX 1060:

- ✅ restore, Release build with no warnings, and `dotnet format --verify-no-changes`
- ✅ 17 deterministic core tests
- ✅ WGC and DXGI capture smokes, and both end-to-end at 30 and 60 FPS
- ✅ hardware H.264 encoder MFT selected, output decodes
- ✅ self-contained x64 package with matching SHA-256

Explicitly **not** verified: mixed-DPI and multi-monitor visual accuracy,
device-removal and suspend/resume recovery, microphone capture end to end (no
endpoint on the test host), the full resolution × FPS × DPI matrix, MSIX
packaging, and the remote CI result.

## Guards

The CI workflow fails the build if native sources reference FFmpeg, Python, a
process launch, or any network API, and if legacy Python/FFmpeg artifacts or a
tracked `bin/*.exe` reappear. These guards are part of the product contract, not
just hygiene: AreaRec is supposed to be a self-contained local program.
