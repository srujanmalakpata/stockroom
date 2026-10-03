namespace Inventory.Application.Abstractions;

/// <summary>The requested resource does not exist (HTTP 404).</summary>
public sealed class NotFoundException(string resource, object key)
    : Exception($"{resource} '{key}' was not found.")
{
    public string Resource { get; } = resource;
}

/// <summary>The request conflicts with current state, e.g. a duplicate SKU (HTTP 409).</summary>
public class ConflictException : Exception
{
    public ConflictException(string code, string message)
        : this(code, message, null)
    {
    }

    public ConflictException(string code, string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>
/// Optimistic concurrency check failed: another request changed a row we read. Operations are retried
/// by <see cref="Services.ConcurrencyRetry"/>; this escapes to the caller only when retries run out.
/// </summary>
public sealed class ConcurrencyConflictException(string message, Exception? innerException = null)
    : ConflictException("concurrency_conflict", message, innerException);
