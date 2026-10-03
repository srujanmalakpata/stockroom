using Inventory.Application.Abstractions;
using Inventory.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Http;

/// <summary>
/// Maps exceptions to RFC 9457 ProblemDetails responses. Business errors keep their stable <c>code</c>
/// so clients can branch on it; unexpected errors become a generic 500 with no internals leaked.
/// </summary>
public sealed partial class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Non-standard "Client Closed Request" status (nginx convention); nobody reads the response.</summary>
    public const int StatusClientClosedRequest = 499;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // The client disconnected and the request's token cancelled the work. That is not a server error:
        // there is nobody to send a body to, so just record it quietly.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            LogAborted(logger, httpContext.Request.Method, httpContext.Request.Path);
            httpContext.Response.StatusCode = StatusClientClosedRequest;
            return true;
        }

        var problem = exception switch
        {
            InsufficientStockException e => Problem(StatusCodes.Status409Conflict, "Insufficient stock", e.Message, e.Code,
                new() { ["productId"] = e.ProductId, ["requested"] = e.Requested, ["available"] = e.Available }),
            DomainException { Kind: DomainErrorKind.InvalidInput } e =>
                Problem(StatusCodes.Status400BadRequest, "Invalid request", e.Message, e.Code),
            DomainException e => Problem(StatusCodes.Status409Conflict, "Business rule violated", e.Message, e.Code),
            NotFoundException e => Problem(StatusCodes.Status404NotFound, $"{e.Resource} not found", e.Message, "not_found"),
            ConflictException e => Problem(StatusCodes.Status409Conflict, "Conflict", e.Message, e.Code),
            BadHttpRequestException e => Problem(e.StatusCode, "Bad request", "The request could not be read.", "bad_request"),
            _ => null,
        };

        if (problem is null)
        {
            LogUnhandled(logger, exception, httpContext.Request.Method, httpContext.Request.Path);
            problem = Problem(StatusCodes.Status500InternalServerError, "Internal server error",
                "An unexpected error occurred.", "internal_error");
        }
        else
        {
            LogHandled(logger, problem.Status ?? 0, problem.Extensions[ProblemCodes.Key] as string ?? "", exception.Message);
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static ProblemDetails Problem(int status, string title, string detail, string code, Dictionary<string, object?>? extra = null)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
        problem.Extensions[ProblemCodes.Key] = code;
        foreach (var (key, value) in extra ?? [])
        {
            problem.Extensions[key] = value;
        }

        return problem;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Request rejected with {Status} {Code}: {Detail}")]
    private static partial void LogHandled(ILogger logger, int status, string code, string detail);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Client aborted {Method} {Path}")]
    private static partial void LogAborted(ILogger logger, string method, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, string path);
}
