# AreaRec repository guidance

AreaRec is a Windows-native .NET 8 screen recorder. The supported product path
is the solution in `AreaRec.sln`; the former Python/FFmpeg implementation is
historical documentation only.

## Architecture boundaries

- Keep UI orchestration in `src/AreaRec.App`.
- Keep platform-independent contracts, geometry, timing and lifecycle logic in
  `src/AreaRec.Core`.
- Keep WGC and DXGI capture backends in `src/AreaRec.Capture`.
- Keep D3D11 resource processing in `src/AreaRec.Graphics`.
- Keep Media Foundation encoding, MP4 finalization and playback validation in
  `src/AreaRec.Media`.
- Keep Win32, WinRT, DPI and WASAPI interop in
  `src/AreaRec.Platform.Windows`.

Do not add an external encoder, subprocess, network service, telemetry, cloud
dependency or third-party runtime. Do not reintroduce Python, FFmpeg, Electron
or a second recording implementation.

## Verification

Use the x64 .NET SDK on Windows where possible:

```powershell
dotnet restore AreaRec.sln --runtime win-x64
dotnet build AreaRec.sln --configuration Release --no-restore
dotnet format AreaRec.sln --verify-no-changes --no-restore
dotnet run --project tests/AreaRec.Core.Tests/AreaRec.Core.Tests.csproj --configuration Release --no-build
```

The runtime and end-to-end smoke tests in `docs/development/testing.md` require an
interactive Windows desktop. Record hardware-dependent gaps as `NOT VERIFIED`;
never infer runtime correctness from compilation alone.

Generated `bin/`, `obj/`, `artifacts/` and recordings are ignored. Preserve
atomic output finalization, explicit disposal of COM/D3D resources, and
CancellationToken-aware operations.
