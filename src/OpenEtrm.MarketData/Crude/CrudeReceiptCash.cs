using System.Collections.Immutable;
using static OpenEtrm.MarketData.Crude.CrudePhysicalCashRules;

namespace OpenEtrm.MarketData.Crude;

public enum CrudeReceiptTitle { NotTransferred = 1, Transferred }
public enum CrudeDispositionKind { OwnedInventory = 1, OnwardDelivery }
public enum CrudeCashComponent { Consideration = 1, HandlingFee }
public enum CrudeInvoiceState { Open = 1, PartPaid, Overdue, PartPaidOverdue, Paid, NoPaymentDue }
public sealed record CrudeCashRate(UnitPrice Price, ExactDecimal Direction);
public sealed record CrudeCashRounding(string Implementation, int DecimalPlaces, CashTieRule TieRule);
public sealed record CrudePhysicalCashTerms(string Implementation, Guid TradeId, Guid OwnerLegalEntityId,
    CrudeDeliveryLocation Location, ReferenceKey Grade, QuantitySpecification Quantity, ExactDecimal StandardTemperature,
    string TemperatureScale, Quantity ContractQuantity, CrudeDeliveryWindow Delivery, DateTimeOffset PaymentAt,
    CrudeCashRate Consideration, CrudeCashRate? HandlingFee, CrudeCashRounding Rounding);
public sealed record CrudeAcceptedReceipt(Guid Id, DateTimeOffset AcceptedAt, Quantity Quantity,
    ReferenceKey Grade, CrudeDeliveryLocation Location, CrudeReceiptTitle Title);
public sealed record CrudeOnwardSale(Guid TradeId, int Revision, Guid SellerLegalEntityId, ReferenceKey Grade,
    CrudeDeliveryLocation Location, ReferenceKey Quantity, Quantity ContractQuantity, CrudeDeliveryWindow Delivery);
public sealed record CrudeReceiptDisposition(Guid Id, Guid ReceiptId, Quantity Quantity, CrudeDispositionKind Kind,
    DateTimeOffset RecordedAt, CrudeOnwardSale? Sale, ReferenceKey? InventoryLocation, Guid? OwnerLegalEntityId);
public sealed record CrudeReceiptDispatch(Guid Id, Guid DispositionId, Quantity Quantity, DateTimeOffset DispatchedAt,
    CrudeReceiptTitle Title);
public sealed record CrudeCashInvoice(Guid Id, CrudeCashComponent Component, Guid SourceId,
    int ComponentOrdinal, Guid? PriorInvoiceId, DateTimeOffset IssuedAt)
{
    public int EventVersion { get; init; }
}
public sealed record CrudeCashPayment(Guid Id, Guid InvoiceId, Money Amount, DateTimeOffset PaidAt)
{
    public int EventVersion { get; init; }
}
public enum CrudeCashReversalKind { PaymentRecord = 1, InvoiceRecord }
public sealed record CrudeCashRecordReversal(Guid Id, CrudeCashReversalKind Kind, Guid TargetId,
    DateTimeOffset ReversedAt, int EventVersion, string Implementation);
public sealed record CrudeInvoiceAllocation(ExactMoney ExactAmount, Money Amount, Quantity CumulativeQuantity)
{
    public ExactMoney RoundingEffect => ExactMoney.FromMoney(Amount).Subtract(ExactAmount);
}
public sealed record CrudeInvoiceBalance(Guid InvoiceId, CrudeCashComponent Component, ExactMoney ExactAmount,
    Money Amount, Money Paid, Money Open, DateTimeOffset DueAt, CrudeInvoiceState State);
public sealed record CrudeComponentCashBalance(CrudeCashComponent Component, Money Original, Money Invoiced,
    Money Paid, Money Outstanding, Money Uninvoiced, ExactMoney ExactOriginal, ExactMoney ExactInvoiced, ExactMoney ExactUninvoiced);
public sealed record CrudeReceiptCashResult(Quantity AcceptedQuantity, Quantity RemainingQuantity,
    Quantity UndisposedQuantity, Quantity OwnedInventoryQuantity, Quantity OnwardQuantity,
    Quantity OwnedQuantity, Quantity ReservedQuantity, Quantity DispatchedQuantity,
    ImmutableArray<CrudeInvoiceBalance> Invoices, ImmutableArray<CrudeComponentCashBalance> Cash)
{
    public ImmutableArray<Guid> ReversedInvoiceIds { get; init; } = [];
    public ImmutableArray<Guid> ReversedPaymentIds { get; init; } = [];
}

// These are selected accepted facts, not forecasts. The caller owns authority,
// knowledge-time selection and immutable/idempotent persistence of each fact.
public static class CrudeReceiptCash
{
    public const string Implementation = "physical_crude_receipt_cash_v1";
    public const string ReversalImplementation = "crude_record_correction_v1";
    public const string AllocationImplementation = "crude_cumulative_receipt_cash_v1";
    public const string RoundingImplementation = "currency_minor_units_to_even_v1";

    public static CrudeInvoiceAllocation AllocateInvoice(UnitPrice price, ExactDecimal direction,
        Quantity priorInvoiced, Quantity receipt, CrudeCashRounding rounding) =>
        CrudeCashLedger.AllocateInvoice(price,direction,priorInvoiced,receipt,rounding);

    public static CrudeReceiptCashResult Reconcile(CrudePhysicalCashTerms terms,
        ImmutableArray<CrudeAcceptedReceipt> receipts, ImmutableArray<CrudeReceiptDisposition> dispositions,
        ImmutableArray<CrudeReceiptDispatch> dispatches, ImmutableArray<CrudeCashInvoice> invoices,
        ImmutableArray<CrudeCashPayment> payments, DateTimeOffset asOf) =>
        ReconcileCore(terms, receipts, dispositions, dispatches, invoices, payments, asOf, false);

    public static CrudeReceiptCashResult Reconcile(CrudePhysicalCashTerms terms,
        ImmutableArray<CrudeAcceptedReceipt> receipts, ImmutableArray<CrudeReceiptDisposition> dispositions,
        ImmutableArray<CrudeReceiptDispatch> dispatches, ImmutableArray<CrudeCashInvoice> invoices,
        ImmutableArray<CrudeCashPayment> payments, ImmutableArray<CrudeCashRecordReversal> reversals, DateTimeOffset asOf)
    {
        RequireTerms(terms);
        var selected = CrudeCashLedger.SelectActiveCash(terms, invoices, payments, reversals, asOf);
        return ReconcileCore(terms, receipts, dispositions, dispatches, selected.Invoices, selected.Payments, asOf, true) with
        { ReversedInvoiceIds = selected.ReversedInvoices, ReversedPaymentIds = selected.ReversedPayments };
    }

    private static CrudeReceiptCashResult ReconcileCore(CrudePhysicalCashTerms terms,
        ImmutableArray<CrudeAcceptedReceipt> receipts, ImmutableArray<CrudeReceiptDisposition> dispositions,
        ImmutableArray<CrudeReceiptDispatch> dispatches, ImmutableArray<CrudeCashInvoice> invoices,
        ImmutableArray<CrudeCashPayment> payments, DateTimeOffset asOf, bool reversedHistory)
    {
        RequireTerms(terms);
        RequireTime(asOf, "crude_context");
        RequireFacts(receipts, r => r.Id); RequireFacts(dispositions, d => d.Id);
        RequireFacts(dispatches, d => d.Id); RequireFacts(invoices, i => i.Id); RequireFacts(payments, p => p.Id);
        var byReceipt = receipts.ToDictionary(r => r.Id);
        var byDisposition = dispositions.ToDictionary(d => d.Id);
        var zero = default(ExactDecimal);
        var accepted = zero; var inventory = zero; var onward = zero; var dispatched = zero;
        foreach (var receipt in receipts)
        {
            RequireQuantity(receipt.Quantity, terms.Quantity.Key, "crude_receipt_quantity");
            if (receipt.Grade != terms.Grade || receipt.Location != terms.Location)
                throw Error("crude_receipt_reference", "Accepted receipt must retain the exact contractual grade and custody references.");
            if (receipt.Title != CrudeReceiptTitle.Transferred)
                throw Error("crude_receipt_title", "This receipt profile requires explicit title transfer at acceptance.");
            RequireTime(receipt.AcceptedAt, "crude_receipt_time");
            if (receipt.AcceptedAt < terms.Delivery.From || receipt.AcceptedAt >= terms.Delivery.To || receipt.AcceptedAt > asOf)
                throw Error("crude_receipt_time", "Receipt acceptance must belong to the contractual window and selected effective context.");
            accepted += receipt.Quantity.Amount;
        }
        if (accepted.CompareTo(terms.ContractQuantity.Amount) > 0) throw Error("crude_receipt_quantity", "Accepted quantity cannot exceed the zero-tolerance contractual total.");
        foreach (var disposition in dispositions)
        {
            RequireQuantity(disposition.Quantity, terms.Quantity.Key, "crude_disposition_quantity");
            if (!byReceipt.TryGetValue(disposition.ReceiptId, out var receipt))
                throw Error("crude_disposition_receipt", "Every disposition requires its accepted receipt.");
            RequireTime(disposition.RecordedAt, "crude_disposition_time");
            if (disposition.RecordedAt < receipt.AcceptedAt || disposition.RecordedAt > asOf)
                throw Error("crude_disposition_time", "A disposition cannot precede receipt acceptance or the selected context.");
            if (disposition.Kind == CrudeDispositionKind.OwnedInventory)
            {
                if (disposition.Sale is not null || disposition.InventoryLocation != terms.Location.CustodyPoint ||
                    disposition.OwnerLegalEntityId != terms.OwnerLegalEntityId)
                    throw Error("crude_inventory_title", "Owned inventory requires the purchasing entity and retained custody point explicitly.");
                inventory += disposition.Quantity.Amount;
            }
            else if (disposition.Kind == CrudeDispositionKind.OnwardDelivery)
            {
                var sale = disposition.Sale;
                if (sale is null || sale.TradeId == Guid.Empty || sale.TradeId == terms.TradeId || sale.Revision < 1 ||
                    sale.SellerLegalEntityId != terms.OwnerLegalEntityId || sale.Grade != terms.Grade || sale.Location != terms.Location ||
                    sale.Quantity != terms.Quantity.Key || sale.Delivery is null || disposition.RecordedAt >= sale.Delivery.To || sale.ContractQuantity is null ||
                    sale.ContractQuantity.Specification != terms.Quantity.Key || sale.ContractQuantity.Amount.CompareTo(default) <= 0 || disposition.InventoryLocation is not null || disposition.OwnerLegalEntityId is not null)
                    throw Error("crude_onward_sale", "Onward reservation requires an exact compatible sale revision by the same owner at the same custody point.");
                onward += disposition.Quantity.Amount;
            }
            else throw Error("crude_disposition", "Choose owned inventory or onward delivery explicitly.");
        }
        foreach (var group in dispositions.GroupBy(d => d.ReceiptId))
            if (Sum(group.Select(d => d.Quantity.Amount)).CompareTo(byReceipt[group.Key].Quantity.Amount) > 0)
                throw Error("crude_disposition_quantity", "A receipt cannot be allocated more than once across dispositions.");
        foreach (var group in dispositions.Where(d => d.Sale is not null).GroupBy(d => (d.Sale!.TradeId, d.Sale.Revision)))
            if (group.Select(d => d.Sale).Distinct().Count() != 1 ||
                Sum(group.Select(d => d.Quantity.Amount)).CompareTo(group.First().Sale!.ContractQuantity.Amount) > 0)
                throw Error("crude_onward_quantity", "Onward reservations require one exact sale revision and cannot exceed its contractual quantity.");
        foreach (var dispatch in dispatches)
        {
            RequireQuantity(dispatch.Quantity, terms.Quantity.Key, "crude_dispatch_quantity");
            if (!byDisposition.TryGetValue(dispatch.DispositionId, out var disposition) || disposition.Kind != CrudeDispositionKind.OnwardDelivery)
                throw Error("crude_dispatch_disposition", "Dispatch requires its explicit onward reservation.");
            RequireTime(dispatch.DispatchedAt, "crude_dispatch_time");
            if (dispatch.DispatchedAt < disposition.RecordedAt || dispatch.DispatchedAt > asOf || dispatch.DispatchedAt < disposition.Sale!.Delivery.From ||
                dispatch.DispatchedAt >= disposition.Sale.Delivery.To || dispatch.Title != CrudeReceiptTitle.Transferred)
                throw Error("crude_dispatch_time", "Dispatch requires explicit title transfer after reservation under the selected context.");
            dispatched += dispatch.Quantity.Amount;
        }
        foreach (var group in dispatches.GroupBy(d => d.DispositionId))
            if (Sum(group.Select(d => d.Quantity.Amount)).CompareTo(byDisposition[group.Key].Quantity.Amount) > 0)
                throw Error("crude_dispatch_quantity", "Dispatch cannot exceed its reserved quantity.");
        var cash=CrudeCashLedger.Reconcile(terms,[..receipts.Select(r=>new CrudeCashSource(r.Id,r.AcceptedAt,r.Quantity))],invoices,payments,asOf,reversedHistory);
        Quantity Q(ExactDecimal value) => new(value, terms.Quantity.Key);
        return new(Q(accepted), Q(terms.ContractQuantity.Amount - accepted), Q(accepted - inventory - onward), Q(inventory), Q(onward),
            Q(accepted - dispatched), Q(onward - dispatched), Q(dispatched), cash.Invoices, cash.Cash);
    }

}
