// CdScan.ConsoleTest — low-level CD reader + filesystem inspection tool
// Focused on recovering data from damaged discs.

using CdScan.Core;
using CdScan.Core.Iso9660;

if (args.Length == 0)
{
    PrintUsage();
    return;
}

string firstArg = args[0].ToUpperInvariant();

try
{
    if (firstArg == "LIST")
    {
        var drives = OpticalDrive.Enumerate();
        Console.WriteLine("Detected optical drives:");
        if (drives.Count == 0)
            Console.WriteLine("  (none)");
        foreach (var d in drives)
            Console.WriteLine($"  {d.DisplayName}");
        return;
    }

    if (firstArg == "RESCAN-TEST")
    {
        // Self-test for AggressiveRescan fix: exercises unrecoverable + weak + extra pass paths using synthetic faults. No drive needed.
        // The output "rescan-test.raw" is written to cwd (project dir) and left for inspection.
        string img = Path.GetTempFileName();
        string rawOut = "rescan-test.raw";
        if (File.Exists(rawOut)) File.Delete(rawOut);
        try
        {
            const int SECTOR = 2048;
            const int COUNT = 12;
            byte[] imgData = new byte[SECTOR * COUNT];
            // Seed patterns: vary sync counts. Bad LBAs will be injected as faults (no data).
            for (int s = 0; s < COUNT; s++)
            {
                int baseOff = s * SECTOR;
                int syncsWanted = (s % 5 == 0) ? 1 : (s % 3 == 0 ? 4 : 11); // mix weak / medium / strong
                for (int p = 0; p < syncsWanted && p * 188 < SECTOR; p++)
                    imgData[baseOff + p * 188] = 0x47;
                // fill rest with noise
                for (int j = 1; j < SECTOR; j++) if (imgData[baseOff + j] == 0) imgData[baseOff + j] = (byte)(j & 0x7F);
            }
            File.WriteAllBytes(img, imgData);

            var bad = new HashSet<long> { 2, 5, 6, 10 }; // these will be "completely unrecoverable"
            var faulty = new FaultyImageReader(img, bad);

            Console.WriteLine("=== RESCAN-TEST starting (synthetic bad sectors) ===");
            VideoScanner.AggressiveRescanRegion(faulty, 0, COUNT, rawOut, maxRetriesPerSector: 50);
            Console.WriteLine("=== RESCAN-TEST finished ===");

            // Verify the written raw: bad LBAs must be all zeros, good LBAs must match the original pattern we embedded
            bool verified = true;
            byte[] rec = File.ReadAllBytes(rawOut);
            for (int s = 0; s < COUNT; s++)
            {
                int off = s * SECTOR;
                bool isBad = bad.Contains(s);
                if (isBad)
                {
                    bool allZero = true;
                    for (int j = 0; j < SECTOR; j++) if (rec[off + j] != 0) { allZero = false; break; }
                    if (!allZero) { verified = false; Console.WriteLine($"  VERIFY FAIL: LBA {s} should be zeros"); }
                }
                else
                {
                    bool match = true;
                    for (int j = 0; j < SECTOR; j++) if (rec[off + j] != imgData[off + j]) { match = false; break; }
                    if (!match) { verified = false; Console.WriteLine($"  VERIFY FAIL: LBA {s} data mismatch"); }
                }
            }
            Console.WriteLine($"Output raw: {rawOut} (len={rec.Length})");
            Console.WriteLine(verified ? "RESCAN-TEST: OK (no hang on unrecoverables + data verified)" : "RESCAN-TEST: PARTIAL (data verify failed)");
        }
        finally
        {
            try { File.Delete(img); } catch { }
            // rawOut ("rescan-test.raw") is intentionally left in cwd for inspection
        }
        return;
    }

    if (firstArg == "COPY-RESILIENT" || firstArg == "COPY")
    {
        // Resilient file-level copy: works on the file path the OS already resolved (uses intact UDF
        // metadata, no LBA math). Recovers everything readable; only truly dead 2KB sectors become gaps.
        if (args.Length < 3)
        {
            Console.WriteLine("Usage: copy-resilient <sourceFile> <destFile> [logfile] [slowRetries] [fastBlockRetries]");
            Console.WriteLine("  slowRetries      = retries/sector in the deep-grind phase (default 60; use ~4 to push past unreadable defect clusters)");
            Console.WriteLine("  fastBlockRetries = retries/block in the fast mapping phase (default 1)");
            Console.WriteLine("Example: copy-resilient \"E:\\Feb 12 PM -13\\AVCHD\\BDMV\\STREAM\\00058.MTS\" \"D:\\recovered\\00058.MTS\" D:\\00058-copy.log 4");
            return;
        }
        string copySource = args[1];
        string copyDest = args[2];
        string? copyLog = args.Length >= 4 ? args[3] : null;
        int slowRetries = 60;
        if (args.Length >= 5 && int.TryParse(args[4], out int sr) && sr > 0) slowRetries = sr;
        int fastBlockRetries = 1;
        if (args.Length >= 6 && int.TryParse(args[5], out int fr) && fr > 0) fastBlockRetries = fr;
        VideoScanner.CopyFileResilient(copySource, copyDest, copyLog,
            fastPassBlockRetries: fastBlockRetries, slowPassSectorRetries: slowRetries);
        return;
    }

    // First argument is a drive letter
    string driveLetter = firstArg.TrimEnd(':');

    if (args.Length < 2)
    {
        Console.WriteLine("Error: Missing subcommand or LBA after drive letter.");
        PrintUsage();
        return;
    }

    string subCommand = args[1].ToUpperInvariant();

    using var reader = CdReaderFactory.OpenDefault(driveLetter);

    switch (subCommand)
    {
        case "LIST-FILES":
        case "LS":
            Console.WriteLine($"Scanning filesystem on {driveLetter}: (this may take a moment on damaged discs)...");

            // Try ISO9660/Joliet first (fast path for many discs)
            var files = Iso9660Parser.GetAllFiles(reader);

            // If ISO didn't find anything useful, try UDF (very common for AVCHD camcorder discs)
            if (files.Count == 0)
            {
                Console.WriteLine("[Parser] ISO9660/Joliet not found or failed (common on AVCHD discs) — trying UDF...");
                files = CdScan.Core.Udf.UdfParser.GetAllFiles(reader);
            }

            var mtsFiles = files.Where(f => f.Path.EndsWith(".MTS", StringComparison.OrdinalIgnoreCase)).ToList();
            Console.WriteLine($"\nFound {files.Count} total entries. {mtsFiles.Count} .MTS video files:");

            foreach (var f in mtsFiles.OrderBy(f => f.Path))
            {
                string size = f.Length > 10 * 1024 * 1024
                    ? $"{f.Length / (1024 * 1024)} MB"
                    : $"{f.Length / 1024} KB";
                Console.WriteLine($"  {f.Path,-50}  LBA {f.StartLba,8}  Size: {size}");
            }
            break;

        case "FIND":
        case "FIND-FILE":
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: CdScan.ConsoleTest.exe E: find 00058.MTS   (e.g. Feb 12 PM -13\\AVCHD\\BDMV\\STREAM\\00058.MTS)");
                return;
            }
            string searchName = args[2];
            var allFiles = Iso9660Parser.GetAllFiles(reader);
            var matches = allFiles.Where(f => f.Path.Contains(searchName, StringComparison.OrdinalIgnoreCase)).ToList();

            if (matches.Count == 0)
            {
                Console.WriteLine($"No files matching '{searchName}' found on the disc.");
            }
            else
            {
                Console.WriteLine($"Found {matches.Count} match(es):");
                foreach (var m in matches)
                {
                    Console.WriteLine($"  {m.Path}");
                    Console.WriteLine($"    Start LBA : {m.StartLba}");
                    Console.WriteLine($"    Length    : {m.Length} bytes ({m.Length / (1024 * 1024)} MB)");
                    Console.WriteLine($"    Is Dir    : {m.IsDirectory}");
                }
            }
            break;

        case "BROAD-SCAN":
        case "SCAN":
            long startSector = 0;
            long sectorCount = 2_000_000;

            if (args.Length >= 3)
                long.TryParse(args[2], out startSector);
            if (args.Length >= 4)
                long.TryParse(args[3], out sectorCount);

            Console.WriteLine($"[Broad Scan] Starting broad FID scan from sector {startSector} for {sectorCount:N0} sectors...");
            Console.WriteLine("[Broad Scan] Looking for .MTS references. Progress every 100k sectors.");

            var broadHits = CdScan.Core.Udf.VolumeDescriptorSequence.ScanForFileIdentifierDescriptorsWithSize(
                reader,
                ".MTS",
                startLba: startSector,
                maxSectors: sectorCount,
                progressCallback: (cur, tot) =>
                {
                    double pct = cur * 100.0 / tot;
                    Console.WriteLine($"[Broad Scan] Progress: {cur:N0} / {tot:N0} sectors ({pct:F1}%)");
                });

            Console.WriteLine($"\n[Broad Scan] Completed. Found {broadHits.Count} matches.");
            foreach (var hit in broadHits)
            {
                Console.WriteLine($"  LBA {hit.FidLba,10} : {hit.Filename} (size: {hit.Size})");
            }
            break;

        case "SCAN-VIDEO":
        case "FIND-VIDEO":
            long videoStart = 0;
            long videoCount = 500_000;
            string? logFile = null;
            bool resume = false;

            if (args.Length >= 3)
                long.TryParse(args[2], out videoStart);
            if (args.Length >= 4)
                long.TryParse(args[3], out videoCount);
            if (args.Length >= 5)
                logFile = args[4];
            if (args.Length >= 6 && args[5].ToLower() == "resume")
                resume = true;

            // Auto-resume if log file exists and user didn't explicitly say no
            if (!resume && !string.IsNullOrEmpty(logFile) && File.Exists(logFile))
            {
                Console.WriteLine($"[VideoScanner] Log file exists. Auto-resuming from last position...");
                resume = true;
            }

            CdScan.Core.VideoScanner.ScanForTransportStream(reader, videoStart, videoCount, logFile, resume);
            break;

        case "FULL-VIDEO-SCAN":
            // Convenient full disc video scan with logging + auto-resume
            string defaultLog = $"D:\\cd-video-scan-{DateTime.Now:yyyyMMdd-HHmmss}.log";
            string logPath = args.Length >= 2 ? args[1] : defaultLog;

            bool autoResume = File.Exists(logPath);
            if (autoResume)
                Console.WriteLine($"[Full Video Scan] Log exists — resuming from last position...");

            Console.WriteLine("[Full Video Scan] Scanning entire disc for MPEG-TS data with logging...");
            CdScan.Core.VideoScanner.ScanForTransportStream(reader, 0, 4_500_000, logPath, autoResume);
            break;

        case "AGGRESSIVE-RESCAN":
        case "RESCAN":
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: E: rescan <startLBA> <sectorCount> [output.raw]");
                Console.WriteLine("Example: E: rescan 1900420 100 D:\\rescantest-1900.raw  (for 00058.MTS bad spot)");
                return;
            }

            long rescanStart = long.Parse(args[2]);
            long rescanCount = long.Parse(args[3]);
            string? rescanOut = args.Length >= 5 ? args[4] : null;

            // For aggressive rescan we want direct control over the many read attempts (no outer retry wrapper multiplying work on dead sectors).
            using (var rawReader = WindowsRawCdReader.Open(driveLetter))
            {
                CdScan.Core.VideoScanner.AggressiveRescanRegion(rawReader, rescanStart, rescanCount, rescanOut, maxRetriesPerSector: 50);
            }
            break;

        case "REBUILD-MTS":
            if (args.Length < 5)
            {
                Console.WriteLine("Usage: rebuild-mts <size-bytes> <data-start-lba> <recovered.raw> <output.mts>");
                Console.WriteLine("Example (use size and dataLba from mts-sizes): rebuild-mts 245678912 1899000 D:\\rescantest-1900.raw \"D:\\Feb 12 PM -13\\AVCHD\\BDMV\\STREAM\\00058.MTS\"");
                return;
            }

            long rebuildSize = long.Parse(args[2]);
            long dataLba = long.Parse(args[3]);
            string recoveredRaw = args[4];
            string rebuiltOut = args[5];

            CdScan.Core.VideoScanner.RebuildMtsFromRecoveredSectors(rebuildSize, dataLba, recoveredRaw, rebuiltOut);
            break;

        case "LIST-STREAM-SIZES":
        case "MTS-SIZES":
            long mtsStart = 0;
            long mtsCount = 2_500_000;
            string? mtsLog = null;
            bool mtsResume = false;

            if (args.Length >= 3)
                long.TryParse(args[2], out mtsStart);
            if (args.Length >= 4)
                long.TryParse(args[3], out mtsCount);
            if (args.Length >= 5)
                mtsLog = args[4];
            if (args.Length >= 6 && args[5].ToLower() == "resume")
                mtsResume = true;

            string mtsContains = ".MTS";
            if (args.Length >= 7)
                mtsContains = args[6];

            if (!mtsResume && !string.IsNullOrEmpty(mtsLog) && File.Exists(mtsLog))
            {
                Console.WriteLine("[Udf] Log exists, auto-resuming...");
                mtsResume = true;
            }

            Console.WriteLine($"[Udf] Scanning for files matching '{mtsContains}' and original sizes starting at LBA {mtsStart}...");
            Console.WriteLine($"[Udf] Target example: Feb 12 PM -13\\AVCHD\\BDMV\\STREAM\\00058.MTS (use contains '00058' or '.MTS')");
            if (mtsLog != null)
                Console.WriteLine($"[Udf] Logging to {mtsLog}");

            // Use lighter retries for metadata scan to make it faster (save heavy retries for data recovery)
            var lightReader = new CdScan.Core.RetryingSectorReader(reader, new CdScan.Core.RetryPolicy { MaxAttempts = 3, DelayBetweenAttemptsMs = 10 });

            var sizeHits = CdScan.Core.Udf.VolumeDescriptorSequence.ScanForFileIdentifierDescriptorsWithSize(
                lightReader,
                mtsContains,
                startLba: mtsStart,
                maxSectors: mtsCount,
                progressCallback: (current, total) =>
                {
                    double pct = current * 100.0 / total;
                    Console.WriteLine($"[Udf] mts-sizes progress: {current:N0} / {total:N0} sectors ({pct:F1}%)");
                },
                logFile: mtsLog,
                resume: mtsResume);

            Console.WriteLine($"\nFound {sizeHits.Count} matching references with size info:");
            foreach (var (fidLba, name, size, dLba) in sizeHits)
            {
                string sizeStr = size > 0 ? $"{size:N0} bytes ({size / (1024 * 1024)} MB)" : "size unknown";
                string dataStr = dLba > 0 ? $", data at LBA {dLba}" : "";
                Console.WriteLine($"  {name}  (FID at LBA {fidLba})  → {sizeStr}{dataStr}");
            }
            break;

        default:
            // Legacy LBA / range reading mode
            string range = subCommand;
            if (range.Contains('-'))
            {
                var parts = range.Split('-', 2);
                long start = long.Parse(parts[0]);
                long end = long.Parse(parts[1]);
                int success = 0, fail = 0;

                for (long lba = start; lba <= end; lba++)
                {
                    bool ok = reader.TryReadSector(lba, out _, out string? err);
                    if (ok) success++;
                    else { fail++; Console.WriteLine($"  LBA {lba}: FAILED - {err}"); }
                }
                Console.WriteLine($"\nRange {start}-{end}: {success} ok, {fail} failed ({(success + fail > 0 ? success * 100.0 / (success + fail) : 0):F1}% success)");
            }
            else
            {
                long lba = long.Parse(range);
                bool ok = reader.TryReadSector(lba, out byte[]? data, out string? err);
                if (ok && data != null)
                {
                    Console.WriteLine($"LBA {lba} read OK ({data.Length} bytes). First 64 bytes (hex):");
                    Console.WriteLine(BitConverter.ToString(data, 0, Math.Min(64, data.Length)).Replace("-", " "));
                }
                else
                {
                    Console.WriteLine($"LBA {lba} FAILED: {err}");
                }
            }
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    if (ex.InnerException != null)
        Console.Error.WriteLine($"  Inner: {ex.InnerException.Message}");
}

static void PrintUsage()
{
    Console.WriteLine("CdScan.ConsoleTest - CD recovery inspection tool");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  LIST                                    List optical drives");
    Console.WriteLine("  copy-resilient <src> <dest> [logfile]   Resilient copy of one file (retries bad sectors, fills gaps, keeps going)");
    Console.WriteLine("                                          e.g. copy-resilient \"E:\\Feb 12 PM -13\\AVCHD\\BDMV\\STREAM\\00058.MTS\" \"D:\\recovered\\00058.MTS\"");
    Console.WriteLine("  E: 16                                   Read raw sector (legacy)");
    Console.WriteLine("  E: 10000-10100                          Test raw read range");
    Console.WriteLine("  E: list-files   (or ls)                 List all files on disc using raw reader");
    Console.WriteLine("  E: find 00058.MTS                       Find specific file and show its LBA + size (e.g. \"Feb 12 PM -13\\AVCHD\\BDMV\\STREAM\\00058.MTS\")");
    Console.WriteLine("  E: scan-video <start> <count> [logfile] Scan for MPEG-TS video data (very useful on damaged discs)");
    Console.WriteLine("                                          Add 'resume' as 5th arg or just reuse same logfile to auto-resume");
    Console.WriteLine("  E: full-video-scan [logfile]            Scan entire disc for video (with logging + auto-resume)");
    Console.WriteLine("  E: rescan <start> <count> [out.raw]     Aggressively hammer a bad region (high retries)");
    Console.WriteLine("  RESCAN-TEST                             Synthetic self-test (no drive): verifies rescan on unrecoverables/weak hits");
    Console.WriteLine("  E: rebuild-mts <size> <dataLba> <rec.raw> <out>  Rebuild .MTS from mts-sizes info + recovered raw (from rescan on dataLba)");
    Console.WriteLine("                                               e.g. for Feb 12 PM -13\\AVCHD\\BDMV\\STREAM\\00058.MTS");
    Console.WriteLine("  E: mts-sizes (or list-stream-sizes) [start] [count] [logfile] [resume] [filter]   Scan for .MTS + sizes (auto-resume if logfile given); filter e.g. 00058 or STREAM");
    Console.WriteLine();
    Console.WriteLine("Note: Run as Administrator for raw drive access.");
}