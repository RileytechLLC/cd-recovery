using System.Buffers.Binary;

namespace CdScan.Core.Udf;

/// <summary>
/// UDF Anchor Volume Descriptor Pointer (usually at LBA 256 and near the end of the disc).
/// </summary>
public readonly struct AnchorVolumeDescriptorPointer
{
    public DescriptorTag Tag { get; }
    public long MainVolumeDescriptorSequenceExtentLocation { get; }
    public long MainVolumeDescriptorSequenceExtentLength { get; }
    public long ReserveVolumeDescriptorSequenceExtentLocation { get; }
    public long ReserveVolumeDescriptorSequenceExtentLength { get; }

    public AnchorVolumeDescriptorPointer(ReadOnlySpan<byte> sector)
    {
        Tag = new DescriptorTag(sector[0..16]);

        MainVolumeDescriptorSequenceExtentLocation = BinaryPrimitives.ReadUInt32LittleEndian(sector[16..20]);
        MainVolumeDescriptorSequenceExtentLength = BinaryPrimitives.ReadUInt32LittleEndian(sector[20..24]);

        ReserveVolumeDescriptorSequenceExtentLocation = BinaryPrimitives.ReadUInt32LittleEndian(sector[24..28]);
        ReserveVolumeDescriptorSequenceExtentLength = BinaryPrimitives.ReadUInt32LittleEndian(sector[28..32]);
    }

    public static bool TryRead(ISectorReader reader, long lba, out AnchorVolumeDescriptorPointer avdp)
    {
        avdp = default;

        if (!reader.TryReadSector(lba, out byte[]? sector, out _))
            return false;

        var tag = new DescriptorTag(sector.AsSpan(0, 16));
        if (tag.TagIdentifier != UdfConstants.TagIdentifierAnchorVolumeDescriptorPointer)
            return false;

        avdp = new AnchorVolumeDescriptorPointer(sector);
        return true;
    }
}
