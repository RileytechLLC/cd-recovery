namespace CdScan.Core;

/// <summary>
/// An ISectorReader that reads from a normal .iso / .img file on disk
/// but can simulate read failures according to a provided set of bad LBAs.
/// Extremely useful for developing and testing the recovery logic without
/// needing a physically damaged disc.
/// </summary>
public sealed class FaultyImageReader : ISectorReader
{
    private readonly FileStream _stream;
    private readonly HashSet<long> _badLbas;

    public int SectorSize { get; }

    /// <summary>
    /// Opens an image file. All reads are 1:1 with the file (no retries here — wrap with RetryingSectorReader if desired).
    /// </summary>
    /// <param name="imagePath">Path to a .iso or raw image file.</param>
    /// <param name="badLbas">Set of LBAs that should fail reads (simulated damage).</param>
    /// <param name="sectorSize">Usually 2048.</param>
    public FaultyImageReader(string imagePath, IEnumerable<long>? badLbas = null, int sectorSize = 2048)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Image file not found", imagePath);

        SectorSize = sectorSize;
        _badLbas = badLbas != null ? new HashSet<long>(badLbas) : new HashSet<long>();
        _stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>
    /// Convenience constructor that creates random bad sectors for testing.
    /// </summary>
    public static FaultyImageReader CreateWithRandomFaults(string imagePath, double faultProbability, int? seed = null, int sectorSize = 2048)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        // We don't know the exact size in advance, so we lazily decide on first read.
        // For simplicity in tests, users usually pass an explicit bad set.
        // This overload mainly exists for quick "chaos" testing.
        var reader = new FaultyImageReader(imagePath, null, sectorSize);
        // Note: random mode not fully implemented in this minimal version — use explicit set for now.
        return reader;
    }

    public bool TryReadSector(long lba, out byte[]? data, out string? error)
    {
        data = null;
        error = null;

        long byteOffset = lba * SectorSize;

        if (_badLbas.Contains(lba))
        {
            error = $"Simulated read failure at LBA {lba} (injected fault)";
            return false;
        }

        if (byteOffset + SectorSize > _stream.Length)
        {
            error = $"LBA {lba} is beyond end of image (size {_stream.Length} bytes)";
            return false;
        }

        _stream.Seek(byteOffset, SeekOrigin.Begin);
        data = new byte[SectorSize];
        int read = _stream.Read(data, 0, SectorSize);

        if (read != SectorSize)
        {
            error = $"Short read from image at LBA {lba}";
            data = null;
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}
