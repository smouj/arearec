# Native verification matrix

Date: 2026-09-20  
Repository HEAD: native migration checkpoint (generated build outputs ignored; see the final report for the exact commit)

This matrix records evidence available on the current Windows 10 Pro x64 host.
`NOT VERIFIED` means that the repository may contain an implementation, but the
required hardware, remote runner or visual acceptance evidence was not available.

| Requirement | Evidence | Status |
| --- | --- | --- |
| Native restore and build | Main workspace and a fresh local clone both restored `AreaRec.sln --runtime win-x64`, built Release with 0 warnings/errors, and passed `dotnet format --verify-no-changes` | PASS locally |
| Python/FFmpeg removal | No `*.py`, `*.pyc`, `pyproject.toml` or Python workflow; native dependency guard passes | PASS in checkout |
| No native encoder/network process | Static guard rejects FFmpeg, Python, process launch and network APIs; no `AreaRec`/`ffmpeg` process remains after tests | PASS by guard/process audit; runtime network instrumentation not performed |
| Region selection and hotkey | App UI smoke passes `Ctrl+Shift+R`, selector, Escape and drag | PASS locally |
| Real UI recording and stop | Fresh self-contained publish; UI smoke saves and decodes a `120×80` MP4 (`decoded=29–30` across fresh runs) | PASS locally |
| Negative physical coordinates | Core intersection and region normalization tests | PASS by deterministic tests |
| Mixed-DPI and multi-monitor visual accuracy | `PerMonitorV2` manifest is wired into the app; physical-coordinate compositor partition tests cover negative origins and monitor boundaries | **NOT VERIFIED** visually on this one-monitor host |
| Windows Graphics Capture | Runtime smoke: 3 frames at `1360×768` | PASS locally |
| DXGI Desktop Duplication | COM startup path builds; trace shows repeated `DXGI_ERROR_WAIT_TIMEOUT (0x887A0027)` during a pumped visible pulse | **NOT VERIFIED** on this host |
| D3D11 crop/readback and pacing | WGC-to-MP4 E2E with D3D11 processing, bounded queue, duplicate/drop accounting | PASS locally |
| 30 FPS H.264/MP4 | Hardware E2E with playback validation: 22 encoded/decoded frames, `640×360`, `733 ms` track; NVIDIA H.264 Encoder MFT selected | PASS locally |
| 60 FPS H.264/MP4 | Hardware E2E with playback validation: 44 encoded/decoded frames, `640×360`, `733 ms` track; NVIDIA H.264 Encoder MFT selected | PASS locally |
| Cursor option | Cursor-enabled hardware E2E decodes 22 frames; saved cursor OFF/ON MP4 probe found 466 differing decoded NV12 bytes | PASS locally |
| Hardware H.264 MFT availability | `MFTEnumEx` enumerated `NVIDIA H.264 Encoder MFT` for hardware H.264 | PASS enumeration |
| Effective hardware transform selection | NV12 input plus Sink Writer transform probe selected `NVIDIA H.264 Encoder MFT`, `hardware=True`; Source Reader decoded 30 frames | PASS locally |
| Safe finalization | Atomic temporary MP4 path, zero-frame rejection, error/abort core tests, and MF abort smoke with zero leftover temporaries | PASS by tests and local runs |
| Capture fault lifecycle | WGC item-close and DXGI access-loss paths report actionable failures; D3D11/WGC partial-start cleanup is guarded; actual device removal, display hot-unplug, mode change and suspend/resume are not reproducible here | **NOT VERIFIED** on faulting hardware |
| Robustness matrix | Small/fullscreen/off-screen regions and cancellation are covered in code/tests; the requested 1920×1080, 2560×1440, 3840×2160 × 30/60 FPS × 100–200% DPI × 1/2-monitor matrix, disk-full and unwritable-path runs are not all available on this host | **NOT VERIFIED** as a complete matrix |
| Settings | Versioned JSON (`Version=1`) with temporary sibling and replacement | PASS locally |
| Audio | WASAPI loopback smoke: 48 kHz, 2 channels, 32-bit, 3 chunks / 11,520 bytes; WGC + WASAPI + RecordingSession E2E (`22` decoded frames, AAC track); deterministic mixer/resampler/session-clock tests | Loopback/mixing/resampling/session/A-V mux PASS locally; microphone runtime **NOT VERIFIED** |
| Self-contained x64 publish | `PUBLISH_PORTABLE.ps1` produced a 78,889,806-byte ZIP with `AreaRec.exe`, `hostfxr.dll`, and matching SHA-256 (`120b521a7c2fde7d5285cc35aa12ec9c79a0733a1c996c5a40440463a3e0af7f`); packaged UI smoke passes | PASS locally |
| CI | `.github/workflows/native-ci.yml` covers restore, build, format, dependency guard, tests and publish | Remote GitHub check **NOT VERIFIED** |
| Performance | Local WGC baseline plus synthetic MF sink measurements at 640×360, 1080p, 1440p and 4K documented in `docs/PERFORMANCE.md` | Encoder/conversion sizes PASS; sustained capture/GPU engine/multi-monitor targets **NOT VERIFIED** |

The native product path is now the only supported runtime. Installer/MSIX, tray
visual acceptance and additional hardware matrix coverage remain follow-up work.
