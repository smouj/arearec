<p align="center">
  <img src="assets/logo.png" alt="AreaRec" width="128"><br>
  <strong>AreaRec</strong>
</p>

<p align="center">
  <em>Select a region. Record it. Get an MP4.</em>
</p>

<p align="center">
  <img src="assets/social-banner.png" alt="AreaRec — Select a region. Record it. Get an MP4." width="680">
</p>

<p align="center">
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20x64-blue?style=flat-square" alt="Platform: Windows 10/11 x64">
  <img src="https://img.shields.io/badge/engine-.NET%208-512bd4?style=flat-square" alt="Engine: .NET 8">
  <img src="https://img.shields.io/badge/capture-Windows%20Graphics%20Capture-0067c0?style=flat-square" alt="Capture: Windows Graphics Capture with DXGI fallback">
  <img src="https://img.shields.io/badge/license-MIT-green?style=flat-square" alt="License: MIT">
  <img src="https://img.shields.io/badge/status-alpha-orange?style=flat-square" alt="Status: alpha">
</p>

**AreaRec** is a tiny, local-first screen recorder for Windows built around one workflow: drag a rectangle over your screen, record exactly that area, get an MP4. No account, no cloud, no telemetry, no editor, no plugin system.

It is the capture half of the Area family:

| App | Job |
| --- | --- |
| **AreaRec** | Select a region. Record it. Get an MP4. |
| [**AreaCut**](https://github.com/smouj/areacut) | Cut it, reframe it, caption it, export it. |

The two are independent applications. AreaRec produces a plain MP4 that any tool can open — see [AreaCut](https://github.com/smouj/areacut) if you want to edit it.

## Contents

- [Status](#status)
- [Features](#features)
- [Requirements](#requirements)
- [Download and install](#download-and-install)
- [Run from source](#run-from-source)
- [Documentation](#documentation)
- [Privacy](#privacy)
- [License](#license)

## Status

AreaRec is **alpha**: the recorder works and has been exercised end to end, but
there is no published release and the cross-hardware verification matrix is
incomplete. What is proven and what is not is listed item by item in
[docs/history/verification-2026-09-20.md](docs/history/verification-2026-09-20.md).

| Area | State |
| --- | --- |
| Region selection, 30/60 FPS, cursor on/off, start/stop | ✅ implemented, smoke-tested locally |
| MP4 / H.264 output through Media Foundation | ✅ local end-to-end recordings decode |
| Windows Graphics Capture with DXGI Desktop Duplication fallback | ✅ local smoke on one host |
| Global hotkey `Ctrl+Shift+R`, tray lifecycle, versioned local settings | ✅ local UI smoke |
| Hardware H.264 encoder selection | ✅ NVIDIA encoder MFT selected on the test host |
| Self-contained portable package | ✅ produced locally, with SHA-256 |
| Mixed-DPI and multi-monitor visual accuracy | 🚧 **not verified** — needs two-monitor hardware |
| Device removal, display hot-unplug, suspend/resume | 🚧 **not verified** |
| Hardware matrix at 1080p/1440p/4K, 30 and 60 FPS | 🚧 **not verified** |
| System audio in the product UI | 🚧 engine ready, deliberately not exposed |
| Public release | ⛔ none yet |
| GitHub Actions on `main` | ⚠️ currently failing |

## Features

- **Mouse-driven region selection** — exact X/Y/width/height in physical pixels, negative virtual-desktop coordinates included
- **Windows Graphics Capture** as the primary backend, with **DXGI Desktop Duplication** as an automatic fallback
- **Direct3D 11** crop, scale and readback — no CPU bitmap round trip on the capture path
- **H.264 / MP4** through Media Foundation, hardware encoder when available, with a clear error when it is not
- **30 or 60 FPS**, cursor on or off
- **Global hotkey** `Ctrl+Shift+R` to start and stop, plus a tray lifecycle
- **Versioned local settings** for FPS, quality, cursor and save folder
- **Post-save validation** — the finished MP4 is re-inspected before the app reports success
- **Safe finalization** — recordings are written to a temporary file and moved into place atomically; a zero-frame session is rejected rather than saved as an empty video
- **No Python, no FFmpeg, no Electron, no network** — self-contained Windows-native build

## Requirements

- Windows 10 (2004 / build 19041) or Windows 11, x64
- Media Foundation and Direct3D 11 (both included in Windows)
- A hardware H.264 encoder is used when present; a software path exists
- .NET 8 SDK — only if you build from source

## Download and install

**No public release has been published yet.** Once the first one is out it will
appear under [Releases](https://github.com/smouj/arearec/releases) as a
self-contained `AreaRec-win-x64.zip` with a SHA-256 sidecar — no installer and
no .NET runtime required. Extract it, then optionally run
`INSTALL_PORTABLE.ps1` from the extracted folder to add Start Menu and desktop
shortcuts. Details: [docs/user/install.md](docs/user/install.md).

## Run from source

From Windows PowerShell:

```powershell
git clone https://github.com/smouj/arearec
cd arearec
dotnet restore AreaRec.sln --runtime win-x64
dotnet build AreaRec.sln --configuration Release --no-restore
dotnet run --project src\AreaRec.App\AreaRec.App.csproj
```

Capture and end-to-end smokes need an interactive Windows desktop. Full build,
test and packaging instructions: [docs/development/building.md](docs/development/building.md).

## Documentation

| Audience | Start here |
| --- | --- |
| Users | [docs/user/install.md](docs/user/install.md) · [usage](docs/user/usage.md) · [privacy](docs/user/privacy.md) |
| Contributors | [CONTRIBUTING.md](CONTRIBUTING.md) · [docs/development/building.md](docs/development/building.md) · [testing](docs/development/testing.md) |
| Architecture | [docs/development/architecture.md](docs/development/architecture.md) · [capture pipeline](docs/development/capture-pipeline.md) |
| Measurement and history | [performance](docs/development/performance.md) · [verification evidence](docs/history/verification-2026-09-20.md) |
| Full index | [docs/README.md](docs/README.md) |

## Design constraints

AreaRec will not become a video editor or an OBS replacement. System audio,
microphone, webcam, annotations and multi-monitor composition are outside the
current product surface; the audio engine exists behind the scenes and stays
unexposed until it can be verified. The v0.1 contract is written down in
[docs/development/product-scope.md](docs/development/product-scope.md).

## Privacy

AreaRec makes no network requests. Recordings are written straight to the folder
you choose, settings live in `%LOCALAPPDATA%\AreaRec`, and nothing is uploaded
or telemetered. Details: [docs/user/privacy.md](docs/user/privacy.md).

## License

MIT — see [LICENSE](LICENSE). The native build uses Windows APIs and the .NET
runtime only; the dependency inventory is in [THIRD_PARTY.md](THIRD_PARTY.md).
Brand assets: [docs/BRANDING.md](docs/BRANDING.md).

## Related

- [AreaCut](https://github.com/smouj/areacut) — the editing half of the family
- [github.com/smouj](https://github.com/smouj) — the rest of the desktop suite
