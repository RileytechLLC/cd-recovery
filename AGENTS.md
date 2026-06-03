# AGENTS.md

Guidance for AI agents and contributors working in this repo. Read this before driving a disc recovery or changing the code. It captures the strategy and the hard-won gotchas so you don't repeat them.

## What this project is

A Windows console tool (.NET 8) for recovering files from scratched/weak optical discs — especially **AVCHD camcorder discs** (UDF filesystem, `.MTS` clips under `…\BDMV\STREAM\`). The philosophy mirrors GNU ddrescue: **read everything readable, push through what isn't, never abort, never shrink the file.**

The hero feature is `VideoScanner.CopyFileResilient` (CLI: `copy-resilient`). Most real recoveries only need it.

## Repo layout

```
src/CdScan.Core/         Library
  ISectorReader.cs         Core abstraction (2048-byte sector reads)
  WindowsRawCdReader.cs    Raw \\.\E: reads via CreateFile/ReadFile/SetFilePointerEx
  RetryingSectorReader.cs  Retry + backoff wrapper; RetryPolicy
  VideoScanner.cs          CopyFileResilient (PRIMARY) + blind-scan/rescan/rebuild helpers
  CdReaderFactory.cs       Open helpers
  OpticalDrive.cs          Drive enumeration
  FaultyImageReader.cs     Synthetic faults for offline tests
  Iso9660/*.cs             ISO9660/Joliet walker
  Udf/*.cs                 UDF descriptors + brute-force FID scanner (dir walk is partial)
src/CdScan.ConsoleTest/  The actual CLI (Program.cs dispatches commands)
src/CdScan.App/          WinForms — empty placeholder, not implemented
scripts/                 Helper scripts
  Merge-AvchdClips.ps1     Rejoin .MTS clips per disc into full-video.mp4 (ffmpeg concat demuxer)
CdScan.slnx              Solution
```

## Build & test

```powershell
dotnet build -c Release
# Offline self-test of the rescan logic — no drive needed:
.\src\CdScan.ConsoleTest\bin\Release\net8.0\CdScan.ConsoleTest.exe RESCAN-TEST
```

There is no unit-test project yet. `RESCAN-TEST` + `FaultyImageReader` are the way to exercise recovery logic without a physical disc. If you add logic, add a synthetic-fault check.

Run an **elevated** terminal for raw-drive commands. `copy-resilient` goes through the filesystem and usually doesn't need elevation.

## Guiding principle: recovery first, speed never

Optimize for **completeness of recovery, not runtime.** Hours-long passes are acceptable; the user's data is irreplaceable. But this does **not** mean brute-forcing a single dead sector indefinitely — that can actually *reduce* total recovery by leaving the rest of the file unread. Recovery-first is achieved through *strategy*: secure all readable data first, map the damage, then chase hard spots with additive re-runs and a different drive. Time spent must buy recovered data; if it isn't, change tactics rather than wait. Never trade away recovery for speed, and never trade away recovery for the *appearance* of effort.

## The recovery playbook (follow this order)

1. **Is the file visible in Explorer and the metadata intact?** (Other files copy fine.) → Use `copy-resilient`. You do NOT need any filesystem parsing — the OS already knows where the file is.
2. **Run `copy-resilient <src> <dest> <log>`.** Watch the task output, not the file size (output is pre-allocated to full length up front).
3. **If it stalls on a defect band** (consecutive `[GAP]` lines, no `healed` writes for a long time, drive blocked tens of seconds per read): stop it and re-run with **fewer retries**, e.g. `... <log> 4`. This confirms dead sectors quickly and lets Phase 2 reach the readable data *beyond* the band. Don't burn hours at 60 retries on sectors failing 60/60 — they're physical defects.
4. **To actually recover a defect band, use a DIFFERENT physical drive.** Same command, same dest (additive — only holes are retried). Different optics often read through a scratch one drive can't. Gentle disc cleaning (wipe center→edge) helps too.
5. **Only if the filesystem metadata itself is damaged** (Explorer can't even see the file): fall back to the raw path — `mts-sizes` to locate, `rescan` to pull raw sectors, `rebuild-mts` to reassemble. See caveats below; this path is rougher.
6. **Rejoin AVCHD clips** with ffmpeg's concat demuxer (`-f concat -c copy`), not raw binary append, or timing/seeking breaks. See README's end-to-end workflow.

### Reading progress while a copy runs
- The process is **I/O-bound** during deep grind — low CPU and a stale "last write" time are NORMAL (gaps don't write). Check it's alive + that gap log lines are advancing; don't assume "hung."
- `copy-resilient` prints `phase2 progress: N/total bad sectors`. Gaps are flushed to the log file live (AutoFlush). The console/task output is the live signal.

## Critical invariants — do not break these

- **Never shrink the output.** Unreadable regions must be filled with the same number of bytes (zeros), at the same offset. Dropping bytes shifts every subsequent MPEG-TS packet and destroys the whole stream. `CopyFileResilient` enforces this via `SetLength` + writing at explicit offsets.
- **Stay additive.** `copy-resilient` opens the dest with `OpenOrCreate` (not `Create`) and never overwrites a recovered sector with zeros. This is what makes re-runs and multi-drive passes safe. Preserve this.
- **Reopen the source handle after an `IOException`.** Optical handles can be left in a bad state after a failed read; `TryReadAt` disposes and reopens. Keep that.
- **Read-only on the disc.** The tool must never write to the optical drive.

## Known gotchas (don't "rediscover" these)

- **AVCHD `.MTS` is 192-byte packets** (4-byte timecode prefix + 188), not 188. `VideoScanner`'s TS-sync heuristic (`rescan`, `ScanForTransportStream`) checks every 188 bytes — wrong for AVCHD and a known bug. It can zero out perfectly good sectors. For known files always use `copy-resilient` (faithful byte copy, no content filtering). Fixing scan to 192 framing is on the roadmap.
- **UDF data extents are partition-relative.** `Udf/FileEntry.cs` reads the first extent from a hardcoded offset (~168) and does NOT add the Partition Descriptor base — so `mts-sizes`'s "data at LBA N" is likely mis-located. The allocation descriptor isn't at a fixed offset either (File Entry: `176 + L_EA`; Extended File Entry: `216 + L_EA`). Treat raw-LBA results as approximate until this is fixed.
- **UDF directory walker is a stub** — it reaches the File Set Descriptor then stops (`UdfParser.GetAllFiles` returns empty). The dependable locator on damaged UDF discs is the brute-force FID scan (`mts-sizes`).
- **`find`/`list-files` use ISO9660**, which returns nothing on AVCHD/UDF discs.
- **Drives vary wildly in per-failed-read latency** (sub-second to ~90s). A high retry count × slow drive = a pass that takes many hours or days. Always offer/tune `slowRetries`.

## Style & conventions

- Match the surrounding C# style. Public APIs in `VideoScanner` take explicit params with sensible defaults; CLI parsing lives in `ConsoleTest/Program.cs`.
- New CLI commands: add a `case`/branch in `Program.cs`, update `PrintUsage()`, and document in `README.md`'s command table.
- Keep recovery output human-readable and resumable (log files, progress lines). Long operations should be safe to interrupt and re-run.
- This is not a git repo yet. If asked to publish: `git init`, confirm `.gitignore` excludes `bin/ obj/ recovered/ *.exe *.dll` (it does), and do not commit recovered media or `*.raw`/`*.log` artifacts.

## Good first improvements

1. A faithful raw `copy-range <drive> <startLba> <count> <out>` that writes bytes without TS filtering (the non-broken sibling of `rescan`).
2. Fix TS framing to 192 bytes (configurable) in `VideoScanner`.
3. Add partition-base resolution to the UDF path; finish the directory walker.
4. Wire the WinForms app: pick drive → pick file → pick output → call `CopyFileResilient` with a progress bar.
