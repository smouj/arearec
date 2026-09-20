# Native migration baseline and plan

Date: 2026-09-20  
Baseline commit: `865ca70` (`feat: visible region overlay, persistent config, folder selector, DPI awareness`)

## Initial state (M0 audit)

AreaRec is a small Python 3.11+ package with a Tkinter UI. The current flow is:

```text
Tkinter window
  -> full-screen selection overlay
  -> Region(x, y, width, height)
  -> subprocess.Popen(ffmpeg.exe, gdigrab)
  -> stdin "q"
  -> H.264 MP4
```

The implementation is split between `src/arearec/app.py` (UI, selection,
configuration and process lifecycle) and `src/arearec/core.py` (region value
object, FFmpeg discovery and command construction). Configuration is stored in
`arearec.json` beside the package. The repository contains a placeholder
`bin/.gitkeep` and does not redistribute an FFmpeg binary.

Current user-visible behavior:

- drag a rectangle over one screen;
- normalize coordinates and force even dimensions;
- select 30 or 60 FPS;
- include or exclude the cursor;
- choose an output directory and MP4 filename;
- show a pre-recording border;
- stop by sending `q` to FFmpeg;
- remember the last region, directory, FPS and cursor setting.

Current limitations and risks:

- Python and Tkinter are runtime requirements;
- FFmpeg is an external encoder/process and `gdigrab` is the capture backend;
- selection uses one Tk window sized from `winfo_screenwidth/height`, so virtual
  desktop coordinates, negative monitor origins and mixed-DPI monitors are not
  represented correctly;
- there is no explicit recording state machine, monotonic timestamp model,
  backpressure policy, dropped-frame accounting or recovery file strategy;
- capture is not exposed as an engine abstraction and there is no Direct3D,
  Windows Graphics Capture, Desktop Duplication or Media Foundation code;
- there is no global hotkey, tray lifecycle, settings model, Windows-specific
  integration test or end-to-end MP4 smoke test;
- CI installs Python and runs only the Python unit tests and `compileall`.

Repository inventory at the baseline:

- application: `src/arearec/app.py`, `src/arearec/core.py`, `src/arearec/__main__.py`;
- tests: `tests/test_core.py` (seven pure-logic tests);
- documentation: `README.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`,
  `docs/SCOPE.md`, `THIRD_PARTY.md`, contribution/security files;
- CI: `.github/workflows/ci.yml` (Windows + Python 3.13);
- packaging: `pyproject.toml` setuptools entry point; no native solution or
  installer;
- assets: `assets/logo.png`, `assets/social-banner.png`;
- no native project, native test project or third-party runtime library.

## Baseline verification

- `python -m compileall -q src`: **PASS**.
- `python -m pytest -q`: **NOT RUN** because the clean environment has no
  `pytest` module. The project did not declare pytest as a development
  dependency and the baseline CI installs it ad hoc.
- Real capture/encoding behavior: **NOT VERIFIED** in this audit.

The failed test invocation is part of the baseline evidence; it must not be
silently presented as a passing test run.

## Target decisions

1. Use a Windows-targeted C#/.NET solution, with nullable reference types and
   warnings treated as errors in native projects.
2. Keep product/application orchestration separate from the engine contracts:
   capture, graphics, media, recording, audio and platform integration have
   independent responsibilities.
3. Make Windows Graphics Capture the primary capture backend. Keep the
   `ICaptureSource` boundary backend-neutral so a DXGI Desktop Duplication
   implementation can be added as a fallback without UI changes.
4. Use Direct3D 11 resources for the graphics path and avoid a CPU bitmap round
   trip unless a capability or diagnostic path explicitly requires it.
5. Use Windows Media Foundation for H.264 and MP4. No CLI encoder, custom H.264
   implementation or hand-written MP4 muxer is permitted.
6. Model region coordinates explicitly as physical virtual-screen coordinates;
   keep logical/client/DPI conversion at the platform boundary.
7. Put clock, frame pacing, timestamps and accounting in a hardware-independent
   recording core so they can be unit-tested with synthetic frames.
8. Define WASAPI-oriented audio interfaces now, but do not block the first
   verified video release on audio implementation.
9. Keep the runtime local-only. Native builds must not add network, account,
   cloud, telemetry or external-process dependencies.

## Components to replace

| Current component | Native replacement | Removal gate |
| --- | --- | --- |
| Python/Tkinter application | Native Windows application and selector | Native UI smoke test covers select/start/stop |
| `Region` plus Tk coordinates | Core physical-region model, geometry partitioner and DPI-aware virtual-screen selector | Negative/physical partition tests pass; mixed-DPI hardware verification remains pending |
| `resolve_ffmpeg`/`build_ffmpeg_command` | Media Foundation encoder and MP4 sink | Native synthetic-media smoke test creates and Source Reader decodes a non-empty MP4 |
| FFmpeg `gdigrab` subprocess | WGC primary plus DXGI Desktop Duplication fallback | Capture backend integration tests and manual multi-monitor hardware verification |
| implicit process lifecycle | Explicit recording session state machine | Start/stop/error/cancel tests pass |
| ad hoc JSON dictionary | Versioned local native settings model | Settings migration and round-trip tests pass |
| Python CI/package flow | .NET restore/build/test/publish workflow | Clean Windows runner publish dry-run passes |

## Migration strategy

Work incrementally and keep `main` buildable at each migration boundary:

1. **M0 — Baseline:** this audit and parity matrix.
2. **M1 — Native skeleton:** solution, core contracts, diagnostics, CI and
   tests; no claim of capture parity yet.
3. **M2 — Region selector:** per-monitor DPI-aware physical coordinates,
   negative origins, cancellation and dimensions.
4. **M3 — Capture:** D3D11 device/resource layer and WGC backend, with a clean
   DXGI fallback seam.
5. **M4 — Recording core:** monotonic timing, pacing, bounded queues,
   cancellation and frame accounting using synthetic sources.
6. **M5 — Media Foundation:** H.264 encoder, MP4 sink, capability probing and
   safe 30/60 FPS quality profiles.
7. **M6 — End-to-end:** region to real MP4, cursor option, stop/finalization,
   output validation and recovery paths.
8. **M7–M9 — Reliability and productization:** error states, safe temporary
   files, settings, hotkey, tray, packaging and measured performance.
9. **M10–M11 — Cleanup and release candidate:** remove Python/FFmpeg only after
   parity evidence, update all documentation and require CI/publish/smoke gates.

Legacy code may remain only while a native replacement is being verified. It is
not considered a second supported runtime and must have an explicit removal
issue/gate.

## Parity and completion criteria

The migration is not complete when the native code merely compiles. Evidence
must cover, at minimum:

- clean checkout build without Python, FFmpeg, Node or Visual Studio installed;
- region selection at 100/125/150/175/200% DPI, including negative virtual
  coordinates and mixed-DPI multi-monitor layouts;
- real WGC capture, 30 and 60 FPS, cursor on/off, correct dimensions and
  playable H.264 MP4 duration;
- explicit start/record/stop/save/error states and safe finalization;
- synthetic timing, frame pacing, queue pressure, dropped/duplicated-frame and
  duration tests independent of hardware;
- zero runtime network requests, zero external encoder processes and no
  telemetry/account/cloud requirement;
- CI restore/build/static checks/unit tests/integration tests/publish dry-run;
- measured CPU, GPU, memory, startup and frame-drop results documented in
  `docs/PERFORMANCE.md` on the actual tested machine;
- every unsupported hardware or environment claim labeled `NOT VERIFIED`.

## Known risks

- Windows Graphics Capture availability, picker/permission behavior and
  interop details vary by Windows build and require Windows-specific testing.
- Media Foundation encoder availability differs by hardware and installed
  codecs; capability probing and a deterministic fallback policy are required.
- Mixed-DPI virtual-screen coordinate conversion is easy to get subtly wrong;
  tests must use physical-coordinate fixtures and manual verification.
- Device removal, monitor changes, lock/suspend and output-disk failures need
  explicit lifecycle handling rather than process termination.
- A self-contained single-file publish still depends on Windows graphics/media
  platform components; the release documentation must state that boundary.

## Current status

M0 audit: **COMPLETE**.  
M1 native skeleton: **COMPLETE**.  
M2 region selector: **IMPLEMENTED**, with build/test evidence pending on actual
multi-monitor and mixed-DPI hardware.  
M3 capture backends: **IMPLEMENTED**. WGC and DXGI each have a local runtime
smoke PASS with three real frames at `1360×768`; the latest DXGI trace reports
successful `AcquireNextFrame` calls after a controlled visible pulse. A multi-monitor
compositor now captures one backend per intersecting display and composes the
selected intersections; multi-monitor and mixed-DPI runtime behavior remain
**NOT VERIFIED**.  
M4 recording core: **IMPLEMENTED**, with deterministic timestamp/accounting,
drop and duplicate-frame tests passing.  
M5 Media Foundation sink: **IMPLEMENTED** for CPU-readable BGRA frames with
BGRA→NV12 conversion, with a synthetic MP4 smoke pass. D3D11 crop/readback and
RecordingSession wiring are implemented. The product path requests hardware
Media Foundation transforms when enabled, enumerates hardware H.264 MFTs, supplies NV12 input and probes the
selected Sink Writer transform. This host selected `NVIDIA H.264 Encoder MFT`
with a hardware URL and the Source Reader decoded the resulting MP4.  
M6 local end-to-end: **IMPLEMENTED/VERIFIED** on the current desktop for 30 and
60 FPS, producing non-empty `640×360` MP4 containers. The Media Foundation
Source Reader playback check decodes the synthetic smoke file and both local
30/60 FPS E2E outputs; a cursor-enabled 30 FPS E2E output also decodes
successfully. Multi-monitor/mixed-DPI behavior, performance and packaging
criteria remain
**NOT VERIFIED**.

M10 cleanup: **COMPLETE** for the product path. The Python/Tkinter application,
FFmpeg command path, Python tests, Python packaging metadata and legacy Python
workflow were removed after the native UI/E2E parity gate passed. The baseline
and replacement table above remain as historical migration evidence; they do
not describe supported runtime dependencies.

The local UI smoke now verifies that the published application registers
`Ctrl+Shift+R`, opens the selector overlay, returns to the main window after
`Escape`, accepts a drag selection, enables `Record`, records through the real
Save dialog, stops via the hotkey and produces an MP4 decoded by Media
Foundation. Visual pixel accuracy, mixed-DPI behavior and tray interaction
remain **NOT VERIFIED**.

Audio contracts: **IMPLEMENTED** as a timestamped format/chunk/mixer boundary;
`WasapiLoopbackSource` now captures the default render endpoint through an
event-driven WASAPI loopback smoke (`48 kHz`, 2 channels, 32-bit), and
`WasapiMicrophoneSource` provides the analogous capture-endpoint implementation.
`PcmAudioMixer` provides deterministic aligned PCM mixing with saturation, and
`PcmAudioResampler` provides linear PCM rate conversion. `RecordingSession` now
shares one media timestamp origin between video and audio, and the Media
Foundation sink can expose an optional AAC stream; synthetic AAC+H.264 MP4 and
a local WGC + WASAPI loopback E2E pass. The current host has no microphone endpoint
(`0x80070490`) to verify, and the product UI keeps audio opt-in/unexposed for
the first video release.
