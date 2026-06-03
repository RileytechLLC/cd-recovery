namespace CdScan.Core;

/// <summary>
/// Wraps any ISectorReader and applies configurable retries + backoff.
/// This is the main lever for recovering data from weak sectors.
/// </summary>
public sealed class RetryingSectorReader : ISectorReader
{
    private readonly ISectorReader _inner;
    private readonly RetryPolicy _policy;

    public int SectorSize => _inner.SectorSize;

    public RetryingSectorReader(ISectorReader inner, RetryPolicy? policy = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _policy = policy ?? RetryPolicy.Default;
    }

    public bool TryReadSector(long lba, out byte[]? data, out string? error)
    {
        data = null;
        error = null;

        for (int attempt = 1; attempt <= _policy.MaxAttempts; attempt++)
        {
            if (_inner.TryReadSector(lba, out data, out error))
            {
                return true; // success
            }

            // Last attempt or non-retryable error?
            bool isLast = attempt == _policy.MaxAttempts;
            bool shouldRetry = _policy.ShouldRetry(error);

            if (isLast || !shouldRetry)
            {
                error = $"LBA {lba} failed after {attempt} attempt(s). Last error: {error}";
                return false;
            }

            // Backoff before next try
            if (_policy.DelayBetweenAttemptsMs > 0)
            {
                Thread.Sleep(_policy.DelayBetweenAttemptsMs * attempt); // simple linear backoff
            }
        }

        error = $"LBA {lba} failed after {_policy.MaxAttempts} attempts (no more retries).";
        return false;
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// Configurable retry behavior for sector reads.
/// </summary>
public sealed record RetryPolicy
{
    public int MaxAttempts { get; init; } = 12;
    public int DelayBetweenAttemptsMs { get; init; } = 80;

    /// <summary>
    /// Optional predicate to decide whether a particular error message is worth retrying.
    /// </summary>
    public Func<string?, bool> ShouldRetry { get; init; } = DefaultShouldRetry;

    public static RetryPolicy Default => new();

    public static RetryPolicy Aggressive => new()
    {
        MaxAttempts = 30,
        DelayBetweenAttemptsMs = 150
    };

    private static bool DefaultShouldRetry(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return true;

        // Common patterns that indicate a weak sector / hardware retry opportunity
        // rather than a permanent "no disc" or permission problem.
        var lower = error.ToLowerInvariant();
        return lower.Contains("crc") ||
               lower.Contains("io device") ||
               lower.Contains("readfile failed") ||
               lower.Contains("setfilepointer") ||
               lower.Contains("device");
    }
}
