using Inventory.Application.Abstractions;
using Inventory.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Inventory.Application.Tests;

public class ConcurrencyRetryTests
{
    private readonly FakeUnitOfWork _unitOfWork = new();

    private ConcurrencyRetry Retry(int maxAttempts) =>
        new(_unitOfWork, new ConcurrencyOptions { MaxAttempts = maxAttempts, MaxBackoffMilliseconds = 2 }, NullLogger<ConcurrencyRetry>.Instance);

    [Fact]
    public async Task RetriesOnConflict_DiscardingStaleStateBeforeEachRetry()
    {
        var calls = 0;

        var result = await Retry(5).ExecuteAsync(
            "test",
            _ => ++calls < 3 ? throw new ConcurrencyConflictException("stale") : Task.FromResult(calls),
            CancellationToken.None);

        Assert.Equal(3, result);
        Assert.Equal(2, _unitOfWork.Discards);
    }

    [Fact]
    public async Task GivesUpAfterMaxAttempts()
    {
        var calls = 0;

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => Retry(3).ExecuteAsync<int>(
            "test",
            _ =>
            {
                calls++;
                throw new ConcurrencyConflictException("stale");
            },
            CancellationToken.None));

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task DoesNotRetryOtherFailures()
    {
        var calls = 0;

        await Assert.ThrowsAsync<ConflictException>(() => Retry(5).ExecuteAsync<int>(
            "test",
            _ =>
            {
                calls++;
                throw new ConflictException("duplicate_sku", "duplicate");
            },
            CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.Equal(0, _unitOfWork.Discards);
    }
}
