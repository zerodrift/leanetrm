using System.Collections.Immutable;
using static OpenEtrm.MarketData.Crude.CrudePhysicalCashRules;
namespace OpenEtrm.MarketData.Crude;

public sealed record CrudeSaleDelivery(Guid Id, DateTimeOffset DeliveredAt, Quantity Quantity,
    ReferenceKey Grade, CrudeDeliveryLocation Location, CrudeReceiptTitle Title);
public static class CrudeSaleCash
{
    public const string Implementation = "physical_crude_sale_cash_v1";
    public static CrudeReceiptCashResult Reconcile(CrudePhysicalCashTerms terms,
        ImmutableArray<CrudeSaleDelivery> deliveries, ImmutableArray<CrudeCashInvoice> invoices,
        ImmutableArray<CrudeCashPayment> payments, ImmutableArray<CrudeCashRecordReversal> reversals, DateTimeOffset asOf)
    {
        RequireTerms(terms,Implementation,"1");RequireTime(asOf,"crude_context");RequireFacts(deliveries,d=>d.Id);
        var delivered=default(ExactDecimal);
        foreach(var delivery in deliveries)
        {
            RequireQuantity(delivery.Quantity,terms.Quantity.Key,"crude_sale_quantity");
            RequireTime(delivery.DeliveredAt,"crude_sale_delivery");
            if(delivery.Grade!=terms.Grade||delivery.Location!=terms.Location||delivery.Title!=CrudeReceiptTitle.Transferred||
                delivery.DeliveredAt<terms.Delivery.From||delivery.DeliveredAt>=terms.Delivery.To||delivery.DeliveredAt>asOf)
                throw Error("crude_sale_delivery","Sales cash requires exact compatible certified deliveries with transferred title inside the sale window.");
            delivered+=delivery.Quantity.Amount;
        }
        if(delivered.CompareTo(terms.ContractQuantity.Amount)>0)throw Error("crude_sale_quantity","Delivered quantity cannot exceed the zero-tolerance sale obligation.");
        var selected=CrudeCashLedger.SelectActiveCash(terms,invoices,payments,reversals,asOf);
        var cash=CrudeCashLedger.Reconcile(terms,[..deliveries.Select(d=>new CrudeCashSource(d.Id,d.DeliveredAt,d.Quantity))],selected.Invoices,selected.Payments,asOf,true);
        Quantity Q(ExactDecimal amount)=>new(amount,terms.Quantity.Key);
        return new(Q(delivered),Q(terms.ContractQuantity.Amount-delivered),Q(default),Q(default),Q(default),Q(default),Q(default),Q(delivered),cash.Invoices,cash.Cash)
        {ReversedInvoiceIds=selected.ReversedInvoices,ReversedPaymentIds=selected.ReversedPayments};
    }
}
