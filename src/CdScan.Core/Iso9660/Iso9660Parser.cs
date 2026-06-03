using System.Buffers.Binary;
using System.Text;

namespace CdScan.Core.Iso9660;

/// <summary>
/// Minimal but functional ISO9660 + Joliet parser that works over an ISectorReader.
/// Designed to be resilient and focused on recovering files from damaged discs.
/// </summary>
public static class Iso9660Parser
{
    private const int SectorSize = 2048;
    private const int PvdLba = 16;



    /// <summary>
    /// Walks the disc and returns all files (with preference for Joliet long names when available).
    /// Returns an empty list on failure (e.g. UDF disc or heavily damaged ISO structures).
    /// </summary>
    public static List<Core.FileEntry> GetAllFiles(ISectorReader reader)
    {
        try
        {
            // Try Joliet first (Supplementary Volume Descriptor)
            var jolietRoot = TryGetJolietRoot(reader);
            if (jolietRoot != null)
            {
                Console.WriteLine("[Iso9660] Using Joliet supplementary volume descriptor.");
                return WalkDirectory(reader, jolietRoot.Value, "", useJoliet: true);
            }

            // Fall back to standard ISO9660
            var pvdRoot = GetPrimaryVolumeRoot(reader);
            Console.WriteLine("[Iso9660] Using Primary Volume Descriptor (no Joliet found).");
            return WalkDirectory(reader, pvdRoot, "", useJoliet: false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Iso9660] Parsing failed: {ex.Message}");
            return new List<Core.FileEntry>();
        }
    }

    private static (long Lba, long Length)? TryGetJolietRoot(ISectorReader reader)
    {
        // Scan volume descriptors starting at LBA 16
        for (int i = 0; i < 8; i++) // Usually within first few sectors
        {
            if (!reader.TryReadSector(PvdLba + i, out byte[]? sector, out _))
                continue;

            byte type = sector[0];
            if (type == 255) // Volume Descriptor Set Terminator
                break;

            if (type == 2) // Supplementary Volume Descriptor
            {
                // Check for Joliet escape sequence at offset 0x58
                ReadOnlySpan<byte> escape = sector.AsSpan(88, 3);
                if (escape.SequenceEqual("\x25\x2F\x40"u8) || // UCS-2 Level 1
                    escape.SequenceEqual("\x25\x2F\x43"u8) || // UCS-2 Level 2
                    escape.SequenceEqual("\x25\x2F\x45"u8))   // UCS-2 Level 3
                {
                    // Root directory record is at offset 156 (same as PVD)
                    var rootRecord = new DirectoryRecord(sector.AsSpan(156));
                    return (rootRecord.ExtentLocation, rootRecord.DataLength);
                }
            }
        }
        return null;
    }

    private static (long Lba, long Length) GetPrimaryVolumeRoot(ISectorReader reader)
    {
        if (!reader.TryReadSector(PvdLba, out byte[]? pvd, out string? err))
            throw new InvalidOperationException($"Failed to read Primary Volume Descriptor: {err}");

        if (pvd[0] != 1)
            throw new InvalidOperationException("LBA 16 is not a Primary Volume Descriptor");

        var rootRecord = new DirectoryRecord(pvd.AsSpan(156));
        return (rootRecord.ExtentLocation, rootRecord.DataLength);
    }

    private static List<FileEntry> WalkDirectory(ISectorReader reader, (long Lba, long Length) dirExtent, string currentPath, bool useJoliet)
    {
        var results = new List<FileEntry>();
        long bytesToRead = dirExtent.Length;
        long currentLba = dirExtent.Lba;

        while (bytesToRead > 0)
        {
            if (!reader.TryReadSector(currentLba, out byte[]? sector, out string? err))
            {
                // Directory sector is damaged — this is common on bad discs.
                // Log and try to continue with the next sector.
                Console.WriteLine($"[Iso9660] WARNING: Failed to read directory sector at LBA {currentLba}: {err}");
                currentLba++;
                bytesToRead -= SectorSize;
                continue;
            }

            int offset = 0;
            while (offset < SectorSize)
            {
                if (!DirectoryRecord.TryParse(sector.AsSpan(offset), out var record) || record!.Length == 0)
                    break;

                string name = record.FileIdentifier;

                // For Joliet, the identifier is UCS-2 big-endian and stored in the same field (we need better handling later)
                // For now we use the raw identifier. A proper implementation would decode it properly.
                if (useJoliet && name.Length > 0)
                {
                    // Decode Joliet (UCS-2 Big Endian) filename
                    try
                    {
                        int idLen = record.FileIdentifierLength;
                        if (idLen > 0 && idLen <= sector.Length - (offset + 33))
                        {
                            var utf16 = Encoding.BigEndianUnicode.GetString(sector, offset + 33, idLen);
                            var cleaned = utf16.TrimEnd('\0', ' ');
                            if (!string.IsNullOrWhiteSpace(cleaned))
                                name = cleaned;
                        }
                    }
                    catch { /* fall back to the ASCII/ISO name we already parsed */ }
                }

                if (name != "." && name != "..")
                {
                    string fullPath = string.IsNullOrEmpty(currentPath) ? name : $"{currentPath}/{name}";

                    results.Add(new Core.FileEntry(fullPath, record.ExtentLocation, record.DataLength, record.IsDirectory));

                    if (record.IsDirectory)
                    {
                        var subEntries = WalkDirectory(reader, (record.ExtentLocation, record.DataLength), fullPath, useJoliet);
                        results.AddRange(subEntries);
                    }
                }

                offset += record.Length;
            }

            currentLba++;
            bytesToRead -= SectorSize;
        }

        return results;
    }
}
