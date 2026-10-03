using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Inventory.Api.Http;

/// <summary>Validates the endpoint's <typeparamref name="T"/> body and returns a 400 ValidationProblem on failure.</summary>
public sealed class ValidationFilter<T>(IValidator<T> validator) : IEndpointFilter
    where T : class
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var body = context.Arguments.OfType<T>().FirstOrDefault();
        if (body is null)
        {
            return Problem(context.HttpContext, new Dictionary<string, string[]> { ["body"] = ["A JSON request body is required."] });
        }

        var result = await validator.ValidateAsync(body, context.HttpContext.RequestAborted);
        return result.IsValid ? await next(context) : Problem(context.HttpContext, result.ToDictionary());
    }

    // ValidationProblem results do not pass through IProblemDetailsService in .NET 8, so the code and
    // traceId that ProblemCodes.Apply adds elsewhere are added here explicitly.
    private static ValidationProblem Problem(HttpContext http, IDictionary<string, string[]> errors) =>
        TypedResults.ValidationProblem(
            errors,
            extensions: new Dictionary<string, object?>
            {
                [ProblemCodes.Key] = "validation_failed",
                [ProblemCodes.TraceIdKey] = ProblemCodes.TraceId(http),
            });
}

public static class ValidationFilterExtensions
{
    public static RouteHandlerBuilder Validate<T>(this RouteHandlerBuilder builder)
        where T : class =>
        builder.AddEndpointFilter<ValidationFilter<T>>().ProducesValidationProblem();
}
