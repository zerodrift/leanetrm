using System.Collections.Immutable;
using static OpenEtrm.MarketData.Crude.CrudePhysicalCashRules;
namespace OpenEtrm.MarketData.Crude;

public sealed record CrudeInventoryAllocation(Guid Id,Guid ReceiptId,Quantity Quantity,DateTimeOffset AllocatedAt,CrudeOnwardSale Sale);
public sealed record CrudeInventoryCashResult(CrudeReceiptCashResult Balances,ImmutableDictionary<Guid,Quantity> Available,
    ImmutableDictionary<Guid,Quantity> RemainingReservations);

// Explicit reservations split owned quantities without changing receipt or purchase cash facts.
public static class CrudeInventoryCash
{
 public static CrudeInventoryCashResult Reconcile(CrudePhysicalCashTerms terms,ImmutableArray<CrudeAcceptedReceipt> receipts,
    ImmutableArray<CrudeReceiptDisposition> dispositions,ImmutableArray<CrudeInventoryAllocation> allocations,
    ImmutableArray<CrudeReceiptDispatch> dispatches,ImmutableArray<CrudeCashInvoice> invoices,
    ImmutableArray<CrudeCashPayment> payments,ImmutableArray<CrudeCashRecordReversal> reversals,DateTimeOffset asOf)
 {
    RequireTerms(terms);RequireFacts(receipts,r=>r.Id);RequireFacts(dispositions,d=>d.Id);RequireFacts(allocations,a=>a.Id);
    RequireFacts(dispatches,d=>d.Id);
    // Validate original title/ownership even when allocations consume a lot completely.
    _=CrudeReceiptCash.Reconcile(terms,receipts,dispositions,[],[],[],[],asOf);
    var available=ImmutableDictionary.CreateBuilder<Guid,Quantity>();
    var selected=ImmutableArray.CreateBuilder<CrudeReceiptDisposition>();
    foreach(var allocation in allocations)
    {
        RequireQuantity(allocation.Quantity,terms.Quantity.Key,"crude_allocation_quantity");
        var source=dispositions.Where(d=>d.ReceiptId==allocation.ReceiptId&&d.Kind==CrudeDispositionKind.OwnedInventory).ToArray();
        if(source.Length!=1||!receipts.Any(r=>r.Id==allocation.ReceiptId)||allocation.Sale is null||allocation.AllocatedAt<source[0].RecordedAt)
            throw Error("crude_allocation_source","An allocation requires one current owned receipt lot and an explicit onward sale after ownership was recorded.");
        selected.Add(new(allocation.Id,allocation.ReceiptId,allocation.Quantity,CrudeDispositionKind.OnwardDelivery,allocation.AllocatedAt,allocation.Sale,null,null));
    }
    foreach(var disposition in dispositions)
    {
        if(disposition.Kind!=CrudeDispositionKind.OwnedInventory){selected.Add(disposition);continue;}
        var allocated=Sum(allocations.Where(a=>a.ReceiptId==disposition.ReceiptId).Select(a=>a.Quantity.Amount));
        var remainder=disposition.Quantity.Amount-allocated;
        if(remainder.CompareTo(default)<0)throw Error("crude_allocation_capacity","Reservations cannot exceed the selected owned lot.");
        if(!available.TryAdd(disposition.ReceiptId,new(remainder,terms.Quantity.Key)))throw Error("crude_allocation_source","Owned lots require one explicit disposition.");
        // Fully reserved lots have no remaining owned-inventory disposition.
        if(remainder.CompareTo(default)>0)selected.Add(disposition with{Quantity=new(remainder,terms.Quantity.Key)});
    }
    var ledger=CrudeReceiptCash.Reconcile(terms,receipts,selected.ToImmutable(),dispatches,invoices,payments,reversals,asOf);
    var remaining=selected.Where(d=>d.Kind==CrudeDispositionKind.OnwardDelivery).ToImmutableDictionary(d=>d.Id,
        d=>new Quantity(d.Quantity.Amount-Sum(dispatches.Where(x=>x.DispositionId==d.Id).Select(x=>x.Quantity.Amount)),terms.Quantity.Key));
    return new(ledger,available.ToImmutable(),remaining);
 }
}
