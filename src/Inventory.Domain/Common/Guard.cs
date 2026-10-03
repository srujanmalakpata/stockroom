namespace Inventory.Domain.Common;

internal static class Guard
{
    public static int Positive(int value, string what) =>
        value > 0
            ? value
            : throw new DomainException(DomainErrorKind.InvalidInput, "invalid_quantity", $"{what} must be greater than zero (was {value}).");

    public static int NotNegative(int value, string what) =>
        value >= 0
            ? value
            : throw new DomainException(DomainErrorKind.InvalidInput, "invalid_quantity", $"{what} must not be negative (was {value}).");
}
