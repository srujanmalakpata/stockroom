using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Inventory.Api.Auth;

public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>
    /// Accepted keys. Supply them through configuration (user secrets, environment variables, or an
    /// Azure Key Vault reference); never commit a real key. Several keys allow zero-downtime rotation.
    /// </summary>
    public IList<string> Keys { get; } = [];
}

/// <summary>
/// Authenticates callers that send a known key in the <c>X-Api-Key</c> header. Requests without the
/// header are "no result" (anonymous), so read endpoints stay open while write endpoints require the
/// <see cref="ApiKeyDefaults.WritePolicy"/> policy.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.HeaderName, out var provided) || string.IsNullOrEmpty(provided))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!Options.Keys.Any(key => FixedTimeEquals(key, provided.ToString())))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "api-key-client"), new Claim(ApiKeyDefaults.ScopeClaim, ApiKeyDefaults.WriteScope)],
            ApiKeyDefaults.Scheme);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    // Compare hashes so the comparison time depends on neither the key contents nor its length.
    private static bool FixedTimeEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
}

public static class ApiKeyDefaults
{
    public const string Scheme = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string ScopeClaim = "scope";
    public const string WriteScope = "inventory.write";
    public const string WritePolicy = "InventoryWrite";

    public static IServiceCollection AddApiKeyAuth(this IServiceCollection services, IConfiguration configuration)
    {
        var keys = configuration.GetSection("Auth:ApiKeys").Get<string[]>() ?? [];
        services
            .AddAuthentication(Scheme)
            .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(Scheme, o =>
            {
                foreach (var key in keys.Where(k => !string.IsNullOrWhiteSpace(k)))
                {
                    o.Keys.Add(key);
                }
            });
        services.AddAuthorizationBuilder()
            .AddPolicy(WritePolicy, p => p.RequireAuthenticatedUser().RequireClaim(ScopeClaim, WriteScope));
        return services;
    }
}
