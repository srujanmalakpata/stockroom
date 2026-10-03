using Inventory.Domain.Alerts;
using Inventory.Domain.Common;
using Inventory.Domain.Products;

namespace Inventory.Domain.Tests;

public class ProductAndAlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_NormalizesSkuAndTrimsName()
    {
        var product = Product.Create(Guid.NewGuid(), " widget-red-01 ", "  Red widget ", 5, Now);

        Assert.Equal("WIDGET-RED-01", product.Sku);
        Assert.Equal("Red widget", product.Name);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("-BAD")]
    [InlineData("HAS SPACE")]
    public void Create_RejectsInvalidSku(string sku)
    {
        Assert.Equal("invalid_sku", Assert.Throws<DomainException>(() => Product.Create(Guid.NewGuid(), sku, "Name", 0, Now)).Code);
    }

    [Fact]
    public void ReorderThreshold_CannotBeNegative()
    {
        var product = Product.Create(Guid.NewGuid(), "SKU-1", "Name", 0, Now);

        Assert.Throws<DomainException>(() => product.ChangeReorderThreshold(-1));
        Assert.Throws<DomainException>(() => Product.Create(Guid.NewGuid(), "SKU-2", "Name", -1, Now));
    }

    [Theory]
    [InlineData(4, 5, false, LowStockDecision.Raise)]
    [InlineData(4, 5, true, LowStockDecision.NoChange)]
    [InlineData(5, 5, true, LowStockDecision.Resolve)]
    [InlineData(5, 5, false, LowStockDecision.NoChange)]
    [InlineData(0, 0, false, LowStockDecision.NoChange)]
    public void LowStockPolicy_IsEdgeTriggered(int available, int threshold, bool hasOpenAlert, LowStockDecision expected)
    {
        Assert.Equal(expected, LowStockPolicy.Evaluate(available, threshold, hasOpenAlert));
    }

    [Fact]
    public void Alert_CanBeResolvedOnlyOnce()
    {
        var alert = LowStockAlert.Raise(Guid.NewGuid(), Guid.NewGuid(), "SKU-1", 2, 5, Now);
        Assert.True(alert.IsOpen);

        alert.Resolve(Now.AddMinutes(5));

        Assert.False(alert.IsOpen);
        Assert.Equal("alert_already_resolved", Assert.Throws<DomainException>(() => alert.Resolve(Now)).Code);
    }

    [Fact]
    public void ThresholdAndStockChanges_BumpProductVersion()
    {
        var product = Product.Create(Guid.NewGuid(), "SKU-V", "Versioned", 1, Now);

        product.ChangeReorderThreshold(3);
        product.MarkStockChanged();

        Assert.Equal(2, product.Version);
        Assert.Throws<DomainException>(() => product.ChangeReorderThreshold(-1));
        Assert.Equal(2, product.Version); // a rejected change does not bump it
    }

    [Fact]
    public void LowStockPolicy_HandlesTotalsAboveIntRange()
    {
        Assert.Equal(LowStockDecision.Resolve, LowStockPolicy.Evaluate(3_000_000_000L, int.MaxValue, hasOpenAlert: true));
    }

    [Fact]
    public void Timestamps_AreStoredAsUtcWholeMilliseconds()
    {
        var local = new DateTimeOffset(2026, 10, 3, 14, 0, 0, TimeSpan.FromHours(2)).AddTicks(1_234_567);

        var product = Product.Create(Guid.NewGuid(), "SKU-T", "Name", 1, local);
        var alert = LowStockAlert.Raise(Guid.NewGuid(), product.Id, product.Sku, 0, 1, local);
        alert.Resolve(local);

        var expected = new DateTimeOffset(2026, 10, 3, 12, 0, 0, 123, TimeSpan.Zero);
        Assert.Equal((expected, TimeSpan.Zero), (product.CreatedAt, product.CreatedAt.Offset));
        Assert.Equal(expected.UtcTicks, alert.RaisedAt.UtcTicks);
        Assert.Equal(TimeSpan.Zero, alert.ResolvedAt!.Value.Offset);
    }

    [Fact]
    public void ErrorKinds_SeparateBadInputFromStateConflicts()
    {
        var product = Product.Create(Guid.NewGuid(), "SKU-K", "Name", 0, Now);
        var alert = LowStockAlert.Raise(Guid.NewGuid(), product.Id, product.Sku, 0, 1, Now);
        alert.Resolve(Now);

        Assert.Equal(DomainErrorKind.InvalidInput, Assert.Throws<DomainException>(() => product.ChangeReorderThreshold(-1)).Kind);
        Assert.Equal(DomainErrorKind.RuleViolation, Assert.Throws<DomainException>(() => alert.Resolve(Now)).Kind);
    }
}
