<#
    Regenerates pluginmaster.json from the DalamudPackager-built plugin manifest.
    Keeps the hand-written descriptive fields, refreshes the version + timestamp.
#>
param(
    [Parameter(Mandatory = $true)] [string] $ManifestPath,
    [string] $PluginMasterPath = "pluginmaster.json",
    [string] $InternalName = "Emptor",
    [string] $ZipName = "Emptor.zip",
    [string] $RepoSlug = "Evernow/DalamudPlugins"
)

$ErrorActionPreference = "Stop"

$built = Get-Content $ManifestPath -Raw | ConvertFrom-Json
$master = Get-Content $PluginMasterPath -Raw | ConvertFrom-Json

$entry = $master | Where-Object { $_.InternalName -eq $InternalName }
if (-not $entry) { throw "No entry for $InternalName in $PluginMasterPath" }

$version = $built.AssemblyVersion
$api     = $built.DalamudApiLevel
$now     = [int][double]::Parse((Get-Date -UFormat %s))
$dl      = "https://github.com/$RepoSlug/releases/latest/download/$ZipName"

$entry.AssemblyVersion        = $version
$entry.TestingAssemblyVersion = $version
$entry.DalamudApiLevel        = $api
$entry.ApplicableVersion      = "any"
$entry.LastUpdate             = $now
$entry.DownloadLinkInstall    = $dl
$entry.DownloadLinkUpdate     = $dl
$entry.DownloadLinkTesting    = $dl

$master | ConvertTo-Json -Depth 10 | Set-Content $PluginMasterPath -Encoding UTF8
Write-Host "pluginmaster.json -> $InternalName $version (api $api)"
