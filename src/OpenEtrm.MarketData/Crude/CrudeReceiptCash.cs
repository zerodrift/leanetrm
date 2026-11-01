using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Crude;

public enum CrudeReceiptTitle { NotTransferred = 1, Transferred }
public enum CrudeDispositionKind { OwnedInventory = 1, OnwardDelivery }
public enum CrudeCashComponent { Consideration = 1, HandlingFee }
public enum CrudeInvoiceState { Open = 1, PartPaid, Overdue, PartPaidOverdue, Paid, NoPaymentDue }
public sealed record CrudeCashRate(UnitPrice Price, ExactDecimal Direction);
public sealed record CrudeCashRounding(string Implementation, int DecimalPlaces, CashTieRule TieRule);
public sealed record CrudeReceiptCashTerms(string Implementation, Guid TradeId, Guid BuyerLegalEntityId,
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
public sealed record CrudeReceiptInvoice(Guid Id, CrudeCashComponent Component, Guid ReceiptId,
    int ComponentOrdinal, Guid? PriorInvoiceId, DateTimeOffset IssuedAt)
{
    public int EventVersion { get; init; }
}
public sealed record CrudeReceiptPayment(Guid Id, Guid InvoiceId, Money Amount, DateTimeOffset PaidAt)
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

    public static CrudeReceiptCashResult Reconcile(CrudeReceiptCashTerms terms,
        ImmutableArray<CrudeAcceptedReceipt> receipts, ImmutableArray<CrudeReceiptDisposition> dispositions,
        ImmutableArray<CrudeReceiptDispatch> dispatches, ImmutableArray<CrudeReceiptInvoice> invoices,
        ImmutableArray<CrudeReceiptPayment> payments, DateTimeOffset asOf) =>
        ReconcileCore(terms, receipts, dispositions, dispatches, invoices, payments, asOf, false);

    public static CrudeReceiptCashResult Reconcile(CrudeReceiptCashTerms terms,
        ImmutableArray<CrudeAcceptedReceipt> receipts, ImmutableArray<CrudeReceiptDisposition> dispositions,
        ImmutableArray<CrudeReceiptDispatch> dispatches, ImmutableArray<CrudeReceiptInvoice> invoices,
        ImmutableArray<CrudeReceiptPayment> payments, ImmutableArray<CrudeCashRecordReversal> reversals, DateTimeOffset asOf)
    {
        RequireTerms(terms);
        var selected = SelectActiveCash(terms, invoices, payments, reversals, asOf);
        return ReconcileCore(terms, receipts, dispositions, dispatches, selected.Invoices, selected.Payments, asOf, true) with
        { ReversedInvoiceIds = selected.ReversedInvoices, ReversedPaymentIds = selected.ReversedPayments };
    }

    private static CrudeReceiptCashResult ReconcileCore(CrudeReceiptCashTerms terms,
        ImmutableArray<CrudeAcceptedReceipt> receipts, ImmutableArray<CrudeReceiptDisposition> dispositions,
        ImmutableArray<CrudeReceiptDispatch> dispatches, ImmutableArray<CrudeReceiptInvoice> invoices,
        ImmutableArray<CrudeReceiptPayment> payments, DateTimeOffset asOf, bool reversedHistory)
    {
        RequireTerms(terms);
        RequireTime(asOf, "crude_context");
        RequireFacts(receipts, r => r.Id); RequireFacts(dispositions, d => d.Id);
        RequireFacts(dispatches, d => d.Id); RequireFacts(invoices, i => i.Id); RequireFacts(payments, p => p.Id);
        var byReceipt = receipts.ToDictionary(r => r.Id);
        var byDisposition = dispositions.ToDictionary(d => d.Id);
        var byInvoice = invoices.ToDictionary(i => i.Id);
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
                    disposition.OwnerLegalEntityId != terms.BuyerLegalEntityId)
                    throw Error("crude_inventory_title", "Owned inventory requires the purchasing entity and retained custody point explicitly.");
                inventory += disposition.Quantity.Amount;
            }
            else if (disposition.Kind == CrudeDispositionKind.OnwardDelivery)
            {
                var sale = disposition.Sale;
                if (sale is null || sale.TradeId == Guid.Empty || sale.TradeId == terms.TradeId || sale.Revision < 1 ||
                    sale.SellerLegalEntityId != terms.BuyerLegalEntityId || sale.Grade != terms.Grade || sale.Location != terms.Location ||
                    sale.Quantity != terms.Quantity.Key || sale.Delivery is null || sale.ContractQuantity is null ||
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
        if (invoices.Select(i => (i.ReceiptId, i.Component)).Distinct().Count() != invoices.Length)
            throw Error("crude_invoice_duplicate", "A receipt can be invoiced only once for each independent cash component.");
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
                if (!byReceipt.TryGetValue(invoice.ReceiptId, out var receipt)) throw Error("crude_invoice_receipt", "Invoice requires its accepted receipt.");
                RequireTime(invoice.IssuedAt, "crude_invoice_time");
                if (invoice.IssuedAt < receipt.AcceptedAt || invoice.IssuedAt > asOf || priorIssuedAt is {} previous && invoice.IssuedAt < previous) throw Error("crude_invoice_time", "Invoice must follow receipt acceptance within the selected context.");
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
        Quantity Q(ExactDecimal value) => new(value, terms.Quantity.Key);
        return new(Q(accepted), Q(terms.ContractQuantity.Amount - accepted), Q(accepted - inventory - onward), Q(inventory), Q(onward),
            Q(accepted - dispatched), Q(onward - dispatched), Q(dispatched), invoiceBalances.ToImmutable(), cashBalances.ToImmutable());
    }

    private sealed record CashEvent(int Version, DateTimeOffset At, Guid Id, object Fact);
    private sealed record ActiveCash(ImmutableArray<CrudeReceiptInvoice> Invoices, ImmutableArray<CrudeReceiptPayment> Payments,
        ImmutableArray<Guid> ReversedInvoices, ImmutableArray<Guid> ReversedPayments);

    // Event versions establish acceptance order; timestamps alone cannot order
    // same-instant corrections. Removing a payment record does not book a refund.
    private static ActiveCash SelectActiveCash(CrudeReceiptCashTerms terms, ImmutableArray<CrudeReceiptInvoice> invoices,
        ImmutableArray<CrudeReceiptPayment> payments, ImmutableArray<CrudeCashRecordReversal> reversals, DateTimeOffset asOf)
    {
        RequireTime(asOf, "crude_context");
        RequireFacts(invoices, i => i.Id); RequireFacts(payments, p => p.Id); RequireFacts(reversals, r => r.Id);
        var events = invoices.Select(i => new CashEvent(i.EventVersion, i.IssuedAt, i.Id, i))
            .Concat(payments.Select(p => new CashEvent(p.EventVersion, p.PaidAt, p.Id, p)))
            .Concat(reversals.Select(r => new CashEvent(r.EventVersion, r.ReversedAt, r.Id, r))).OrderBy(e => e.Version).ToArray();
        if (events.Any(e => e.Version <= 0) || events.Select(e => e.Version).Distinct().Count() != events.Length ||
            events.Select(e => e.Id).Distinct().Count() != events.Length)
            throw Error("crude_cash_history", "Cash facts require distinct explicit accepted event versions and identities.");
        var activeInvoices = new Dictionary<Guid, CrudeReceiptInvoice>();
        var activePayments = new Dictionary<Guid, CrudeReceiptPayment>();
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
                case CrudeReceiptInvoice invoice:
                    if (invoice.Component == CrudeCashComponent.HandlingFee && terms.HandlingFee is null)
                        throw Error("crude_invoice_component", "Invoice history cannot add an uncontracted handling-fee component.");
                    var latest = activeInvoices.Values.Where(i => i.Component == invoice.Component).MaxBy(i => i.ComponentOrdinal);
                    if (!Enum.IsDefined(invoice.Component) || invoice.ComponentOrdinal != componentOrdinals.GetValueOrDefault(invoice.Component) + 1 ||
                        invoice.PriorInvoiceId != latest?.Id)
                        throw Error("crude_invoice_chain", "Each issued invoice requires its next immutable component ordinal and latest active predecessor.");
                    if (activeInvoices.Values.Any(i => i.Component == invoice.Component && i.ReceiptId == invoice.ReceiptId))
                        throw Error("crude_invoice_duplicate", "The accepted receipt component already has an active invoice.");
                    componentOrdinals[invoice.Component] = invoice.ComponentOrdinal;
                    activeInvoices.Add(invoice.Id, invoice);
                    break;
                case CrudeReceiptPayment payment:
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

    private static void RequireTerms(CrudeReceiptCashTerms terms)
    {
        if (terms is null || terms.Implementation != Implementation || terms.TradeId == Guid.Empty || terms.BuyerLegalEntityId == Guid.Empty ||
            terms.Location is null || terms.Quantity is null || terms.Delivery is null || terms.Consideration?.Price is null ||
            terms.Consideration.Price.Value.CompareTo(default) <= 0 || terms.Consideration.Direction != ExactDecimal.Parse("-1") ||
            terms.Quantity.Dimension.Code != "volume" || terms.StandardTemperature != ExactDecimal.Parse("60") || terms.TemperatureScale != "fahrenheit")
            throw Error("crude_receipt_terms", "Select the fixed-price purchase receipt contract with explicit standard volume, ownership and cash terms.");
        foreach (var reference in new[] { terms.Quantity.Key, terms.Quantity.Commodity, terms.Quantity.MeasurementBasis,
            terms.Quantity.Unit, terms.Quantity.Dimension, terms.Consideration.Price.Unit.Currency })
        {
            Money.RequireKey(reference);
            if (reference.Revision != terms.Quantity.Key.Revision)
                throw Error("crude_receipt_terms", "All receipt quantity and currency references must retain the exact selected revision.");
        }
        Money.RequireKey(terms.Grade);
        if (terms.Grade.Revision != terms.Quantity.Key.Revision || terms.Location.Facility.Revision != terms.Quantity.Key.Revision)
            throw Error("crude_receipt_terms", "The receipt contract requires exact references from one selected revision.");
        RequireQuantity(terms.ContractQuantity, terms.Quantity.Key, "crude_receipt_terms");
        RequireTime(terms.PaymentAt, "crude_receipt_terms");
        if (terms.PaymentAt < terms.Delivery.To) throw Error("crude_receipt_terms", "Retain the original contractual payment date.");
        RequireRounding(terms.Rounding);
        foreach (var rate in terms.HandlingFee is null ? new[] { terms.Consideration } : new[] { terms.Consideration, terms.HandlingFee })
            if (rate.Price is null || rate.Price.Value.CompareTo(default) < 0 || !Sign(rate.Direction) || rate.Price.Unit.Quantity != terms.Quantity.Key || rate.Price.Unit.Multiplier != ExactDecimal.Parse("1") ||
                rate.Price.Unit.Currency != terms.Consideration.Price.Unit.Currency)
                throw Error("crude_receipt_terms", "Cash components require their independent explicit signs and exact native units/currency.");
    }
    private static void RequirePaymentAmount(Money? amount, ReferenceKey currency, ExactDecimal direction, CrudeCashRounding rounding)
    {
        if (amount is null || amount.Currency != currency || amount.Amount == default ||
            (amount.Amount.CompareTo(default) > 0) != (direction.CompareTo(default) > 0) ||
            Round(ExactMoney.FromMoney(amount), rounding) != amount)
            throw Error("crude_payment_amount", "Payments require the component currency, direction and cash precision explicitly.");
    }

    private static void RequireRounding(CrudeCashRounding rounding)
    {
        if (rounding is null || rounding.Implementation != RoundingImplementation || rounding.DecimalPlaces is < 0 or > 12 || rounding.TieRule != CashTieRule.ToEven)
            throw Error("crude_cash_rounding", "Select explicit currency precision and the supported cash rounding version.");
    }
    private static Money Round(ExactMoney value, CrudeCashRounding rounding) => value.ToMoney(new AmountFormat(rounding.DecimalPlaces, rounding.TieRule, false, false));
    private static bool Sign(ExactDecimal value) => value == ExactDecimal.Parse("1") || value == ExactDecimal.Parse("-1");
    private static ExactDecimal Magnitude(ExactDecimal value) => value.CompareTo(default) < 0 ? default(ExactDecimal) - value : value;
    private static ExactDecimal Sum(IEnumerable<ExactDecimal> values) => values.Aggregate(default(ExactDecimal), (sum, value) => sum + value);
    private static void RequireQuantity(Quantity quantity, ReferenceKey specification, string code)
    { if (quantity is null || quantity.Specification != specification || quantity.Amount.CompareTo(default) <= 0) throw Error(code, "Use a positive quantity in the exact contractual standard-volume specification."); }
    private static void RequireTime(DateTimeOffset at, string code)
    { if (!TradingInstant.IsSupported(at)) throw Error(code, "Use a supported exact UTC instant."); }
    private static void RequireFacts<T>(ImmutableArray<T> facts, Func<T, Guid> key) where T : class
    {
        if (facts.IsDefault || facts.Length > 10000 || facts.Any(f => f is null) || facts.Any(f => key(f) == Guid.Empty) || facts.Select(key).Distinct().Count() != facts.Length)
            throw Error("crude_receipt_facts", "Supply explicit bounded facts with distinct durable identities.");
    }
    private static MarketDataException Error(string code, string message) => new(code, message);
}
