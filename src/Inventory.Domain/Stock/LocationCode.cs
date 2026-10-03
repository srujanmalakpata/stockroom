using System.Text.RegularExpressions;
using Inventory.Domain.Common;

namespace Inventory.Domain.Stock;

/// <summary>Warehouse bin/location codes such as <c>A-01-03</c> (aisle-rack-shelf).</summary>
public static partial class LocationCode
{
    public static bool IsValid(string? code) =>
        code is not null && Pattern().IsMatch(code.Trim().ToUpperInvariant());

    public static string Normalize(string code)
    {
        if (!IsValid(code))
        {
            throw new DomainException(
                DomainErrorKind.InvalidInput,
                "invalid_location",
                "Location code must be 1-32 characters: letters, digits or '-', starting with a letter or digit.");
        }

        return code.Trim().ToUpperInvariant();
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
