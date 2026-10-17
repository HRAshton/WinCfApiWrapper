# Release transaction: stage immutable build inputs BEFORE pushing to nuget.org.
# Draft GitHub Release + original artifacts + manifest survive workflow retries.
# A retry never repacks a version known to be published on nuget.org.
param(
    [Parameter(Mandatory)]
    [ValidateSet("Resolve", "Stage", "VerifyStage", "InspectNuGet", "PushNuGet", "Finalize", "SelfTest")]
    [string]$Phase
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false

$repo = "HRAshton/WinCfApiWrapper"
$packageId = "WinCfApiWrapper"
$signer = "$repo/.github/workflows/attested-release-build.yml"
$stageDir = Join-Path (Get-Location) "release-stage"
$originalDir = Join-Path (Get-Location) "artifacts"
$manifestName = "release-manifest.json"
$provenanceExtension = ".intoto.jsonl"
$nugetRoot = "https://api.nuget.org/v3-flatcontainer"

function Invoke-Cli {
    param([string]$Executable, [string[]]$Arguments)
    $output = & $Executable @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable $($Arguments -join ' ') failed: $($output | Out-String)"
    }
    return ($output | Out-String).Trim()
}

function Write-OutputVariable {
    param([string]$Name, [string]$Value)
    if (-not $env:GITHUB_OUTPUT) { throw "GITHUB_OUTPUT is not configured." }
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "$Name=$Value"
}

function Confirm-ExpectedNotFound {
    param([string]$Output, [string]$Pattern, [string]$Operation)
    if ($Output -notmatch $Pattern) { throw "$Operation : $Output" }
    $global:LASTEXITCODE = 0
}

function Read-Release {
    param([string]$Tag)
    $output = & gh release view $Tag --repo $repo --json tagName,isDraft,assets 2>&1
    if ($LASTEXITCODE -ne 0) {
        Confirm-ExpectedNotFound -Output ($output | Out-String) -Pattern '(?i)(release not found|http 404)' -Operation "Cannot query GitHub Release $Tag"
        return $null
    }
    return ($output | Out-String | ConvertFrom-Json)
}

function Read-NuGetPackage {
    param([string]$Version, [string]$Destination)
    $idLower = $packageId.ToLowerInvariant()
    $vLower = $Version.ToLowerInvariant()
    $url = "$nugetRoot/$idLower/$vLower/$idLower.$vLower.nupkg"
    $client = [System.Net.Http.HttpClient]::new()
    try {
        $client.Timeout = [TimeSpan]::FromSeconds(60)
        $response = $client.GetAsync($url, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            if ([int]$response.StatusCode -eq 404) { return $false }
            if (-not $response.IsSuccessStatusCode) {
                throw "NuGet package lookup returned HTTP $([int]$response.StatusCode) from $url"
            }
            $source = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $dest = [System.IO.File]::Create($Destination)
            try { $source.CopyTo($dest) } finally { $dest.Dispose(); $source.Dispose() }
            return $true
        } finally { $response.Dispose() }
    } finally { $client.Dispose() }
}

function Read-ZipContentHashes {
    param([string]$Path)
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $hashes = [System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName -eq ".signature.p7s") { continue }
            if ($entry.FullName.EndsWith("/")) { continue }
            $stream = $entry.Open()
            try {
                $digest = [System.Security.Cryptography.SHA256]::HashData($stream)
                $hash = [Convert]::ToHexString($digest)
            } finally { $stream.Dispose() }
            if (-not $hashes.TryAdd($entry.FullName, $hash)) {
                throw "Duplicate ZIP entry: $($entry.FullName)"
            }
        }
        return $hashes
    } finally { $zip.Dispose() }
}

function Assert-SamePackagePayload {
    param([string]$Original, [string]$Published)
    $orig = Read-ZipContentHashes $Original
    $remote = Read-ZipContentHashes $Published
    if ($orig.Count -ne $remote.Count) { throw "Published NuGet package has different ZIP entries." }
    foreach ($name in $orig.Keys) {
        if (-not $remote.ContainsKey($name) -or $remote[$name] -cne $orig[$name]) {
            throw "NuGet package content mismatch for entry '$name'. Manual investigation required."
        }
    }
}

function Confirm-SignedNuGetPackage {
    param([string]$Original, [string]$Published)
    # nuget.org adds only .signature.p7s to unsigned packages.
    # Compare every other uncompressed ZIP entry by name and SHA256.
    Invoke-Cli "dotnet" @("nuget", "verify", $Published, "--all") | Out-Host
    Assert-SamePackagePayload -Original $Original -Published $Published
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Published)
    try {
        if (-not $zip.GetEntry(".signature.p7s")) {
            throw "NuGet package has no repository signature."
        }
    } finally { $zip.Dispose() }
    Write-Host "NuGet repository-signed package content matches the attested original."
}

function Confirm-NuGetState {
    param(
        [string]$Version,
        [string]$Original,
        [ValidateRange(1, 360)][int]$Attempts = 1,
        [ValidateRange(0, 600)][int]$RetryDelaySeconds = 10,
        [scriptblock]$Fetch = {
            param($v, $destination)
            Read-NuGetPackage -Version $v -Destination $destination
        },
        [scriptblock]$Verify = {
            param($original, $published)
            Confirm-SignedNuGetPackage -Original $original -Published $published
        }
    )
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
    $downloaded = Join-Path $stageDir "nuget-signed.nupkg"
    for ($i = 1; $i -le $Attempts; $i++) {
        if (Test-Path $downloaded) { Remove-Item -LiteralPath $downloaded -Force }
        if (& $Fetch $Version $downloaded) {
            & $Verify $Original $downloaded
            return $true
        }
        if ($i -lt $Attempts) {
            if ($i -eq 1 -or $i % 6 -eq 0) {
                Write-Host "NuGet $Version is not indexed yet (attempt $i/$Attempts); waiting for validation."
            }
            if ($RetryDelaySeconds -gt 0) { Start-Sleep -Seconds $RetryDelaySeconds }
        }
    }
    return $false
}

function Test-RemoteTag {
    param([string]$Tag)
    $response = & gh api "repos/$repo/git/ref/tags/$Tag" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Confirm-ExpectedNotFound -Output ($response | Out-String) -Pattern '(?i)(HTTP 404|Not Found)' -Operation "Cannot inspect remote release tag $Tag"
        return $false
    }
    $ref = ($response | Out-String | ConvertFrom-Json)
    if ($ref.ref -cne "refs/tags/$Tag") {
        throw "Remote tag lookup returned unexpected ref: $($ref.ref)"
    }
    return $true
}

function New-RemoteTag {
    param([string]$Tag, [string]$Commit)
    # Create a lightweight tag with the Actions token. GitHub rejects moving
    # existing tags; VerifyStage separately checks the remote commit target.
    Invoke-Cli "gh" @("api", "--method", "POST", "repos/$repo/git/refs",
        "-f", "ref=refs/tags/$Tag", "-f", "sha=$Commit") | Out-Null
}

function Ensure-ReleaseTag {
    param(
        [string]$Tag, [string]$Commit,
        # Simulated operations are injectable for offline CI regression tests.
        [scriptblock]$Exists = { param($t) Test-RemoteTag -Tag $t },
        [scriptblock]$Create = { param($t, $sha) New-RemoteTag -Tag $t -Commit $sha },
        [scriptblock]$Verify = { param($t, $sha) Confirm-TagTarget -Tag $t -Commit $sha }
    )
    if ($Tag -cnotmatch '^v\d+\.\d+\.\d+$' -or
        $Commit -cnotmatch '^[0-9a-f]{40}$') {
        throw "Invalid release tag or commit identity."
    }
    if (-not (& $Exists $Tag)) {
        try {
            & $Create $Tag $Commit
            Write-Host "Created remote release tag $Tag at $Commit."
        } catch {
            # Another actor may have created it; tolerate only if valid.
            if (-not (& $Exists $Tag)) { throw }
            Write-Host "Release tag appeared during creation; verifying its target."
        }
    }
    if (-not (& $Exists $Tag)) { throw "Release tag $Tag is still missing after creation." }
    & $Verify $Tag $Commit
    Write-Host "Verified remote tag $Tag at $Commit."
}

function Confirm-TagTarget {
    param([string]$Tag, [string]$Commit)
    # Fetch the remote tag and peel annotated tags to their target commit.
    Invoke-Cli "git" @("fetch", "--force", "--tags", "origin") | Out-Null
    $actual = Invoke-Cli "git" @("rev-list", "-n", "1", "refs/tags/$Tag")
    if ($actual -ne $Commit) {
        throw "Release tag $Tag points at $actual instead of expected $Commit."
    }
}

function Confirm-SignedProvenanceBundle {
    param(
        [string]$Path, [string[]]$Packages, [string]$Commit,
        [scriptblock]$Verify = {
            param($file, $bundle, $sourceCommit)
            Invoke-Cli "gh" @("attestation", "verify", $file, "--bundle", $bundle,
                "--repo", $repo, "--signer-workflow", $signer,
                "--source-ref", "refs/heads/master", "--source-digest", $sourceCommit) | Out-Null
        }
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Signed provenance bundle is missing." }
    if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw "Invalid provenance source commit." }
    $raw = (Get-Content -LiteralPath $Path -Raw).Trim()
    if (-not $raw -or @($raw -split '\r?\n').Count -ne 1) {
        throw "Provenance must be exactly one signed Sigstore JSON bundle."
    }
    try { $bundle = ConvertFrom-Json -InputObject $raw -ErrorAction Stop }
    catch { throw "Malformed signed provenance JSON bundle: $_" }
    if (-not $bundle.dsseEnvelope -or -not $bundle.verificationMaterial -or
        -not $bundle.dsseEnvelope.payload -or @($bundle.dsseEnvelope.signatures).Count -lt 1) {
        throw "Signed provenance is not a Sigstore DSSE bundle."
    }
    foreach ($file in $Packages) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Provenance subject is missing: $file" }
        # The CLI validates signature, certificate identity, source commit, and
        # this exact package digest; JSON shape alone is not trusted.
        & $Verify $file $Path $Commit
    }
}

function Test-MatchingSha256 {
    param([string]$Path, [string]$Expected)
    # Hex digests represent bytes, so letter casing does not affect equality.
    return ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Expected)
}

function Test-CompleteReleaseStage {
    param([string[]]$AssetNames, [string]$Version)
    foreach ($required in @(
        $manifestName,
        "$packageId.$Version.nupkg",
        "$packageId.$Version.snupkg",
        "$packageId.$Version$provenanceExtension"
    )) {
        if ($required -notin $AssetNames) { return $false }
    }
    return $true
}

function Read-StagedManifest {
    $tag = $env:RELEASE_TAG
    $commit = $env:RELEASE_COMMIT
    $version = $env:RELEASE_VERSION
    if (-not $tag -or -not $commit -or -not $version) { throw "Missing release identity variables." }

    $release = Read-Release $tag
    if (-not $release) { throw "Release $tag does not exist; no durable artifact stage." }
    Confirm-TagTarget -Tag $tag -Commit $commit

    if (Test-Path $stageDir) { Remove-Item $stageDir -Force -Recurse }
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
    Invoke-Cli "gh" @("release", "download", $tag, "--repo", $repo, "--dir", $stageDir) | Out-Null
    $manifestPath = Join-Path $stageDir $manifestName
    if (-not (Test-Path $manifestPath)) { throw "Staged release $tag has no manifest." }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    # Every staged release requires the schema-2 signed provenance asset.
    if ($manifest.schema -ne 2 -or $manifest.repository -cne $repo -or
        $manifest.packageId -cne $packageId -or
        $manifest.version -cne $version -or $manifest.tag -cne $tag -or
        $manifest.commit -cne $commit -or @($manifest.packages).Count -ne 2) {
        throw "Staged manifest does not match expected release identity."
    }
    $extensions = @($manifest.packages | ForEach-Object { [IO.Path]::GetExtension($_.name) } | Sort-Object)
    if (($extensions -join ",") -cne ".nupkg,.snupkg") {
        throw "Staged release must contain precisely one .nupkg and one .snupkg."
    }
    $assetNames = @($release.assets | ForEach-Object { $_.name })
    foreach ($asset in $manifest.packages) {
        if ([IO.Path]::GetFileName($asset.name) -cne $asset.name -or $asset.name -notin $assetNames) {
            throw "Unexpected or missing staged asset: $($asset.name)"
        }
        $path = Join-Path $stageDir $asset.name
        if (-not (Test-Path $path)) { throw "Cannot download staged $($asset.name)" }
        if (-not (Test-MatchingSha256 -Path $path -Expected $asset.sha256)) { throw "Staged asset SHA256 mismatch: $($asset.name)" }
        Invoke-Cli "gh" @("attestation", "verify", $path, "--repo", $repo, "--signer-workflow", $signer, "--source-ref", "refs/heads/master", "--source-digest", $commit) | Out-Null
    }
    $nupkg = @($manifest.packages | Where-Object { $_.name.EndsWith(".nupkg") })[0]
    if ($nupkg.name -cne "$packageId.$version.nupkg") {
        throw "Staged NuGet package name does not match release version."
    }
    $provenanceName = "$packageId.$version$provenanceExtension"
    if (-not $manifest.provenance -or $manifest.provenance.name -cne $provenanceName -or
        $manifest.provenance.sha256 -cnotmatch '^[0-9a-fA-F]{64}$' -or
        $provenanceName -notin $assetNames) {
        throw "Missing or unexpected signed provenance asset in staged manifest."
    }
    $provenanceFile = Join-Path $stageDir $provenanceName
    if (-not (Test-Path -LiteralPath $provenanceFile -PathType Leaf) -or
        -not (Test-MatchingSha256 -Path $provenanceFile -Expected $manifest.provenance.sha256)) {
        throw "Signed provenance asset does not match the staged manifest."
    }
    $packageFiles = @($manifest.packages | ForEach-Object { Join-Path $stageDir $_.name })
    Confirm-SignedProvenanceBundle -Path $provenanceFile -Packages $packageFiles -Commit $commit
    Write-Host "Verified signed in-toto provenance for both release packages."
    Write-Host "Verified both original artifact SHA256 digests, tag commit, and attestations."
    return $manifest
}

function Assert-NextReleaseVersion {
    param([string]$Candidate, [string]$PreviousTag)
    if ($Candidate -cnotmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        throw "GitVersion did not produce a stable MajorMinorPatch version: $Candidate"
    }
    if ($PreviousTag) {
        if ($PreviousTag -cnotmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
            throw "Invalid previous release tag: $PreviousTag"
        }
        if ([version]$Candidate -le [version]$PreviousTag.Substring(1)) {
            throw "GitVersion $Candidate does not advance beyond latest release $PreviousTag."
        }
    }
    return $Candidate
}

function Test-ResumeRelease {
    param([string]$LastCommit, [string]$Head, [object]$Release)
    return ($LastCommit -eq $Head -or $null -eq $Release -or [bool]$Release.isDraft)
}

switch ($Phase) {
    "SelfTest" {
        # Local regression checks; no GH or NuGet credentials/network required.
        $tmp = Join-Path ([IO.Path]::GetTempPath()) ("WinCfRelease-" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $tmp | Out-Null
        try {
            $original = Join-Path $tmp "original.nupkg"
            $signed = Join-Path $tmp "signed.nupkg"
            $tampered = Join-Path $tmp "tampered.nupkg"
            foreach ($item in @(
                @{ path = $original; payload = "matching" ; signed = $false },
                @{ path = $signed; payload = "matching"; signed = $true },
                @{ path = $tampered; payload = "tampered"; signed = $true }
            )) {
                $zip = [IO.Compression.ZipFile]::Open($item.path, [IO.Compression.ZipArchiveMode]::Create)
                try {
                    $entry = $zip.CreateEntry("lib/net8.0-windows/payload.bin")
                    $writer = [IO.StreamWriter]::new($entry.Open())
                    try { $writer.Write($item.payload) } finally { $writer.Dispose() }
                    if ($item.signed) {
                        $entry = $zip.CreateEntry(".signature.p7s")
                        $writer = [IO.StreamWriter]::new($entry.Open())
                        try { $writer.Write("signature") } finally { $writer.Dispose() }
                    }
                } finally { $zip.Dispose() }
            }
            # Case-only digest differences are valid; distinct bytes are not.
            $originalDigest = (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
            if (-not (Test-MatchingSha256 -Path $original -Expected $originalDigest.ToLowerInvariant()) -or
                -not (Test-MatchingSha256 -Path $original -Expected $originalDigest.ToUpperInvariant()) -or
                (Test-MatchingSha256 -Path $signed -Expected $originalDigest)) {
                throw "SHA256 comparison must ignore case without accepting different bytes."
            }
            Assert-SamePackagePayload -Original $original -Published $signed
            $rejected = $false
            try { Assert-SamePackagePayload -Original $original -Published $tampered } catch { $rejected = $true }
            if (-not $rejected) { throw "Content mismatch was not rejected." }
            # First release: missing remote tag is created at exact SHA.
            $expectedSha = "a" * 40
            $script:tagTarget = $null
            $script:tagCreates = 0
            $lookup = { param($t) return ($null -ne $script:tagTarget) }
            $create = {
                param($t, $sha)
                $script:tagCreates++
                $script:tagTarget = $sha
            }
            $verify = {
                param($t, $sha)
                if ($script:tagTarget -cne $sha) {
                    throw "Existing tag points at an unexpected commit."
                }
            }
            Ensure-ReleaseTag -Tag "v0.1.0" -Commit $expectedSha -Exists $lookup -Create $create -Verify $verify
            if ($script:tagCreates -ne 1) { throw "Missing-tag path did not create exactly one tag." }

            # Retry with an existing matching tag does not create another ref.
            Ensure-ReleaseTag -Tag "v0.1.0" -Commit $expectedSha -Exists $lookup -Create $create -Verify $verify
            if ($script:tagCreates -ne 1) { throw "Matching-tag retry attempted another creation." }

            # A different commit must be rejected, never moved.
            $conflictRejected = $false
            try {
                Ensure-ReleaseTag -Tag "v0.1.0" -Commit ("b" * 40) -Exists $lookup -Create $create -Verify $verify
            } catch { $conflictRejected = $true }
            if (-not $conflictRejected -or $script:tagCreates -ne 1) {
                throw "Conflicting tag did not fail closed."
            }

            # Never proceed when a create operation leaves the tag missing.
            $script:tagTarget = $null
            $missingRejected = $false
            try {
                Ensure-ReleaseTag -Tag "v0.1.1" -Commit $expectedSha -Exists $lookup -Create { param($t, $sha) } -Verify $verify
            } catch { $missingRejected = $true }
            if (-not $missingRejected) { throw "Missing tag was not detected after creation." }

            if ((Assert-NextReleaseVersion -Candidate "0.1.0") -ne "0.1.0" -or
                (Assert-NextReleaseVersion -Candidate "0.1.1" -PreviousTag "v0.1.0") -ne "0.1.1" -or
                (Assert-NextReleaseVersion -Candidate "0.2.0" -PreviousTag "v0.1.1") -ne "0.2.0") {
                throw "First or subsequent stable release selection failed."
            }
            foreach ($candidate in @("0.1.0-1", "0.1.0+build", "0.1", "1.0.0.0")) {
                $rejected = $false
                try { $null = Assert-NextReleaseVersion -Candidate $candidate } catch { $rejected = $true }
                if (-not $rejected) { throw "Invalid candidate $candidate was accepted." }
            }
            foreach ($candidate in @("0.1.0", "0.0.9")) {
                $rejected = $false
                try {
                    $null = Assert-NextReleaseVersion -Candidate $candidate -PreviousTag "v0.1.0"
                } catch { $rejected = $true }
                if (-not $rejected) { throw "Non-advancing candidate $candidate was accepted." }
            }
            if (-not (Test-ResumeRelease -LastCommit $expectedSha -Head $expectedSha -Release ([pscustomobject]@{ isDraft = $false })) -or
                -not (Test-ResumeRelease -LastCommit $expectedSha -Head ("b" * 40) -Release ([pscustomobject]@{ isDraft = $true })) -or
                -not (Test-ResumeRelease -LastCommit $expectedSha -Head ("b" * 40) -Release $null) -or
                (Test-ResumeRelease -LastCommit $expectedSha -Head ("b" * 40) -Release ([pscustomobject]@{ isDraft = $false }))) {
                throw "Release retry or no-change-week selection failed."
            }

            $global:LASTEXITCODE = 1
            Confirm-ExpectedNotFound -Output 'release not found' -Pattern '(?i)(release not found|http 404)' -Operation 'Test release lookup'
            if ($LASTEXITCODE -ne 0) { throw 'Expected 404 left a failed native exit code.' }

            $global:LASTEXITCODE = 1
            Confirm-ExpectedNotFound -Output 'HTTP 404 Not Found' -Pattern '(?i)(HTTP 404|Not Found)' -Operation 'Test tag lookup'
            if ($LASTEXITCODE -ne 0) { throw 'Expected tag 404 left a failed native exit code.' }

            $global:LASTEXITCODE = 1
            $unexpectedRejected = $false
            try {
                Confirm-ExpectedNotFound -Output 'HTTP 403 Forbidden' -Pattern '(?i)(release not found|http 404)' -Operation 'Test denied lookup'
            } catch { $unexpectedRejected = $true }
            if (-not $unexpectedRejected) { throw 'Unexpected API failure was ignored.' }
            $global:LASTEXITCODE = 0

            # Indexed package detection after delayed publication must stop polling
            # immediately, and verifier failures must never be accepted as success.
            $script:nugetPolls = 0
            $script:nugetVerifications = 0
            $mockFetch = {
                param($v, $destination)
                $script:nugetPolls++
                return $script:nugetPolls -ge 3
            }
            $mockVerify = {
                param($orig, $published)
                $script:nugetVerifications++
            }
            $found = Confirm-NuGetState -Version "0.1.0" -Original $original -Attempts 5 -RetryDelaySeconds 0 -Fetch $mockFetch -Verify $mockVerify
            if (-not $found -or $script:nugetPolls -ne 3 -or $script:nugetVerifications -ne 1) {
                throw "Delayed NuGet indexing was not verified correctly."
            }

            $script:nugetPolls = 0
            $script:nugetVerifications = 0
            $found = Confirm-NuGetState -Version "0.1.0" -Original $original -Attempts 2 -RetryDelaySeconds 0 -Fetch $mockFetch -Verify $mockVerify
            if ($found -or $script:nugetPolls -ne 2 -or $script:nugetVerifications -ne 0) {
                throw "NuGet availability timeout was not respected."
            }

            $script:nugetPolls = 0
            $mismatchRejected = $false
            try {
                $null = Confirm-NuGetState -Version "0.1.0" -Original $original -Attempts 5 -RetryDelaySeconds 0 -Fetch $mockFetch -Verify {
                    param($orig, $published)
                    throw "Mock published content mismatch."
                }
            } catch { $mismatchRejected = $true }
            if (-not $mismatchRejected) { throw "Mismatched published package was accepted." }

            # New schema-2 provenance must be well-formed, verified for both
            # subjects, and rejected if missing, invalid, or unverifiable.
            $provenance = Join-Path $tmp "mock.intoto.jsonl"
            $validBundle = @{
                mediaType = "application/vnd.dev.sigstore.bundle.v0.3+json"
                dsseEnvelope = @{ payload = "Zm9v"; signatures = @(@{ sig = "dGVzdA==" }) }
                verificationMaterial = @{ timestampVerificationData = @{ rfc3161Timestamps = @() } }
            } | ConvertTo-Json -Compress -Depth 10
            [IO.File]::WriteAllText($provenance, ($validBundle + [Environment]::NewLine))
            $script:verifiedPackages = 0
            $mockBundleVerify = { param($file, $bundle, $sha) $script:verifiedPackages++ }
            Confirm-SignedProvenanceBundle -Path $provenance -Packages @($original, $signed) -Commit $expectedSha -Verify $mockBundleVerify
            if ($script:verifiedPackages -ne 2) { throw "Provenance did not verify both packages." }
            $invalidRejected = $false
            [IO.File]::WriteAllText($provenance, ('{"unexpected":"not signed"}' + [Environment]::NewLine))
            try {
                Confirm-SignedProvenanceBundle -Path $provenance -Packages @($original) -Commit $expectedSha -Verify $mockBundleVerify
            } catch { $invalidRejected = $true }
            if (-not $invalidRejected) { throw "Unsigned provenance was accepted." }
            [IO.File]::WriteAllText($provenance, ($validBundle + [Environment]::NewLine))
            $badSignatureRejected = $false
            try {
                Confirm-SignedProvenanceBundle -Path $provenance -Packages @($original) -Commit $expectedSha -Verify {
                    param($file, $bundle, $sha)
                    throw "Signature verification failed."
                }
            } catch { $badSignatureRejected = $true }
            if (-not $badSignatureRejected) { throw "Invalid provenance signature was accepted." }
            $missingProvenanceRejected = $false
            try {
                Confirm-SignedProvenanceBundle -Path (Join-Path $tmp "missing.intoto.jsonl") -Packages @($original) -Commit $expectedSha -Verify $mockBundleVerify
            } catch { $missingProvenanceRejected = $true }
            if (-not $missingProvenanceRejected) { throw "Missing provenance was accepted." }

            # Stage completeness must reject every missing asset, including
            # the signed provenance bundle, so retries rebuild rather than skip.
            $requiredAssets = @(
                $manifestName,
                "$packageId.0.2.0.nupkg",
                "$packageId.0.2.0.snupkg",
                "$packageId.0.2.0$provenanceExtension"
            )
            if (-not (Test-CompleteReleaseStage -AssetNames $requiredAssets -Version "0.2.0")) {
                throw "Complete release stage was not detected."
            }
            foreach ($missing in $requiredAssets) {
                $incomplete = @($requiredAssets | Where-Object { $_ -cne $missing })
                if (Test-CompleteReleaseStage -AssetNames $incomplete -Version "0.2.0") {
                    throw "Incomplete stage missing $missing was accepted."
                }
            }

            Write-Host "Release regression checks passed: payload integrity; tag creation and retries; stable version progression; expected lookup exits; NuGet publication polling; signed provenance asset; stage completeness."
        } finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }
        break
    }


    "Resolve" {
        $head = Invoke-Cli "git" @("rev-parse", "HEAD")
        $tags = @((& git tag --list "v*" --sort=-version:refname) |
            Where-Object { $_ -cmatch '^v\d+\.\d+\.\d+$' })
        $lastTag = if ($tags.Count -gt 0) { [string]$tags[0] } else { $null }
        $resume = $false
        if ($lastTag) {
            $lastCommit = Invoke-Cli "git" @("rev-list", "-n", "1", "refs/tags/$lastTag")
            & git merge-base --is-ancestor $lastCommit $head
            if ($LASTEXITCODE -ne 0) { throw "Latest release tag is not an ancestor of master." }
            $release = Read-Release $lastTag
            # Unfinished previous releases take priority even when master advanced.
            $resume = Test-ResumeRelease -LastCommit $lastCommit -Head $head -Release $release
        }
        if ($resume) {
            $tag = $lastTag
            $version = $tag.Substring(1)
            $commit = $lastCommit
        } else {
            $commit = $head
            Invoke-Cli "dotnet" @("tool", "install", "--global", "GitVersion.Tool", "--version", "6.8.2") | Out-Host
            $candidate = (Invoke-Cli "dotnet-gitversion" @("/showvariable", "MajorMinorPatch", "/config", "GitVersion.yml")).Trim()
            $version = Assert-NextReleaseVersion -Candidate $candidate -PreviousTag $lastTag
            $tag = "v$version"
            if ((& git tag --list $tag) -contains $tag) { throw "Version tag already exists: $tag" }
        }
        $release = Read-Release $tag
        $assetNames = if ($release) { @($release.assets | ForEach-Object { $_.name }) } else { @() }
        $completeStage = Test-CompleteReleaseStage -AssetNames $assetNames -Version $version
        $needsBuild = -not $completeStage
        if ($needsBuild) {
            # Even if a tag already exists, rebuilding is permitted ONLY
            # before nuget.org has a version under this identity.
            $temp = Join-Path $env:RUNNER_TEMP "nuget-existence-probe.nupkg"
            if (Read-NuGetPackage -Version $version -Destination $temp) {
                throw "NuGet $version exists but no complete staged artifacts are available. Refusing to rebuild."
            }
            if ($release -and -not $release.isDraft) {
                throw "Published GitHub Release has incomplete staged artifacts."
            }
            if ($commit -ne $head) {
                throw "Unstaged release $tag references an older master commit. Manual recovery required."
            }
        }
        Write-OutputVariable "tag" $tag
        Write-OutputVariable "version" $version
        Write-OutputVariable "commit" $commit
        Write-OutputVariable "build" $needsBuild.ToString().ToLowerInvariant()
        Write-Host "Release $tag / commit $commit / rebuild required: $needsBuild"
        break
    }

    "Stage" {
        $version = $env:RELEASE_VERSION
        $tag = $env:RELEASE_TAG
        $commit = $env:RELEASE_COMMIT
        $package = Join-Path $originalDir "$packageId.$version.nupkg"
        $symbols = Join-Path $originalDir "$packageId.$version.snupkg"
        foreach ($file in @($package, $symbols)) {
            if (-not (Test-Path $file)) { throw "Missing built artifact: $file" }
        }
        $provenanceFile = Join-Path $originalDir "$packageId.$version$provenanceExtension"
        # Validate the attestation before creating any remote tag or release.
        Confirm-SignedProvenanceBundle -Path $provenanceFile -Packages @($package, $symbols) -Commit $commit
        $probe = Join-Path $env:RUNNER_TEMP "nuget-before-staging.nupkg"
        if (Read-NuGetPackage -Version $version -Destination $probe) {
            throw "NuGet version already published. Never replace staged artifacts after publication."
        }
        $manifest = [ordered]@{
            schema = 2
            repository = $repo
            packageId = $packageId
            version = $version
            tag = $tag
            commit = $commit
            packages = @(
                @{ name = [IO.Path]::GetFileName($package); sha256 = (Get-FileHash $package -Algorithm SHA256).Hash }
                @{ name = [IO.Path]::GetFileName($symbols); sha256 = (Get-FileHash $symbols -Algorithm SHA256).Hash }
            )
            provenance = @{
                name = [IO.Path]::GetFileName($provenanceFile)
                sha256 = (Get-FileHash $provenanceFile -Algorithm SHA256).Hash
            }
        }
        $manifestPath = Join-Path $originalDir $manifestName
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
        # Draft releases don't create their backing Git ref. Create the tag
        # explicitly on GitHub before staging and validate its commit.
        Ensure-ReleaseTag -Tag $tag -Commit $commit
        $release = Read-Release $tag
        if (-not $release) {
            Invoke-Cli "gh" @("release", "create", $tag, "--draft", "--verify-tag", "--target", $commit,
                "--title", $tag, "--generate-notes", "--repo", $repo,
                $package, $symbols, $provenanceFile, $manifestPath) | Out-Host
        } else {
            if (-not $release.isDraft) { throw "Cannot replace assets on a published release." }
            # Safe because the version does not exist on nuget.org.
            Invoke-Cli "gh" @("release", "upload", $tag, $package, $symbols, $provenanceFile, $manifestPath,
                "--clobber", "--repo", $repo) | Out-Host
        }
        Write-Host "Staged $tag and original attested bytes before NuGet push."
        break
    }

    "VerifyStage" {
        $null = Read-StagedManifest
        break
    }

    "InspectNuGet" {
        $manifest = Read-StagedManifest
        $package = Join-Path $stageDir "$packageId.$($manifest.version).nupkg"
        $published = Confirm-NuGetState -Version $manifest.version -Original $package
        Write-OutputVariable "publish" (-not $published).ToString().ToLowerInvariant()
        Write-Host "NuGet already published with matching contents: $published"
        break
    }

    "PushNuGet" {
        $manifest = Read-StagedManifest
        $package = Join-Path $stageDir "$packageId.$($manifest.version).nupkg"
        if (Confirm-NuGetState -Version $manifest.version -Original $package) {
            Write-Host "Package already published: verified exact original content."
            break
        }
        if (-not $env:NUGET_API_KEY) { throw "NuGet Trusted Publishing did not return an API key." }
        $output = & dotnet nuget push $package --api-key $env:NUGET_API_KEY --source "https://api.nuget.org/v3/index.json" 2>&1
        $pushCode = $LASTEXITCODE
        $pushLog = ($output | Out-String).Trim()
        Write-Host "NuGet push exit code: $pushCode"
        if ($pushLog) {
            # GitHub masks known secrets; explicitly redact the exchanged short-lived key too.
            Write-Host $pushLog.Replace($env:NUGET_API_KEY, "[REDACTED]")
        }
        if ($pushCode -ne 0) {
            Write-Warning "NuGet push failed. Checking for a concurrent or partially completed publish."
        }
        # Successful pushes may take substantially longer than three minutes to
        # become visible in NuGet's signed flat-container feed. Wait up to ~20 min
        # without re-pushing; after a failed push, retain the shorter recovery check.
        $attempts = if ($pushCode -eq 0) { 121 } else { 18 }
        if (-not (Confirm-NuGetState -Version $manifest.version -Original $package -Attempts $attempts)) {
            throw "NuGet $($manifest.version) is not verified after push. Draft release remains staged. Review the push response and package validation status; retry recovery later."
        }
        if ($pushCode -ne 0) {
            Write-Host "NuGet push failed but previously published package matches; resuming safely."
            $global:LASTEXITCODE = 0
        }
        break
    }

    "Finalize" {
        $manifest = Read-StagedManifest
        $package = Join-Path $stageDir "$packageId.$($manifest.version).nupkg"
        if (-not (Confirm-NuGetState -Version $manifest.version -Original $package -Attempts 18)) {
            throw "NuGet is not published. Keeping GitHub Release as draft."
        }
        $release = Read-Release $manifest.tag
        if (-not $release) { throw "Staged GitHub Release disappeared." }
        if ($release.isDraft) {
            Invoke-Cli "gh" @("release", "edit", $manifest.tag, "--draft=false", "--repo", $repo) | Out-Host
        }
        $final = Read-Release $manifest.tag
        if (-not $final -or $final.isDraft) { throw "GitHub Release publication remains incomplete." }
        Write-Host "Verified: NuGet signed payload, original attestations, immutable tag, GitHub Release $($manifest.tag)."
        break
    }
}
