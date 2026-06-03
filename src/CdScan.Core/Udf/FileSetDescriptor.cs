using System.Buffers.Binary;
using System.Text;

namespace CdScan.Core.Udf;

/// <summary>
/// UDF File Set Descriptor (points to the root directory).
/// </summary>
public readonly struct FileSetDescriptor
{
    public DescriptorTag Tag { get; }
    public LongAllocationDescriptor RootDirectoryIcb { get; }
    public LongAllocationDescriptor SystemStreamDirectoryIcb { get; }

    public FileSetDescriptor(ReadOnlySpan<byte> sector)
    {
        Tag = new DescriptorTag(sector[0..16]);

        // LogicalVolumeIdentifier etc. are before the ICBs — we skip most of them for minimal parsing

        // Root Directory ICB is at offset 0x200 (512) in the File Set Descriptor for UDF
        // Actually the offsets are:
        // 0x200 = Root Directory ICB (Long Allocation Descriptor)
        RootDirectoryIcb = new LongAllocationDescriptor(sector[0x200..0x210]);

        // System Stream Directory ICB at 0x210
        SystemStreamDirectoryIcb = new LongAllocationDescriptor(sector[0x210..0x220]);
    }

    public static bool TryParse(ISectorReader reader, LongAllocationDescriptor location, out FileSetDescriptor fsd)
    {
        fsd = default;

        if (!reader.TryReadSector(location.ExtentLocation, out byte[]? sector, out _))
            return false;

        var tag = new DescriptorTag(sector.AsSpan(0, 16));
        if (tag.TagIdentifier != UdfConstants.TagIdentifierFileSetDescriptor)
            return false;

        fsd = new FileSetDescriptor(sector);
        return true;
    }
}
