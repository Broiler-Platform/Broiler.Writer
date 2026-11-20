# Validate a selected Documents source snapshot without changing Writer's package pin.
param(
    [Parameter(Mandatory)]
    [string]$DocumentsSourceRoot,

    [Parameter(Mandatory)]
    [string]$OutputRoot,

    [Parameter(Mandatory)]
    [string]$PackageVersion,

    [switch]$CheckPlatforms,

    [switch]$CheckSourceGeometry,

    [switch]$CheckSharedComposition,

    [switch]$CheckUiPackages
)

$ErrorActionPreference = 'Stop'
$writerRoot = Split-Path -Parent $PSScriptRoot
$documentsRoot = (Resolve-Path -LiteralPath $DocumentsSourceRoot).ProviderPath
$output = [IO.Path]::GetFullPath($OutputRoot)
$feed = Join-Path $output 'feed'
$config = Join-Path $output 'NuGet.config'
$writerArtifacts = Join-Path $output 'writer'
New-Item -ItemType Directory -Path $feed -Force | Out-Null

function Invoke-DotNet {
    param([string[]]$CommandArguments)
    & dotnet @CommandArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($CommandArguments -join ' ') exited with $LASTEXITCODE."
    }
}

$packages = @(
    'Broiler.Documents.Model', 'Broiler.Documents', 'Broiler.Documents.FormatCodes',
    'Broiler.Documents.Pagination', 'Broiler.Documents.Docx', 'Broiler.Documents.Odt',
    'Broiler.Documents.Rtf', 'Broiler.Documents.Html', 'Broiler.Documents.Markdown',
    'Broiler.Documents.Pdf', 'Broiler.Documents.Pdf.Fonts', 'Broiler.Documents.Pdf.Images'
)
foreach ($package in $packages) {
    $project = Join-Path $documentsRoot "src/$package/$package.csproj"
    Invoke-DotNet @('pack', $project, '-c', 'Release', '-o', $feed,
        "-p:Version=$PackageVersion", "-p:PackageVersion=$PackageVersion", '--nologo')
}

$uiProjects = [ordered]@{}
if ($CheckUiPackages) {
    $uiProjects = [ordered]@{
        'Broiler.UI' = 'Foundation/Broiler.UI'
        'Broiler.UI.Standard' = 'Foundation/Broiler.UI.Standard'
        'Broiler.UI.RichEdit' = 'Abstractions/Text/Broiler.UI.RichEdit'
        'Broiler.UI.RichEdit.Standard' = 'Implementations/Standard/Text/Broiler.UI.RichEdit.Standard'
    }
}
$uiFeedMappings = ($uiProjects.Keys | ForEach-Object { "      <package pattern=`"$_`" />" }) -join "`n"
$feedXml = [Security.SecurityElement]::Escape($feed)
@"
<configuration>
  <packageSources>
    <clear />
    <add key="candidate" value="$feedXml" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="candidate">
      <package pattern="Broiler.Documents" />
      <package pattern="Broiler.Documents.*" />
$uiFeedMappings
    </packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $config

if ($CheckUiPackages) {
    $uiRoot = (Resolve-Path -LiteralPath (Join-Path $writerRoot '../Broiler.UI')).ProviderPath
    foreach ($package in $uiProjects.Keys) {
        $project = Join-Path $uiRoot "src/$($uiProjects[$package])/$package.csproj"
        Invoke-DotNet @('pack', $project, '-c', 'Release', '-o', $feed, '--nologo',
            "-p:Version=$PackageVersion", "-p:PackageVersion=$PackageVersion", "-p:RestoreConfigFile=$config",
            "-p:BroilerDocumentsModelPackageVersion=$PackageVersion", '-p:BroilerSharedParagraphComposition=true')
    }
}

$options = @(
    "-p:BroilerDocumentsPackageVersion=$PackageVersion",
    "-p:RestoreConfigFile=$config"
)
if ($CheckSourceGeometry -or $CheckUiPackages) {
    $options += '-p:BroilerDocumentsSourceGeometry=true'
}
if ($CheckUiPackages) {
    $options += @('-p:BroilerSharedParagraphComposition=true', '-p:BroilerUiSharedGeometry=true',
        '-p:BroilerUiHostedRichEditMenu=true', '-p:UseBroilerUiSource=false',
        "-p:BroilerUiPackageVersion=$PackageVersion", "-p:BroilerUiRichEditPackageVersion=$PackageVersion")
} elseif ($CheckSharedComposition) {
    $options += @('-p:BroilerSharedParagraphComposition=true', '-p:UseBroilerUiSource=true',
        "-p:BroilerDocumentsModelPackageVersion=$PackageVersion")
}
$testSolution = Join-Path $writerRoot 'Broiler.Writer.Tests.slnx'
Invoke-DotNet (@('restore', $testSolution, '--artifacts-path', $writerArtifacts, '--force-evaluate', '--nologo') + $options)

$assetsFile = Join-Path $writerArtifacts 'obj/Broiler.Writer.FormatCodes.Tests/project.assets.json'
$assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json
$resolved = @($assets.libraries.PSObject.Properties.Name -match '^Broiler\.Documents(?:\.[^/]+)?/')
$expected = $packages | ForEach-Object { "$_/$PackageVersion" }
if (Compare-Object $expected $resolved) {
    throw "Unexpected Documents package graph: $($resolved -join ', '). Use a new candidate version for each source snapshot."
}
foreach ($package in ($packages + @($uiProjects.Keys))) {
    $file = Join-Path $feed "$package.$PackageVersion.nupkg"
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA512).Hash
    $resolvedEntry = $assets.libraries.PSObject.Properties["$package/$PackageVersion"]
    $resolvedHash = if ($resolvedEntry) { $resolvedEntry.Value.sha512 }
    if ([Convert]::ToBase64String([Convert]::FromHexString($hash)) -cne $resolvedHash) {
        throw "The restored $package differs from the package just built. Choose a new candidate version."
    }
}

Invoke-DotNet (@('test', $testSolution, '-c', 'Release', '--no-restore', '--artifacts-path', $writerArtifacts,
        '--nologo', '-warnaserror', '--logger', 'trx;LogFileName=writer-candidate.trx', '--results-directory', $output) + $options)

if ($CheckPlatforms) {
    if ($IsWindows) {
        $project = Join-Path $writerRoot 'src/Broiler.Writer.Windows/Broiler.Writer.Windows.csproj'
        Invoke-DotNet (@('publish', $project, '-c', 'Release-Windows', '-p:PublishAot=true',
                '--artifacts-path', (Join-Path $output 'windows')) + $options)
        Invoke-DotNet (@('build', (Join-Path $writerRoot 'Broiler.Linux.Writer.slnx'), '-c', 'Release',
                '--artifacts-path', (Join-Path $output 'linux')) + $options)
    } else {
        $project = Join-Path $writerRoot 'src/Broiler.Writer.Linux/Broiler.Writer.Linux.csproj'
        Invoke-DotNet (@('publish', $project, '-c', 'Release-Linux', '-p:PublishAot=true',
                '--artifacts-path', (Join-Path $output 'linux')) + $options)
    }
    $project = Join-Path $writerRoot 'src/Broiler.Writer.WebAssembly/Broiler.Writer.WebAssembly.csproj'
    Invoke-DotNet (@('publish', $project, '-c', 'Release', '--artifacts-path', (Join-Path $output 'browser')) + $options)
    Invoke-DotNet (@('build', (Join-Path $writerRoot 'Broiler.Android.Writer.slnx'), '-c', 'Release',
            '--artifacts-path', (Join-Path $output 'android')) + $options)
}
