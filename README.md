# cd-scan

**Damaged CD / DVD / AVCHD disc recovery tool** — for discs where the files are still visible in Windows Explorer but a normal copy aborts on a bad sector, *and* for more damaged discs where the filesystem itself needs to be reconstructed.

It is built around one proven, ddrescue-style idea: **read everything the drive physically can, push through the parts it can't, and never abort.** Unreadable spots become same-length zero holes so file alignment is preserved, instead of the whole copy failing.

> Real-world result this tool was built for: a scratched AVCHD camcorder disc with one file (`00058.MTS`) that Windows refused to copy. `cd-scan` recovered **98.8%** of it (the rest was a single physical defect band the drive cannot read), and the file plays end-to-end with one sub-second glitch.

> ## 🐢 Recovery first, speed never
> **This tool optimizes for getting *all* your data back, not for finishing fast.** It will happily run for hours — that's expected, not a bug. Your irreplaceable footage is worth more than your afternoon.
>
> But "recovery first" is *not* the same as brute-forcing one bad sector forever. The smart strategy (which this tool follows) is: **secure every readable byte first, then chase the hard parts** with additive re-runs and a second drive. Every minute spent should *buy* recovered data — if grinding a sector isn't producing reads, change tactics (lower retries to move on, swap drives), don't just wait longer. See *How `copy-resilient` works* and the *Troubleshooting* FAQ.

---

## TL;DR — recover one stubborn file

The file is visible in Explorer but copy fails? This is the command you want:

```powershell
# Build once
dotnet build -c Release
$exe = ".\src\CdScan.ConsoleTest\bin\Release\net8.0\CdScan.ConsoleTest.exe"

# Resilient copy: <source> <dest> [logfile] [slowRetries] [fastBlockRetries]
& $exe copy-resilient "E:\path\on\disc\VIDEO.MTS" "D:\recovered\VIDEO.MTS" "D:\recovered\VIDEO.log"
```

- It opens the file **through the normal filesystem**, so it uses the disc's intact directory metadata — no LBA/partition math, no filesystem parsing needed.
- It copies in two stacked passes (see below), keeps going through bad sectors, and preserves the exact original file length.
- **Re-running is additive** — recovered sectors are kept; only the remaining holes are retried. Running it again **with the disc in a different drive** is the single most effective way to close a stubborn gap.

If a defect band is grinding too slowly, lower the per-sector retries to push past it and bank the readable data on the far side:

```powershell
# 4 retries/sector instead of the default 60 — confirms dead sectors fast, moves on
& $exe copy-resilient "E:\...\VIDEO.MTS" "D:\recovered\VIDEO.MTS" "D:\recovered\VIDEO.log" 4
```

---

## How `copy-resilient` works (two stacked passes)

```
PHASE 1  — fast map
  Read the whole file in large (64 KB) blocks with almost no retries.
  Good blocks are written immediately; any failing block is recorded as a
  "bad range" and skipped. Secures every easily-readable byte of the ENTIRE
  file quickly and produces a damage map.

PHASE 2  — deep grind
  Walk only the bad ranges sector-by-sector (2 KB) with heavy retries.
  Each recovered sector is written into its exact place. Sectors still dead
  after all retries are left as same-length zero holes so stream alignment
  is preserved.
```

Why this order matters: it banks the easy data across the *whole* file first (protected if the drive/disc degrades mid-recovery), then attacks the hard spots — instead of getting stuck for hours on the first defect while the rest of the file goes unread.

**Fill semantics:** unreadable sectors become zeros of the same length, never dropped. For MPEG-TS / AVCHD video this matters — the player resyncs at the next `0x47` packet boundary, so a hole is a brief glitch, not a destroyed file. Dropping bytes (shrinking the file) would shift every following packet and break the whole stream.

---

## Prerequisites

- **Windows** (uses Windows raw device APIs).
- **.NET 8 SDK** to build — https://dotnet.microsoft.com/download/dotnet/8.0
- **Administrator** terminal for raw-drive commands (recommended for all).
- **ffmpeg** — *optional, only for rejoining AVCHD clips* (step 2 of the workflow below). It is **not** needed for the actual file recovery. See *Getting ffmpeg* below.

## Build

Requires the **.NET 8 SDK** on Windows.

```powershell
dotnet build -c Release
```

The console tool lands at:

```
.\src\CdScan.ConsoleTest\bin\Release\net8.0\CdScan.ConsoleTest.exe
```

> **Run an elevated (Administrator) terminal** for the raw-drive commands (`list-files`, `mts-sizes`, `rescan`, `scan-video`, raw LBA reads). `copy-resilient` works through the filesystem and usually doesn't require elevation, but running elevated is recommended for the most reliable read path.

---

## Commands

Invoke as `CdScan.ConsoleTest.exe <command> ...`. For drive-letter commands, `E:` is the optical drive.

| Command | What it does |
|---|---|
| `LIST` | List detected optical drives |
| `copy-resilient <src> <dest> [log] [slowRetries] [fastBlockRetries]` | **Primary recovery.** Two-phase resilient file copy (see above). Additive across runs/drives. |
| `E: list-files` (or `ls`) | List files on the disc (tries ISO9660/Joliet, then UDF). |
| `E: find <name>` | Find a file and show its start LBA + size (ISO9660). |
| `E: mts-sizes [start] [count] [log] [resume] [filter]` | Brute-force scan for UDF File Identifier Descriptors + sizes. Effective on badly damaged UDF discs. |
| `E: scan-video <start> <count> [log]` | Scan a sector range for MPEG-TS video signatures (auto-resumes from log). |
| `E: full-video-scan [log]` | Scan the whole disc for video (with logging + auto-resume). |
| `E: rescan <start> <count> [out.raw]` | Aggressively hammer a raw LBA range (high retries), keep best data. |
| `E: rebuild-mts <size> <dataLba> <rec.raw> <out>` | Rebuild a `.MTS` from a recovered raw stream + UDF size/offset. |
| `E: <lba>` / `E: <start>-<end>` | Read a raw sector / test a sector range's success rate. |
| `RESCAN-TEST` | Self-test (no drive) using synthetic faults — verifies the rescan logic. |

> **Note on `rescan`:** its TS-sync heuristic checks `0x47` every **188** bytes (raw MPEG-TS). AVCHD `.MTS`/`.M2TS` actually uses **192**-byte packets (4-byte timecode prefix). For recovering a *known* file, prefer `copy-resilient`, which copies bytes faithfully and never filters by content. `rescan` is for blind scanning of heavily corrupted discs where the filesystem is gone.

---

## End-to-end AVCHD workflow (what this was built for)

AVCHD camcorders split one recording into numbered clips (`00000.MTS`, `00001.MTS`, …) under `…\AVCHD\BDMV\STREAM\`. To rescue a recording:

**1. Copy every clip off the disc.** Healthy clips copy normally in Explorer; for any that won't, use `copy-resilient`. For a stubborn defect band, re-run with the disc in a **different drive** (additive).

**2. Rejoin the clips into one continuous video.** A raw binary concatenation plays but has broken timing — each clip carries its own timestamp clock. Use ffmpeg's **concat demuxer** to rebuild one continuous timeline (lossless stream copy, no re-encode).

**The easy way** — the bundled script finds every `…\BDMV\STREAM` folder and writes a `full-video.mp4` at the top of each disc folder (handles both `AVCHD\BDMV\STREAM` and `BDMV\STREAM` layouts):

```powershell
.\scripts\Merge-AvchdClips.ps1 -Root "D:\recovered" -FfmpegPath "D:\ffmpeg\bin\ffmpeg.exe"
```

**The manual way** — one folder at a time:

```powershell
$ff = "C:\path\to\ffmpeg.exe"
$dir = "D:\recovered\<DiscName>\AVCHD\BDMV\STREAM"   # or ...\BDMV\STREAM on some discs
$list = "D:\recovered\concat-list.txt"

# Build the ordered list (zero-padded names sort correctly)
Get-ChildItem -LiteralPath $dir -Filter *.MTS | Sort-Object Name |
  ForEach-Object { "file '" + ($_.FullName -replace '\\','/') + "'" } |
  Set-Content -LiteralPath $list -Encoding ascii

# Lossless join with a clean, continuous timeline
& $ff -fflags +genpts -f concat -safe 0 -i $list -c copy "D:\recovered\<DiscName>\full-video.mp4"
```

The output plays as one video with correct duration and seeking. AVCHD streams are H.264 + AC-3; `.mp4` carries both fine (`.mkv` if a player is fussy about AC-3). Corrupt-packet warnings during the join are normal if a clip contains a recovered defect-band hole — ffmpeg resyncs past it.

### Getting ffmpeg

ffmpeg is a free, portable tool — no installation required, just an `ffmpeg.exe` you point the script at.

- **Download a Windows build:** https://www.gyan.dev/ffmpeg/builds/ (the "full" or "essentials" build) or https://ffmpeg.org/download.html. Unzip it anywhere; the executable is in the `bin\` folder, e.g. `D:\ffmpeg\...\bin\ffmpeg.exe`.
- **Or via a package manager:** `winget install Gyan.FFmpeg` (then `ffmpeg` is on your PATH and you can pass `-FfmpegPath ffmpeg`).

You only need ffmpeg for the clip-rejoin step. Recovering the files themselves (`copy-resilient`) needs nothing but the built tool.

## Troubleshooting / FAQ

- **"Access is denied" / can't open the drive** — run the terminal **as Administrator**. Raw-drive commands require it.
- **`copy-resilient` says "Source file not found"** — make sure the disc is in the drive and the path/drive letter is right (it's the path *as Explorer shows it*).
- **A recovery is grinding forever on one spot** — you've hit a physical defect band. Stop and re-run with fewer retries to push past it and bank the readable data: `copy-resilient <src> <dest> <log> 4`. Then retry the remaining holes **in a different drive** (additive). This is normal and expected on scratched discs.
- **It looks frozen / no output for a long time** — during deep grind the process is just waiting on the drive (low CPU, stale file timestamp is normal). It's working as long as gap log lines keep appearing. Don't kill it unless you're switching strategy.
- **The recovered video glitches/skips at one spot** — that's an unrecoverable defect filled with zeros (by design, to keep the rest playable). Try recovering that file again in a different drive to shrink or close the gap.
- **Merged video has wrong duration or won't seek** — you binary-concatenated instead of using the ffmpeg concat demuxer. Use `Merge-AvchdClips.ps1` (or the manual ffmpeg command); it rebuilds the timeline.
- **Merged `.mp4` has no sound in some player** — that player dislikes AC-3 in MP4. Re-run with a `.mkv` output, or transcode the audio.

---

## Architecture

```
WinForms UI (placeholder — not yet implemented)
        │
Recovery commands  (CdScan.ConsoleTest)
        │
VideoScanner.CopyFileResilient  ← primary, filesystem-level
        │                                  │
UDF / ISO9660 parsers              ISectorReader
(for damaged-metadata cases)               │
        └──────────── RetryingSectorReader ←── WindowsRawCdReader
                                              (or FaultyImageReader for tests)
```

- `ISectorReader` — the core abstraction (2048-byte sector reads).
- `WindowsRawCdReader` — opens `\\.\E:` and reads sectors via `CreateFile`/`ReadFile`/`SetFilePointerEx`.
- `RetryingSectorReader` / `RetryPolicy` — configurable retries + backoff around any reader.
- `VideoScanner` — `CopyFileResilient` (two-phase recovery), plus blind-scan/raw-rescan/rebuild helpers.
- `Iso9660Parser`, `Udf.*` — filesystem walkers for the harder cases (UDF directory walking is partial; the brute-force FID scan is the reliable path today).
- `OpticalDrive` — drive enumeration. `FaultyImageReader` — synthetic damage for tests.

The WinForms `CdScan.App` project is currently an empty form (placeholder for a future GUI).

---

## Safety

- The tool only **reads** from the optical drive.
- All recovered data is written to an output folder **you choose** on your hard drive. Never point output at the disc.
- `copy-resilient` is additive and non-destructive: it preserves previously recovered data and only re-fills remaining holes — safe to run repeatedly and across multiple drives.

---

## Known limitations

- **UDF directory walker is partial.** It reaches the File Set Descriptor; full directory traversal is a TODO. The brute-force FID scan (`mts-sizes`) is the dependable locator on damaged UDF discs.
- **UDF data-LBA extraction is approximate** (`rescan`/`rebuild-mts` path): the file-entry allocation descriptor is read from a fixed offset and is **partition-relative** (the partition base is not yet added). Prefer `copy-resilient` for files Explorer can still see.
- **`rescan` uses 188-byte TS framing**, while AVCHD is 192-byte — see note above.
- Some drives spend tens of seconds per failed read on defect bands; high retry counts can make a pass take hours. Tune `slowRetries` and try a different drive.

## Roadmap

1. ✅ Low-level raw reader + retries
2. ✅ Resilient filesystem-level file copy (`copy-resilient`, two-phase)
3. UDF directory walker + correct partition-relative data extents
4. Fix `rescan`/scan to 192-byte AVCHD framing; faithful (non-filtering) raw range copy
5. Usable WinForms UI ("pick drive → pick file → pick output → recover")
6. Single-file `.exe` + elevation manifest

## References

- Microsoft SPTI sample (deeper drive control): https://github.com/microsoft/windows-driver-samples/tree/main/storage/tools/spti
- GNU ddrescue (the strategy this tool mirrors): https://www.gnu.org/software/ddrescue/
