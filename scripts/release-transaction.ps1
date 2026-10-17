# Release transaction: stage immutable build inputs BEFORE pushing to nuget.org.
# Draft GitHub Release + original artifacts + manifest survive workflow retries.
# A retry never repacks a version known to be published on nuget.org.
param(
    [Parameter(Mandatory)]
    [ValidateSet("Resolve", "Stage", "VerifyStage", "InspectNuGet", "PushNuGet", "Finalize", "SyntaxCheck")]
    [string]$Phase
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false

$repo = "HRAshton/WinCfApiWrapper"
$packageId = "WinCfApiWrapper"
$signer = "$repo/.github/workflows/release.yml"
$stageDir = Join-Path (Get-Location) "release-stage"
$originalDir = Join-Path (Get-Location) "artifacts"
$manifestName = "release-manifest.json"
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

function Read-Release {
    param([string]$Tag)
    $output = & gh release view $Tag --repo $repo --json tagName,isDraft,assets 2>&1
    if ($LASTEXITCODE -ne 0) {
        if (($output | Out-String) -match '(?i)(release not found|http 404)') { return $null }
        throw "Cannot query GitHub Release $Tag : $($output | Out-String)"
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

function Confirm-SignedNuGetPackage {
    param([string]$Original, [string]$Published)
    # nuget.org adds only .signature.p7s to unsigned packages.
    # Compare every other uncompressed ZIP entry by name and SHA256.
    Invoke-Cli "dotnet" @("nuget", "verify", $Published, "--all") | Out-Host
    $orig = Read-ZipContentHashes $Original
    $remote = Read-ZipContentHashes $Published
    if ($orig.Count -ne $remote.Count) { throw "Published NuGet package has different ZIP entries." }
    foreach ($name in $orig.Keys) {
        if (-not $remote.ContainsKey($name) -or $remote[$name] -cne $orig[$name]) {
            throw "NuGet package content mismatch for entry '$name'. Manual investigation required."
        }
    }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Published)
    try {
        if (-not $zip.GetEntry(".signature.p7s")) {
            throw "NuGet package has no repository signature."
        }
    } finally { $zip.Dispose() }
    Write-Host "NuGet repository-signed package content matches the attested original."
}

function Confirm-NuGetState {
    param([string]$Version, [string]$Original, [int]$Attempts = 1)
    New-Item -ItemType Directory -Path $stageDir -Force | Out-Null
    $downloaded = Join-Path $stageDir "nuget-signed.nupkg"
    for ($i = 1; $i -le $Attempts; $i++) {
        if (Test-Path $downloaded) { Remove-Item -LiteralPath $downloaded -Force }
        if (Read-NuGetPackage -Version $Version -Destination $downloaded) {
            Confirm-SignedNuGetPackage -Original $Original -Published $downloaded
            return $true
        }
        if ($i -lt $Attempts) { Start-Sleep -Seconds 10 }
    }
    return $false
}

function Confirm-TagTarget {
    param([string]$Tag, [string]$Commit)
    # Fetch any tag created by GitHub Release after checkout.
    Invoke-Cli "git" @("fetch", "--force", "--tags", "origin") | Out-Null
    $actual = Invoke-Cli "git" @("rev-list", "-n", "1", "refs/tags/$Tag")
    if ($actual -ne $Commit) {
        throw "Release tag $Tag points at $actual instead of expected $Commit."
    }
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
    if ($manifest.schema -ne 1 -or $manifest.repository -cne $repo -or
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
        $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($actual -cne $asset.sha256) { throw "Staged asset SHA256 mismatch: $($asset.name)" }
        Invoke-Cli "gh" @("attestation", "verify", $path, "--repo", $repo, "--signer-workflow", $signer, "--source-ref", "refs/heads/master") | Out-Null
    }
    $nupkg = @($manifest.packages | Where-Object { $_.name.EndsWith(".nupkg") })[0]
    if ($nupkg.name -cne "$packageId.$version.nupkg") {
        throw "Staged NuGet package name does not match release version."
    }
    Write-Host "Verified both original artifact SHA256 digests, tag commit, and attestations."
    return $manifest
}

switch ($Phase) {
    "SyntaxCheck" { Write-Host "PowerShell release transaction script parsed successfully."; break }

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
            $resume = ($lastCommit -eq $head -or -not $release -or $release.isDraft)
        }
        if ($resume) {
            $tag = $lastTag
            $version = $tag.Substring(1)
            $commit = $lastCommit
        } else {
            $commit = $head
            Invoke-Cli "dotnet" @("tool", "install", "--global", "GitVersion.Tool", "--version", "6.8.2") | Out-Host
            $version = (Invoke-Cli "dotnet-gitversion" @("/showvariable", "SemVer", "/config", "GitVersion.yml")).Trim()
            if ($version -cnotmatch '^\d+\.\d+\.\d+$') { throw "GitVersion did not produce stable SemVer: $version" }
            $tag = "v$version"
            if ((& git tag --list $tag) -contains $tag) { throw "Version tag already exists: $tag" }
        }
        $release = Read-Release $tag
        $assetNames = if ($release) { @($release.assets | ForEach-Object { $_.name }) } else { @() }
        $completeStage = ($manifestName -in $assetNames -and
            "$packageId.$version.nupkg" -in $assetNames -and
            "$packageId.$version.snupkg" -in $assetNames)
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
        $probe = Join-Path $env:RUNNER_TEMP "nuget-before-staging.nupkg"
        if (Read-NuGetPackage -Version $version -Destination $probe) {
            throw "NuGet version already published. Never replace staged artifacts after publication."
        }
        $manifest = [ordered]@{
            schema = 1
            repository = $repo
            packageId = $packageId
            version = $version
            tag = $tag
            commit = $commit
            packages = @(
                @{ name = [IO.Path]::GetFileName($package); sha256 = (Get-FileHash $package -Algorithm SHA256).Hash }
                @{ name = [IO.Path]::GetFileName($symbols); sha256 = (Get-FileHash $symbols -Algorithm SHA256).Hash }
            )
        }
        $manifestPath = Join-Path $originalDir $manifestName
        $manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
        $release = Read-Release $tag
        if (-not $release) {
            Invoke-Cli "gh" @("release", "create", $tag, "--draft", "--target", $commit,
                "--title", $tag, "--generate-notes", "--repo", $repo,
                $package, $symbols, $manifestPath) | Out-Host
        } else {
            if (-not $release.isDraft) { throw "Cannot replace assets on a published release." }
            # Safe because the version does not exist on nuget.org.
            Invoke-Cli "gh" @("release", "upload", $tag, $package, $symbols, $manifestPath,
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
        if ($pushCode -ne 0) {
            Write-Warning "NuGet push failed. Checking for a concurrent or partially completed publish."
            Write-Host ($output | Out-String)
        }
        if (-not (Confirm-NuGetState -Version $manifest.version -Original $package -Attempts 18)) {
            throw "NuGet package is not verified after push. Draft release remains staged. Retry safely later."
        }
        if ($pushCode -ne 0) {
            Write-Host "NuGet push failed but previously published package matches; resuming safely."
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
