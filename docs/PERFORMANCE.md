# Performance measurement

No performance claim is made until a native build can capture and encode a real
region. This file records the methodology so later results are reproducible.

## Measurements required

- warm startup time from process launch to idle UI;
- idle CPU and private working set;
- recording CPU, GPU engine usage, private working set and encoder latency;
- captured, encoded, dropped and duplicated frames;
- effective FPS and output duration drift;
- 1920×1080, 2560×1440 and 3840×2160 where the hardware supports them;
- 30 FPS and 60 FPS;
- cursor on and off.

## Method

Each result must identify Windows build, .NET runtime, CPU, GPU, monitor
resolution/DPI, capture backend, encoder profile and output dimensions. Warm-up
and recording windows must be stated. CPU/GPU/memory values must come from the
actual run, not targets or estimates. A result unavailable in the current
environment is recorded as `NOT VERIFIED`.

## Current results

## Local measurements

These are smoke measurements, not release targets. Captured on 2026-09-20:

- Windows 10 Pro 10.0.19045 x64; .NET 8.0.425.
- Intel Core i7-6700 (4 cores / 8 logical processors); NVIDIA GeForce GTX 1060
  6 GB, driver 32.0.15.8157.
- WGC → D3D11 crop/readback → Media Foundation MP4, `640×360`, cursor off,
  `preferHardware=true` (hardware H.264 MFT enumeration found
  `NVIDIA H.264 Encoder MFT`; the Media Foundation smoke now selects that
  transform with `hardware=True` after the BGRA→NV12 conversion).
- 30 FPS: 22 encoded/decoded frames, 20 duplicated and 2 dropped, MP4 track
  733 ms, wall 1447 ms, process CPU 5.8%, working set 89 MB. This run used
  Source Reader playback validation and selected the NVIDIA H.264 Encoder MFT.
- 60 FPS: 44 encoded/decoded frames, 41 duplicated and 1 dropped, MP4 track
  733 ms, wall 1474 ms, process CPU 7.7%, working set 89 MB. This run also used
  Source Reader playback validation and selected the NVIDIA H.264 Encoder MFT.
- Cursor enabled at 30 FPS: 22 encoded/decoded frames, 16 duplicated and 1
  dropped, MP4 track 733 ms, wall 1514 ms, process CPU 6.2%, working set 90 MB.

### Synthetic Media Foundation benchmark

The `tests/AreaRec.Media.Smoke performance` command writes synthetic BGRA
frames through the same BGRA→NV12 conversion and Media Foundation H.264 sink,
without desktop capture. It was run on the same host with hardware transform
selection enabled:

| Size | Frames | Wall | Effective FPS | Process CPU | Working set | Hardware |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| 640×360 | 10 | 777 ms | 12.9 | 10.6% | 49 MB | yes |
| 1920×1080 | 6 | 618 ms | 9.7 | 12.3% | 102 MB | yes |
| 2560×1440 | 4 | 618 ms | 6.5 | 11.7% | 122 MB | yes |
| 3840×2160 | 2 | 817 ms | 2.4 | 10.0% | 177 MB | yes |

These are encoder/conversion measurements only. They do not prove sustained
WGC/D3D11 capture at the target FPS, GPU engine utilization, or multi-monitor
performance; those remain **NOT VERIFIED**.

The Source Reader playback check was run with the `playback` argument and is
part of the reproducibility evidence. These runs prove a local path and provide
a reproducible baseline. The D3D11 staging texture is reused across frames when
the output dimensions remain stable. They do not
prove 1080p/1440p/4K behavior, sustained capture, GPU engine utilization, or
multi-monitor performance; those remain **NOT VERIFIED**.
