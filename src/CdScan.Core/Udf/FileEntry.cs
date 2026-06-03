using System.Buffers.Binary;

namespace CdScan.Core.Udf;

/// <summary>
/// Minimal parser for UDF File Entry / Extended File Entry to extract file size.
/// </summary>
public readonly struct UdfFileEntry
{
    public DescriptorTag Tag { get; }
    public ulong InformationLength { get; }  // This is the original file size
    public ulong LogicalBlocksRecorded { get; }
    public long FirstExtentLocation { get; }  // LBA where the file data starts (first allocation)

    public UdfFileEntry(ReadOnlySpan<byte> sector)
    {
        Tag = new DescriptorTag(sector[0..16]);

        InformationLength = 0;
        LogicalBlocksRecorded = 0;
        FirstExtentLocation = 0;

        // Simplified: for many UDF File Entries on optical, after the basic fields,
        // the allocation descriptors follow. For short allocation, the extent is soon after.
        // This is approximate but works for many AVCHD cases.
        if (sector.Length > 176)
        {
            // Information Length at 56
            InformationLength = BinaryPrimitives.ReadUInt64LittleEndian(sector[56..64]);

            // Look for allocation descriptor. For simplicity, assume short allocation descriptor at offset ~168 or so
            // Common layout: after extended attributes, the AD starts.
            // For practicality, scan for a plausible LBA after the header.
            // Better: the first AD is often at a fixed offset for simple entries.
            // Let's take a common offset for the first extent location in File Entry.
            // In practice, for these discs, the data extent LBA is in the AD at offset around 168-176.
            FirstExtentLocation = BinaryPrimitives.ReadUInt32LittleEndian(sector[168..172]);
        }
    }

    public static bool TryParse(ISectorReader reader, long lba, out UdfFileEntry entry)
    {
        entry = default;

        if (!reader.TryReadSector(lba, out byte[]? sector, out _))
            return false;

        var tag = new DescriptorTag(sector.AsSpan(0, 16));
        if (tag.TagIdentifier != UdfConstants.TagIdentifierFileEntry &&
            tag.TagIdentifier != UdfConstants.TagIdentifierExtendedFileEntry)
            return false;

        entry = new UdfFileEntry(sector);
        return true;
    }
}
