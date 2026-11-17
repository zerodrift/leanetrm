using System.Collections.Immutable;
using OpenEtrm.MarketData.Crude;

namespace OpenEtrm.MarketData.Physical;

public sealed record CrudeReceiptDeliveryInput(CrudePhysicalCashTerms Terms,
    ImmutableArray<CrudeAcceptedReceipt> Receipts, ImmutableArray<CrudeReceiptDisposition> Dispositions,
    ImmutableArray<CrudeInventoryAllocation> Allocations, ImmutableArray<CrudeReceiptDispatch> Dispatches,
    ImmutableArray<CrudeCashInvoice> Invoices, ImmutableArray<CrudeCashPayment> Payments,
    ImmutableArray<CrudeCashRecordReversal> Reversals, DateTimeOffset AsOf);

// Accepted receipts and dispatches remain native transfer events. The existing
// reconciler owns title, reservation, cash and correction interpretation; this
// adapter supplies a common delivery inspection without a second ledger or PV.
public sealed class CrudeReceiptDeliveryBehavior : DeliveryBehavior<CrudeReceiptDeliveryInput>
{
    public const string Implementation = "crude_receipt_transfer_v1";
    public override string Profile => Implementation;
    private static readonly ImmutableArray<DeliveryCapability> Supported = [
        new("accepted_receipt_events", "Certified native receipt transfers"),
        new("dispatch_events", "Native dispatch transfers"),
        new("receipt_reconciliation", "Native receipt, reservation and dispatch reconciliation")];

    protected override DeliveryAssessment AssessResolved(CrudeReceiptDeliveryInput inputs)
    {
        if (inputs.Terms is null || inputs.Terms.Implementation != CrudeReceiptCash.Implementation || inputs.Receipts.IsDefault ||
            inputs.Dispositions.IsDefault || inputs.Allocations.IsDefault || inputs.Dispatches.IsDefault || inputs.Invoices.IsDefault ||
            inputs.Payments.IsDefault || inputs.Reversals.IsDefault) throw Invalid();
        var result = CrudeInventoryCash.Reconcile(inputs.Terms, inputs.Receipts, inputs.Dispositions, inputs.Allocations,
            inputs.Dispatches, inputs.Invoices, inputs.Payments, inputs.Reversals, inputs.AsOf).Balances;
        var events = inputs.Receipts.Select(r => new DeliveryActivity(r.Id, "accepted_receipt", "Accepted receipt",
                new DeliveryTransferGeometry(r.AcceptedAt), r.Location.CustodyPoint, r.Quantity.Amount.ToString()))
            .Concat(inputs.Dispatches.Select(d => new DeliveryActivity(d.Id, "dispatch", "Dispatched transfer",
                new DeliveryTransferGeometry(d.DispatchedAt), inputs.Terms.Location.CustodyPoint, d.Quantity.Amount.ToString())))
            .OrderBy(e => e.Geometry.From).ThenBy(e => e.Stage, StringComparer.Ordinal).ThenBy(e => e.SourceId).ToImmutableArray();
        return new(Profile, inputs.Terms.Quantity.Key, events, [
            new("contractual", "Contractual receipt", inputs.Terms.ContractQuantity.Amount.ToString()),
            new("accepted", "Accepted receipt", result.AcceptedQuantity.Amount.ToString()),
            new("remaining", "Contractual quantity remaining", result.RemainingQuantity.Amount.ToString()),
            new("owned", "Owned quantity", result.OwnedQuantity.Amount.ToString()),
            new("unreserved_inventory", "Unreserved owned inventory", result.OwnedInventoryQuantity.Amount.ToString()),
            new("reserved", "Remaining onward reservations", result.ReservedQuantity.Amount.ToString()),
            new("dispatched", "Dispatched quantity", result.DispatchedQuantity.Amount.ToString()),
            new("undisposed", "Accepted quantity awaiting disposition", result.UndisposedQuantity.Amount.ToString())],
            [new("receipt_reconciliation", "Native accepted-receipt reconciliation", "within_limit", null, null,
                result.AcceptedQuantity.Amount.ToString(), inputs.Terms.ContractQuantity.Amount.ToString(), result.RemainingQuantity.Amount.ToString(), "quantity")], Supported);
    }
}
