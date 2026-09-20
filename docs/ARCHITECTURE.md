# AreaRec architecture

AreaRec is a Windows-native .NET application. The former Python/Tkinter and
FFmpeg path is retained only as historical context in the migration audit and
is not part of the product or build.

## Native dependency direction

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

The UI must orchestrate these services, not own capture, graphics, encoding or
file finalization. Backends are selected behind interfaces so the UI does not
know whether a frame came from Windows Graphics Capture or Desktop Duplication.

## Projects

- `AreaRec.Core`: platform-independent value objects, settings validation,
  recording states, video/audio contracts, monotonic clock and frame pacing.
- `AreaRec.Capture`: capture source implementations. The WGC backend now owns
  the D3D11 device, monitor item, free-threaded frame pool and bounded frame
  queue; the DXGI Desktop Duplication backend is an independent GPU-native
  fallback selected by `PreferredCaptureSource`. `MultiMonitorCaptureSource`
  owns one preferred backend per intersecting display and exposes a composite
  native frame to the graphics layer.
- `AreaRec.Graphics`: D3D11 device/resource and crop/scale/format processing.
  Readback staging textures and multi-monitor component processors are reused
  across frames for stable output dimensions.
- `AreaRec.Media`: Media Foundation capability probing, H.264 encoder and MP4
  sink.
- `AreaRec.Platform.Windows`: Windows-specific DPI, monitor, WinRT/COM, WASAPI
  loopback and native interop boundaries.
- `AreaRec.App`: small WinForms application shell. It exposes the native
  selector, tray, hotkey and recording controls. Session startup is dispatched
  to a worker/MTA context so Media Foundation COM objects are not created on
  the WinForms STA and then consumed from another apartment.
- `tests/AreaRec.Core.Tests`: dependency-free executable tests for deterministic
  core behavior. Windows integration and media smoke tests are later phases.

## Ownership and lifecycle

The component that creates a native resource owns its disposal. GPU textures,
capture sessions, MF samples, COM/WinRT objects and output streams must be
disposed in the same lifecycle boundary, including cancellation and error
paths. No project may start an encoder process or hide one behind another API.

## Current status

M1 is complete and M3 WGC capture plus the DXGI fallback have local runtime
smoke passes on the current desktop. M4 timing and
session accounting are covered by deterministic core tests. The Media Foundation
sink is implemented and has a synthetic BGRA-to-MP4 smoke pass. GPU readback,
crop/scale, RecordingSession wiring, tray, hotkey and local settings persistence
are implemented. Local WGC-to-MP4 smoke passes at 30 and 60 FPS. The optional
Media Foundation Source Reader playback check decodes the synthetic MP4 and both
30/60 FPS WGC E2E files locally. Selector hardware verification, performance
targets and multi-monitor runtime behavior remain **NOT VERIFIED**. A local UI
smoke verifies `Ctrl+Shift+R`, selector opening, Escape cancellation, drag
selection, the enabled Record state, a real UI recording, hotkey stop and MP4
playback; visual pixel/DPI accuracy remains pending. The local inspector also
verifies the MP4 video track, dimensions and positive duration.

The versioned settings file is written through a temporary sibling and atomic
replacement. The audio boundary now carries format and timestamped chunks and
exposes a mixer/clock seam. `WasapiLoopbackSource` captures the default render
  endpoint through event-driven loopback; the microphone source is implemented
  but has no endpoint to verify on this host. `PcmAudioMixer` handles aligned
  PCM chunks and `PcmAudioResampler` performs linear rate conversion;
  the optional `RecordingSession` audio path shares one timestamp origin with
  video, and `MediaFoundationMp4Sink` can mux WASAPI-format PCM through the
  Windows AAC encoder. Synthetic AAC+H.264 MP4 and a local WGC + WASAPI
  loopback E2E pass; the product UI remains video-only by default and
  microphone runtime support is **NOT VERIFIED** on this host.
