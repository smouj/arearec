# AreaRec architecture

AreaRec is a Windows-native .NET 8 application. Everything in the product path
is in-process: capture, graphics, encoding and file finalization all use Windows
APIs. There is no external encoder, no subprocess and no network access.

The former Python/Tkinter + FFmpeg implementation is gone from the product path.
It survives only as historical evidence in
[../history/native-migration.md](../history/native-migration.md) and
[../history/final-audit.md](../history/final-audit.md), and the build guards fail
if any of it reappears.

## Dependency direction

```text
AreaRec.App (WinForms shell, tray, hotkey, settings)
        |
        +--> AreaRec.Platform.Windows (DPI, virtual screen, Win32/WGC interop)
        |
        +--> AreaRec.Core (region, settings, clock, state, contracts)
                   |
                   +--> AreaRec.Capture (WGC and DXGI backends)
                   +--> AreaRec.Graphics (D3D11 resources and processing)
                   +--> AreaRec.Media (Media Foundation H.264/MP4)
                   +--> AreaRec.Platform.Windows (WASAPI loopback/microphone sources)
```

The UI orchestrates these services; it does not own capture, graphics, encoding
or file finalization. Backends sit behind interfaces, so the UI cannot tell
whether a frame came from Windows Graphics Capture or Desktop Duplication.

## Projects

- **`AreaRec.Core`** — platform-independent value objects, settings validation,
  recording states, video/audio contracts, monotonic clock and frame pacing.
- **`AreaRec.Capture`** — capture backends. The WGC backend owns the D3D11
  device, monitor item, free-threaded frame pool and bounded frame queue; the
  DXGI Desktop Duplication backend is an independent GPU-native fallback chosen
  by `PreferredCaptureSource`. `MultiMonitorCaptureSource` owns one preferred
  backend per intersecting display and exposes a composite native frame.
- **`AreaRec.Graphics`** — D3D11 device, resources and crop/scale/format
  processing. Readback staging textures and multi-monitor component processors
  are reused across frames to keep output dimensions stable.
- **`AreaRec.Media`** — Media Foundation capability probing, the H.264 encoder
  and the MP4 sink.
- **`AreaRec.Platform.Windows`** — Windows-specific DPI, monitor, WinRT/COM,
  WASAPI loopback and native interop boundaries.
- **`AreaRec.App`** — a small WinForms shell exposing the selector, tray, hotkey
  and recording controls. Session startup is dispatched to an MTA worker context,
  so Media Foundation COM objects are never created on the WinForms STA and then
  consumed from another apartment.

## Ownership and lifecycle

The component that creates a native resource owns its disposal. GPU textures,
capture sessions, MF samples, COM/WinRT objects and output streams are disposed
in the same lifecycle boundary, including cancellation and error paths.

Two rules follow from the product contract and are enforced in CI:

1. No project may start an encoder process or hide one behind another API.
2. No project may make a network request.

The recording path is:

```text
physical region → WGC/DXGI → D3D11 texture → crop/readback → NV12
→ Media Foundation H.264 → temporary MP4 → atomic final path → validation
```

Frame flow, coordinate handling, timing and the explicit failure boundaries are
documented in [capture-pipeline.md](capture-pipeline.md).

## Current status

Per-milestone state, what is verified and what is not, and the parked features
are tracked in [roadmap.md](roadmap.md). The dated evidence behind those claims
is in [../history/verification-2026-09-20.md](../history/verification-2026-09-20.md).
