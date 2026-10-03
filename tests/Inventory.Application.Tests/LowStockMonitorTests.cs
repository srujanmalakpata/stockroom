using Inventory.Application.Services;
using Inventory.Domain.Products;
using Inventory.Domain.Stock;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Inventory.Application.Tests;

public class LowStockMonitorTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero));
    private readonly InMemoryAlertRepository _alerts = new();
    private readonly LowStockMonitor _monitor;

    public LowStockMonitorTests() =>
        _monitor = new LowStockMonitor(_alerts, _clock, NullLogger<LowStockMonitor>.Instance);

    [Fact]
    public async Task SumsAvailableAcrossLocations_RaisesOnce_ThenResolves()
    {
        var product = Product.Create(Guid.NewGuid(), "SKU-1", "Thing", reorderThreshold: 10, _clock.GetUtcNow());
        var a = StockItem.Open(Guid.NewGuid(), product.Id, "A");
        var b = StockItem.Open(Guid.NewGuid(), product.Id, "B");
        a.Receive(6);
        b.Receive(6);
        b.Reserve(3); // available: 6 + 3 = 9 < 10

        await _monitor.EvaluateAsync([product], [a, b], CancellationToken.None);
        await _monitor.EvaluateAsync([product], [a, b], CancellationToken.None);

        var alert = Assert.Single(_alerts.Alerts);
        Assert.Equal((9, 10, "SKU-1"), (alert.AvailableAtRaise, alert.Threshold, alert.Sku));

        _clock.Advance(TimeSpan.FromHours(1));
        a.Receive(1); // available 10: no longer below threshold
        await _monitor.EvaluateAsync([product], [a, b], CancellationToken.None);

        Assert.Equal(_clock.GetUtcNow(), alert.ResolvedAt);
    }

    [Fact]
    public async Task IgnoresStockOfOtherProducts()
    {
        var product = Product.Create(Guid.NewGuid(), "SKU-2", "Thing", reorderThreshold: 1, _clock.GetUtcNow());
        var other = StockItem.Open(Guid.NewGuid(), Guid.NewGuid(), "A");
        other.Receive(100);

        await _monitor.EvaluateAsync([product], [other], CancellationToken.None);

        Assert.Single(_alerts.Alerts);
    }

    [Fact]
    public async Task EveryEvaluation_BumpsProductVersion_SoConcurrentEvaluationsConflict()
    {
        var product = Product.Create(Guid.NewGuid(), "SKU-3", "Thing", reorderThreshold: 0, _clock.GetUtcNow());

        await _monitor.EvaluateAsync([product], [], CancellationToken.None);
        await _monitor.EvaluateAsync([product], [], CancellationToken.None);

        Assert.Equal(2, product.Version);
        Assert.Empty(_alerts.Alerts);
    }
}
