using System.Buffers.Binary;

namespace CdScan.Core.Udf;

/// <summary>
/// UDF Long Allocation Descriptor (used to point to File Set Descriptor, directories, files, etc.).
/// </summary>
public readonly struct LongAllocationDescriptor
{
    public uint ExtentLength { get; }
    public long ExtentLocation { get; }   // Logical block number
    public ushort PartitionReferenceNumber { get; }

    public LongAllocationDescriptor(ReadOnlySpan<byte> data)
    {
        ExtentLength = BinaryPrimitives.ReadUInt32LittleEndian(data[0..4]);
        ExtentLocation = BinaryPrimitives.ReadUInt32LittleEndian(data[4..8]);
        PartitionReferenceNumber = BinaryPrimitives.ReadUInt16LittleEndian(data[8..10]);
    }
}
