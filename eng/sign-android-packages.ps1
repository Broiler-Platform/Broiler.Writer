# Copied unchanged from Broiler-Platform/Broiler scripts/sign-android-packages.ps1 at ac913fd4,
# the canonical copy. Change it there and copy it again, so both repositories sign alike.
<#
.SYNOPSIS
    Signs Broiler's Android packages with the release key held in repository secrets.

.DESCRIPTION
    The two package formats are signed by different tools, because they are checked by different
    things:

    * An .aab is signed with jarsigner. apksigner only speaks the APK signature schemes, which a
      bundle does not carry, and the JAR signature is what Play checks on upload.
    * An .apk is zipaligned and signed with apksigner, which writes the v2/v3 signature blocks.
      An APK carrying only a JAR signature is refused at install from Android 11 on.

    The key material never reaches a command line or the repository. The script reads four
    environment variables, which .github/workflows/broiler-preview-package.yml maps from
    repository secrets of the same names:

        ANDROID_KEYSTORE_BASE64      the keystore file, base64-encoded (PKCS#12 or JKS)
        ANDROID_KEYSTORE_PASSWORD    password of that keystore
        ANDROID_KEY_ALIAS            alias of the signing key inside it
        ANDROID_KEY_PASSWORD         password of that key

    The keystore is decoded to a temporary file outside the workspace — so no packaging glob or
    artifact upload can reach it — and deleted again before the script returns. The passwords are
    handed to the signing tools through their env-variable forms (-storepass:env, --ks-pass env:),
    so they never appear in the process list.

    Signing is verified rather than assumed. Each package has to verify, and its signer
    certificate has to be the one the keystore holds under ANDROID_KEY_ALIAS — verifying alone
    only says the package is signed by *some* key, which a debug key satisfies. Neither tool's
    exit code is sufficient on its own either: `jarsigner -verify` exits 0 on an unsigned jar and
    reports the verdict only in its output.

    Returns the SHA-256 fingerprint of the signing certificate, for BUILD-INFO.txt and the release
    notes to quote.

.PARAMETER BundlePath
    App bundles (.aab) to sign in place.

.PARAMETER ApkPath
    APKs to align and sign in place. Needs the Android SDK build-tools, located through
    ANDROID_HOME or ANDROID_SDK_ROOT.

.PARAMETER ValidateOnly
    Check the secrets, the toolchain, and the keystore without signing anything. The preview
    package workflow runs this before the Android build so a missing or wrong secret fails in
    seconds instead of after an hour of building.

.EXAMPLE
    ./scripts/sign-android-packages.ps1 -ValidateOnly

.EXAMPLE
    ./scripts/sign-android-packages.ps1 -BundlePath ./Broiler.Browser.aab, ./Broiler.Writer.aab

.EXAMPLE
    ./scripts/sign-android-packages.ps1 -ApkPath ./Broiler.Browser-arm64.apk
#>
[CmdletBinding(DefaultParameterSetName = 'Sign')]
param(
    [Parameter(ParameterSetName = 'Sign', Position = 0)]
    [string[]] $BundlePath = @(),

    [Parameter(ParameterSetName = 'Sign')]
    [string[]] $ApkPath = @(),

    [Parameter(Mandatory = $true, ParameterSetName = 'Validate')]
    [switch] $ValidateOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# keytool and jarsigner report through exit codes this script inspects itself; letting PowerShell
# turn a non-zero exit into its own terminating error would replace every diagnostic below with
# 'the command failed'.
$PSNativeCommandUseErrorActionPreference = $false

# keytool and jarsigner localize their output, and this script parses it. Pin them to English so a
# runner's locale cannot turn 'jar verified.' into something the check does not recognize.
$JdkLocaleArguments = @('-J-Duser.language=en', '-J-Duser.country=US')

$RequiredSecrets = @(
    'ANDROID_KEYSTORE_BASE64'
    'ANDROID_KEYSTORE_PASSWORD'
    'ANDROID_KEY_ALIAS'
    'ANDROID_KEY_PASSWORD'
)

function Resolve-JdkTool {
    param([Parameter(Mandatory = $true)][string] $Name)

    # JAVA_HOME first: a CI runner carries several JDKs, and the one setup-java selected is the
    # one whose keytool matches the jarsigner that does the signing.
    if (-not [string]::IsNullOrWhiteSpace($env:JAVA_HOME)) {
        foreach ($extension in @('', '.exe')) {
            $candidate = Join-Path $env:JAVA_HOME "bin/$Name$extension"
            if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                return $candidate
            }
        }
    }

    $onPath = @(Get-Command -Name $Name -CommandType Application -ErrorAction SilentlyContinue)
    if ($onPath.Count -gt 0) {
        return $onPath[0].Source
    }

    throw "$Name was not found. Set JAVA_HOME to a JDK 21 installation, or put $Name on PATH."
}

function Resolve-BuildToolsTool {
    param([Parameter(Mandatory = $true)][string] $Name)

    $sdkRoot = if (-not [string]::IsNullOrWhiteSpace($env:ANDROID_HOME)) { $env:ANDROID_HOME } else { $env:ANDROID_SDK_ROOT }
    if ([string]::IsNullOrWhiteSpace($sdkRoot)) {
        throw "Signing an APK needs the Android SDK build-tools ($Name). Neither ANDROID_HOME nor ANDROID_SDK_ROOT is set."
    }

    $buildTools = Join-Path $sdkRoot 'build-tools'
    if (-not (Test-Path -LiteralPath $buildTools -PathType Container)) {
        throw "The Android SDK at $sdkRoot has no build-tools directory, so $Name is not available."
    }

    # Highest version wins, compared as versions rather than as text so 9.0.0 does not sort above
    # 36.0.0. The workflow pins a build-tools version, but a runner image may carry others.
    $candidates = @(Get-ChildItem -LiteralPath $buildTools -Directory -ErrorAction SilentlyContinue |
        Sort-Object -Property @{ Expression = {
                $parsed = [version]'0.0'
                if ([version]::TryParse($_.Name, [ref] $parsed)) { $parsed } else { [version]'0.0' }
            }
        } -Descending)

    foreach ($candidate in $candidates) {
        foreach ($extension in @('', '.bat', '.exe')) {
            $tool = Join-Path $candidate.FullName "$Name$extension"
            if (Test-Path -LiteralPath $tool -PathType Leaf) {
                return $tool
            }
        }
    }

    $versions = if ($candidates.Count -gt 0) { @($candidates | ForEach-Object { $_.Name }) -join ', ' } else { '(none)' }
    throw "$Name was not found in any build-tools under $buildTools. Installed versions: $versions."
}

function Invoke-SigningTool {
    param(
        [Parameter(Mandatory = $true)][string] $Executable,
        [Parameter(Mandatory = $true)][string[]] $Arguments,
        [string] $StandardInput
    )

    # Merged stderr arrives as error records; they are diagnostics to report, not failures in
    # themselves, so they must not trip $ErrorActionPreference. The assignment is function-scoped.
    $ErrorActionPreference = 'Continue'

    $output = if ($PSBoundParameters.ContainsKey('StandardInput')) {
        $StandardInput | & $Executable @Arguments 2>&1
    }
    else {
        & $Executable @Arguments 2>&1
    }

    return [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output   = (@($output | ForEach-Object { $_.ToString() }) -join [Environment]::NewLine)
    }
}

function Get-CertificateFingerprint {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $ToolOutput)

    # 'SHA256:' on current JDKs, 'SHA-256:' on older ones, always 32 colon-separated hex bytes. The
    # end of the line is deliberately unanchored, for the same reason as in Get-ApkSignerDigest: a
    # tool that one day appends a note to the line should not read as "no fingerprint at all".
    $found = [regex]::Matches($ToolOutput, '(?im)^\s*SHA-?256:\s*((?:[0-9A-F]{2}:){31}[0-9A-F]{2})')
    return @($found | ForEach-Object { $_.Groups[1].Value.ToUpperInvariant() })
}

function Get-ApkSignerDigest {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string] $ToolOutput)

    # apksigner writes the same 32 bytes keytool does, as unseparated lowercase hex — but it labels
    # the signer in one of two shapes, and only the middle of the line is common to both:
    #
    #   Signer #1 certificate SHA-256 digest: 674f25f7…
    #   Signer (minSdkVersion=33, maxSdkVersion=2147483647) certificate SHA-256 digest: 5daa4d29…
    #
    # The second appears when one signer does not cover the whole supported SDK range — a rotated
    # key, where the previous certificate still signs for older platforms — and carries no number
    # at all. So anchor on 'certificate SHA-256 digest:' rather than on either label, and leave the
    # end of the line unanchored so a trailing note cannot hide a digest either. 'public key
    # SHA-256 digest' lines carry a different value and must not be picked up.
    $found = [regex]::Matches($ToolOutput, '(?im)^Signer\b[^\r\n]*?\bcertificate SHA-256 digest:\s*([0-9a-f]{64})')
    return @($found | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() })
}

function ConvertTo-ComparableFingerprint {
    param([Parameter(Mandatory = $true)][string] $Fingerprint)

    return ($Fingerprint -replace '[^0-9A-Fa-f]', '').ToLowerInvariant()
}

function Assert-SignatureBlock {
    param([Parameter(Mandatory = $true)][string] $Path)

    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $entries = @($archive.Entries | ForEach-Object { $_.FullName })
    }
    finally {
        $archive.Dispose()
    }

    # A JAR signature is three entries: the manifest of digests, the .SF signature file over it,
    # and the .RSA/.DSA/.EC block holding the signature and the signer certificate.
    $missing = @()
    if ($entries -notcontains 'META-INF/MANIFEST.MF') { $missing += 'META-INF/MANIFEST.MF' }
    if (-not ($entries | Where-Object { $_ -match '^META-INF/[^/]+\.SF$' })) { $missing += 'META-INF/*.SF' }
    if (-not ($entries | Where-Object { $_ -match '^META-INF/[^/]+\.(RSA|DSA|EC)$' })) { $missing += 'META-INF/*.RSA' }

    if ($missing) {
        throw "$Path carries no JAR signature after signing; missing $($missing -join ', ')."
    }
}

$missingSecrets = @($RequiredSecrets | Where-Object {
        [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_))
    })
if ($missingSecrets) {
    $verb = if ($missingSecrets.Count -eq 1) { 'is' } else { 'are' }
    throw @"
Android release signing is not configured: $($missingSecrets -join ', ') $verb empty or unset.

All four values are repository secrets, set under Settings > Secrets and variables > Actions:

  ANDROID_KEYSTORE_BASE64      base64 of the release keystore ('base64 -w0 release.keystore')
  ANDROID_KEYSTORE_PASSWORD    password of that keystore
  ANDROID_KEY_ALIAS            alias of the signing key inside it
  ANDROID_KEY_PASSWORD         password of that key

A preview package with unsigned packages in it is not one this workflow ships, so this is a hard
failure rather than a fallback.
"@
}

if (-not $ValidateOnly -and $BundlePath.Count -eq 0 -and $ApkPath.Count -eq 0) {
    throw 'Nothing to sign. Pass -BundlePath, -ApkPath, or both — or -ValidateOnly to just check the secrets.'
}

$keytool = Resolve-JdkTool -Name 'keytool'
$jarsigner = Resolve-JdkTool -Name 'jarsigner'

# Resolved only when there are APKs to sign: the build-tools are an Android SDK dependency, and a
# bundles-only run — or the secrets check, which the workflow runs before it provisions the SDK —
# has no business requiring one.
$zipalign = $null
$apksigner = $null
if ($ApkPath.Count -gt 0) {
    $zipalign = Resolve-BuildToolsTool -Name 'zipalign'
    $apksigner = Resolve-BuildToolsTool -Name 'apksigner'
}

$storePassword = [Environment]::GetEnvironmentVariable('ANDROID_KEYSTORE_PASSWORD')
$keyAlias = [Environment]::GetEnvironmentVariable('ANDROID_KEY_ALIAS')

# RUNNER_TEMP, never the workspace: nothing under the checkout may hold key material, where a
# packaging glob or an artifact upload could pick it up.
$temporaryRoot = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) { [System.IO.Path]::GetTempPath() } else { $env:RUNNER_TEMP }
$signingDirectory = Join-Path $temporaryRoot ('broiler-android-signing-' + [Guid]::NewGuid().ToString('N'))
$keystorePath = Join-Path $signingDirectory 'release.keystore'
$fingerprint = $null

try {
    New-Item -ItemType Directory -Force -Path $signingDirectory | Out-Null

    try {
        # Whitespace-tolerant: a secret pasted from wrapped base64 output is still the keystore.
        $keystoreBytes = [Convert]::FromBase64String(([Environment]::GetEnvironmentVariable('ANDROID_KEYSTORE_BASE64') -replace '\s', ''))
    }
    catch {
        throw "ANDROID_KEYSTORE_BASE64 is not valid base64: $($_.Exception.Message)"
    }

    if ($keystoreBytes.Length -eq 0) {
        throw 'ANDROID_KEYSTORE_BASE64 decodes to an empty file.'
    }

    [System.IO.File]::WriteAllBytes($keystorePath, $keystoreBytes)

    # keytool has no :env password form of its own, and a password on a command line is readable
    # by every process on the machine, so it goes in on stdin — which keytool falls back to when
    # it is not attached to a console, as here.
    $listing = Invoke-SigningTool -Executable $keytool -StandardInput $storePassword -Arguments (
        $JdkLocaleArguments + @('-list', '-v', '-keystore', $keystorePath, '-alias', $keyAlias))
    if ($listing.ExitCode -ne 0) {
        throw @"
keytool could not read alias '$keyAlias' from the keystore (exit code $($listing.ExitCode)).
Check ANDROID_KEY_ALIAS against the keystore and ANDROID_KEYSTORE_PASSWORD against its password.

$($listing.Output)
"@
    }

    # @() around the call: PowerShell unrolls a single-element result on the way out of a function,
    # and a lone string is not a collection to count or search.
    $keystoreFingerprints = @(Get-CertificateFingerprint -ToolOutput $listing.Output)
    if ($keystoreFingerprints.Count -eq 0) {
        throw "keytool printed no SHA-256 certificate fingerprint for alias '$keyAlias':`n$($listing.Output)"
    }

    # The first is the leaf — the certificate that signs — with any CA certificates after it.
    $fingerprint = $keystoreFingerprints[0]
    Write-Host "==> signing key '$keyAlias', certificate SHA-256 $fingerprint"

    if ($ValidateOnly) {
        Write-Host '==> Android signing secrets validated; nothing signed.'
    }
    else {
        foreach ($bundle in $BundlePath) {
            if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) {
                throw "App bundle not found: $bundle"
            }

            $resolved = (Resolve-Path -LiteralPath $bundle).ProviderPath

            # jarsigner rewrites the archive in place. -sigalg is left to jarsigner so an EC key
            # signs as readily as an RSA one; -digestalg is pinned because SHA-256 is the floor
            # Play accepts.
            $signing = Invoke-SigningTool -Executable $jarsigner -Arguments (
                $JdkLocaleArguments + @(
                    '-keystore', $keystorePath
                    '-storepass:env', 'ANDROID_KEYSTORE_PASSWORD'
                    '-keypass:env', 'ANDROID_KEY_PASSWORD'
                    '-digestalg', 'SHA-256'
                    $resolved
                    $keyAlias
                ))
            if ($signing.ExitCode -ne 0) {
                $hint = ''
                if ($signing.Output -match 'not a private key|cannot recover key') {
                    # PKCS#12 keeps one password for the store and its keys; keytool drops a
                    # -keypass that differs from -storepass when it creates one, which surfaces
                    # here as a key that cannot be unlocked.
                    $hint = "`n`nIf the keystore is a PKCS#12 file, ANDROID_KEY_PASSWORD has to be the same value as ANDROID_KEYSTORE_PASSWORD."
                }

                throw "jarsigner failed on $resolved (exit code $($signing.ExitCode)).$hint`n`n$($signing.Output)"
            }

            # jarsigner reports an unsigned jar as 'jar is unsigned.' and still exits 0, so the
            # verdict has to be read out of the output, not the exit code.
            $verification = Invoke-SigningTool -Executable $jarsigner -Arguments (
                $JdkLocaleArguments + @('-verify', '-keystore', $keystorePath, $resolved))
            if ($verification.ExitCode -ne 0 -or $verification.Output -notmatch '(?m)^jar verified\.') {
                throw "jarsigner did not verify $resolved after signing it (exit code $($verification.ExitCode)):`n`n$($verification.Output)"
            }

            Assert-SignatureBlock -Path $resolved

            # Verified is not enough: it says the bundle is signed by *some* key. This says it is
            # signed by ours, which is what a debug key slipping through would fail.
            $signer = Invoke-SigningTool -Executable $keytool -Arguments (
                $JdkLocaleArguments + @('-printcert', '-jarfile', $resolved))
            $signerFingerprints = @(Get-CertificateFingerprint -ToolOutput $signer.Output)
            if ($signerFingerprints -notcontains $fingerprint) {
                $reported = if ($signerFingerprints.Count -gt 0) { $signerFingerprints -join ', ' } else { '(no certificate fingerprint recognized)' }
                throw @"
$resolved is not signed by '$keyAlias'.

  expected certificate : $fingerprint
  found                : $reported

keytool -printcert -jarfile reported:

$($signer.Output)
"@
            }

            $megabytes = [Math]::Round((Get-Item -LiteralPath $resolved).Length / 1MB, 1)
            Write-Host "==> signed and verified $(Split-Path -Leaf $resolved): $megabytes MB, signer $fingerprint"
        }

        foreach ($apk in $ApkPath) {
            if (-not (Test-Path -LiteralPath $apk -PathType Leaf)) {
                throw "APK not found: $apk"
            }

            $resolved = (Resolve-Path -LiteralPath $apk).ProviderPath

            # Align before signing, never after: zipalign moves entries within the archive, which
            # would invalidate a signature already over them. -p page-aligns the uncompressed
            # native libraries, which is what lets Android map them straight out of the APK.
            $alignedPath = "$resolved.aligned"
            $alignment = Invoke-SigningTool -Executable $zipalign -Arguments @('-p', '-f', '4', $resolved, $alignedPath)
            if ($alignment.ExitCode -ne 0) {
                throw "zipalign failed on $resolved (exit code $($alignment.ExitCode)):`n`n$($alignment.Output)"
            }
            Move-Item -LiteralPath $alignedPath -Destination $resolved -Force

            # apksigner rather than jarsigner: it writes the v2/v3 signature blocks, and an APK
            # carrying only a JAR signature is refused at install from Android 11 on. v4 is off
            # because it writes a detached .idsig that only an incremental `adb install` reads —
            # the package should hold packages and nothing else.
            $signing = Invoke-SigningTool -Executable $apksigner -Arguments @(
                'sign'
                '--ks', $keystorePath
                '--ks-pass', 'env:ANDROID_KEYSTORE_PASSWORD'
                '--key-pass', 'env:ANDROID_KEY_PASSWORD'
                '--ks-key-alias', $keyAlias
                '--v4-signing-enabled', 'false'
                $resolved
            )
            if ($signing.ExitCode -ne 0) {
                $hint = ''
                if ($signing.Output -match 'not a private key|cannot recover key|password was incorrect') {
                    $hint = "`n`nIf the keystore is a PKCS#12 file, ANDROID_KEY_PASSWORD has to be the same value as ANDROID_KEYSTORE_PASSWORD."
                }

                throw "apksigner failed on $resolved (exit code $($signing.ExitCode)).$hint`n`n$($signing.Output)"
            }

            # Belt and braces: if a build-tools version ever ignores --v4-signing-enabled, the
            # sidecar still does not reach the package.
            Remove-Item -LiteralPath "$resolved.idsig" -Force -ErrorAction SilentlyContinue

            # -v as well as --print-certs: without it apksigner prints the certificates but not the
            # 'Verifies' verdict or the per-scheme lines, and this checks both.
            $verification = Invoke-SigningTool -Executable $apksigner -Arguments @('verify', '--print-certs', '-v', $resolved)
            if ($verification.ExitCode -ne 0 -or $verification.Output -notmatch '(?m)^Verifies') {
                throw "apksigner did not verify $resolved after signing it (exit code $($verification.ExitCode)):`n`n$($verification.Output)"
            }

            # Same certificate check as the bundles, against apksigner's unseparated lowercase
            # rendering of the same 32 bytes. A rotated key legitimately reports more than one
            # signer — the previous certificate still signs for older platforms — so ours has to be
            # among them rather than the only one.
            $signerDigests = @(Get-ApkSignerDigest -ToolOutput $verification.Output)
            $expectedDigest = ConvertTo-ComparableFingerprint -Fingerprint $fingerprint

            # The digest is the evidence; the wording around it is not. If apksigner ever labels a
            # signer in a shape this parser does not know, fall back to finding the digest anywhere
            # in the output rather than failing a package that is correctly signed. Thirty-two
            # bytes do not turn up by accident.
            $labelled = $signerDigests -contains $expectedDigest
            if (-not ($labelled -or $verification.Output -match "(?i)\b$expectedDigest\b")) {
                $reported = if ($signerDigests.Count -gt 0) { $signerDigests -join ', ' } else { '(no certificate digest recognized)' }
                throw @"
$resolved is not signed by '$keyAlias'.

  expected certificate : $fingerprint
  found                : $reported

apksigner verify --print-certs -v reported:

$($verification.Output)
"@
            }

            if (-not $labelled) {
                Write-Host "==> note: apksigner labelled its signers in a shape this script does not parse; matched the certificate digest in the raw output instead. Worth teaching Get-ApkSignerDigest the new shape."
            }

            # Which schemes signed it is the point of using apksigner, so it goes in the log:
            # v1 alone would install nowhere from Android 11 on.
            $schemes = @(
                [regex]::Matches($verification.Output, '(?im)^Verified using (v[0-9.]+) scheme[^:]*:\s*true\s*$') |
                    ForEach-Object { $_.Groups[1].Value }
            )
            $megabytes = [Math]::Round((Get-Item -LiteralPath $resolved).Length / 1MB, 1)
            Write-Host "==> signed and verified $(Split-Path -Leaf $resolved): $megabytes MB, $($schemes -join '+') signature, signer $fingerprint"
        }
    }
}
finally {
    if (Test-Path -LiteralPath $signingDirectory) {
        Remove-Item -LiteralPath $signingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}

return $fingerprint
