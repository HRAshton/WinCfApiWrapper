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
    $_.target -eq "branch" -and (
        "~DEFAULT_BRANCH" -in @($_.conditions.ref_name.include) -or
        "refs/heads/master" -in @($_.conditions.ref_name.include)
    )
})
$masterConfig = Join-Path $rulesRoot "master.json"
if ($masterRules.Count -gt 1) {
    throw "Multiple rulesets govern master. Inspect them manually; no duplicate created."
}
if ($masterRules.Count -eq 1) {
    $ruleId = $masterRules[0].id
    $master = ConvertFrom-Json -InputObject (
        (Invoke-Gh -Arguments @("api", "repos/$repository/rulesets/$ruleId")) -join "`n"
    )
    $types = @($master.rules | ForEach-Object { $_.type })
    $requiredRules = @("pull_request", "required_status_checks", "deletion", "non_fast_forward")
    $missing = @($requiredRules | Where-Object { $_ -notin $types })
    $pr = @($master.rules | Where-Object { $_.type -eq "pull_request" }) | Select-Object -First 1
    $checks = @($master.rules | Where-Object { $_.type -eq "required_status_checks" }) |
        Select-Object -First 1
    $contexts = @($checks.parameters.required_status_checks | ForEach-Object { $_.context })
    if ($master.enforcement -ne "active" -or $missing.Count -gt 0 -or
        "Build and unit tests (Windows)" -notin $contexts -or
        $pr.parameters.required_approving_review_count -ne 0) {
        throw "Existing master ruleset lacks required protections or blocks zero-review Renovate automerge."
    }
    Write-Host "Verified active master ruleset #$ruleId; leaving existing settings unchanged"
    if ("code_quality" -in $types -or "code_coverage" -in $types) {
        Write-Warning "Existing master ruleset includes Code Quality / coverage thresholds. Confirm their workflows and coverage uploads run before merging."
    }
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
