param(
    [Parameter(Mandatory)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string] $Repository,
    [Parameter(Mandatory)]
    [ValidatePattern('^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string] $Tag,
    [Parameter(Mandatory)]
    [string] $AssetsDirectory
)

$ErrorActionPreference = 'Stop'
$endpoint = "repos/$Repository/releases"
$version = [version] $Tag.Substring(1)
$series = "$($version.Major).$($version.Minor)"
$assetNames = @('ThinMarketMonitor-Setup-win-x64.exe', 'ThinMarketMonitor-win-x64.exe', 'SHA256SUMS.txt')
$headings = @('Добавлено', 'Изменено', 'Исправлено')

function Invoke-Gh([string[]] $Arguments) {
    & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI failed ($($Arguments[0..1] -join ' '))." }
}

function Invoke-Git([string[]] $Arguments) {
    & git @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Git history query failed ($($Arguments[0]))." }
}

function Get-Releases {
    $pages = Invoke-Gh @('api', ($endpoint + '?per_page=100'), '--paginate', '--slurp')
    foreach ($page in (ConvertFrom-Json -InputObject ($pages -join "`n") -NoEnumerate)) {
        foreach ($entry in $page) {
            if ($entry.tag_name -cmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
                $parsed = $null
                if ([version]::TryParse($entry.tag_name.Substring(1), [ref] $parsed)) {
                    [pscustomobject]@{ Version = $parsed; Entry = $entry }
                }
            }
        }
    }
}

function In-Series([version] $Candidate) {
    $Candidate.Major -eq $version.Major -and $Candidate.Minor -eq $version.Minor
}

function Read-Sections([string[]] $Lines) {
    $section = $null
    foreach ($line in $Lines) {
        if ($line -match '^#{1,6}\s+(.+?)\s*$') {
            $section = if ($headings -contains $Matches[1]) { $Matches[1] }
        }
        elseif ($section -and $line -match '^\s*[-*]\s+(.+?)\s*$') {
            [pscustomobject]@{ Section = $section; Text = $Matches[1] }
        }
    }
}

function New-ReleaseNotes($Releases) {
    $ancestors = @(Invoke-Git @('tag', '--merged', $Tag, '--list', 'v*'))
    if ($Tag -cnotin $ancestors) { throw "Tag $Tag is missing from the checkout." }
    $firstTag = $ancestors | Where-Object { $_ -cmatch "^v$([regex]::Escape($series))\.\d+$" } |
        Sort-Object { [version] $_.Substring(1) } | Select-Object -First 1
    $firstAncestors = if ($firstTag -eq $Tag) { $ancestors } else { Invoke-Git @('tag', '--merged', $firstTag, '--list', 'v*') }
    $earlierLine = $Releases | Where-Object {
        $_.Entry.published_at -and !$_.Entry.prerelease -and $_.Version -lt $version -and
            !(In-Series $_.Version) -and $_.Entry.tag_name -cin $firstAncestors
    } | Sort-Object Version -Descending | Select-Object -First 1
    $previous = $Releases | Where-Object {
        $_.Entry.published_at -and !$_.Entry.prerelease -and $_.Version -lt $version -and
            (In-Series $_.Version) -and $_.Entry.tag_name -cin $ancestors -and
            $_.Entry.body -and $_.Entry.body.StartsWith("Сборка **$($_.Entry.tag_name)**.")
    } | Sort-Object Version -Descending | Select-Object -First 1
    $priorNotes = @(Read-Sections ($previous.Entry.body -split '\r?\n'))
    $baseTag = if ($priorNotes.Count) { $previous.Entry.tag_name } else { $earlierLine.Entry.tag_name }
    $revision = if ($baseTag) { "$baseTag..$Tag" } else { $Tag }
    $items = @(foreach ($commit in (Invoke-Git @('log', '--no-merges', '--format=%H', $revision))) {
        $message = @(Invoke-Git @('show', '-s', '--format=%s%n%b', $commit))
        $userNotes = @(Read-Sections $message)
        if (!$userNotes.Count) {
            $section = switch -Regex ($message[0]) {
                '^(feat(\([^)]*\))?!?:|add\b|добав)' { 'Добавлено'; break }
                '^(fix(\([^)]*\))?!?:|fix\b|repair\b|исправ)' { 'Исправлено'; break }
                default { 'Изменено' }
            }
            $userNotes = @([pscustomobject]@{ Section = $section; Text = $message[0] })
        }
        foreach ($item in $userNotes) {
            [pscustomobject]@{ Section = $item.Section; Text = ('{0}{1}{0} {2}' -f [char]96, $Tag, $item.Text) }
        }
    })
    $items += $priorNotes

    $lines = @("Сборка **$Tag**. Здесь собраны изменения всей линейки **$series**.", '')
    foreach ($heading in $headings) {
        $lines += "## $heading", ''
        $bullets = @($items | Where-Object Section -eq $heading | ForEach-Object { "- $($_.Text)" })
        $lines += if ($bullets.Count) { $bullets } else { '—' }
        $lines += ''
    }
    $url = "https://github.com/$Repository"
    $lines += if ($earlierLine) { "[Коммиты линейки]($url/compare/$($earlierLine.Entry.tag_name)...$Tag)" }
        else { "[Код линейки]($url/tree/$Tag)" }
    ($lines -join "`n") + "`n"
}

function Read-Manifest([string[]] $Lines) {
    $hashes = @{}
    foreach ($line in $Lines) {
        if ($line -notmatch '^([a-fA-F0-9]{64})\s+(ThinMarketMonitor-(?:Setup-)?win-x64\.exe)$' -or $hashes.ContainsKey($Matches[2])) {
            throw 'Unexpected entry in SHA256SUMS.txt.'
        }
        $hashes[$Matches[2]] = 'sha256:' + $Matches[1].ToLowerInvariant()
    }
    if ($hashes.Count -ne 2) { throw 'SHA256SUMS.txt must include both Windows builds.' }
    $hashes
}

function Find-Asset($Release, [string] $Name) {
    $entries = @($Release.assets | Where-Object name -ceq $Name)
    if ($entries.Count -eq 1) { $entries[0] }
}

function Test-UploadedAsset($Release, $File) {
    $asset = Find-Asset $Release $File.Name
    $asset -and $asset.state -eq 'uploaded' -and $asset.size -eq $File.Size -and $asset.digest -ceq $File.Digest
}

function Assert-PublicAssets($Release) {
    if (!$Release -or $Release.draft -or $Release.prerelease -or $Release.tag_name -cne $Tag) {
        throw "Release $Tag is not public."
    }
    foreach ($name in $assetNames) {
        $asset = Find-Asset $Release $name
        if (!$asset -or $asset.state -ne 'uploaded' -or $asset.size -le 0 -or $asset.digest -cnotmatch '^sha256:[a-f0-9]{64}$') {
            throw "Release $Tag is missing a verified $name."
        }
    }
    $manifest = Read-Manifest (Invoke-Gh @('release', 'download', $Tag, '--repo', $Repository,
        '--pattern', 'SHA256SUMS.txt', '--output', '-'))
    foreach ($name in $manifest.Keys) {
        if ((Find-Asset $Release $name).digest -cne $manifest[$name]) { throw "Published checksum mismatch for $name." }
    }
}

$releases = @(Get-Releases)
$public = @($releases | Where-Object { !$_.Entry.draft -and !$_.Entry.prerelease })
$older = @($public | Where-Object { (In-Series $_.Version) -and $_.Version -lt $version })
$newer = $public | Where-Object Version -gt $version | Sort-Object Version -Descending | Select-Object -First 1
$expectedLatest = if ($newer) { $newer.Entry.tag_name } else { $Tag }
$latest = $expectedLatest -ceq $Tag
$current = ($releases | Where-Object { $_.Entry.tag_name -ceq $Tag }).Entry
if ($current.prerelease) { throw "Tag $Tag already belongs to a prerelease." }
if (($public | Where-Object { (In-Series $_.Version) -and $_.Version -gt $version }) -or
    ($current.draft -and $current.published_at)) {
    Write-Host "Release $Tag has already been superseded."
    return
}

if (!$current -or $current.draft) {
    $manifest = Read-Manifest (Get-Content -LiteralPath (Join-Path $AssetsDirectory 'SHA256SUMS.txt'))
    $files = foreach ($name in $assetNames) {
        $file = Get-Item -LiteralPath (Join-Path $AssetsDirectory $name)
        $hash = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($file.Length -le 0 -or ($name -in $manifest.Keys -and $hash -cne $manifest[$name])) {
            throw "Artifact checksum mismatch for $name."
        }
        [pscustomobject]@{ Name = $name; Path = $file.FullName; Size = $file.Length; Digest = $hash }
    }

    $title = "Thin Market Monitor $series"
    $notes = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($notes, (New-ReleaseNotes $releases))
        if (!$current) {
            Invoke-Gh @('release', 'create', $Tag, '--repo', $Repository, '--verify-tag', '--draft',
                '--title', $title, '--notes-file', $notes) | Out-Null
            $current = ((Get-Releases) | Where-Object { $_.Entry.tag_name -ceq $Tag }).Entry
        }
        if (!$current.draft -or $current.published_at) { throw "Cannot upload to release $Tag." }
        foreach ($file in $files) {
            if (!(Test-UploadedAsset $current $file)) {
                Invoke-Gh @('release', 'upload', $Tag, $file.Path, '--repo', $Repository, '--clobber')
            }
        }
        $current = Invoke-Gh @('api', "$endpoint/$($current.id)") | ConvertFrom-Json
        foreach ($file in $files) {
            if (!(Test-UploadedAsset $current $file)) {
                throw "Uploaded checksum mismatch for $($file.Name)."
            }
        }
        Invoke-Gh @('release', 'edit', $Tag, '--repo', $Repository, '--draft=false',
            "--latest=$($latest.ToString().ToLowerInvariant())", '--title', $title, '--notes-file', $notes) | Out-Null
    }
    finally { Remove-Item -LiteralPath $notes -ErrorAction SilentlyContinue }
}

$current = Invoke-Gh @('api', "$endpoint/$($current.id)") | ConvertFrom-Json
Assert-PublicAssets $current
if ((Invoke-Gh @('api', "$endpoint/latest", '--jq', '.tag_name')) -cne $expectedLatest) {
    throw "GitHub did not select $expectedLatest as the latest public version."
}

foreach ($candidate in ($older | Sort-Object Version -Descending)) {
    $old = $candidate.Entry
    $archived = Invoke-Gh @('api', '-X', 'PATCH', "$endpoint/$($old.id)", '-F', 'draft=true',
        '-f', 'make_latest=false') | ConvertFrom-Json
    if (!$archived.draft -or $archived.id -ne $old.id -or $archived.tag_name -cne $old.tag_name) {
        throw "Could not hide release $($old.tag_name)."
    }
    Write-Host "Moved $($old.tag_name) to drafts."
}
Write-Host "Public release $Tag is verified."
