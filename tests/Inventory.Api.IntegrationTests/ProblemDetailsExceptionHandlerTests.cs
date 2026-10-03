using Inventory.Api.Http;
using Inventory.Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inventory.Api.IntegrationTests;

/// <summary>Unit tests for the exception-to-status mapping that are awkward to provoke over HTTP.</summary>
public sealed class ProblemDetailsExceptionHandlerTests
{
    private readonly RecordingProblemDetailsService _writer = new();

    private ProblemDetailsExceptionHandler Handler => new(_writer, NullLogger<ProblemDetailsExceptionHandler>.Instance);

    [Theory]
    [InlineData(DomainErrorKind.InvalidInput, StatusCodes.Status400BadRequest)]
    [InlineData(DomainErrorKind.RuleViolation, StatusCodes.Status409Conflict)]
    public async Task DomainErrors_MapByKind_NotByCode(DomainErrorKind kind, int expectedStatus)
    {
        var http = new DefaultHttpContext();

        await Handler.TryHandleAsync(http, new DomainException(kind, "some_new_rule", "A rule added later."), default);

        Assert.Equal(expectedStatus, http.Response.StatusCode);
        Assert.Equal("some_new_rule", _writer.Written!.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public async Task ClientDisconnect_Is499WithNoBody_NotA500()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var http = new DefaultHttpContext { RequestAborted = aborted.Token };

        var handled = await Handler.TryHandleAsync(http, new OperationCanceledException(aborted.Token), default);

        Assert.True(handled);
        Assert.Equal(ProblemDetailsExceptionHandler.StatusClientClosedRequest, http.Response.StatusCode);
        Assert.Null(_writer.Written);
    }

    [Fact]
    public async Task CancellationWithoutDisconnect_IsStillAnUnexpected500()
    {
        var http = new DefaultHttpContext();

        await Handler.TryHandleAsync(http, new OperationCanceledException(), default);

        Assert.Equal(StatusCodes.Status500InternalServerError, http.Response.StatusCode);
        Assert.Equal("internal_error", _writer.Written!.ProblemDetails.Extensions["code"]);
    }

    private sealed class RecordingProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetailsContext? Written { get; private set; }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Written = context;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Written = context;
            return ValueTask.FromResult(true);
        }
    }
}
