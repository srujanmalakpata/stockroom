using Inventory.Application.Abstractions;
using Inventory.Application.Contracts;

namespace Inventory.Application.Services;

public sealed class AlertService(IAlertRepository alerts)
{
    public async Task<IReadOnlyList<LowStockAlertResponse>> ListAsync(bool includeResolved, int offset, int limit, CancellationToken ct)
    {
        var (skip, take) = Paging.Clamp(offset, limit);
        return (await alerts.ListAsync(includeResolved, skip, take, ct)).Select(LowStockAlertResponse.From).ToList();
    }
}
