using System.Diagnostics;

namespace CdScan.Core.Udf;

/// <summary>
/// Helper to walk a UDF Volume Descriptor Sequence (VDS) and locate key descriptors
/// such as the File Set Descriptor.
/// </summary>
public static class VolumeDescriptorSequence
{
    /// <summary>
    /// Scans the Main Volume Descriptor Sequence looking for a File Set Descriptor (tag 256).
    /// Returns its LBA if found. Also logs encountered tag types for diagnostics.
    /// </summary>
    public static long? FindFileSetDescriptorLba(ISectorReader reader, long vdsStartLba, uint vdsLength)
    {
        long sectorsToScan = (vdsLength + 2047) / 2048;
        long maxScan = Math.Min(sectorsToScan, 2048); // scan up to ~4MB worth of descriptors

        var foundTags = new System.Collections.Generic.Dictionary<ushort, int>();

        for (long i = 0; i < maxScan; i++)
        {
            long lba = vdsStartLba + i;

            if (!reader.TryReadSector(lba, out byte[]? sector, out _))
                continue;

            var tag = new DescriptorTag(sector.AsSpan(0, 16));
            if (!tag.IsValid)
                continue;

            if (!foundTags.ContainsKey(tag.TagIdentifier))
                foundTags[tag.TagIdentifier] = 0;
            foundTags[tag.TagIdentifier]++;

            if (tag.TagIdentifier == UdfConstants.TagIdentifierFileSetDescriptor)
            {
                Console.WriteLine($"[Udf] Found File Set Descriptor (tag 256) at LBA {lba}");
                return lba;
            }

            if (tag.TagIdentifier == 8) // Terminator
            {
                break;
            }
        }

        Console.WriteLine("[Udf] Scanned VDS but did not find File Set Descriptor (tag 256).");
        Console.WriteLine("[Udf] Tag types encountered in VDS scan: " + 
            string.Join(", ", foundTags.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}x{kv.Value}")));

        return null;
    }

    /// <summary>
    /// Broad scan across the disc for File Identifier Descriptors (tag 257).
    /// This is one of the most effective techniques on badly damaged UDF discs.
    ///
    /// It looks for directory entry structures and attempts to extract the actual filename + size.
    /// Reports progress periodically.
    /// </summary>
    public static List<(long FidLba, string Filename, long Size, long DataLba)> ScanForFileIdentifierDescriptorsWithSize(
        ISectorReader reader,
        string containsName,
        long startLba = 0,
        long maxSectors = 2_000_000,
        Action<long, long>? progressCallback = null,
        string? logFile = null,
        bool resume = false)
    {
        var results = new List<(long FidLba, string Filename, long Size, long DataLba)>();
        var searchUpper = containsName.ToUpperInvariant();
        long lastProgressReport = 0;
        const long progressInterval = 100_000;

        long currentLba = startLba;

        // Resume support
        if (resume && !string.IsNullOrWhiteSpace(logFile) && File.Exists(logFile))
        {
            try
            {
                var lines = File.ReadAllLines(logFile);
                long lastLba = -1;
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    if (lines[i].Contains("mts-sizes progress:"))
                    {
                        foreach (var token in lines[i].Split(' ', '/', ','))
                        {
                            string clean = token.Replace(",", "").Trim();
                            if (long.TryParse(clean, out long val) && val > lastLba)
                                lastLba = val;
                        }
                        break;
                    }
                }
                if (lastLba > currentLba)
                {
                    currentLba = lastLba + 1;
                    Console.WriteLine($"[Udf] Resuming mts-sizes from LBA {currentLba}");
                }
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(logFile))
        {
            try
            {
                File.AppendAllText(logFile, $"[Udf] Starting mts-sizes at {DateTime.Now} from LBA {currentLba}\n");
            }
            catch { }
        }

        for (long i = 0; i < maxSectors; i++)
        {
            long lba = currentLba + i;

            if (!reader.TryReadSector(lba, out byte[]? sector, out _))
                continue;

            if (sector.Length < 16) continue;

            ushort tagId = BitConverter.ToUInt16(sector, 0);
            if (tagId != UdfConstants.TagIdentifierFileIdentifierDescriptor)
                continue;

            string? filename = TryExtractFilenameFromFID(sector);
            if (filename != null && filename.ToUpperInvariant().Contains(searchUpper))
            {
                ulong size = 0;
                long dataLba = 0;

                for (int j = 1; j <= 3; j++)
                {
                    if (UdfFileEntry.TryParse(reader, lba + j, out var fe) && fe.InformationLength > 0)
                    {
                        size = fe.InformationLength;
                        dataLba = fe.FirstExtentLocation;
                        break;
                    }
                }

                results.Add((lba, filename, (long)size, dataLba));
                if (size > 0)
                    Console.WriteLine($"[Udf] Found {filename} at FID LBA {lba}, size = {size} bytes, data starts at LBA {dataLba}");
                else
                    Console.WriteLine($"[Udf] Found {filename} at FID LBA {lba} (size unknown)");

                if (!string.IsNullOrWhiteSpace(logFile))
                {
                    try
                    {
                        File.AppendAllText(logFile, $"[Udf] Found {filename} at FID LBA {lba}, size = {size} bytes\n");
                    }
                    catch { }
                }
            }

            if (i - lastProgressReport >= progressInterval)
            {
                progressCallback?.Invoke(i, maxSectors);
                lastProgressReport = i;

                if (!string.IsNullOrWhiteSpace(logFile))
                {
                    try
                    {
                        double pct = i * 100.0 / maxSectors;
                        File.AppendAllText(logFile, $"[Udf] mts-sizes progress: {i:N0} / {maxSectors:N0} sectors ({pct:F1}%)\n");
                    }
                    catch { }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Attempts to parse a File Identifier Descriptor and extract the filename.
    /// UDF FIDs have a somewhat complex but consistent layout.
    /// </summary>
    private static string? TryExtractFilenameFromFID(byte[] sector)
    {
        try
        {
            // FID header (simplified):
            // 0-1:   Tag Identifier (already checked)
            // 2-3:   Descriptor Version
            // 4:     Tag Checksum
            // ... (we skip to the interesting parts)

            // Offset 16: File Version Number (2 bytes)
            // Offset 18: File Characteristics (1 byte)
            // Offset 19: Length of File Identifier (1 byte)  <-- important
            // Offset 20: ICB (16 bytes)
            // Offset 36: Length of Implementation Use (2 bytes)
            // Offset 38: Implementation Use (variable)
            // Then: File Identifier (the name)

            if (sector.Length < 40) return null;

            byte fileIdLength = sector[19];
            if (fileIdLength == 0 || fileIdLength > 255) return null;

            int implUseLen = BitConverter.ToUInt16(sector, 36);
            int nameOffset = 38 + implUseLen;

            if (nameOffset + fileIdLength > sector.Length) return null;

            // UDF filenames are usually OSTA Compressed Unicode (starts with 8 or 16)
            byte compressionId = sector[nameOffset];
            int nameStart = nameOffset + 1;
            int nameBytes = fileIdLength - 1;

            if (nameBytes <= 0) return null;

            if (compressionId == 8)
            {
                // 8-bit compressed
                string name = System.Text.Encoding.ASCII.GetString(sector, nameStart, Math.Min(nameBytes, sector.Length - nameStart));
                return name.TrimEnd('\0');
            }
            else if (compressionId == 16)
            {
                // 16-bit compressed (big endian Unicode)
                string name = System.Text.Encoding.BigEndianUnicode.GetString(sector, nameStart, Math.Min(nameBytes, sector.Length - nameStart));
                return name.TrimEnd('\0');
            }
            else
            {
                // Fallback
                string name = System.Text.Encoding.ASCII.GetString(sector, nameOffset, Math.Min(fileIdLength, sector.Length - nameOffset));
                return name.TrimEnd('\0');
            }
        }
        catch
        {
            return null;
        }
    }
}
