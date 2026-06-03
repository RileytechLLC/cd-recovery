using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CdScan.Core;

/// <summary>
/// Reads sectors directly from a Windows optical drive using the raw device handle (\\.\D: etc).
/// This often gives better behavior under weak sectors than high-level file copy because we can
/// apply explicit retries and (later) speed control + SPTI.
/// </summary>
public sealed class WindowsRawCdReader : ISectorReader
{
    private readonly SafeFileHandle _handle;
    private readonly string _devicePath;
    private bool _disposed;

    public int SectorSize => 2048;

    private WindowsRawCdReader(SafeFileHandle handle, string devicePath)
    {
        _handle = handle;
        _devicePath = devicePath;
    }

    /// <summary>
    /// Opens a CD/DVD drive for raw sector access (e.g. driveLetter = "D").
    /// Must usually be run as Administrator.
    /// </summary>
    public static WindowsRawCdReader Open(string driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter))
            throw new ArgumentException("Drive letter is required", nameof(driveLetter));

        // Normalize to "D:" style then to \\.\D:
        var letter = driveLetter.Trim().TrimEnd(':', '\\', '/');
        if (letter.Length != 1 || !char.IsLetter(letter[0]))
            throw new ArgumentException("Drive letter must be a single letter (A-Z)", nameof(driveLetter));

        string devicePath = $@"\\.\{letter}:";

        const uint GENERIC_READ = 0x80000000;
        const uint FILE_SHARE_READ = 0x00000001;
        const uint FILE_SHARE_WRITE = 0x00000002;
        const uint OPEN_EXISTING = 3;
        const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        SafeFileHandle handle = CreateFile(
            devicePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Failed to open optical drive {devicePath}. " +
                "This usually requires running as Administrator. Error code: " + error);
        }

        return new WindowsRawCdReader(handle, devicePath);
    }

    public bool TryReadSector(long lba, out byte[]? data, out string? error)
    {
        data = null;
        error = null;

        if (_disposed)
        {
            error = "Reader has been disposed";
            return false;
        }

        if (lba < 0)
        {
            error = "Negative LBA is not valid";
            return false;
        }

        // Seek to the logical block
        long byteOffset = lba * SectorSize;
        if (!SetFilePointerEx(_handle, byteOffset, out _, 0 /* FILE_BEGIN */))
        {
            int err = Marshal.GetLastWin32Error();
            error = $"SetFilePointerEx failed for LBA {lba} (offset {byteOffset}): Win32 error {err}";
            return false;
        }

        byte[] buffer = new byte[SectorSize];
        if (!ReadFile(_handle, buffer, (uint)buffer.Length, out uint bytesRead, IntPtr.Zero))
        {
            int err = Marshal.GetLastWin32Error();
            // Common errors for weak sectors: 23 (ERROR_CRC), 1117 (ERROR_IO_DEVICE), etc.
            error = $"ReadFile failed at LBA {lba}: Win32 error {err} ({new Win32Exception(err).Message})";
            return false;
        }

        if (bytesRead != SectorSize)
        {
            error = $"Short read at LBA {lba}: got {bytesRead} bytes instead of {SectorSize}";
            return false;
        }

        data = buffer;
        return true;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _handle.Dispose();
            _disposed = true;
        }
    }

    // === P/Invoke ===

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        SafeFileHandle hFile,
        [Out] byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(
        SafeFileHandle hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);
}
