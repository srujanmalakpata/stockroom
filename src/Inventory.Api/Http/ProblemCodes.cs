using System.Diagnostics;

namespace Inventory.Api.Http;

/// <summary>
/// Makes every ProblemDetails body carry a stable <c>code</c> and the <c>traceId</c>. Exceptions get their
/// code from <see cref="ProblemDetailsExceptionHandler"/>; this fills in the responses that never throw:
/// status-code pages (a 400 from request binding, 401 from the auth challenge, 404 for an unknown route,
/// and so on) and validation problems.
/// </summary>
public static class ProblemCodes
{
    public const string Key = "code";
    public const string TraceIdKey = "traceId";

    /// <summary>
    /// The W3C <c>traceparent</c> of the current request (<c>00-{trace}-{span}-{flags}</c>). Its trace-id part
    /// is what the JSON log scopes and Application Insights record, so a client can quote it to find the
    /// request. Falls back to
    /// Kestrel's connection-scoped request id only when no <see cref="Activity"/> is running.
    /// </summary>
    public static string TraceId(HttpContext http) => Activity.Current?.Id ?? http.TraceIdentifier;

    public static void Apply(ProblemDetailsContext context)
    {
        var problem = context.ProblemDetails;
        problem.Extensions[TraceIdKey] = TraceId(context.HttpContext);
        if (!problem.Extensions.ContainsKey(Key))
        {
            problem.Extensions[Key] = ForStatus(problem.Status ?? context.HttpContext.Response.StatusCode);
        }
    }

    public static string ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "bad_request",
        StatusCodes.Status401Unauthorized => "unauthorized",
        StatusCodes.Status403Forbidden => "forbidden",
        StatusCodes.Status404NotFound => "not_found",
        StatusCodes.Status405MethodNotAllowed => "method_not_allowed",
        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
        >= 500 => "internal_error",
        _ => "http_" + status.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
