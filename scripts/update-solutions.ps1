[CmdletBinding()]
param(
    [switch] $Verify
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $repositoryRoot 'eng/solutions.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

# Each component repository carries nested checkouts of the components it depends on so
# that it still builds standalone. Composed here, those nested copies duplicate a top-level
# checkout, so a solution generated straight from the reference graph would list the same
# assembly twice. Fold every nested path onto the single top-level checkout it duplicates.
# Longest keys first: the doubly nested Broiler.UI paths have to fold before the shorter
# Broiler.Graphics ones can match.
$duplicateCheckoutMappings = [ordered]@{
    'Broiler.UI/Broiler.Documents/Broiler.Graphics/Broiler.Media/' = 'Broiler.Media/'
    'Broiler.Documents/Broiler.Graphics/Broiler.Media/'            = 'Broiler.Media/'
    'Broiler.UI/Broiler.Documents/Broiler.Graphics/'               = 'Broiler.Graphics/'
    'Broiler.UI/Broiler.Graphics/Broiler.Media/'                   = 'Broiler.Media/'
    'Broiler.UI/Broiler.Documents/Broiler.DOM/'                    = 'Broiler.DOM/'
    'Broiler.Documents/Broiler.Graphics/'                          = 'Broiler.Graphics/'
    'Broiler.Graphics/Broiler.Media/'                              = 'Broiler.Media/'
    'Broiler.Graphics/Broiler.Input/'                              = 'Broiler.Input/'
    'Broiler.Media/Broiler.Graphics/'                              = 'Broiler.Graphics/'
    'Broiler.Documents/Broiler.DOM/'                               = 'Broiler.DOM/'
    'Broiler.UI/Broiler.Documents/'                                = 'Broiler.Documents/'
    'Broiler.UI/Broiler.Graphics/'                                 = 'Broiler.Graphics/'
    'Broiler.UI/Broiler.Input/'                                    = 'Broiler.Input/'
}


$referenceCache = @{}

function Convert-ToRepositoryPath {
    param(
        [Parameter(Mandatory)]
        [string] $FullPath
    )

    $normalizedFullPath = [IO.Path]::GetFullPath($FullPath)
    $rootPrefix = $repositoryRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $normalizedFullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Project path escapes the repository: $normalizedFullPath"
    }
    $relativePath = $normalizedFullPath.Substring($rootPrefix.Length).Replace('\', '/')

    foreach ($mapping in $duplicateCheckoutMappings.GetEnumerator()) {
        if ($relativePath.StartsWith($mapping.Key, [StringComparison]::OrdinalIgnoreCase)) {
            $relativePath = $mapping.Value + $relativePath.Substring($mapping.Key.Length)
            break
        }
    }


    $canonicalFullPath = Join-Path $repositoryRoot $relativePath
    if (-not (Test-Path -LiteralPath $canonicalFullPath -PathType Leaf)) {
        throw "Project does not exist: $relativePath"
    }

    return $relativePath
}

function Resolve-ProjectReference {
    param(
        [Parameter(Mandatory)]
        [string] $ProjectPath,

        [Parameter(Mandatory)]
        [string] $Include
    )

    $projectFullPath = Join-Path $repositoryRoot $ProjectPath
    $projectDirectory = Split-Path -Parent $projectFullPath

    $resolvedInclude = $Include
    $resolvedInclude = $resolvedInclude.Replace(
        '$(MSBuildThisFileDirectory)',
        $projectDirectory + [IO.Path]::DirectorySeparatorChar)
    $resolvedInclude = $resolvedInclude.Replace(
        '$(BroilerDomPath)',
        (Join-Path $repositoryRoot 'Broiler.DOM/Broiler.Dom/Broiler.Dom.csproj'))
    $resolvedInclude = $resolvedInclude.Replace(
        '$(BroilerGraphicsPath)',
        (Join-Path $repositoryRoot 'Broiler.Graphics/src/Broiler.Graphics/Broiler.Graphics.csproj'))
    # A root rather than a project: Broiler.UI reaches its Documents references
    # through it, so a consumer with its own copy builds one of each assembly.
    $resolvedInclude = $resolvedInclude.Replace(
        '$(BroilerDocumentsRoot)',
        (Join-Path $repositoryRoot 'Broiler.Documents'))
    $resolvedInclude = $resolvedInclude.Replace(
        '$(BroilerGraphicsRoot)',
        (Join-Path $repositoryRoot 'Broiler.Graphics'))
    $resolvedInclude = $resolvedInclude.Replace(
        '$(BroilerInputRoot)',
        (Join-Path $repositoryRoot 'Broiler.Input'))
    $resolvedInclude = $resolvedInclude.Replace(
        '$(BroilerMediaRoot)',
        (Join-Path $repositoryRoot 'Broiler.Media'))
    $resolvedInclude = $resolvedInclude.Replace(
        '$(BroilerDomRoot)',
        (Join-Path $repositoryRoot 'Broiler.DOM'))


    if ($resolvedInclude.Contains('$(')) {
        throw "Unsupported property in ProjectReference '$Include' from '$ProjectPath'."
    }

    # MSBuild writes ProjectReference includes with backslashes whatever the host. On a
    # non-Windows host a backslash is an ordinary filename character, so GetFullPath below
    # would fold '..\..\Broiler.Graphics\...' into one nonsensical component instead of
    # walking up two directories. Fold them onto the platform separator first; on Windows
    # this is a no-op. The absolute paths substituted above are already host-native.
    $resolvedInclude = $resolvedInclude.Replace('\', [IO.Path]::DirectorySeparatorChar)

    if (-not [IO.Path]::IsPathRooted($resolvedInclude)) {
        $resolvedInclude = Join-Path $projectDirectory $resolvedInclude
    }

    return Convert-ToRepositoryPath -FullPath $resolvedInclude
}

function Get-ProjectReferences {
    param(
        [Parameter(Mandatory)]
        [string] $ProjectPath
    )

    if ($referenceCache.ContainsKey($ProjectPath)) {
        return @($referenceCache[$ProjectPath])
    }

    $projectFullPath = Join-Path $repositoryRoot $ProjectPath
    [xml] $project = Get-Content -Raw -LiteralPath $projectFullPath
    $references = @(
        $project.SelectNodes('//ProjectReference[@Include]') |
            ForEach-Object {
                foreach ($include in ([string] $_.Include).Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
                    Resolve-ProjectReference -ProjectPath $ProjectPath -Include $include
                }
            } |
            Sort-Object -Unique
    )

    $referenceCache[$ProjectPath] = $references
    return @($references)
}

function Get-ProjectClosure {
    param(
        [Parameter(Mandatory)]
        [string[]] $Roots
    )

    $pending = [Collections.Generic.Queue[string]]::new()
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

    foreach ($root in $Roots) {
        $pending.Enqueue((Convert-ToRepositoryPath -FullPath (Join-Path $repositoryRoot $root)))
    }

    while ($pending.Count -gt 0) {
        $projectPath = $pending.Dequeue()
        if (-not $visited.Add($projectPath)) {
            continue
        }

        foreach ($reference in Get-ProjectReferences -ProjectPath $projectPath) {
            $pending.Enqueue($reference)
        }
    }

    return @($visited | Sort-Object)
}

function Convert-ToXmlAttribute {
    param(
        [Parameter(Mandatory)]
        [string] $Value
    )

    return [Security.SecurityElement]::Escape($Value)
}

function New-SolutionText {
    param(
        [Parameter(Mandatory)]
        [pscustomobject] $Definition,

        [Parameter(Mandatory)]
        [string[]] $Projects
    )

    $rootSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($root in $Definition.roots) {
        [void] $rootSet.Add((Convert-ToRepositoryPath -FullPath (Join-Path $repositoryRoot $root)))
    }

    # Solution-level deploy flags. Visual Studio's F5 and `msbuild /t:Deploy` on
    # the solution use these to install an application head; `dotnet build` does
    # not, which is why nothing in CI depends on them. They live in the manifest
    # rather than in the .slnx because a hand-edit to a generated file is silently
    # reverted by the next generator run.
    $deployByProject = @{}
    foreach ($entry in @($Definition.deploy)) {
        if ($null -eq $entry) {
            continue
        }

        $deployProject = Convert-ToRepositoryPath -FullPath (Join-Path $repositoryRoot $entry.project)
        if ($deployProject -notin $Projects) {
            throw (
                "$($Definition.path) declares a deploy entry for '$($entry.project)', " +
                'which is not in its project closure.')
        }

        $solutionExpression = [string] $entry.solution
        if ([string]::IsNullOrWhiteSpace($solutionExpression)) {
            throw "$($Definition.path) declares a deploy entry for '$($entry.project)' with no solution expression."
        }

        $deployByProject[$deployProject] = $solutionExpression
    }

    $groups = [ordered]@{}
    $groups['Entry points'] = @($Projects | Where-Object { $rootSet.Contains($_) })
    foreach ($project in $Projects | Where-Object { -not $rootSet.Contains($_) }) {
        $topLevelDirectory = $project.Split('/')[0]
        $groupName = "Dependencies/$topLevelDirectory"
        if (-not $groups.Contains($groupName)) {
            $groups[$groupName] = @()
        }
        $groups[$groupName] += $project
    }

    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('<Solution>')
    $lines.Add('  <!-- Generated from eng/solutions.json by scripts/update-solutions.ps1. -->')
    $lines.Add('  <Configurations>')
    $lines.Add('    <BuildType Name="Debug" />')
    $lines.Add('    <BuildType Name="Release" />')
    $lines.Add('  </Configurations>')

    foreach ($group in $groups.GetEnumerator()) {
        if ($group.Value.Count -eq 0) {
            continue
        }

        $folderName = Convert-ToXmlAttribute -Value "/$($group.Key)/"
        $lines.Add("  <Folder Name=`"$folderName`">")
        foreach ($project in $group.Value | Sort-Object) {
            $projectPath = Convert-ToXmlAttribute -Value $project
            if ($deployByProject.ContainsKey($project)) {
                $deploySolution = Convert-ToXmlAttribute -Value $deployByProject[$project]
                $lines.Add("    <Project Path=`"$projectPath`">")
                $lines.Add("      <Deploy Solution=`"$deploySolution`" />")
                $lines.Add('    </Project>')
            }
            else {
                $lines.Add("    <Project Path=`"$projectPath`" />")
            }
        }
        $lines.Add('  </Folder>')
    }

    $lines.Add('</Solution>')
    return ($lines -join "`n") + "`n"
}

$manifestSolutionPaths = @($manifest.solutions | ForEach-Object { [string] $_.path })
$duplicateManifestPaths = @(
    $manifestSolutionPaths |
        Group-Object |
        Where-Object Count -gt 1 |
        ForEach-Object Name
)
if ($duplicateManifestPaths.Count -gt 0) {
    throw "Duplicate solution paths in eng/solutions.json:`n  $($duplicateManifestPaths -join "`n  ")"
}

$testRootOwners = @{}
foreach ($definition in $manifest.solutions | Where-Object path -Like '*.Tests.slnx') {
    foreach ($root in $definition.roots) {
        $canonicalRoot = Convert-ToRepositoryPath -FullPath (Join-Path $repositoryRoot $root)
        if ($testRootOwners.ContainsKey($canonicalRoot)) {
            throw (
                "Test root '$canonicalRoot' belongs to both " +
                "'$($testRootOwners[$canonicalRoot])' and '$($definition.path)'.")
        }
        $testRootOwners[$canonicalRoot] = $definition.path
    }
}

$topLevelSolutions = @(
    Get-ChildItem -LiteralPath $repositoryRoot -Filter '*.slnx' -File |
        ForEach-Object { $_.Name } |
        Sort-Object
)
$unexpectedSolutions = @(
    $topLevelSolutions |
        Where-Object { $_ -notin $manifestSolutionPaths }
)
if ($unexpectedSolutions.Count -gt 0) {
    throw "Top-level solution files are not declared in eng/solutions.json:`n  $($unexpectedSolutions -join "`n  ")"
}

$errors = [Collections.Generic.List[string]]::new()
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)

foreach ($definition in $manifest.solutions) {
    $solutionPath = Join-Path $repositoryRoot $definition.path
    $projects = Get-ProjectClosure -Roots @($definition.roots)

    if ($definition.path -notlike '*.Tests.slnx' -and
        $definition.path -ne 'Broiler.Benchmarks.slnx') {
        $qualityProjects = @(
            $projects |
                Where-Object { $_ -match '(?i)\.(Tests|Benchmarks)\.csproj$' }
        )
        if ($qualityProjects.Count -gt 0) {
            $errors.Add(
                "$($definition.path) includes test or benchmark projects: " +
                ($qualityProjects -join ', '))
        }
    }

    foreach ($pattern in @(
        $definition.forbiddenProjectPatterns |
            Where-Object { -not [string]::IsNullOrWhiteSpace([string] $_) }
    )) {
        $violations = @($projects | Where-Object { $_ -match $pattern })
        if ($violations.Count -gt 0) {
            $errors.Add(
                "$($definition.path) crosses a declared platform boundary for '$pattern': " +
                ($violations -join ', '))
        }
    }

    $expectedText = New-SolutionText -Definition $definition -Projects $projects
    if ($Verify) {
        if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
            $errors.Add("$($definition.path) is missing. Run scripts/update-solutions.ps1.")
            continue
        }

        $actualText = [IO.File]::ReadAllText($solutionPath).Replace("`r`n", "`n")
        if ($actualText -ne $expectedText) {
            $errors.Add("$($definition.path) is stale. Run scripts/update-solutions.ps1.")
        }
    }
    else {
        # The solution text is built with LF because that is what -Verify compares
        # against, but a .slnx is a Visual Studio format and this working tree holds
        # them with CRLF. Writing the LF form left all five files modified after a
        # run that changed nothing about them.
        $fileText = $expectedText.Replace("`r`n", "`n").Replace("`n", "`r`n")
        $current = if (Test-Path -LiteralPath $solutionPath -PathType Leaf) {
            [IO.File]::ReadAllText($solutionPath)
        }
        else {
            $null
        }

        # Written only when it differs. An unconditional write rewrites every
        # solution on every run, which touches timestamps the build watches and
        # reports work that did not happen.
        if ($current -ceq $fileText) {
            Write-Host ("Unchanged {0} ({1} roots, {2} projects)." -f
                $definition.path,
                @($definition.roots).Count,
                $projects.Count)
        }
        else {
            [IO.File]::WriteAllText($solutionPath, $fileText, $utf8WithoutBom)
            Write-Host ("Updated {0} ({1} roots, {2} projects)." -f
                $definition.path,
                @($definition.roots).Count,
                $projects.Count)
        }
    }
}

if ($errors.Count -gt 0) {
    throw "Solution verification failed:`n  $($errors -join "`n  ")"
}

if ($Verify) {
    Write-Host "Verified $($manifest.solutions.Count) focused solutions against eng/solutions.json."
}
