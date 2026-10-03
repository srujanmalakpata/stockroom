using System.Net.Http.Json;
using System.Text.Json;

namespace Inventory.Api.IntegrationTests;

/// <summary>Minimal reader for application/problem+json bodies.</summary>
internal sealed record ProblemJson(int Status, string? Title, string? Code, JsonElement Raw)
{
    public static async Task<ProblemJson> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return new ProblemJson(
            json.GetProperty("status").GetInt32(),
            json.TryGetProperty("title", out var title) ? title.GetString() : null,
            json.TryGetProperty("code", out var code) ? code.GetString() : null,
            json);
    }
}
