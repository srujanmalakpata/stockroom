namespace Inventory.Domain.Common;

/// <summary>What kind of mistake a <see cref="DomainException"/> reports, which decides its HTTP status.</summary>
public enum DomainErrorKind
{
    /// <summary>The input itself is invalid (bad quantity, SKU, location...), whatever the data says. HTTP 400.</summary>
    InvalidInput,

    /// <summary>The input is well-formed but conflicts with the current state (not enough stock, closed order...). HTTP 409.</summary>
    RuleViolation,
}

/// <summary>
/// Raised when an operation would break a business rule. The <see cref="Code"/> is a stable,
/// machine-readable identifier that the API surfaces in ProblemDetails responses, and
/// <see cref="Kind"/> tells the API whether the caller sent bad input or hit a state conflict.
/// Every throw site must choose a kind, so a new rule cannot silently get the wrong status.
/// </summary>
public class DomainException : Exception
{
    public DomainException(DomainErrorKind kind, string code, string message)
        : base(message)
    {
        Kind = kind;
        Code = code;
    }

    public DomainErrorKind Kind { get; }

    public string Code { get; }
}
