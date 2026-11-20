
$ErrorActionPreference = 'Stop'
try {
    $d = [Environment]::GetEnvironmentVariable('CLAUDE_CONFIG_DIR')
    if ($d -notmatch '^[a-zA-Z]:[\\/]') { return }
    $dir = [IO.DirectoryInfo]::new($d)
    if ($dir.Name -cne '__UUID__' -or $dir.Parent.Name -ne 'claude-code') { return }
    for ($p=$dir; $null -ne $p; $p=$p.Parent) {
        if ($p.Attributes -band [IO.FileAttributes]::ReparsePoint) { return }
    }
    $flag = Join-Path $d 'multiclaude-limits.enabled'
    if (-not [IO.File]::Exists($flag) -or ([IO.File]::GetAttributes($flag) -band 1024)) { return }
    if ([IO.FileInfo]::new($flag).Length -ne 97) { return }
    if ([IO.File]::ReadAllText($flag) -cnotmatch '^__UUID__:[a-f0-9]{64}$') { return }
    $json = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $result = @{observed_at=$now}
    $labels = @()
    foreach ($name in 'five_hour','seven_day') {
        $w = $json.rate_limits.$name
        if ($null -eq $w) { continue }
        $v = $w.used_percentage
        $r = $w.resets_at
        if (($v -isnot [double] -and $v -isnot [long] -and $v -isnot [int] -and $v -isnot [decimal]) -or
            ($r -isnot [long] -and $r -isnot [int]) -or $v -lt 0 -or $v -gt 100 -or $r -le $now -or $r -gt 253402300799) { continue }
        $result[$name] = @{used_percentage=$v; resets_at=$r}
        $label = if ($name -eq 'five_hour') { '5h' } else { '7d' }
        $labels += ($label + ' ' + $v + '%')
    }
    if ($labels.Count -eq 0) { return }
    $target = Join-Path $d 'multiclaude-limits.json'
    $tmp = Join-Path $d ([Guid]::NewGuid().ToString('N') + '.rate.tmp')
    try {
        [IO.File]::WriteAllText($tmp, ($result | ConvertTo-Json -Compress))
        if (-not [IO.File]::Exists($flag)) { return }
        if ([IO.File]::Exists($target)) {
            if ([IO.File]::GetAttributes($target) -band 1024) { return }
            [IO.File]::Replace($tmp,$target,$tmp+'.old')
        } else { [IO.File]::Move($tmp,$target) }
    } finally {
        [IO.File]::Delete($tmp)
        [IO.File]::Delete($tmp+'.old')
    }
    [Console]::WriteLine(($labels -join ' | '))
} catch { }
