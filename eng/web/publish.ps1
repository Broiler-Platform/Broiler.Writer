[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string] $Tag,
    [string] $OutputDirectory,
    [string] $HostingVersion,
    [string] $GraphicsVersion,
    [switch] $VerificationOnly
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$config = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'producer.json') | ConvertFrom-Json
function Invoke-Checked([string] $Executable, [string[]] $Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?\z' -or $Tag -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]+\z') { throw 'Explicit semantic version and safe source tag required.' }
Push-Location $PSScriptRoot
try {
    $sdk = (& dotnet --version).Trim()
    if ($sdk -notmatch '^10\.0\.') { throw 'Provision a .NET 10 SDK (10.0.x).' }
    $workloads = & dotnet workload list
    if (($workloads -join "`n") -notmatch 'wasm-tools\s+(10\.0\.\S+)') { throw 'Provision the .NET 10 wasm-tools workload before publishing.' }
    $workload = $Matches[1]
    $workloadSet = (& dotnet workload --version).Trim()
    $commit = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}\z') { throw 'Cannot resolve producer commit.' }
    if ($env:GITHUB_SHA -and $env:GITHUB_SHA -ne $commit) { throw 'Checkout must match the workflow source SHA.' }
    $dirty = @(& git -C $root status --porcelain --untracked-files=normal).Count -gt 0
    if ($dirty -and !$VerificationOnly) { throw 'Release producers require committed clean sources. Use -VerificationOnly for local uncommitted checks; those assets are not release eligible.' }
    $tagCommit = & git -C $root rev-parse --verify --quiet "refs/tags/$Tag^{commit}"
    if ($LASTEXITCODE -eq 0 -and $tagCommit.Trim() -ne $commit) { throw 'The source tag already points at a different commit.' }
    $properties = @("-p:$($config.versionProperty)=$Version")
    if ($config.product -eq 'launcher') {
        if (!$HostingVersion -or !$GraphicsVersion) { throw 'Launcher requires explicitly released Hosting and Graphics versions; no local-feed fallback is allowed.' }
        foreach ($item in @(@{Id='broiler.hosting.contracts';Version=$HostingVersion},@{Id='broiler.hosting.webassembly';Version=$HostingVersion},@{Id='broiler.graphics';Version=$GraphicsVersion},@{Id='broiler.graphics.webassembly';Version=$GraphicsVersion})) {
            $feed = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$($item.Id)/index.json"
            if ($item.Version -notin $feed.versions) { throw "Unreleased dependency: $($item.Id) $($item.Version)" }
        }
        $properties += "-p:BroilerWebHostingVersion=$HostingVersion", "-p:BroilerWebGraphicsVersion=$GraphicsVersion"
    }
    if (!$OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts/web-producer/$Version" }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output) { throw 'Output directory must be new; release assets cannot be overwritten.' }
    New-Item -ItemType Directory -Path $output | Out-Null
    $project = Join-Path $root $config.project
    $trimmed = (& dotnet msbuild $project -nologo -p:Configuration=Release -getProperty:PublishTrimmed).Trim()
    if ($LASTEXITCODE -ne 0 -or $trimmed -ne 'true') { throw 'The browser head must explicitly enable PublishTrimmed.' }
    Invoke-Checked dotnet (@('publish', $project, '-c', 'Release', '-m:1', '-p:UseBroilerUiSource=false', '-p:RunAOTCompilation=false', '-o', (Join-Path $output 'publish')) + $properties)
    $assets = Get-Content -Raw -LiteralPath (Join-Path (Split-Path $project) 'obj/project.assets.json') | ConvertFrom-Json
    $packages = @($assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Name } | Sort-Object)
    if ($config.product -eq 'launcher') {
        foreach ($asset in @('broiler.graphics.replay.json','broiler.hosting.webassembly.js')) {
            if (!(Test-Path -LiteralPath (Join-Path $output "publish/wwwroot/$asset"))) { throw "The released W1 cohort did not provide $asset" }
        }
        foreach ($required in @('Broiler.Hosting.Contracts','Broiler.Hosting.WebAssembly','Broiler.Graphics','Broiler.Graphics.WebAssembly')) {
            $expected = if ($required -like 'Broiler.Hosting.*') { $HostingVersion } else { $GraphicsVersion }
            if ("$required/$expected" -notin $packages) { throw "Resolved package differs from requested release: $required" }
        }
        if ($packages | Where-Object { $_ -match '^Broiler\.(Hosting|Graphics)\.(Windows|Linux|Android)/' }) { throw 'Native dependencies reached the web launcher.' }
    }
    $replayManifest = Join-Path $output 'publish/wwwroot/broiler.graphics.replay.json'
    if (Test-Path -LiteralPath $replayManifest) {
        $replayProtocol = (Get-Content -Raw -LiteralPath $replayManifest | ConvertFrom-Json).protocol
        if ($replayProtocol -ne 'broiler.canvas/1') { throw 'Unsupported replay protocol.' }
    } elseif ('Broiler.Graphics.WebAssembly/0.1.0-preview.14' -in $packages) {
        $replayProtocol = 'broiler.canvas/legacy-preview14'
    } else { throw 'The selected renderer must declare its replay protocol.' }
    $inputProtocol = if ($config.product -eq 'launcher') { 'broiler.host/1' } else { "$($config.product).browser/1" }
    $identity = @{
        product=$config.product; productName=$config.productName; repository=$(if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { $config.repository }); version=$Version; tag=$Tag; commit=$commit
        sdk=$sdk; workload=$workload; workloadSet=$workloadSet; runtimeMode='trimmed-interpreter'; publishTrimmed=$true; runAotCompilation=$false
        dirty=[bool]$dirty; verificationOnly=[bool]$VerificationOnly; releaseEligible=(!$dirty -and !$VerificationOnly); dependencies=$packages
        replayProtocol=$replayProtocol; inputProtocol=$inputProtocol
    }
    $identityPath = Join-Path $output 'identity.json'
    $identity | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $identityPath -Encoding utf8
    & dotnet --info | Out-File (Join-Path $output 'dotnet-info.txt')
    $workloads | Out-File (Join-Path $output 'workloads.txt')
    Invoke-Checked python @((Join-Path $PSScriptRoot 'artifact.py'), 'pack', '--wwwroot', (Join-Path $output 'publish/wwwroot'), '--output', (Join-Path $output 'release'), '--identity', $identityPath)
    $archive = @(Get-ChildItem -LiteralPath (Join-Path $output 'release') -Filter '*.zip')[0].FullName
    $metadata = @(Get-ChildItem -LiteralPath (Join-Path $output 'release') -Filter '*.web-artifact.json')[0].FullName
    Invoke-Checked python @((Join-Path $PSScriptRoot 'artifact.py'), 'verify', '--archive', $archive, '--metadata', $metadata, '--extract', (Join-Path $output 'extracted'))
    "Published and verified $archive"
} finally { Pop-Location }
