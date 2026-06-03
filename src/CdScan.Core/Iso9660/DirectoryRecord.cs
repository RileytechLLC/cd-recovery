using System.Buffers.Binary;
using System.Text;

namespace CdScan.Core.Iso9660;

/// <summary>
/// Represents a single directory record from an ISO9660 / Joliet filesystem.
/// </summary>
public sealed class DirectoryRecord
{
    public byte Length { get; }
    public byte ExtendedAttributeRecordLength { get; }
    public long ExtentLocation { get; }      // Start LBA
    public long DataLength { get; }
    public DateTime RecordingDateTime { get; }
    public byte FileFlags { get; }
    public byte FileIdentifierLength { get; }
    public string FileIdentifier { get; }
    public bool IsDirectory => (FileFlags & 0x02) != 0;

    public DirectoryRecord(ReadOnlySpan<byte> data)
    {
        Length = data[0];
        if (Length == 0)
            throw new ArgumentException("Zero-length directory record");

        ExtendedAttributeRecordLength = data[1];
        ExtentLocation = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(2, 4));
        DataLength = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(10, 4));

        // Recording date/time (7 bytes)
        RecordingDateTime = ParseRecordingDateTime(data.Slice(18, 7));

        FileFlags = data[25];
        FileIdentifierLength = data[32];

        // File identifier (can be padded to even length)
        var idBytes = data.Slice(33, FileIdentifierLength);
        FileIdentifier = ParseFileIdentifier(idBytes);
    }

    private static DateTime ParseRecordingDateTime(ReadOnlySpan<byte> bytes)
    {
        // Year since 1900, month, day, hour, minute, second, timezone offset in 15-min intervals from GMT
        try
        {
            int year = 1900 + bytes[0];
            int month = bytes[1];
            int day = bytes[2];
            int hour = bytes[3];
            int minute = bytes[4];
            int second = bytes[5];
            // byte 6 = offset from GMT in 15 min intervals (we ignore for now)

            return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string ParseFileIdentifier(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 1 && bytes[0] == 0)
            return ".";   // Current directory
        if (bytes.Length == 1 && bytes[0] == 1)
            return "..";  // Parent directory

        // ISO9660 filenames are often padded with ';' + version number
        string id = Encoding.ASCII.GetString(bytes).TrimEnd('\0');

        // Remove version number (e.g. "FILE.TXT;1" → "FILE.TXT")
        int semi = id.IndexOf(';');
        if (semi > 0)
            id = id[..semi];

        return id;
    }

    public static bool TryParse(ReadOnlySpan<byte> data, out DirectoryRecord? record)
    {
        record = null;
        if (data.Length < 1 || data[0] == 0)
            return false;

        try
        {
            record = new DirectoryRecord(data);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
