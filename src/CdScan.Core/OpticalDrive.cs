using System.Runtime.InteropServices;

namespace CdScan.Core;

/// <summary>
/// Represents a detected optical (CD/DVD/Blu-ray) drive on the system.
/// </summary>
public sealed record OpticalDrive(string Letter, string DisplayName)
{
    /// <summary>
    /// The drive letter without colon, e.g. "D".
    /// </summary>
    public string Letter { get; } = Letter;

    /// <summary>
    /// Human friendly display, e.g. "D:  (MY_BACKUP)".
    /// </summary>
    public string DisplayName { get; } = DisplayName;

    public static List<OpticalDrive> Enumerate()
    {
        var drives = new List<OpticalDrive>();

        // GetLogicalDriveStrings returns a buffer of null-terminated strings ended by double null.
        uint bufferSize = 256;
        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize * 2);

        try
        {
            uint length = GetLogicalDriveStrings(bufferSize, buffer);
            if (length == 0 || length > bufferSize)
                return drives; // fail silently for now

            string allDrives = Marshal.PtrToStringUni(buffer, (int)length) ?? "";
            string[] parts = allDrives.Split('\0', StringSplitOptions.RemoveEmptyEntries);

            foreach (string root in parts)
            {
                if (root.Length < 2) continue;

                char letter = char.ToUpperInvariant(root[0]);
                if (!char.IsLetter(letter)) continue;

                uint type = GetDriveType(root);
                if (type == DRIVE_CDROM)
                {
                    string display = $"{letter}:";
                    // Try to get volume label (best effort, may fail for empty/no disc)
                    try
                    {
                        string label = GetVolumeLabel(letter + ":");
                        if (!string.IsNullOrWhiteSpace(label))
                            display += $"  ({label})";
                    }
                    catch { /* ignore */ }

                    drives.Add(new OpticalDrive(letter.ToString(), display));
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return drives;
    }

    private static string GetVolumeLabel(string rootPath)
    {
        var labelBuilder = new System.Text.StringBuilder(256);
        var fsBuilder = new System.Text.StringBuilder(256);
        uint serial, maxComponent, flags;

        bool ok = GetVolumeInformation(
            rootPath,
            labelBuilder,
            (uint)labelBuilder.Capacity,
            out serial,
            out maxComponent,
            out flags,
            fsBuilder,
            (uint)fsBuilder.Capacity);

        return ok ? labelBuilder.ToString() : "";
    }

    private const uint DRIVE_CDROM = 5;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetLogicalDriveStrings(uint nBufferLength, IntPtr lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string lpRootPathName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        System.Text.StringBuilder lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        System.Text.StringBuilder lpFileSystemNameBuffer,
        uint nFileSystemNameSize);
}
