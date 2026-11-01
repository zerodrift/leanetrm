using System.Collections.Immutable;
using static OpenEtrm.MarketData.Crude.CrudePhysicalCashRules;
using static OpenEtrm.MarketData.Crude.CrudeReceiptCash;
namespace OpenEtrm.MarketData.Crude;

internal sealed record CrudeCashSource(Guid Id, DateTimeOffset At, Quantity Quantity);
internal static class CrudeCashLedger
{
    public static CrudeInvoiceAllocation AllocateInvoice(UnitPrice price, ExactDecimal direction,
        Quantity priorInvoiced, Quantity receipt, CrudeCashRounding rounding)
    {
        RequireRounding(rounding);
        if (price is null || priorInvoiced is null || receipt is null || !Sign(direction) || price.Value.CompareTo(default) < 0 ||
            priorInvoiced.Specification != price.Unit.Quantity || receipt.Specification != price.Unit.Quantity ||
            priorInvoiced.Amount.CompareTo(default) < 0 || receipt.Amount.CompareTo(default) <= 0)
            throw Error("crude_invoice_terms", "Supply an explicit nonnegative rate, unit direction and compatible prior/receipt quantities.");
        var cumulative = new Quantity(priorInvoiced.Amount + receipt.Amount, receipt.Specification);
        var prior = Round(price.ExactTimes(priorInvoiced).Scale(direction), rounding);
        var next = Round(price.ExactTimes(cumulative).Scale(direction), rounding);
        return new(price.ExactTimes(receipt).Scale(direction), new(next.Amount - prior.Amount, price.Unit.Currency), cumulative);
    }

    internal static (ImmutableArray<CrudeInvoiceBalance> Invoices, ImmutableArray<CrudeComponentCashBalance> Cash) Reconcile(
        CrudePhysicalCashTerms terms, ImmutableArray<CrudeCashSource> sources, ImmutableArray<CrudeCashInvoice> invoices,
        ImmutableArray<CrudeCashPayment> payments, DateTimeOffset asOf, bool reversedHistory)
    {
        var bySource=sources.ToDictionary(s=>s.Id);
        var byInvoice=invoices.ToDictionary(i=>i.Id);
        var zero=default(ExactDecimal);
        if (invoices.Select(i => (i.SourceId, i.Component)).Distinct().Count() != invoices.Length)
            throw Error("crude_invoice_duplicate", "A quantity source can be invoiced only once for each independent cash component.");
        foreach (var payment in payments)
        {
            if (!byInvoice.TryGetValue(payment.InvoiceId, out var invoice)) throw Error("crude_payment_invoice", "Payment requires its exact issued invoice.");
            RequireTime(payment.PaidAt, "crude_payment_time");
            if (payment.PaidAt < invoice.IssuedAt || payment.PaidAt > asOf) throw Error("crude_payment_time", "Payment must follow invoice issue within the selected context.");
        }
        var invoiceBalances = ImmutableArray.CreateBuilder<CrudeInvoiceBalance>();
        var cashBalances = ImmutableArray.CreateBuilder<CrudeComponentCashBalance>();
        foreach (var component in new[] { CrudeCashComponent.Consideration, CrudeCashComponent.HandlingFee })
        {
            var rate = component == CrudeCashComponent.Consideration ? terms.Consideration : terms.HandlingFee;
            var selected = invoices.Where(i => i.Component == component).OrderBy(i => i.ComponentOrdinal).ToArray();
            if (rate is null)
            {
                if (selected.Length != 0) throw Error("crude_invoice_component", "No handling fee was contracted.");
                continue;
            }
            var priorQuantity = new Quantity(zero, terms.Quantity.Key);
            Guid? priorId = null;
            DateTimeOffset? priorIssuedAt = null;
            var totalInvoiced = zero; var totalPaid = zero;
            for (var n = 0; n < selected.Length; n++)
            {
                var invoice = selected[n];
                if ((!reversedHistory && invoice.ComponentOrdinal != n + 1) || invoice.PriorInvoiceId != priorId)
                    throw Error("crude_invoice_chain", "Invoice allocation requires the exact contiguous component ordinal and prior invoice.");
                if (!bySource.TryGetValue(invoice.SourceId, out var receipt)) throw Error("crude_invoice_receipt", "Invoice requires its exact certified quantity source.");
                RequireTime(invoice.IssuedAt, "crude_invoice_time");
                if (invoice.IssuedAt < receipt.At || invoice.IssuedAt > asOf || priorIssuedAt is {} previous && invoice.IssuedAt < previous) throw Error("crude_invoice_time", "Invoice must follow the certified source event within the selected context.");
                var allocation = AllocateInvoice(rate.Price, rate.Direction, priorQuantity, receipt.Quantity, terms.Rounding);
                priorQuantity = allocation.CumulativeQuantity; priorId = invoice.Id; priorIssuedAt = invoice.IssuedAt;
                var paid = zero;
                foreach (var payment in payments.Where(p => p.InvoiceId == invoice.Id))
                {
                    if (allocation.Amount.Amount == zero)
                        throw Error("crude_payment_amount", "A zero invoice allocation cannot accept payment.");
                    RequirePaymentAmount(payment.Amount, allocation.Amount.Currency, rate.Direction, terms.Rounding);
                    paid += payment.Amount.Amount;
                }
                if (Magnitude(paid).CompareTo(Magnitude(allocation.Amount.Amount)) > 0) throw Error("crude_payment_amount", "Payment cannot exceed the selected invoice amount.");
                var open = allocation.Amount.Amount - paid;
                var state = allocation.Amount.Amount == zero ? CrudeInvoiceState.NoPaymentDue : open == zero ? CrudeInvoiceState.Paid :
                    asOf > terms.PaymentAt ? paid == zero ? CrudeInvoiceState.Overdue : CrudeInvoiceState.PartPaidOverdue :
                    paid == zero ? CrudeInvoiceState.Open : CrudeInvoiceState.PartPaid;
                invoiceBalances.Add(new(invoice.Id, component, allocation.ExactAmount, allocation.Amount,
                    new(paid, allocation.Amount.Currency), new(open, allocation.Amount.Currency), terms.PaymentAt, state));
                totalInvoiced += allocation.Amount.Amount; totalPaid += paid;
            }
            var exactOriginal = rate.Price.ExactTimes(terms.ContractQuantity).Scale(rate.Direction);
            var exactInvoiced = rate.Price.ExactTimes(priorQuantity).Scale(rate.Direction);
            var original = Round(exactOriginal, terms.Rounding);
            cashBalances.Add(new(component, original, new(totalInvoiced, original.Currency), new(totalPaid, original.Currency),
                new(original.Amount - totalPaid, original.Currency), new(original.Amount - totalInvoiced, original.Currency),
                exactOriginal, exactInvoiced, exactOriginal.Subtract(exactInvoiced)));
        }
        if (invoices.Any(i => !Enum.IsDefined(i.Component))) throw Error("crude_invoice_component", "Select an explicit contracted cash component.");
        return (invoiceBalances.ToImmutable(),cashBalances.ToImmutable());
    }
    private sealed record CashEvent(int Version, DateTimeOffset At, Guid Id, object Fact);
    internal sealed record ActiveCash(ImmutableArray<CrudeCashInvoice> Invoices, ImmutableArray<CrudeCashPayment> Payments,
        ImmutableArray<Guid> ReversedInvoices, ImmutableArray<Guid> ReversedPayments);

    // Event versions establish acceptance order; timestamps alone cannot order
    // same-instant corrections. Removing a payment record does not book a refund.
    internal static ActiveCash SelectActiveCash(CrudePhysicalCashTerms terms, ImmutableArray<CrudeCashInvoice> invoices,
        ImmutableArray<CrudeCashPayment> payments, ImmutableArray<CrudeCashRecordReversal> reversals, DateTimeOffset asOf)
    {
        RequireTime(asOf, "crude_context");
        RequireFacts(invoices, i => i.Id); RequireFacts(payments, p => p.Id); RequireFacts(reversals, r => r.Id);
        var events = invoices.Select(i => new CashEvent(i.EventVersion, i.IssuedAt, i.Id, i))
            .Concat(payments.Select(p => new CashEvent(p.EventVersion, p.PaidAt, p.Id, p)))
            .Concat(reversals.Select(r => new CashEvent(r.EventVersion, r.ReversedAt, r.Id, r))).OrderBy(e => e.Version).ToArray();
        if (events.Any(e => e.Version <= 0) || events.Select(e => e.Version).Distinct().Count() != events.Length ||
            events.Select(e => e.Id).Distinct().Count() != events.Length)
            throw Error("crude_cash_history", "Cash facts require distinct explicit accepted event versions and identities.");
        var activeInvoices = new Dictionary<Guid, CrudeCashInvoice>();
        var activePayments = new Dictionary<Guid, CrudeCashPayment>();
        var componentOrdinals = new Dictionary<CrudeCashComponent, int>();
        var reversedInvoices = ImmutableArray.CreateBuilder<Guid>();
        var reversedPayments = ImmutableArray.CreateBuilder<Guid>();
        DateTimeOffset? previousAt = null;
        foreach (var item in events)
        {
            RequireTime(item.At, "crude_reversal_time");
            if (item.At > asOf || previousAt is {} prior && item.At < prior)
                throw Error("crude_reversal_time", "Accepted cash history must remain chronological within the selected effective context.");
            previousAt = item.At;
            switch (item.Fact)
            {
                case CrudeCashInvoice invoice:
                    if (invoice.Component == CrudeCashComponent.HandlingFee && terms.HandlingFee is null)
                        throw Error("crude_invoice_component", "Invoice history cannot add an uncontracted handling-fee component.");
                    var latest = activeInvoices.Values.Where(i => i.Component == invoice.Component).MaxBy(i => i.ComponentOrdinal);
                    if (!Enum.IsDefined(invoice.Component) || invoice.ComponentOrdinal != componentOrdinals.GetValueOrDefault(invoice.Component) + 1 ||
                        invoice.PriorInvoiceId != latest?.Id)
                        throw Error("crude_invoice_chain", "Each issued invoice requires its next immutable component ordinal and latest active predecessor.");
                    if (activeInvoices.Values.Any(i => i.Component == invoice.Component && i.SourceId == invoice.SourceId))
                        throw Error("crude_invoice_duplicate", "The certified source component already has an active invoice.");
                    componentOrdinals[invoice.Component] = invoice.ComponentOrdinal;
                    activeInvoices.Add(invoice.Id, invoice);
                    break;
                case CrudeCashPayment payment:
                    if (!activeInvoices.TryGetValue(payment.InvoiceId, out var paymentInvoice))
                        throw Error("crude_payment_invoice", "Payment requires an issued invoice whose record has not been reversed.");
                    var rate = paymentInvoice.Component == CrudeCashComponent.Consideration ? terms.Consideration : terms.HandlingFee;
                    if (rate is null || rate.Price.Value == default)
                        throw Error("crude_payment_amount", "Payment history requires a nonzero contracted cash component.");
                    RequirePaymentAmount(payment.Amount, rate.Price.Unit.Currency, rate.Direction, terms.Rounding);
                    activePayments.Add(payment.Id, payment);
                    break;
                case CrudeCashRecordReversal reversal:
                    if (reversal.Implementation != ReversalImplementation || !Enum.IsDefined(reversal.Kind))
                        throw Error("crude_reversal_policy", "Select the explicit full-record correction contract.");
                    if (reversal.Kind == CrudeCashReversalKind.PaymentRecord)
                    {
                        if (!activePayments.Remove(reversal.TargetId))
                            throw Error("crude_reversal_target", "Select an exact active payment record for a single full reversal.");
                        reversedPayments.Add(reversal.TargetId);
                    }
                    else
                    {
                        if (!activeInvoices.TryGetValue(reversal.TargetId, out var target))
                            throw Error("crude_reversal_target", "Select an exact active invoice record for reversal.");
                        if (activePayments.Values.Any(p => p.InvoiceId == target.Id))
                            throw Error("crude_invoice_active_payment", "Reverse active payment records before reversing their invoice.");
                        if (activeInvoices.Values.Any(i => i.Component == target.Component && i.ComponentOrdinal > target.ComponentOrdinal))
                            throw Error("crude_invoice_reversal_order", "Reverse the newest active component invoice first to preserve cumulative allocation.");
                        activeInvoices.Remove(target.Id);
                        reversedInvoices.Add(target.Id);
                    }
                    break;
            }
        }
        return new([..activeInvoices.Values.OrderBy(i => i.EventVersion)], [..activePayments.Values.OrderBy(p => p.EventVersion)],
            reversedInvoices.ToImmutable(), reversedPayments.ToImmutable());
    }

}
