<#
.SYNOPSIS
  Builds an installer for the assistant: one setup file that needs nothing else on the other PC.

.DESCRIPTION
  1. Publishes the app self-contained for 64-bit Windows (no .NET install needed).
  2. Packs it with Velopack into Releases\DesktopAssistant-win-Setup.exe, plus the update files.
  3. With -Repo, the app checks that GitHub repository's Releases for updates, and earlier releases are
     downloaded first so updates can be small "delta" downloads.
  4. With -Repo and -Token, the new version is uploaded to GitHub Releases, where installed copies find it.

.EXAMPLE
  .\build-release.ps1 -Version 1.0.0
  .\build-release.ps1 -Version 1.0.1 -Repo https://github.com/you/assistant -Token ghp_xxx
#>
param(
    [Parameter(Mandatory)][string]$Version,   # e.g. 1.0.0; every release needs a higher one
    [string]$Repo = "",                       # https://github.com/<user>/<repo>; empty: no update checks
    [string]$Token = ""                       # a GitHub token with "contents: write", only for uploading
)

$ErrorActionPreference = "Stop"

# A token typed as %name% in cmd arrives with the percent signs when no such variable exists.
if ($Token -match '^%.*%$') {
    throw "The token still has % signs around it. Save it with setx ASSISTANT_GH_TOKEN ""github_pat_..."", open a new cmd window, then pass -Token %ASSISTANT_GH_TOKEN% exactly like that."
}
$vpkVersion = "1.2.161"   # keep equal to the Velopack package version in Assistant.App.csproj
$publish = Join-Path $PSScriptRoot "publish"
$releases = Join-Path $PSScriptRoot "Releases"

if (-not (Test-Path (Join-Path $PSScriptRoot "lib\Everything64.dll"))) {
    throw "lib\Everything64.dll is missing. Copy it from the Everything SDK first."
}

Write-Host "== Installing the Velopack packer (vpk $vpkVersion)"
dotnet tool update -g vpk --version $vpkVersion
if ($LASTEXITCODE -ne 0) { throw "Couldn't install vpk." }

Write-Host "== Publishing version $Version"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish (Join-Path $PSScriptRoot "src\Assistant.App\Assistant.App.csproj") `
    -c Release -r win-x64 --self-contained -o $publish `
    -p:Version=$Version -p:UpdateRepo=$Repo
if ($LASTEXITCODE -ne 0) { throw "Publishing failed." }

if ($Repo) {
    Write-Host "== Getting earlier releases from $Repo (for small delta updates)"
    $download = @("download", "github", "--repoUrl", $Repo, "--outputDir", $releases)
    if ($Token) { $download += @("--token", $Token) }
    vpk @download
    # No releases yet is fine for the first one.
}

Write-Host "== Packing"
vpk pack --packId DesktopAssistant --packVersion $Version --packDir $publish --mainExe Assistant.exe `
    --packTitle "Assistant" --runtime win-x64 --icon (Join-Path $PSScriptRoot "src\Assistant.App\assistant.ico") `
    --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "Packing failed." }

if ($Repo -and $Token) {
    Write-Host "== Uploading to GitHub Releases"
    vpk upload github --repoUrl $Repo --token $Token --outputDir $releases --publish `
        --releaseName "Assistant $Version" --tag "v$Version"
    if ($LASTEXITCODE -ne 0) { throw "Uploading failed." }
}

Write-Host ""
Write-Host "Done. The installer is $releases\DesktopAssistant-win-Setup.exe"
