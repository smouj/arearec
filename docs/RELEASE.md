# Native release instructions

The native application targets Windows 10/11 x64 and does not require Python,
FFmpeg, Visual Studio, Node.js or the .NET runtime on the user's machine when
published self-contained.

## Build and verify

```powershell
dotnet restore AreaRec.sln --runtime win-x64
dotnet build AreaRec.sln --configuration Release --no-restore
dotnet run --project tests/AreaRec.Core.Tests/AreaRec.Core.Tests.csproj --configuration Release --no-build
dotnet run --project tests/AreaRec.Media.Smoke/AreaRec.Media.Smoke.csproj --configuration Release --no-build -- playback
dotnet run --project tests/AreaRec.Media.Smoke/AreaRec.Media.Smoke.csproj --configuration Release --no-build -- audio
dotnet run --project tests/AreaRec.Media.Smoke/AreaRec.Media.Smoke.csproj --configuration Release --no-build -- audio-mp4
dotnet run --project tests/AreaRec.Media.Smoke/AreaRec.Media.Smoke.csproj --configuration Release --no-build -- performance
dotnet run --project tests/AreaRec.EndToEnd.Smoke/AreaRec.EndToEnd.Smoke.csproj --configuration Release --no-build -- audio
dotnet publish src/AreaRec.App/AreaRec.App.csproj --configuration Release --runtime win-x64 --self-contained true --no-restore --output artifacts/native-publish-self-contained
dotnet run --project tests/AreaRec.App.Smoke/AreaRec.App.Smoke.csproj --configuration Release --no-build -- artifacts/native-publish-self-contained/AreaRec.exe
```

WGC/DXGI capture and end-to-end tests require an interactive Windows desktop;
the UI smoke additionally requires the published executable and verifies the
global hotkey, selector opening, Escape cancellation, drag selection, the
enabled Record state, a real UI recording, hotkey stop and MP4 playback. These
tests are run
locally when that environment is available and are recorded as
`NOT VERIFIED` when no desktop or capture device is available.

## Portable package

```powershell
./PUBLISH_PORTABLE.ps1
```

The script produces `artifacts/AreaRec-win-x64.zip` and a companion SHA-256
file. It also checks that the self-contained payload contains `AreaRec.exe` and
`hostfxr.dll` and rejects retired Python/FFmpeg artifacts. Distribute the ZIP
contents or the complete output directory. The app uses Windows Graphics Capture
with a DXGI fallback, Direct3D 11, Media Foundation and local settings under
`%LOCALAPPDATA%\AreaRec`. No network service or external encoder is required.

The package includes `INSTALL_PORTABLE.ps1`. Run it from the extracted package
to install AreaRec under `%LOCALAPPDATA%\Programs\AreaRec` and create the
desktop and Start menu shortcuts. The executable carries the AreaRec icon, so
the shortcuts remain branded after installation. Pass `-NoDesktopShortcut` to
omit only the desktop shortcut.

An installer/MSIX package and manual UI acceptance matrix remain **NOT
VERIFIED**.

See [docs/VERIFICATION.md](VERIFICATION.md) for the requirement-by-requirement
evidence and the remaining hardware/CI limits.
