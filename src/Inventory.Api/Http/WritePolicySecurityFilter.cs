using Inventory.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Inventory.Api.Http;

/// <summary>
/// Marks only the operations that require the <see cref="ApiKeyDefaults.WritePolicy"/> policy as needing
/// <c>X-Api-Key</c> in the OpenAPI document; anonymous reads stay unmarked.
/// </summary>
public sealed class WritePolicySecurityFilter : IOperationFilter
{
    private static readonly OpenApiSecurityRequirement ApiKeyRequirement = new()
    {
        [new OpenApiSecurityScheme
        {
            Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = ApiKeyDefaults.Scheme },
        }] = [],
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var requiresKey = context.ApiDescription.ActionDescriptor.EndpointMetadata
            .OfType<IAuthorizeData>()
            .Any(a => a.Policy == ApiKeyDefaults.WritePolicy);
        if (requiresKey)
        {
            operation.Security.Add(ApiKeyRequirement);
            operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing or invalid X-Api-Key." });
        }
    }
}
