<#
.SYNOPSIS
    Tabulates the transcription and minutes timings MeetingRecorder writes to
    its own log, so a change can be judged on measured numbers instead of on
    how long the wait felt.

.DESCRIPTION
    The application writes a "Transcription performance:" block after every
    transcription and a "Minutes performance:" block after every minutes run.
    This script reads those blocks out of the log files and prints one row per
    run, oldest first.

    Nothing is sent anywhere. It reads local files and writes to the console.

    HOW TO COMPARE TWO SETTINGS
      1. Open a meeting that has its working audio (文字起こし可能 in the
         status line).
      2. Settings -> 処理速度と精度, choose 高精度, close, press 文字起こし.
      3. Repeat with 標準, and again with 高速. The same audio can be
         transcribed as many times as you like; each run replaces the previous
         transcript and leaves the audio alone.
      4. Run this script. The rows appear in the order the runs happened, so
         the three profiles line up for comparison.

    The RTF column is the number to watch: processing time divided by the
    length of the meeting. 1.00 means a ten minute meeting took ten minutes.

.PARAMETER LogDirectory
    Where the logs are. Defaults to the portable location beside the
    executable, then to %LOCALAPPDATA%\MeetingRecorder\logs.

.PARAMETER Kind
    Which table to print: Transcription, Minutes, or Both (default).

.EXAMPLE
    .\tools\Show-Performance.ps1

.EXAMPLE
    .\tools\Show-Performance.ps1 -LogDirectory 'D:\MeetingRecorder\logs' -Kind Transcription
#>

[CmdletBinding()]
param(
    [string] $LogDirectory,
    [ValidateSet('Transcription', 'Minutes', 'Both')]
    [string] $Kind = 'Both'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-LogDirectory {
    param([string] $Requested)

    if ($Requested) {
        if (-not (Test-Path -LiteralPath $Requested)) {
            throw "Log directory not found: $Requested"
        }
        return (Resolve-Path -LiteralPath $Requested).Path
    }

    $candidates = @(
        (Join-Path $PSScriptRoot '..\logs'),
        (Join-Path $PSScriptRoot '..\publish\MeetingRecorder-win-x64\logs'),
        (Join-Path $env:LOCALAPPDATA 'MeetingRecorder\logs')
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    throw @"
Could not find a log directory. Pass -LogDirectory explicitly.

MeetingRecorder keeps its logs next to the executable when the folder is
writable (portable mode), and under %LOCALAPPDATA%\MeetingRecorder\logs when it
is not. The diagnostics panel in the application shows the exact path.
"@
}

# Reads every "<name> performance:" block out of one log file. A block is the
# header line followed by the indented "key = value" lines the logger wrote as
# one message, so the parse is: find the header, take following lines while they
# are indented.
function Read-PerformanceBlocks {
    param(
        [string] $Path,
        [string] $Header
    )

    $lines = Get-Content -LiteralPath $Path -Encoding UTF8
    $blocks = @()
    $current = $null

    foreach ($line in $lines) {
        if ($line -match [regex]::Escape($Header)) {
            if ($current) { $blocks += , $current }
            $current = [ordered]@{ }
            continue
        }

        if ($null -eq $current) { continue }

        if ($line -match '^\s{2,}(?<key>[^=]+?)\s*=\s*(?<value>.*)$') {
            $key = $Matches.key.Trim()
            $value = $Matches.value.Trim()
            if (-not $current.Contains($key)) {
                $current[$key] = $value
            }
            continue
        }

        # Anything not indented ends the block.
        $blocks += , $current
        $current = $null
    }

    if ($current) { $blocks += , $current }
    return $blocks
}

function Get-Field {
    param($Block, [string] $Name, [string] $Default = '-')
    if ($Block -and $Block.Contains($Name)) { return $Block[$Name] }
    return $Default
}

function Show-Transcription {
    param([string[]] $Files)

    $rows = @()
    foreach ($file in $Files) {
        foreach ($block in (Read-PerformanceBlocks -Path $file -Header 'Transcription performance:')) {
            $rows += [pscustomobject]@{
                Profile    = Get-Field $block 'Profile'
                Model      = Get-Field $block 'Model'
                Beam       = Get-Field $block 'Beam'
                Diarization = Get-Field $block 'Diarization'
                Meeting    = Get-Field $block 'Meeting duration'
                Submitted  = Get-Field $block 'Speech audio submitted'
                Chunks     = Get-Field $block 'Chunks'
                ModelLoad  = Get-Field $block 'Model load'
                Whisper    = Get-Field $block 'Whisper inference'
                DiarTime   = Get-Field $block 'Diarization time'
                Total      = Get-Field $block 'Total'
                RTF        = Get-Field $block 'RTF'
            }
        }
    }

    Write-Host ''
    Write-Host 'Transcription runs (oldest first)' -ForegroundColor Cyan
    if ($rows.Count -eq 0) {
        Write-Host '  none found. Transcribe a meeting, then run this again.' -ForegroundColor Yellow
        return
    }

    $rows | Format-Table -AutoSize
}

function Show-Minutes {
    param([string[]] $Files)

    $rows = @()
    foreach ($file in $Files) {
        foreach ($block in (Read-PerformanceBlocks -Path $file -Header 'Minutes performance:')) {
            $rows += [pscustomobject]@{
                Strategy   = Get-Field $block 'Strategy'
                Tokens     = Get-Field $block 'Transcript tokens'
                Context    = Get-Field $block 'Context window'
                Blocks     = Get-Field $block 'Blocks'
                MapCount   = Get-Field $block 'Map inference count'
                MapTime    = Get-Field $block 'Map time'
                RedCount   = Get-Field $block 'Reduce inference count'
                RedTime    = Get-Field $block 'Reduce time'
                OutTokens  = Get-Field $block 'Output tokens'
                TokPerSec  = Get-Field $block 'Tokens/sec'
                ModelLoad  = Get-Field $block 'Model load'
                Total      = Get-Field $block 'Total'
            }
        }
    }

    Write-Host ''
    Write-Host 'Minutes runs (oldest first)' -ForegroundColor Cyan
    if ($rows.Count -eq 0) {
        Write-Host '  none found. Generate minutes for a meeting, then run this again.' -ForegroundColor Yellow
        return
    }

    $rows | Format-Table -AutoSize
}

$directory = Resolve-LogDirectory -Requested $LogDirectory
Write-Host "Reading logs from: $directory"

# Oldest first: meetingrecorder.5.log is the oldest kept file and
# meetingrecorder.log is the newest.
$files = @(
    Get-ChildItem -LiteralPath $directory -Filter 'meetingrecorder*.log' |
        Sort-Object -Property @{ Expression = {
            if ($_.Name -match '^meetingrecorder\.(\d+)\.log$') { -[int]$Matches[1] } else { 0 }
        } } |
        Select-Object -ExpandProperty FullName
)

if ($files.Count -eq 0) {
    throw "No meetingrecorder*.log files in $directory."
}

if ($Kind -in @('Transcription', 'Both')) { Show-Transcription -Files $files }
if ($Kind -in @('Minutes', 'Both')) { Show-Minutes -Files $files }

Write-Host ''
Write-Host 'Also worth reading in the same log:' -ForegroundColor Cyan
Write-Host '  "Microphone capture format"  - the sample rate Windows actually gave the microphone.'
Write-Host '  "spectral balance"           - how the two streams compare across the speech band.'
Write-Host ''
