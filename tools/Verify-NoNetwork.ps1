<#
.SYNOPSIS
    Watches MeetingRecorder for outbound network connections (procedure T-45).

.DESCRIPTION
    The product's central claim is that meeting audio, transcripts and minutes
    never leave the machine. This script checks that claim from outside the
    application: it starts (or attaches to) MeetingRecorder.exe and samples the
    Windows TCP/UDP tables for connections owned by that process and its
    children, reporting anything it finds.

    Run it while doing the things that would leak if the claim were false:
    record a meeting, let the transcript run, and generate minutes. The expected
    result is zero outbound connections. The one legitimate exception is an AI
    model download, which only happens when you press the button for it.

    This is evidence, not proof. A full packet capture (Wireshark) is the
    stronger check and remains part of T-45 in docs/WINDOWS_E2E_TEST.md; this
    script exists because it takes thirty seconds and people will actually run it.

.PARAMETER ExePath
    Path to MeetingRecorder.exe. Defaults to the copy next to this script's
    parent folder.

.PARAMETER Minutes
    How long to watch. Default 5.

.PARAMETER Attach
    Watch an already-running MeetingRecorder instead of starting one.

.EXAMPLE
    .\Verify-NoNetwork.ps1 -ExePath C:\Tools\MeetingRecorder\MeetingRecorder.exe -Minutes 10
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$Minutes = 5,
    [switch]$Attach
)

$ErrorActionPreference = 'Stop'

function Get-DescendantProcessIds {
    param([int]$RootId)

    $all = Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId
    $result = New-Object System.Collections.Generic.HashSet[int]
    [void]$result.Add($RootId)

    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($p in $all) {
            if ($result.Contains([int]$p.ParentProcessId) -and -not $result.Contains([int]$p.ProcessId)) {
                [void]$result.Add([int]$p.ProcessId)
                $changed = $true
            }
        }
    }

    return $result
}

# ---- Locate and start the application ------------------------------------

if (-not $ExePath) {
    $ExePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'MeetingRecorder.exe'
}

if ($Attach) {
    $process = Get-Process -Name 'MeetingRecorder' -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $process) { throw 'No running MeetingRecorder process was found.' }
    Write-Host "Attached to the running MeetingRecorder (PID $($process.Id))."
} else {
    if (-not (Test-Path $ExePath)) { throw "MeetingRecorder.exe not found at '$ExePath'. Pass -ExePath." }
    $process = Start-Process -FilePath $ExePath -PassThru
    Write-Host "Started $ExePath (PID $($process.Id))."
}

Write-Host ''
Write-Host "Watching for $Minutes minute(s). While this runs, please:"
Write-Host '  1. start a recording,'
Write-Host '  2. let the live transcript run for a few minutes,'
Write-Host '  3. stop the recording and generate minutes.'
Write-Host 'Do NOT download an AI model during the watch (that is the one allowed connection).'
Write-Host ''

# ---- Sample the connection tables -----------------------------------------

$deadline = (Get-Date).AddMinutes($Minutes)
$observed = @{}
$samples = 0

while ((Get-Date) -lt $deadline) {
    if ($process.HasExited) {
        Write-Host 'MeetingRecorder exited; stopping the watch.'
        break
    }

    $pids = Get-DescendantProcessIds -RootId $process.Id
    $samples++

    # Established TCP: an actual conversation with a remote host.
    Get-NetTCPConnection -ErrorAction SilentlyContinue |
        Where-Object { $pids.Contains([int]$_.OwningProcess) -and $_.State -eq 'Established' } |
        ForEach-Object {
            $key = "TCP $($_.RemoteAddress):$($_.RemotePort)"
            if (-not $observed.ContainsKey($key)) {
                $observed[$key] = (Get-Date)
                Write-Host "  [$(Get-Date -Format HH:mm:ss)] OUTBOUND $key" -ForegroundColor Yellow
            }
        }

    # UDP has no state, so a bound remote endpoint is the closest equivalent.
    Get-NetUDPEndpoint -ErrorAction SilentlyContinue |
        Where-Object { $pids.Contains([int]$_.OwningProcess) } |
        ForEach-Object {
            $key = "UDP local $($_.LocalAddress):$($_.LocalPort)"
            if (-not $observed.ContainsKey($key)) {
                $observed[$key] = (Get-Date)
                Write-Host "  [$(Get-Date -Format HH:mm:ss)] UDP socket $key" -ForegroundColor DarkYellow
            }
        }

    Start-Sleep -Seconds 2
}

# ---- Report ----------------------------------------------------------------

Write-Host ''
Write-Host '========================================================'
Write-Host ' T-45  外部通信の確認結果'
Write-Host '========================================================'
Write-Host "監視時間  : $Minutes 分（$samples 回サンプリング）"
Write-Host "対象プロセス: MeetingRecorder.exe (PID $($process.Id)) とその子プロセス"
Write-Host ''

if ($observed.Count -eq 0) {
    Write-Host '結果: 外向きの接続は検出されませんでした。' -ForegroundColor Green
    Write-Host '      会議データが外部へ送信されていないことと矛盾しない結果です。'
    exit 0
}

Write-Host "結果: $($observed.Count) 件の接続を検出しました。" -ForegroundColor Yellow
foreach ($key in $observed.Keys | Sort-Object) {
    Write-Host "  - $key  (初回 $($observed[$key].ToString('HH:mm:ss')))"
}

Write-Host ''
Write-Host 'AIモデルのダウンロード中でなければ想定外です。'
Write-Host 'huggingface.co 以外への接続が見られる場合は、Wireshark 等での確認を行ってください。'
exit 1
