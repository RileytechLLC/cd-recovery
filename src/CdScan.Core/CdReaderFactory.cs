namespace CdScan.Core;

/// <summary>
/// Convenience helpers to open optical drives with sensible defaults.
/// </summary>
public static class CdReaderFactory
{
    /// <summary>
    /// Opens the given drive letter with retries applied.
    /// </summary>
    public static ISectorReader OpenWithRetries(string driveLetter, RetryPolicy? policy = null)
    {
        var raw = WindowsRawCdReader.Open(driveLetter);
        return new RetryingSectorReader(raw, policy);
    }

    /// <summary>
    /// Opens the given drive letter with default retry policy (good starting point for damaged discs).
    /// </summary>
    public static ISectorReader OpenDefault(string driveLetter)
        => OpenWithRetries(driveLetter, RetryPolicy.Default);

    /// <summary>
    /// Opens with the more aggressive retry policy (slower but tries harder).
    /// </summary>
    public static ISectorReader OpenAggressive(string driveLetter)
        => OpenWithRetries(driveLetter, RetryPolicy.Aggressive);
}
