using Inventory.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Inventory.Application.Services;

public sealed class ConcurrencyOptions
{
    public const string SectionName = "Concurrency";

    /// <summary>Total attempts (first try + retries) for an operation that hits a concurrency conflict.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Exclusive upper bound of the random back-off before each retry: the delay is
    /// <c>Random.Next(1, MaxBackoffMilliseconds)</c> ms (1-19 ms by default) times the attempt number.
    /// </summary>
    public int MaxBackoffMilliseconds { get; set; } = 20;
}

/// <summary>
/// Runs a read-modify-write operation and, if the optimistic concurrency check fails at commit, throws
/// away the stale tracked entities and runs the whole operation again against fresh data. Because the
/// operation re-reads stock on each attempt, a retried reservation sees the other request's effect and
/// either fits in what is left or fails with "insufficient stock"; it can never oversell.
/// </summary>
public sealed partial class ConcurrencyRetry(IUnitOfWork unitOfWork, ConcurrencyOptions options, ILogger<ConcurrencyRetry> logger)
{
    public async Task<T> ExecuteAsync<T>(string operation, Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(ct);
            }
            catch (ConcurrencyConflictException) when (attempt < options.MaxAttempts)
            {
                LogRetry(logger, operation, attempt, options.MaxAttempts);
                unitOfWork.DiscardChanges();
#pragma warning disable CA5394 // Random is fine for jitter; it is not security-sensitive.
                var delay = Random.Shared.Next(1, Math.Max(2, options.MaxBackoffMilliseconds)) * attempt;
#pragma warning restore CA5394
                await Task.Delay(delay, ct);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Concurrency conflict in {Operation}; retrying (attempt {Attempt} of {MaxAttempts})")]
    private static partial void LogRetry(ILogger logger, string operation, int attempt, int maxAttempts);
}
