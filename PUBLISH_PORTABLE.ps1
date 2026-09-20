[CmdletBinding()]
param(
    [string]$OutputRoot = "artifacts",
    [string]$PackageName = "AreaRec-win-x64"
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot ".")).Path
$preferredDotnet = Join-Path ${env:ProgramFiles} "dotnet\dotnet.exe"
$dotnetPath = if (Test-Path -LiteralPath $preferredDotnet) {
    (Get-Item -LiteralPath $preferredDotnet).FullName
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}
$outputDirectory = Join-Path $repoRoot $OutputRoot
$packageDirectory = Join-Path $outputDirectory $PackageName
$zipPath = Join-Path $outputDirectory "$PackageName.zip"
$hashPath = Join-Path $outputDirectory "$PackageName.zip.sha256"

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $packageDirectory) {
    Remove-Item -LiteralPath $packageDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
if (Test-Path -LiteralPath $hashPath) {
    Remove-Item -LiteralPath $hashPath -Force
}

& $dotnetPath restore (Join-Path $repoRoot "AreaRec.sln") --runtime win-x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $dotnetPath publish (Join-Path $repoRoot "src\AreaRec.App\AreaRec.App.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --no-restore `
    --output $packageDirectory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Compress-Archive -Path (Join-Path $packageDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $PackageName.zip" | Set-Content -LiteralPath $hashPath -Encoding ASCII

Write-Host "Portable package: $zipPath"
Write-Host "SHA-256: $hash"
