# Capture pipeline

This document defines the native pipeline boundary. It is intentionally more
precise than the current M1 skeleton; implementation claims are only made when
the corresponding phase has been built and run on Windows.

## Target frame flow

```text
physical virtual-screen region
          |
          v
Windows Graphics Capture (primary)
          |
          v
ID3D11Texture2D / GPU-owned frame
          |
          v
D3D11 crop, scale and format conversion
          |
          v
Media Foundation H.264 encoder
          |
          v
Media Foundation MP4 sink -> temporary file -> atomic final path
```

DXGI Desktop Duplication is an alternate `ICaptureSource` implementation. It
must be selected by the capture service and never called directly by the UI.

## Coordinates

The core `PhysicalRegion` uses integer physical pixels in virtual-screen space.
The platform selector is responsible for converting pointer/client/logical
coordinates using the monitor DPI. Negative X/Y values are valid. A region is
normalized only at the end of the drag, and H.264-safe even dimensions are
applied without moving the selected origin.

## Timing

`StopwatchClock` is monotonic and independent of wall-clock changes. The
recording session attaches normalized capture timestamps, keeps a bounded
queue, accounts for dropped and duplicated frames, and reuses the last
cloneable output surface to fill capture gaps through the requested cadence.
`FramePacer` is deterministic and testable without a monitor or GPU; it is not
a substitute for capture synchronization.

## Failure boundaries

- capture startup failure: no output file is presented as complete;
- device removal or monitor change: stop or recreate the backend through the
  session state machine and report the reason;
- encoder failure: abort the temporary file and surface an actionable error;
- zero captured/encoded frames: reject the session and abort the temporary file
  rather than presenting an empty MP4 as a successful recording;
- normal stop: complete the Media Foundation sink, flush, close and atomically
  move the temporary file to the user-selected `.mp4` path.

## Implemented versus pending

Implemented: contracts, physical region model, monotonic clock, frame pacing,
recording-session accounting, WGC monitor capture, DXGI Desktop Duplication
fallback, multi-monitor component capture/composition, D3D11 device creation
with hardware/WARP fallback, free-threaded frame pool and bounded GPU-frame
queue. WGC passes a local smoke with 3 frames at `1360×768` on the current
Windows desktop. DXGI startup/interop is implemented, but current idle-desktop
frame delivery remains **NOT VERIFIED**; the diagnostic trace reports repeated
`DXGI_ERROR_WAIT_TIMEOUT (0x887A0027)` during a pumped visible pulse. The Media
Foundation sink converts CPU-readable BGRA frames to NV12 and commits through a
temporary sibling file. The local end-to-end smoke now captures WGC frames,
crops/readbacks `640×360` through D3D11, and writes an MP4 through the
RecordingSession at both 30 and 60 FPS. `PreferredCaptureSource` selects WGC
first and retries with DXGI when WGC cannot start.
When cursor capture is requested, the fallback fails explicitly rather than
silently producing a recording without cursor pixels. A local WGC readback
probe observed differing BGRA bytes, and the saved cursor OFF/ON MP4 probe
observed 466 differing decoded NV12 bytes after H.264 encoding.

Pending and **NOT VERIFIED**: a zero-copy GPU encoder path, performance targets,
and full
multi-monitor/mixed-DPI runtime verification. The local MP4 inspector
verifies `ftyp`, a `vide` track, H.264 sample dimensions and positive track
duration; the Media Foundation Source Reader check also decodes the generated
samples when the smoke is run with the `playback` argument.
