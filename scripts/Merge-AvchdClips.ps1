<#
.SYNOPSIS
    Joins AVCHD camcorder clips (.MTS) into a single, correctly-timed video per disc.

.DESCRIPTION
    AVCHD camcorders split one recording into numbered clips (00000.MTS, 00001.MTS, ...)
    inside a "...\BDMV\STREAM\" folder. A raw binary concatenation plays but has broken
    timing/seeking, because each clip carries its own timestamp clock. This script uses
    ffmpeg's concat *demuxer* to rebuild one continuous timeline as a LOSSLESS stream copy
    (no re-encode, no quality loss, fast).

    It finds every "...\BDMV\STREAM" folder under -Root (handling both the
    "<Disc>\AVCHD\BDMV\STREAM" and "<Disc>\BDMV\STREAM" layouts) and writes one
    full-video.mp4 at the top of each disc folder.

.PARAMETER Root
    A folder containing one or more recovered disc folders (each with a BDMV\STREAM inside).
    Can also be a single disc folder.

.PARAMETER FfmpegPath
    Full path to ffmpeg.exe. See the README "Getting ffmpeg" section.

.PARAMETER OutputName
    Output file name placed at each disc's top folder. Default: full-video.mp4

.EXAMPLE
    .\Merge-AvchdClips.ps1 -Root "D:\recovered" -FfmpegPath "D:\ffmpeg\bin\ffmpeg.exe"

.NOTES
    Lossless: streams are copied (-c copy), never re-encoded. AVCHD is H.264 + AC-3, which
    .mp4 carries fine. If a player is fussy about AC-3, change the extension to .mkv.
    Corrupt-packet warnings are normal if a clip contains a recovered defect-band hole —
    ffmpeg resyncs past them.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][string]$FfmpegPath,
    [string]$OutputName = "full-video.mp4"
)

if (-not (Test-Path -LiteralPath $FfmpegPath)) { throw "ffmpeg not found at: $FfmpegPath" }
if (-not (Test-Path -LiteralPath $Root))       { throw "Root folder not found: $Root" }

$listDir = Join-Path $env:TEMP "cdscan-concat-lists"
New-Item -ItemType Directory -Force -Path $listDir | Out-Null

$streams = Get-ChildItem -LiteralPath $Root -Recurse -Directory -Filter STREAM -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -match '(?i)\\BDMV\\STREAM$' }

if (-not $streams) { Write-Warning "No '...\BDMV\STREAM' folders found under $Root"; return }

foreach ($s in $streams) {
    # Disc top folder: the directory just above AVCHD (if present), else just above BDMV.
    $bdmv = $s.Parent                                  # ...\BDMV
    $top  = if ($bdmv.Parent.Name -ieq 'AVCHD') { $bdmv.Parent.Parent } else { $bdmv.Parent }

    $clips = Get-ChildItem -LiteralPath $s.FullName -Filter *.MTS | Sort-Object Name
    if (-not $clips) { Write-Host "SKIP (no .MTS): $($s.FullName)"; continue }

    $list = Join-Path $listDir ("{0}.txt" -f $top.Name)
    $clips | ForEach-Object { "file '" + ($_.FullName -replace '\\','/') + "'" } |
        Set-Content -LiteralPath $list -Encoding ascii

    $out = Join-Path $top.FullName $OutputName
    Write-Host "=== $($top.Name): $($clips.Count) clips -> $out ==="

    & $FfmpegPath -hide_banner -loglevel error -fflags +genpts -f concat -safe 0 -i $list -c copy -y $out

    if (Test-Path -LiteralPath $out) {
        $gb  = (Get-Item -LiteralPath $out).Length / 1GB
        $dur = (& $FfmpegPath -hide_banner -i $out 2>&1 | Select-String 'Duration' | Select-Object -First 1)
        "  OK  {0:N2} GB  | {1}" -f $gb, ($dur.ToString().Trim())
    } else {
        Write-Warning "  FAILED — no output produced for $($top.Name)"
    }
    ""
}

Write-Host "Done."
