# Installing AreaRec

> **No public release has been published yet.** AreaRec is alpha: the recorder
> works and has been exercised end to end, but the build is not on the Releases
> page and the cross-hardware verification is incomplete. This page describes
> what installation will look like, and how to run the current source today.

## Requirements

| | |
| --- | --- |
| System | Windows 10 (2004 / build 19041) or Windows 11, x64 |
| Graphics | Direct3D 11, for the capture and crop/readback path |
| Media | Media Foundation — used for H.264 encoding and the MP4 container |
| .NET | Not needed for the packaged build; .NET 8 SDK only to build from source |

Both Media Foundation and Direct3D 11 ship with Windows. A hardware H.264
encoder is used when the machine has one; otherwise the software path is used.

## The packaged release (planned)

The package is self-contained — no installer, no administrator rights and no
separate .NET runtime:

1. Download `AreaRec-win-x64.zip` from
   [Releases](https://github.com/smouj/arearec/releases).
2. Check it against the published SHA-256 sidecar.
3. Extract it anywhere and run `AreaRec.exe`.

To add shortcuts, run `INSTALL_PORTABLE.ps1` from the extracted folder:

```powershell
.\INSTALL_PORTABLE.ps1
```

That copies the build into `%LOCALAPPDATA%\Programs\AreaRec` and creates a Start
Menu entry and a desktop shortcut. Pass `-NoDesktopShortcut` to skip the desktop
one. The executable carries the AreaRec icon, so the shortcuts stay branded.

An MSIX package is **not** available.

## Running the current source

```powershell
git clone https://github.com/smouj/arearec
cd arearec
dotnet restore AreaRec.sln --runtime win-x64
dotnet build AreaRec.sln --configuration Release --no-restore
dotnet run --project src\AreaRec.App\AreaRec.App.csproj
```

See [../development/building.md](../development/building.md) for the full build,
test and packaging flow.

## Uninstalling

The build is portable: delete the folder. If you used `INSTALL_PORTABLE.ps1`,
also delete `%LOCALAPPDATA%\Programs\AreaRec` and the shortcuts it created, and
`%LOCALAPPDATA%\AreaRec` if you want to drop your saved settings.

Your recordings live wherever you told AreaRec to save them — uninstalling never
touches them.
