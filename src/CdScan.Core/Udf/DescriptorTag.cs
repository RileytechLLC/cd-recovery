using System.Buffers.Binary;

namespace CdScan.Core.Udf;

/// <summary>
/// ECMA-167 / UDF Descriptor Tag (common 16-byte header for most descriptors).
/// </summary>
public readonly struct DescriptorTag
{
    public ushort TagIdentifier { get; }
    public ushort DescriptorVersion { get; }
    public byte TagChecksum { get; }
    public ushort TagSerialNumber { get; }
    public ushort DescriptorCrc { get; }
    public ushort DescriptorCrcLength { get; }
    public uint TagLocation { get; }

    public DescriptorTag(ReadOnlySpan<byte> data)
    {
        TagIdentifier = BinaryPrimitives.ReadUInt16LittleEndian(data[0..2]);
        DescriptorVersion = BinaryPrimitives.ReadUInt16LittleEndian(data[2..4]);
        TagChecksum = data[4];
        TagSerialNumber = BinaryPrimitives.ReadUInt16LittleEndian(data[6..8]);
        DescriptorCrc = BinaryPrimitives.ReadUInt16LittleEndian(data[8..10]);
        DescriptorCrcLength = BinaryPrimitives.ReadUInt16LittleEndian(data[10..12]);
        TagLocation = BinaryPrimitives.ReadUInt32LittleEndian(data[12..16]);
    }

    public bool IsValid => TagIdentifier != 0;
}
