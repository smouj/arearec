[CmdletBinding()]
param(
    [string]$SourceRoot,
    [string]$InstallRoot,
    [switch]$NoDesktopShortcut
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($SourceRoot)) { $SourceRoot = $PSScriptRoot }
if ([string]::IsNullOrWhiteSpace($InstallRoot)) { $InstallRoot = Join-Path $env:LOCALAPPDATA "Programs\AreaRec" }
$sourceDirectory = (Resolve-Path -LiteralPath $SourceRoot).Path
$sourceExecutable = Join-Path $sourceDirectory "AreaRec.exe"

if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
    throw "No se encontró AreaRec.exe en '$sourceDirectory'. Ejecuta este script desde la carpeta extraída del paquete portátil."
}

$installDirectory = [System.IO.Path]::GetFullPath($InstallRoot)
New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
Get-ChildItem -LiteralPath $sourceDirectory -Force | Where-Object { $_.FullName -ne $installDirectory } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $installDirectory -Recurse -Force
}

$installedExecutable = Join-Path $installDirectory "AreaRec.exe"
$startMenuDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\AreaRec"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "AreaRec.lnk"
$startMenuShortcut = Join-Path $startMenuDirectory "AreaRec.lnk"

$shell = New-Object -ComObject WScript.Shell
function New-AreaRecShortcut([string]$Path) {
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $installedExecutable
    $shortcut.WorkingDirectory = $installDirectory
    $shortcut.IconLocation = "$installedExecutable,0"
    $shortcut.Description = "Graba una región de la pantalla con AreaRec"
    $shortcut.Save()
}

if (-not $NoDesktopShortcut) {
    New-AreaRecShortcut $desktopShortcut
}
New-AreaRecShortcut $startMenuShortcut

Write-Host "AreaRec instalado en: $installDirectory"
if (-not $NoDesktopShortcut) { Write-Host "Acceso directo de escritorio: $desktopShortcut" }
Write-Host "Acceso directo del menú Inicio: $startMenuShortcut"
