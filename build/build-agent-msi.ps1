<#
.SYNOPSIS
    Builds the DomainNotice Agent MSI (Windows only; WiX 5 does not run on macOS/Linux).

.DESCRIPTION
    1. Reads the version from Directory.Build.props (one version for Server, Console and Agent, plan 11.1).
    2. Publishes DomainNotice.Agent (service) and DomainNotice.Agent.Session (UI) for win-x64, self-contained,
       Release, into artifacts\agent-publish.
    3. Builds DomainNotice.Agent.Installer\DomainNotice.Agent.Installer.wixproj.
    4. Writes artifacts\DomainNotice.Agent-<version>-x64.msi (asset name fixed by plan 11.4) and prints its SHA-256.

    The MSI has no server address built in. First installs pass the values on the command line:
      msiexec /i DomainNotice.Agent-<version>-x64.msi /qn SERVERHOST=<host> SERVERPORT=5150 SERVERFINGERPRINT=<hex> INSTALLPASSWORD=<pw>

.EXAMPLE
    pwsh build/build-agent-msi.ps1
#>
[CmdletBinding()]
param(
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

[xml] $props = Get-Content (Join-Path $root "Directory.Build.props")
$versionNode = $props.SelectSingleNode("//Version")
if (-not $versionNode) {
    throw "Version was not found in Directory.Build.props."
}
$version = $versionNode.InnerText.Trim()
if ($version -notmatch '^\d{1,5}\.\d{1,5}\.\d{1,5}$') {
    throw "Version '$version' is not a plain x.y.z version."
}

$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "agent-publish"
if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $publish | Out-Null

foreach ($project in @("DomainNotice.Agent\DomainNotice.Agent.csproj", "DomainNotice.Agent.Session\DomainNotice.Agent.Session.csproj")) {
    Write-Host "Publishing $project ..."
    dotnet publish (Join-Path $root $project) -c $Configuration -r win-x64 --self-contained true -p:DebugType=none -o $publish
    if ($LASTEXITCODE -ne 0) {
        throw "Publish of $project failed."
    }
}

foreach ($required in @("DomainNotice.Agent.exe", "DomainNotice.Agent.Session.exe")) {
    if (-not (Test-Path (Join-Path $publish $required))) {
        throw "$required is missing from $publish."
    }
}

$installer = Join-Path $root "DomainNotice.Agent.Installer\DomainNotice.Agent.Installer.wixproj"
Write-Host "Building the MSI ..."
dotnet build $installer -c $Configuration "-p:PackageVersion=$version" "-p:AgentPublishDir=$publish"
if ($LASTEXITCODE -ne 0) {
    throw "MSI build failed."
}

$built = Get-ChildItem -Path (Join-Path $root "DomainNotice.Agent.Installer\bin") -Filter "DomainNotice.Agent.msi" -Recurse |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $built) {
    throw "The MSI was not produced."
}

$destination = Join-Path $artifacts "DomainNotice.Agent-$version-x64.msi"
Copy-Item $built.FullName $destination -Force
$hash = (Get-FileHash -Algorithm SHA256 $destination).Hash.ToLowerInvariant()

Write-Host ""
Write-Host "Wrote $destination"
Write-Host "SHA-256: $hash"
Write-Host "SHA256SUMS.txt line: $hash  $(Split-Path -Leaf $destination)"
Write-Host ""
Write-Host "First install: msiexec /i `"$destination`" /qn SERVERHOST=<host> SERVERPORT=5150 SERVERFINGERPRINT=<fingerprint> INSTALLPASSWORD=<password>"
