param(
  [string]$Repo = "smouj/arearec"
)
$ErrorActionPreference = "Stop"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
  throw "GitHub CLI (gh) no está instalado. Instálalo y ejecuta 'gh auth login'."
}

gh auth status | Out-Null

if (-not (Test-Path .git)) { git init -b main }
git add .
if (-not (git rev-parse --verify HEAD 2>$null)) {
  git commit -m "chore: bootstrap project"
}

$exists = $true
try { gh repo view $Repo | Out-Null } catch { $exists = $false }
if (-not $exists) {
  gh repo create $Repo --public --source=. --remote=origin --push --description "Minimal local-first native Windows region screen recorder producing MP4."
} else {
  if (-not (git remote get-url origin 2>$null)) { git remote add origin "https://github.com/$Repo.git" }
  git push -u origin main
}

git push origin --tags

gh repo edit $Repo --enable-issues --disable-wiki --add-topic screen-recorder --add-topic windows --add-topic dotnet --add-topic direct3d --add-topic media-foundation --add-topic offline --add-topic local-first --add-topic open-source

gh label create "scope:v0.1" --repo $Repo --description "Inside the frozen v0.1 product scope" --color "1D76DB" --force
gh label create "windows" --repo $Repo --description "Windows-specific behavior" --color "0078D4" --force
gh label create "release" --repo $Repo --description "Release and packaging work" --color "5319E7" --force

gh issue create --repo $Repo --title "test: Windows 10/11 end-to-end recording smoke test" --label "scope:v0.1,windows" --body "Validate region selection → 30/60 FPS recording → graceful stop → playable native Media Foundation MP4 on Windows 10 and Windows 11. Record Windows build, backend and DPI scaling used."
gh issue create --repo $Repo --title "fix: validate region selection under Windows DPI scaling" --label "scope:v0.1,windows" --body "Verify X/Y and width/height remain exact at common Windows scaling values (100%, 125%, 150%). Keep the solution minimal and do not expand into advanced multi-monitor work."
gh issue create --repo $Repo --title "release: produce the first portable v0.1 package" --label "release,scope:v0.1" --body "Define a reproducible self-contained Windows x64 package and document native Windows/.NET dependency licensing before attaching binaries to a release."

Write-Host "Repository ready: https://github.com/$Repo"
