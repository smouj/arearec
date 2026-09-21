# Roadmap

Status legend: ✅ done · 🚧 in progress or partial · ⛔ not started

## v0.1 — Core recorder

| | Item | State |
| --- | --- | --- |
| 1 | Region selection with physical pixel coordinates | ✅ |
| 2 | 30 / 60 FPS | ✅ |
| 3 | Cursor include / exclude | ✅ |
| 4 | Start and stop | ✅ |
| 5 | MP4 / H.264 output through Media Foundation | ✅ |
| 6 | Local-only operation, no external process or network | ✅ |

The acceptance criteria for this milestone are frozen in
[product-scope.md](product-scope.md).

## v0.2 — Practical polish

| | Item | State |
| --- | --- | --- |
| 7 | Global start/stop hotkey (`Ctrl+Shift+R`) | ✅ |
| 8 | Remember the last output directory | ✅ |
| 9 | Per-monitor DPI-aware physical selector | 🚧 implemented, hardware verification pending |
| 10 | Optional visible capture border | ⛔ |

## v0.3 — Verification and release

| | Item | State |
| --- | --- | --- |
| 11 | Mixed-DPI and multi-monitor verification on two-monitor hardware | ⛔ |
| 12 | Device removal, display hot-unplug and suspend/resume behaviour | ⛔ |
| 13 | Full resolution × FPS × DPI performance matrix | ⛔ |
| 14 | Green CI on `main` | ⛔ currently failing |
| 15 | First public release with a published package and checksum | ⛔ |

## Deliberately parked

These exist in the code but are **not** part of the product surface. They stay
parked until someone asks for them *and* they can be verified:

- **System audio muxing** — WASAPI loopback → AAC, engine-seam only
- **Microphone capture** — implementation present, no endpoint to verify on the
  test host
- **Multi-monitor composition** — implemented, runtime behaviour unverified
- **Hardware-transform verification** — probing implemented and passing locally

## Out of scope

AreaRec will not become a video editor or an OBS replacement. No timeline, no
scenes, no streaming, no annotations, no webcam, no plugin system, no account,
no cloud, no telemetry, and no external encoder process.

## Known problems

- CI on `main` is failing; the failure is in the native workflow and has not
  been diagnosed.
- `PUBLISH_GITHUB.ps1` and `GITHUB.md` are repository bootstrapping leftovers
  from the initial publication. `GITHUB.md` still records the settings the
  repository *should* have, which no longer match what is live on GitHub.
