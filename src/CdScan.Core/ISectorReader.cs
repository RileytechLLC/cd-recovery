namespace CdScan.Core;

/// <summary>
/// Abstraction over reading 2048-byte (or other) sectors from a CD/DVD or a synthetic image.
/// This is the key seam that lets us:
/// - Talk to real optical drives
/// - Read from .iso files for testing
/// - Inject faults for synthetic damage testing
/// </summary>
public interface ISectorReader : IDisposable
{
    /// <summary>
    /// Size of one sector in bytes (usually 2048 for data CDs).
    /// </summary>
    int SectorSize { get; }

    /// <summary>
    /// Attempts to read a single sector.
    /// </summary>
    /// <param name="lba">Logical Block Address (0-based sector number).</param>
    /// <param name="data">The sector data on success. Null on failure.</param>
    /// <param name="error">Human-readable error description on failure.</param>
    /// <returns>True if the sector was read successfully.</returns>
    bool TryReadSector(long lba, out byte[]? data, out string? error);
}
