namespace CdScan.Core.Udf;

/// <summary>
/// Minimal set of UDF constants needed for AVCHD-style discs (typically UDF 1.02 / 1.50).
/// </summary>
public static class UdfConstants
{
    // Volume Recognition Sequence identifiers
    public static readonly byte[] Bea01 = "BEA01"u8.ToArray();
    public static readonly byte[] Nsr02 = "NSR02"u8.ToArray();
    public static readonly byte[] Nsr03 = "NSR03"u8.ToArray();
    public static readonly byte[] Tea01 = "TEA01"u8.ToArray();

    // Common sector locations for UDF
    public const int AnchorVolumeDescriptorPointerLba = 256;
    public const int AnchorVolumeDescriptorSequenceLba = 256;

    // Descriptor tags (from ECMA-167 / UDF)
    public const ushort TagIdentifierAnchorVolumeDescriptorPointer = 2;
    public const ushort TagIdentifierPrimaryVolumeDescriptor = 1;
    public const ushort TagIdentifierFileSetDescriptor = 256;
    public const ushort TagIdentifierFileIdentifierDescriptor = 257;
    public const ushort TagIdentifierFileEntry = 261;
    public const ushort TagIdentifierExtendedFileEntry = 266;

    // File Entry offsets (simplified for common case)
    public const int FileEntryInformationLengthOffset = 56; // Approximate, needs verification per descriptor version
}
