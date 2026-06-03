namespace CdScan.Core;

/// <summary>
/// Common representation of a file or directory on the disc, used by both ISO and UDF parsers.
/// </summary>
public sealed record FileEntry(string Path, long StartLba, long Length, bool IsDirectory);