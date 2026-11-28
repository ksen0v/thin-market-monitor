$ErrorActionPreference = 'Stop'
$publish = Join-Path $PSScriptRoot 'Publish-Release.ps1'
$mock = @{
    Releases = @{}
    Id = 100
    Latest = 'v0.11.7'
    UploadFailure = ''
    ArchiveFailure = ''
    Manifests = @{}
    Writes = [System.Collections.Generic.List[string]]::new()
}
$api = 'repos/fixture/monitor/releases'

function New-FixtureRelease($Tag, $Title, $Body, [bool] $Draft = $false) {
    $mock.Id++
    @{
        id = $mock.Id; tag_name = $Tag; name = $Title; body = $Body
        draft = $Draft; prerelease = $false; assets = @()
        published_at = if (!$Draft) { '2026-01-01T00:00:00Z' }
    }
}

function Assert-Release([bool] $Condition, [string] $Description) {
    if (!$Condition) { throw $Description }
}

# All GitHub CLI operations in this test use the fixture below.
function gh {
    $global:LASTEXITCODE = 0
    if ($args[0] -eq 'api') {
        if ($args[1] -eq ($api + '?per_page=100')) {
            ConvertTo-Json -InputObject (,@($mock.Releases.Values)) -Depth 20 -Compress
            return
        }
        if ($args[1] -eq "$api/latest") {
            $mock.Latest
            return
        }
        $path = if ($args[1] -eq '-X') { $args[3] } else { $args[1] }
        if (!$path.StartsWith("$api/")) { throw "Unexpected fixture API $path." }
        $entry = $mock.Releases.Values | Where-Object id -eq ([int] $path.Split('/')[-1])
        if (!$entry) { throw "No fixture release for $path." }
        if ($args[1] -eq '-X') {
            if ($mock.ArchiveFailure -eq $entry.tag_name) { throw 'Simulated archive failure' }
            Assert-Release ($args -contains 'draft=true') 'The previous release must become a draft.'
            $entry.draft = $true
            $mock.Writes.Add("archive $($entry.tag_name)")
        }
        $entry | ConvertTo-Json -Depth 20 -Compress
        return
    }
    if ($args[0] -ne 'release' -or $args -notcontains 'fixture/monitor') {
        throw "Unexpected fixture command $($args -join ' ')."
    }
    $tag = $args[2]
    $entry = $mock.Releases[$tag]
    switch ($args[1]) {
        'create' {
            Assert-Release (!$entry -and $args -contains '--draft' -and $args -contains '--verify-tag') 'A verified tag must start as a draft.'
            $title = $args[[array]::IndexOf($args, '--title') + 1]
            $body = Get-Content -LiteralPath $args[[array]::IndexOf($args, '--notes-file') + 1] -Raw
            $mock.Releases[$tag] = New-FixtureRelease $tag $title $body $true
            $mock.Writes.Add("create $tag")
        }
        'upload' {
            $file = Get-Item -LiteralPath $args[3]
            if ($mock.UploadFailure -eq $file.Name) { $global:LASTEXITCODE = 1; return }
            Assert-Release ($entry.draft -and !$entry.published_at) 'Uploads belong to the unpublished draft.'
            $mock.Id++
            $digest = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $entry.assets = @($entry.assets | Where-Object name -ne $file.Name) + @{
                id = $mock.Id; name = $file.Name; size = $file.Length; digest = "sha256:$digest"; state = 'uploaded'
            }
            if ($file.Name -eq 'SHA256SUMS.txt') { $mock.Manifests[$tag] = Get-Content -LiteralPath $file.FullName }
            $mock.Writes.Add("upload $tag $($file.Name)")
        }
        'edit' {
            Assert-Release ($entry.draft -and $entry.assets.Count -eq 3 -and $args -contains '--draft=false') 'Publish after attaching the complete build.'
            $entry.body = Get-Content -LiteralPath $args[[array]::IndexOf($args, '--notes-file') + 1] -Raw
            $entry.published_at = '2026-02-01T00:00:00Z'
            $entry.draft = $false
            if ($args -contains '--latest=true') { $mock.Latest = $tag }
            $mock.Writes.Add("publish $tag")
        }
        'download' {
            Assert-Release (!$entry.draft -and $args -contains 'SHA256SUMS.txt') 'Verify the public checksums.'
            $mock.Manifests[$tag]
        }
        default { throw "Unexpected release command $($args[1])." }
    }
}

function Add-Commit([string] $Title, [string] $Body = '') {
    $arguments = @('-c', 'user.name=Release Test', '-c', 'user.email=release-test@example.invalid',
        'commit', '--allow-empty', '--quiet', '-m', $Title)
    if ($Body) { $arguments += '-m', $Body }
    & git @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Could not build fixture history.' }
}

function Run-Publish([string] $Tag) {
    & $publish -Repository fixture/monitor -Tag $Tag -AssetsDirectory $assets
}

function Expect-Failure([string] $Tag, [string] $Message) {
    try { Run-Publish $Tag }
    catch {
        if ($_.Exception.Message -eq $Message) { return }
        throw
    }
    throw "Expected failure: $Message"
}

$tempRoot = [System.IO.Path]::GetTempPath()
$name = 'tmm-release-check-' + [guid]::NewGuid().ToString('N')
$work = (New-Item -Path (Join-Path $tempRoot $name) -ItemType Directory).FullName
Push-Location -LiteralPath $work
try {
    git init --quiet --initial-branch=main
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize fixture history.' }
    Add-Commit 'Add indicator from previous series'
    git tag v0.10.6
    Add-Commit 'Add original screen'
    git tag v0.11.0
    Add-Commit 'Repair a compact chart'
    Add-Commit 'Fix older price level'
    git tag v0.11.7
    Add-Commit 'feat(chart): Add price marker'
    Add-Commit 'Fix chart title'
    Add-Commit 'Adjust workspace defaults' "## Изменено`n`n- Настроен масштаб."
    git tag v0.11.8

    $mock.Releases['v0.10.6'] = New-FixtureRelease 'v0.10.6' 'Thin Market Monitor 0.10' 'Previous series.'
    $old = New-FixtureRelease 'v0.11.7' 'Thin Market Monitor 0.11' @'
Сборка **v0.11.7**. Здесь собраны изменения всей линейки **0.11**.

## Добавлено

- Архивное описание интерфейса.

## Исправлено

- Архивное описание шкалы.

[Коммиты линейки](https://github.com/fixture/monitor/compare/v0.10.6...v0.11.7)
'@
    $old.assets = @(@{ id = 10; name = 'Original asset' })
    $mock.Releases['v0.11.7'] = $old

    $assets = (New-Item -ItemType Directory -Name 'build').FullName
    foreach ($file in @('ThinMarketMonitor-win-x64.exe', 'ThinMarketMonitor-Setup-win-x64.exe')) {
        $path = Join-Path $assets $file
        Set-Content -LiteralPath $path -Value "Fixture $file" -Encoding ascii
        "$((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())  $file" |
            Add-Content -LiteralPath (Join-Path $assets 'SHA256SUMS.txt') -Encoding ascii
    }

    $portable = Join-Path $assets 'ThinMarketMonitor-win-x64.exe'
    $correctBytes = [System.IO.File]::ReadAllBytes($portable)
    Add-Content -LiteralPath $portable -Value 'Invalid checksum' -Encoding ascii
    Expect-Failure 'v0.11.8' 'Artifact checksum mismatch for ThinMarketMonitor-win-x64.exe.'
    Assert-Release (!$mock.Releases.ContainsKey('v0.11.8') -and !$old.draft) 'A bad local file cannot start a release.'
    [System.IO.File]::WriteAllBytes($portable, $correctBytes)

    $mock.UploadFailure = 'ThinMarketMonitor-win-x64.exe'
    Expect-Failure 'v0.11.8' 'GitHub CLI failed (release upload).'
    Assert-Release ($mock.Releases['v0.11.8'].draft -and !$old.draft -and $mock.Latest -eq 'v0.11.7') 'An incomplete upload must leave the previous patch public.'

    $mock.UploadFailure = ''
    $mock.ArchiveFailure = 'v0.11.7'
    Expect-Failure 'v0.11.8' 'Simulated archive failure'
    $new = $mock.Releases['v0.11.8']
    Assert-Release (!$new.draft -and !$old.draft -and $mock.Latest -eq 'v0.11.8') 'A retry can finish hiding the older patch after a successful publication.'
    Assert-Release ($new.name -eq 'Thin Market Monitor 0.11' -and $new.body.Contains('Архивное описание интерфейса.') -and
        $new.body.Contains('Архивное описание шкалы.') -and $new.body.Contains('## Добавлено') -and
        $new.body.Contains('## Изменено') -and $new.body.Contains('## Исправлено') -and
        $new.body.Contains('feat(chart): Add price marker') -and $new.body.Contains('Fix chart title') -and
        $new.body.Contains('Настроен масштаб.') -and !$new.body.Contains('Add indicator from previous series') -and
        !$new.body.Contains('Adjust workspace defaults') -and !$new.body.Contains('Repair a compact chart')) 'New notes must group the current changes and retain the curated history of the series.'

    $mock.ArchiveFailure = ''
    Run-Publish 'v0.11.8'
    Assert-Release ($old.draft -and !$mock.Releases['v0.10.6'].draft -and $old.assets[0].id -eq 10 -and
        $mock.Writes.IndexOf('publish v0.11.8') -lt $mock.Writes.IndexOf('archive v0.11.7')) 'Only older patches of this series move to drafts after publication.'
    $writes = $mock.Writes.Count
    Run-Publish 'v0.11.8'
    Run-Publish 'v0.11.7'
    Run-Publish 'v0.11.0'
    Assert-Release ($mock.Writes.Count -eq $writes) 'Retrying completed or superseded tags must be safe.'

    Add-Commit 'Add feature for new series'
    git tag v0.12.0
    Run-Publish 'v0.12.0'
    Assert-Release ($mock.Latest -eq 'v0.12.0' -and !$new.draft -and
        $mock.Releases['v0.12.0'].body.Contains('Add feature for new series') -and
        !$mock.Releases['v0.12.0'].body.Contains('Add price marker')) 'A new series starts its own notes and preserves the previous series release.'

    git switch --quiet --detach v0.10.6
    Add-Commit 'fix: Correct maintenance build'
    git tag v0.10.7
    Run-Publish 'v0.10.7'
    Assert-Release ($mock.Latest -eq 'v0.12.0' -and $mock.Releases['v0.10.6'].draft -and !$mock.Releases['v0.10.7'].draft) 'A maintenance patch replaces its own series without downgrading automatic updates.'
    Write-Host 'Cumulative release notes, retries, archive ordering, out-of-order tags and maintenance releases passed.'
}
finally {
    Pop-Location
    $resolved = (Resolve-Path -LiteralPath $work).ProviderPath
    if ([System.IO.Path]::GetRelativePath($tempRoot, $resolved) -ne $name) {
        throw 'Unexpected release test directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
