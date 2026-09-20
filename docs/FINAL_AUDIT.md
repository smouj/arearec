# AreaRec Native migration audit

Audit date: 2026-09-20  
Repository: `smouj/arearec`  
Delivery state: local native release candidate; no remote push performed.

## 1. Final commit and working tree

The delivery HEAD is the value returned by `git rev-parse HEAD` at audit time.
The migration is represented by the local commit history beginning with
`e2b42a5` (`feat: migrate AreaRec to native Windows capture`). Subsequent
checkpoints harden cleanup, final-output validation, off-screen regions,
performance measurement and configuration validation. The exact delivery HEAD
and the working tree is clean; generated `bin/`/`obj/` outputs are ignored and
untracked.

## 2. Final architecture

```text
AreaRec.App
  -> AreaRec.Core contracts, settings, state and timing
  -> AreaRec.Capture WGC primary / DXGI fallback / monitor compositor
  -> AreaRec.Graphics D3D11 crop, readback and reusable resources
  -> AreaRec.Media Media Foundation H.264/AAC, MP4 sink and playback checks
  -> AreaRec.Platform.Windows DPI, Win32, WASAPI and native interop
```

The recording path is:

```text
physical region -> WGC/DXGI -> D3D11 texture -> crop/readback -> NV12
-> Media Foundation H.264 -> temporary MP4 -> atomic final path -> validation
```

## 3. Main files and retired implementation

- Native solution: `AreaRec.sln`, `Directory.Build.props`.
- UI: `src/AreaRec.App`.
- Capture: `src/AreaRec.Capture`.
- Core session/timing/contracts: `src/AreaRec.Core`.
- Graphics: `src/AreaRec.Graphics`.
- Media: `src/AreaRec.Media`.
- Windows audio/platform interop: `src/AreaRec.Platform.Windows`.
- Deterministic, media, capture, E2E and UI smoke projects under `tests/`.
- Retired: `pyproject.toml`, `src/arearec`, `tests/test_core.py`, the Python CI
  workflow and the legacy FFmpeg-oriented runtime path.

## 4. Dependencies and licensing

No new third-party runtime or executable was added. The product uses .NET 8
self-contained deployment plus Windows APIs: Win32, Windows Graphics Capture,
Direct3D 11, Media Foundation and WASAPI. See `THIRD_PARTY.md`. The source
license remains MIT.

## 5. Verification executed

- Fresh local clone: restore, Release build with 0 warnings/errors and
  `dotnet format --verify-no-changes`.
- 16 deterministic Core tests, including physical layout, off-screen offsets,
  session lifecycle, pacing, audio clock, mixer/resampler and invalid quality.
- Media Foundation H.264/MP4 playback, AAC+H.264 mux, hardware transform probe
  and abort cleanup.
- WGC capture smoke, cursor pixel probe and WGC-to-MP4 E2E at 30/60 FPS.
- UI smoke: global hotkey, selector, Escape, drag, save dialog, real recording,
  hotkey stop, final MP4 metadata validation and Source Reader playback.
- Static dependency and legacy-artifact guards; no tracked generated outputs.

## 6. End-to-end evidence

- 30 FPS: 22 encoded and 22 decoded frames, `640×360`, NVIDIA H.264 Encoder
  MFT selected.
- 60 FPS: 44 encoded and 44 decoded frames, `640×360`, NVIDIA H.264 Encoder
  MFT selected.
- Cursor ON/OFF saved MP4 pixel probe: decoded NV12 differences observed.
- WASAPI loopback + video + AAC E2E: video and audio tracks present and
  decodable.
- Self-contained x64 package: `AreaRec.exe` and `hostfxr.dll` present; latest
  package hash and size are recorded in `docs/VERIFICATION.md`.

## 7. Performance measurements

Packaged UI smoke runs observed startup to main window at `487–905 ms`, idle CPU
at `0.0–0.8%` and working set at `50–51 MB`. Synthetic Media Foundation
conversion/encoding measurements exist for 640×360, 1080p, 1440p and 4K.
The `<300 ms` startup aspiration was not met in the measured run.

## 8. Compatibility evidence

Verified locally on Windows 10 Pro `10.0.19045` x64, .NET 8, Intel Core i7-6700
and NVIDIA GeForce GTX 1060. WGC and DXGI both pass local capture/E2E playback
smokes on this host. The current host exposes one monitor and no microphone
endpoint.

## 9. NOT VERIFIED

- Visual mixed-DPI and multi-monitor capture on physical 2-monitor hardware.
- Device removal, display hot-unplug, mode change and suspend/resume recovery.
- Microphone capture on a host with an available endpoint.
- Remote GitHub Actions result for the new workflow; local clean-clone CI steps
  pass, but no push was performed.
- Complete 30/60 FPS × 1920×1080/2560×1440/3840×2160 × 100–200% DPI ×
  1/2-monitor performance matrix, GPU engine utilization, disk-full and
  unwritable-path runs.
- Installer/MSIX and tray visual acceptance on multiple Windows configurations.

## 10. Next concrete milestone

Run the existing native workflow and the WGC/DXGI/UI smoke matrix on a second
Windows 10/11 x64 host with two monitors using different DPI scales, an
available microphone endpoint and a supported hardware H.264 encoder. Record
the remote CI check and replace only the corresponding `NOT VERIFIED` entries
with measured evidence.
