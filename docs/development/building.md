# Building AreaRec from source

## Prerequisites

| | |
| --- | --- |
| System | Windows 10 (2004 / build 19041) or Windows 11, x64, for anything that touches capture or Media Foundation |
| SDK | [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) |
| PowerShell | Windows PowerShell 5.1 or PowerShell 7+ |

Visual Studio, Node.js, Python and FFmpeg are **not** required — the native
solution is the only supported product path, and the build has guards that fail
if a legacy artifact or a forbidden runtime dependency reappears.

## Restore and build

```powershell
dotnet restore AreaRec.sln --runtime win-x64
dotnet build AreaRec.sln --configuration Release --no-restore
```

## Run

```powershell
dotnet run --project src/AreaRec.App/AreaRec.App.csproj
```

Recordings need an interactive Windows desktop. On a headless or CI machine the
capture and end-to-end smokes report `NOT VERIFIED` instead of passing.

## Test

```powershell
dotnet run --project tests/AreaRec.Core.Tests/AreaRec.Core.Tests.csproj --configuration Release --no-build
dotnet run --project tests/AreaRec.Media.Smoke/AreaRec.Media.Smoke.csproj --configuration Release --no-build -- playback
dotnet run --project tests/AreaRec.Media.Smoke/AreaRec.Media.Smoke.csproj --configuration Release --no-build -- audio
dotnet run --project tests/AreaRec.Media.Smoke/AreaRec.Media.Smoke.csproj --configuration Release --no-build -- audio-mp4
dotnet run --project tests/AreaRec.Media.Smoke/AreaRec.Media.Smoke.csproj --configuration Release --no-build -- abort
dotnet run --project tests/AreaRec.Media.Smoke/AreaRec.Media.Smoke.csproj --configuration Release --no-build -- performance
dotnet run --project tests/AreaRec.EndToEnd.Smoke/AreaRec.EndToEnd.Smoke.csproj --configuration Release --no-build -- audio
```

The UI smoke additionally needs a published executable:

```powershell
dotnet publish src/AreaRec.App/AreaRec.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --output artifacts/native-publish-self-contained
dotnet run --project tests/AreaRec.App.Smoke/AreaRec.App.Smoke.csproj --configuration Release --no-build -- artifacts/native-publish-self-contained/AreaRec.exe
```

What each of these actually proves is in [testing.md](testing.md).

## Publish a portable package

```powershell
./PUBLISH_PORTABLE.ps1
```

Produces `artifacts/AreaRec-win-x64.zip` plus a SHA-256 sidecar. The script also
verifies that the self-contained payload really contains `AreaRec.exe` and
`hostfxr.dll`, and rejects retired Python/FFmpeg artifacts. Release steps and
the current limitations are in [releasing.md](releasing.md).

## Solution layout

```
AreaRec.sln
├── src/AreaRec.App                WinForms shell, selector, tray, hotkey, settings
├── src/AreaRec.Core               region, settings, clock, state, contracts
├── src/AreaRec.Capture            WGC backend, DXGI fallback, monitor compositor
├── src/AreaRec.Graphics           D3D11 resources, crop/scale/format
├── src/AreaRec.Media              Media Foundation H.264/AAC, MP4 sink, probes
├── src/AreaRec.Platform.Windows   DPI, Win32/WinRT, WASAPI interop
└── tests/                         Core, Media, Capture, EndToEnd, App smokes
```

Dependency direction and ownership rules: [architecture.md](architecture.md).

## Continuous integration

`.github/workflows/native-ci.yml` runs the restore, build, a format/analyzer
check, a native dependency guard, a legacy artifact guard, the core and media
smokes, and the portable publish on `windows-latest`.
