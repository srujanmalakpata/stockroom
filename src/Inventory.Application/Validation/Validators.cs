using FluentValidation;
using Inventory.Application.Contracts;
using Inventory.Domain.Orders;
using Inventory.Domain.Products;
using Inventory.Domain.Stock;

namespace Inventory.Application.Validation;

// Request validators give fast, field-level 400 responses. The domain re-checks the same rules
// (defence in depth), so an invalid value can never reach the database even if a validator is skipped.

public sealed class CreateProductRequestValidator : AbstractValidator<CreateProductRequest>
{
    public CreateProductRequestValidator()
    {
        RuleFor(r => r.Sku).Must(Product.IsValidSku)
            .WithMessage("SKU must be 2-40 characters: letters, digits, '-', '_' or '.'.");
        RuleFor(r => r.Name).NotEmpty().MaximumLength(Product.MaxNameLength);
        RuleFor(r => r.ReorderThreshold).GreaterThanOrEqualTo(0);
    }
}

public sealed class ChangeReorderThresholdRequestValidator : AbstractValidator<ChangeReorderThresholdRequest>
{
    public ChangeReorderThresholdRequestValidator() =>
        RuleFor(r => r.ReorderThreshold).GreaterThanOrEqualTo(0);
}

public sealed class ReceiveStockRequestValidator : AbstractValidator<ReceiveStockRequest>
{
    public const int MaxReceiptQuantity = 1_000_000;

    public ReceiveStockRequestValidator()
    {
        RuleFor(r => r.LocationCode).Must(LocationCode.IsValid)
            .WithMessage("Location code must be 1-32 characters: letters, digits or '-'.");
        RuleFor(r => r.Quantity).InclusiveBetween(1, MaxReceiptQuantity);
    }
}

public sealed class RecordCountRequestValidator : AbstractValidator<RecordCountRequest>
{
    public RecordCountRequestValidator()
    {
        RuleFor(r => r.LocationCode).Must(LocationCode.IsValid)
            .WithMessage("Location code must be 1-32 characters: letters, digits or '-'.");
        RuleFor(r => r.CountedQuantity).InclusiveBetween(0, StockItem.MaxQuantityPerLocation);
    }
}

public sealed class PlaceOrderRequestValidator : AbstractValidator<PlaceOrderRequest>
{
    public const int MaxLines = 100;

    public PlaceOrderRequestValidator()
    {
        RuleFor(r => r.CustomerReference).NotEmpty().MaximumLength(Order.MaxCustomerReferenceLength);
        RuleFor(r => r.Lines).Cascade(CascadeMode.Stop).NotEmpty().Must(l => l.Count <= MaxLines)
            .WithMessage($"An order may have at most {MaxLines} lines.");
        // ChildRules skips null elements, so "lines": [null] must be rejected explicitly here.
        RuleForEach(r => r.Lines).NotNull().WithMessage("Order lines must not be null.").ChildRules(line =>
        {
            line.RuleFor(l => l.ProductId).NotEmpty();
            line.RuleFor(l => l.Quantity).InclusiveBetween(1, ReceiveStockRequestValidator.MaxReceiptQuantity);
        });
    }
}
