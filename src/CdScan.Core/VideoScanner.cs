using System;
using System.IO;
using System.Threading;

namespace CdScan.Core;

/// <summary>
/// Tools for finding actual video data on damaged discs when the filesystem is heavily corrupted.
/// MPEG-2 Transport Stream (.MTS / .M2TS) packets are 188 bytes long and start with 0x47 sync byte.
/// </summary>
public static class VideoScanner
{
    /// <summary>
    /// Scans for MPEG-TS data with multiple sensitivity levels.
    /// Designed for long unattended runs on damaged discs.
    /// </summary>
    public static void ScanForTransportStream(
        ISectorReader reader,
        long startLba,
        long sectorCount,
        string? logFile = null,
        bool resume = false)
    {
        const int TS_PACKET_SIZE = 188;
        long lastReport = 0;
        const long reportInterval = 50_000;

        long currentLba = startLba;
        long endLba = startLba + sectorCount;

        // === RESUME SUPPORT ===
        if (resume && !string.IsNullOrWhiteSpace(logFile) && File.Exists(logFile))
        {
            try
            {
                var lines = File.ReadAllLines(logFile);
                long lastSeenLba = -1;

                // Look for Progress lines first
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    if (lines[i].Contains("Progress:"))
                    {
                        foreach (var token in lines[i].Split(' ', '/', ','))
                        {
                            string clean = token.Replace(",", "").Trim();
                            if (long.TryParse(clean, out long val) && val > lastSeenLba)
                                lastSeenLba = val;
                        }
                        break;
                    }
                }

                // Also check the last few hit lines
                if (lastSeenLba < 0)
                {
                    for (int i = lines.Length - 1; i >= Math.Max(0, lines.Length - 30); i--)
                    {
                        if (lines[i].Contains("LBA ") && (lines[i].Contains("STRONG") || lines[i].Contains("MEDIUM") || lines[i].Contains("WEAK")))
                        {
                            var parts = lines[i].Split(' ');
                            foreach (var p in parts)
                            {
                                if (long.TryParse(p, out long val) && val > lastSeenLba)
                                    lastSeenLba = val;
                            }
                        }
                    }
                }

                if (lastSeenLba > currentLba)
                {
                    currentLba = lastSeenLba + 1;
                    Console.WriteLine($"[VideoScanner] Resuming scan from LBA {currentLba} (parsed from log)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VideoScanner] Warning: Could not resume from log: {ex.Message}");
            }
        }

        int strongHits = 0;
        int mediumHits = 0;
        int weakHits = 0;

        StreamWriter? logWriter = null;
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            logWriter = new StreamWriter(logFile, append: true);
            logWriter.WriteLine($"=== Video scan started at {DateTime.Now} | Range: {startLba} - {endLba} ===");
            logWriter.WriteLine("Format: [LEVEL] LBA <lba> : <sync count> sync packets");
        }

        Console.WriteLine($"[VideoScanner] Starting TS scan from LBA {startLba} for {sectorCount:N0} sectors...");
        if (logWriter != null)
            Console.WriteLine($"[VideoScanner] Logging all hits to: {Path.GetFullPath(logFile!)}");

        int consecutiveReadFailures = 0;
        int lastWeakReport = 0;

        while (currentLba < endLba)
        {
            if (!reader.TryReadSector(currentLba, out byte[]? sector, out _))
            {
                consecutiveReadFailures++;
                currentLba++;

                if (consecutiveReadFailures > 2000)
                {
                    string msg = $"[VideoScanner] Too many consecutive read failures. Reached end of readable area around LBA {currentLba}. Stopping.";
                    Console.WriteLine(msg);
                    logWriter?.WriteLine(msg);
                    break;
                }
                continue;
            }

            consecutiveReadFailures = 0;

            int syncCount = 0;
            for (int offset = 0; offset <= sector.Length - TS_PACKET_SIZE; offset += TS_PACKET_SIZE)
            {
                if (sector[offset] == 0x47)
                    syncCount++;
            }

            string? level = null;

            if (syncCount >= 8)
            {
                strongHits++;
                level = "STRONG";
            }
            else if (syncCount >= 3)
            {
                mediumHits++;
                level = "MEDIUM";
            }
            else if (syncCount >= 1)
            {
                weakHits++;
                if (weakHits - lastWeakReport >= 30)
                {
                    level = "WEAK";
                    lastWeakReport = weakHits;
                }
            }

            if (level != null)
            {
                string msg = $"[{level}] LBA {currentLba} : {syncCount} sync packets";
                Console.WriteLine($"[VideoScanner] {msg}");
                logWriter?.WriteLine(msg);
            }

            currentLba++;

            if (currentLba - lastReport >= reportInterval)
            {
                double percent = (currentLba - startLba) * 100.0 / sectorCount;
                Console.WriteLine($"[VideoScanner] Progress: {currentLba - startLba:N0} / {sectorCount:N0} sectors ({percent:F1}%) | S:{strongHits} M:{mediumHits} W:{weakHits}");
                lastReport = currentLba;
            }
        }

        string summary = $"Scan complete. Strong: {strongHits}, Medium: {mediumHits}, Weak signals: {weakHits}";
        Console.WriteLine($"[VideoScanner] {summary}");
        logWriter?.WriteLine(summary);
        logWriter?.WriteLine($"=== Finished at {DateTime.Now} ===");
        logWriter?.Dispose();
    }

    /// <summary>
    /// Aggressively re-scans a region that previously showed video data.
    /// Hammers each sector with many retries and keeps the best version (highest TS sync count).
    /// This is the "rescan the hell out of fucked up spots" function.
    /// </summary>
    public static void AggressiveRescanRegion(
        ISectorReader baseReader,
        long startLba,
        long sectorCount,
        string? outputFile = null,
        int maxRetriesPerSector = 40)
    {
        const int TS_PACKET_SIZE = 188;
        const int DelayMs = 100;

        Console.WriteLine($"[AggressiveRescan] Starting HEAVY recovery on LBA {startLba} -> {startLba + sectorCount - 1}");
        Console.WriteLine($"[AggressiveRescan] Up to {maxRetriesPerSector} read attempts per sector (best data kept)...");

        int sectorsWithData = 0;
        int totalBestSyncs = 0;

        Stream? outStream = null;
        if (!string.IsNullOrWhiteSpace(outputFile))
            outStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write);

        for (long i = 0; i < sectorCount; i++)
        {
            long lba = startLba + i;
            int bestSync = -1;
            byte[]? bestData = null;
            int failsInARow = 0;
            const int MaxConsecutiveFailsBeforeBail = 8;

            for (int attempt = 1; attempt <= maxRetriesPerSector; attempt++)
            {
                if (!baseReader.TryReadSector(lba, out byte[]? data, out _))
                {
                    failsInARow++;
                    if (attempt < maxRetriesPerSector && DelayMs > 0)
                        Thread.Sleep(DelayMs * Math.Min(attempt, 8)); // modest backoff, capped
                    if (bestSync <= 0 && failsInARow >= MaxConsecutiveFailsBeforeBail)
                        break; // this sector is consistently dead, no point hammering more
                    continue;
                }

                failsInARow = 0;

                int syncs = 0;
                for (int off = 0; off <= data!.Length - TS_PACKET_SIZE; off += TS_PACKET_SIZE)
                    if (data[off] == 0x47) syncs++;

                if (syncs > bestSync)
                {
                    bestSync = syncs;
                    bestData = data;
                }

                if (syncs >= 10) break;
            }

            if (bestData != null && bestSync > 0)
            {
                sectorsWithData++;
                totalBestSyncs += bestSync;

                outStream?.Write(bestData, 0, bestData.Length);

                string level = bestSync >= 8 ? "STRONG" : (bestSync >= 3 ? "MEDIUM" : "WEAK");
                Console.WriteLine($"[AggressiveRescan] LBA {lba} -> best {bestSync} syncs ({level})");

                // For very damaged areas, do extra aggressive passes on weak hits to try for better data
                if (bestSync <= 2)
                {
                    Console.WriteLine($"[AggressiveRescan]    Weak hit detected, doing extra pass with higher retries...");
                    int extraBest = bestSync;
                    byte[]? extraBestData = bestData;
                    int extraAttempts = 20; // enough extra effort on marginals without excessive time
                    int extraDelay = 60;

                    Console.Write("    [extra ");
                    for (int extra = 1; extra <= extraAttempts; extra++)
                    {
                        if (!baseReader.TryReadSector(lba, out byte[]? extraData, out _))
                        {
                            if (extra < extraAttempts && extraDelay > 0)
                                Thread.Sleep(extraDelay);
                            if (extra % 5 == 0) Console.Write(".");
                            continue;
                        }

                        int extraSyncs = 0;
                        for (int off = 0; off <= extraData!.Length - TS_PACKET_SIZE; off += TS_PACKET_SIZE)
                            if (extraData[off] == 0x47) extraSyncs++;

                        if (extraSyncs > extraBest)
                        {
                            extraBest = extraSyncs;
                            extraBestData = extraData;
                        }

                        if (extraSyncs >= 10) break;

                        if (extra % 5 == 0) Console.Write(".");
                    }
                    Console.WriteLine("]");

                    if (extraBest > bestSync)
                    {
                        Console.WriteLine($"[AggressiveRescan]    -> Improved from {bestSync} to {extraBest} syncs with extra pass!");
                        if (outStream != null && extraBestData != null)
                        {
                            // Overwrite the just-written weak sector with the better one we found
                            outStream.Seek(-2048, SeekOrigin.Current);
                            outStream.Write(extraBestData, 0, 2048);
                        }
                    }
                }
            }
            else
            {
                if (outStream != null)
                {
                    byte[] zeros = new byte[2048];
                    outStream.Write(zeros, 0, 2048);
                }
                Console.WriteLine($"[AggressiveRescan] LBA {lba} -> completely unrecoverable");
            }

            if ((i + 1) % 50 == 0)
            {
                Console.WriteLine($"[AggressiveRescan] Progress: {i + 1}/{sectorCount} | Recovered with data: {sectorsWithData}");
            }
        }

        outStream?.Dispose();

        Console.WriteLine($"[AggressiveRescan] Done. {sectorsWithData}/{sectorCount} sectors contained recoverable video data.");
        if (outputFile != null)
            Console.WriteLine($"[AggressiveRescan] Raw recovered stream written to: {outputFile}");
    }

    /// <summary>
    /// Resilient file-level copy for a file that is visible in Explorer but whose normal copy
    /// aborts on a bad sector. Opens the file through the normal filesystem (so it uses the disc's
    /// intact UDF metadata — no LBA/partition math required).
    ///
    /// Two stacked passes (ddrescue-style):
    ///   PHASE 1 (fast map): read the whole file in large blocks with almost no retries. Good blocks
    ///     are written immediately; any block that fails is recorded as a bad range and skipped. This
    ///     secures every easily-readable byte of the entire file quickly and produces a damage map.
    ///   PHASE 2 (deep grind): walk only the bad ranges sector-by-sector with heavy retries. Each
    ///     2KB sector recovered is written into its exact place; sectors still dead after all retries
    ///     are left as same-length zero holes so stream alignment is preserved (players resync at the
    ///     next MPEG-TS 0x47 sync — a brief glitch, not a destroyed file).
    ///
    /// Additive across runs (OpenOrCreate): previously recovered sectors are preserved; re-running, or
    /// running again with the disc in a different drive, only retries the remaining holes.
    /// </summary>
    public static void CopyFileResilient(
        string sourcePath,
        string destPath,
        string? logFile = null,
        int blockSize = 64 * 1024,
        int fastPassBlockRetries = 1,
        int slowPassSectorRetries = 60)
    {
        const int SectorSize = 2048;
        if (blockSize % SectorSize != 0)
            blockSize = (blockSize / SectorSize + 1) * SectorSize;

        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Source file not found (is the disc in the drive and the path correct?)", sourcePath);

        var destDir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        FileStream OpenSrc() => new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        FileStream src = OpenSrc();
        long length = src.Length;

        StreamWriter? log = null;
        if (!string.IsNullOrWhiteSpace(logFile))
        {
            log = new StreamWriter(logFile, append: true) { AutoFlush = true };
            log.WriteLine($"=== Resilient copy started {DateTime.Now} ===");
            log.WriteLine($"Source: {sourcePath}");
            log.WriteLine($"Dest:   {destPath}");
            log.WriteLine($"Size:   {length:N0} bytes ({length / (1024 * 1024)} MB)");
        }

        Console.WriteLine($"[ResilientCopy] {Path.GetFileName(sourcePath)} -> {destPath}");
        Console.WriteLine($"[ResilientCopy] Size: {length:N0} bytes ({length / (1024 * 1024)} MB). Block {blockSize / 1024} KB.");

        long recovered = 0;
        long stillBad = 0;
        int gapCount = 0;

        byte[] block = new byte[blockSize];
        byte[] sectorBuf = new byte[SectorSize];

        // Reads exactly 'count' bytes at 'offset'. On IOException the optical handle can be left in a
        // bad state, so we dispose and reopen the source before reporting failure.
        bool TryReadAt(long offset, byte[] buf, int count)
        {
            try
            {
                src.Seek(offset, SeekOrigin.Begin);
                int total = 0;
                while (total < count)
                {
                    int n = src.Read(buf, total, count - total);
                    if (n == 0) return false; // unexpected short read / EOF
                    total += n;
                }
                return true;
            }
            catch (IOException)
            {
                try { src.Dispose(); } catch { }
                src = OpenSrc();
                return false;
            }
        }

        // Bad ranges discovered in phase 1, coalesced into contiguous spans.
        var badRanges = new List<(long Offset, long Length)>();
        void AddBad(long off, long len)
        {
            if (badRanges.Count > 0)
            {
                var last = badRanges[^1];
                if (last.Offset + last.Length == off) { badRanges[^1] = (last.Offset, last.Length + len); return; }
            }
            badRanges.Add((off, len));
        }

        // OpenOrCreate (not Create) so re-runs are additive: sectors recovered on a previous run are
        // preserved, and only the still-dead spots are retried.
        using (var dest = new FileStream(destPath, FileMode.OpenOrCreate, FileAccess.ReadWrite))
        {
            bool resuming = dest.Length > 0;
            dest.SetLength(length); // preserves existing bytes; zero-fills any extension
            if (resuming)
            {
                Console.WriteLine("[ResilientCopy] Existing output found — preserving prior data, only re-filling holes.");
                log?.WriteLine("Resuming: preserving previously recovered sectors.");
            }

            // ===== PHASE 1: fast pass over the whole file, map bad blocks =====
            Console.WriteLine("[ResilientCopy] PHASE 1 — fast pass (mapping readable data + damage)...");
            log?.WriteLine($"--- Phase 1 fast pass at {DateTime.Now} ---");
            long pos = 0;
            long lastReport = 0;
            while (pos < length)
            {
                int want = (int)Math.Min(blockSize, length - pos);
                bool ok = false;
                for (int attempt = 1; attempt <= fastPassBlockRetries; attempt++)
                    if (TryReadAt(pos, block, want)) { ok = true; break; }

                if (ok)
                {
                    dest.Seek(pos, SeekOrigin.Begin);
                    dest.Write(block, 0, want);
                    recovered += want;
                }
                else
                {
                    AddBad(pos, want);
                    Console.WriteLine($"[ResilientCopy]   bad block @ {pos:N0} (+{want}) — deferring to phase 2");
                }

                pos += want;
                if (pos - lastReport >= 16 * 1024 * 1024 || pos >= length)
                {
                    double pct = length > 0 ? pos * 100.0 / length : 100.0;
                    Console.WriteLine($"[ResilientCopy]   phase1 {pos / (1024 * 1024):N0}/{length / (1024 * 1024):N0} MB ({pct:F1}%) | readable {recovered / (1024 * 1024):N0} MB, bad ranges {badRanges.Count}");
                    lastReport = pos;
                }
            }

            long badBytes = badRanges.Sum(r => r.Length);
            long badSectors = badRanges.Sum(r => (r.Length + SectorSize - 1) / SectorSize);
            Console.WriteLine($"[ResilientCopy] PHASE 1 done. Readable now: {recovered:N0} bytes. Damage map: {badRanges.Count} range(s), {badBytes:N0} bytes (~{badSectors:N0} sectors) to deep-grind.");
            log?.WriteLine($"Phase 1 done. Readable {recovered:N0} bytes. {badRanges.Count} bad range(s), {badBytes:N0} bytes.");
            foreach (var r in badRanges)
            {
                string m = $"  bad range: offset {r.Offset:N0} length {r.Length:N0}";
                Console.WriteLine($"[ResilientCopy] {m}");
                log?.WriteLine(m);
            }

            // ===== PHASE 2: deep per-sector grind over just the bad ranges =====
            if (badRanges.Count > 0)
            {
                Console.WriteLine($"[ResilientCopy] PHASE 2 — deep grind, up to {slowPassSectorRetries} retries/sector. This is the slow part (can run for hours).");
                log?.WriteLine($"--- Phase 2 deep grind at {DateTime.Now} ({slowPassSectorRetries} retries/sector) ---");

                long sectorsDone = 0;
                foreach (var range in badRanges)
                {
                    long end = range.Offset + range.Length;
                    for (long so = range.Offset; so < end; so += SectorSize)
                    {
                        int scount = (int)Math.Min(SectorSize, length - so);
                        bool ok = false;
                        for (int attempt = 1; attempt <= slowPassSectorRetries; attempt++)
                        {
                            if (TryReadAt(so, sectorBuf, scount)) { ok = true; break; }
                            Thread.Sleep(Math.Min(attempt, 10) * 15); // light backoff; let the drive re-seek
                        }

                        if (ok)
                        {
                            dest.Seek(so, SeekOrigin.Begin);
                            dest.Write(sectorBuf, 0, scount);
                            recovered += scount;
                            Console.WriteLine($"[ResilientCopy]   healed sector @ {so:N0}");
                            log?.WriteLine($"[HEAL] offset {so:N0} length {scount}");
                        }
                        else
                        {
                            // Leave as-is (zero on a fresh file, or prior-run data). Same length = alignment preserved.
                            stillBad += scount;
                            gapCount++;
                            string m = $"[GAP] offset {so:N0} length {scount} (unreadable after {slowPassSectorRetries} tries)";
                            Console.WriteLine($"[ResilientCopy]   {m}");
                            log?.WriteLine(m);
                        }

                        sectorsDone++;
                        if (sectorsDone % 16 == 0)
                            Console.WriteLine($"[ResilientCopy]   phase2 progress: {sectorsDone:N0}/{badSectors:N0} bad sectors | healed so far, {gapCount} still dead");
                    }
                }
            }
        }

        src.Dispose();

        string summary = $"Done. Recovered {recovered:N0} of {length:N0} bytes ({recovered * 100.0 / Math.Max(1, length):F3}%). {gapCount} sector(s) still unreadable ({stillBad:N0} bytes).";
        Console.WriteLine($"[ResilientCopy] {summary}");
        Console.WriteLine($"[ResilientCopy] Output: {destPath}");
        if (gapCount == 0)
            Console.WriteLine("[ResilientCopy] No gaps — full file recovered. Test it in VLC.");
        else
            Console.WriteLine("[ResilientCopy] Some sectors are physically unreadable on this drive. Re-running (especially with the disc in a DIFFERENT drive) is additive and may fill more. Then test in VLC.");
        log?.WriteLine(summary);
        log?.WriteLine($"=== Finished {DateTime.Now} ===");
        log?.Dispose();
    }

    /// <summary>
    /// Rebuild a .MTS file using the original size from UDF metadata and recovered raw sectors from a specific data LBA.
    /// This patches the recovered data into the correct place in a zero-filled file of the right size.
    /// Example file: Feb 12 PM -13\AVCHD\BDMV\STREAM\00058.MTS
    /// </summary>
    public static void RebuildMtsFromRecoveredSectors(
        long originalFileSize,
        long dataStartLba,
        string recoveredRawPath,
        string outputPath)
    {
        if (!File.Exists(recoveredRawPath))
            throw new FileNotFoundException("Recovered raw file not found", recoveredRawPath);

        Console.WriteLine($"Rebuilding {Path.GetFileName(outputPath)}...");
        Console.WriteLine($"  Original size : {originalFileSize} bytes");
        Console.WriteLine($"  Data starts at LBA {dataStartLba}");

        using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
        {
            output.SetLength(originalFileSize);

            long byteOffset = (dataStartLba * 2048);
            if (byteOffset < 0 || byteOffset >= originalFileSize)
            {
                Console.WriteLine("Error: dataStartLba leads to invalid offset for the file size.");
                return;
            }

            using (var recovered = new FileStream(recoveredRawPath, FileMode.Open, FileAccess.Read))
            {
                long maxLen = Math.Min(recovered.Length, originalFileSize - byteOffset);
                byte[] buffer = new byte[4096];
                int read;
                long written = 0;
                recovered.Position = 0;
                output.Position = byteOffset;
                while (written < maxLen && (read = recovered.Read(buffer, 0, (int)Math.Min(buffer.Length, maxLen - written))) > 0)
                {
                    output.Write(buffer, 0, read);
                    written += read;
                }
            }
        }

        Console.WriteLine($"Done. Rebuilt file written to: {outputPath}");
        Console.WriteLine("Note: Unrecovered sectors are zero-filled. Expect jumps/corruption in damaged areas. Test in VLC or your editor.");
    }
}
