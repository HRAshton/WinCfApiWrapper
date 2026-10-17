# Run locally with an authenticated GitHub CLI as a repository administrator.
# This is deliberately NOT a GitHub Actions workflow: GITHUB_TOKEN cannot
# administer repository rulesets/environments.
param(
    [string]$NuGetUser = ""
)
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repository = "HRAshton/WinCfApiWrapper"
$rulesRoot = Join-Path $PSScriptRoot "../.github/rulesets"

function Invoke-Gh {
    param([string[]]$Arguments)
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI failed: gh $($Arguments -join ' ')"
    }
    return $output
}

$null = Invoke-Gh -Arguments @("auth", "status")
$allRules = ConvertFrom-Json -InputObject (
    (Invoke-Gh -Arguments @("api", "repos/$repository/rulesets")) -join "`n"
)
$masterRules = @($allRules | Where-Object {
    $_.name -eq "master: require PR and Windows CI" -and $_.target -eq "branch"
})
$masterConfig = Join-Path $rulesRoot "master.json"
if ($masterRules.Count -gt 1) {
    throw "More than one matching master ruleset was found."
}
if ($masterRules.Count -eq 1) {
    $ruleId = $masterRules[0].id
    $null = Invoke-Gh -Arguments @("api", "--method", "PUT",
        "repos/$repository/rulesets/$ruleId", "--input", $masterConfig)
    Write-Host "Updated master ruleset #$ruleId"
} else {
    $null = Invoke-Gh -Arguments @("api", "--method", "POST",
        "repos/$repository/rulesets", "--input", $masterConfig)
    Write-Host "Created master ruleset"
}

# An existing v* ruleset should not be replaced merely to add automation.
# Validate the required safeguards, creating it only if absent.
$allRules = ConvertFrom-Json -InputObject (
    (Invoke-Gh -Arguments @("api", "repos/$repository/rulesets")) -join "`n"
)
$tagRules = @($allRules | Where-Object {
    $_.target -eq "tag" -and $_.name -eq "v*"
})
if ($tagRules.Count -eq 0) {
    $null = Invoke-Gh -Arguments @("api", "--method", "POST",
        "repos/$repository/rulesets",
        "--input", (Join-Path $rulesRoot "release-tags.json"))
    Write-Host "Created v* tag ruleset"
} elseif ($tagRules.Count -eq 1) {
    $tag = ConvertFrom-Json -InputObject (
        (Invoke-Gh -Arguments @("api", "repos/$repository/rulesets/$($tagRules[0].id)")) -join "`n"
    )
    $kinds = @($tag.rules | ForEach-Object { $_.type })
    $required = @("deletion", "non_fast_forward", "update")
    $missing = @($required | Where-Object { $_ -notin $kinds })
    if ($tag.enforcement -ne "active" -or
        "refs/tags/v*" -notin @($tag.conditions.ref_name.include) -or
        $missing.Count -gt 0) {
        throw "Existing v* tag ruleset is not sufficiently protective. Inspect it manually."
    }
    Write-Host "Verified active v* tag ruleset #$($tag.id)"
} else {
    throw "Multiple v* tag rulesets found; inspect before continuing."
}

# Restrict release credentials to protected branches (master currently).
# No required reviewers: Thursday publishing is intended to be unattended.
$environment = '{"wait_timer":0,"prevent_self_review":false,"deployment_branch_policy":{"protected_branches":true,"custom_branch_policies":false}}'
$temp = New-TemporaryFile
try {
    [System.IO.File]::WriteAllText($temp.FullName, $environment,
        [System.Text.UTF8Encoding]::new($false))
    $null = Invoke-Gh -Arguments @("api", "--method", "PUT",
        "repos/$repository/environments/nuget", "--input", $temp.FullName)
} finally {
    Remove-Item $temp.FullName -ErrorAction SilentlyContinue
}
Write-Host "Configured nuget environment to permit protected branches"

if ($NuGetUser.Trim() -ne "") {
    $null = Invoke-Gh -Arguments @("variable", "set", "NUGET_USER",
        "--repo", $repository, "--body", $NuGetUser.Trim())
    Write-Host "Set NUGET_USER Actions variable"
} else {
    Write-Warning "NUGET_USER was not set. Pass -NuGetUser YOUR_NUGET_ORG_USERNAME"
}
Write-Host "GitHub setup complete. Finish Trusted Publishing and Renovate web setup."
