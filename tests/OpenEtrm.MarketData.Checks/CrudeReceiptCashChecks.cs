using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Crude;

internal static class CrudeReceiptCashChecks
{
    internal static void Run()
    {
        var revision = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        ReferenceKey R(string code) => new(revision, code);
        ExactDecimal D(string value) => ExactDecimal.Parse(value);
        var quantity = new QuantitySpecification(R("standard_barrels"), R("synthetic_crude"), R("standard_60f"), R("barrel"), R("volume"));
        Quantity Q(string value) => new(D(value), quantity.Key);
        var location = new CrudeDeliveryLocation(R("synthetic_facility"), R("synthetic_custody"));
        var start = new DateTimeOffset(2040, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var due = start.AddMonths(1).AddDays(5);
        var terms = new CrudeReceiptCashTerms(CrudeReceiptCash.Implementation, Guid.NewGuid(), Guid.NewGuid(), location, R("synthetic_grade"),
            quantity, D("60"), "fahrenheit", Q("1000"), new(start, start.AddMonths(1)), due,
            new(new UnitPrice(D("78"), new PriceUnit(R("usd"), quantity.Key, D("1"))), D("-1")),
            new(new UnitPrice(D("0.5"), new PriceUnit(R("usd"), quantity.Key, D("1"))), D("1")),
            new("currency_minor_units_to_even_v1", 2, CashTieRule.ToEven));
        var receipt = new CrudeAcceptedReceipt(Guid.NewGuid(), start.AddDays(2), Q("400"), terms.Grade, location, CrudeReceiptTitle.Transferred);
        var inventory = new CrudeReceiptDisposition(Guid.NewGuid(), receipt.Id, Q("250"), CrudeDispositionKind.OwnedInventory,
            start.AddDays(3), null, location.CustodyPoint, terms.BuyerLegalEntityId);
        var onward = new CrudeReceiptDisposition(Guid.NewGuid(), receipt.Id, Q("150"), CrudeDispositionKind.OnwardDelivery,
            start.AddDays(3), new(Guid.NewGuid(), 1, terms.BuyerLegalEntityId, terms.Grade, location, quantity.Key, Q("1000"), terms.Delivery), null, null);
        var invoice = new CrudeReceiptInvoice(Guid.NewGuid(), CrudeCashComponent.Consideration, receipt.Id, 1, null, start.AddDays(4));
        var feeInvoice = new CrudeReceiptInvoice(Guid.NewGuid(), CrudeCashComponent.HandlingFee, receipt.Id, 1, null, start.AddDays(4));
        var payment = new CrudeReceiptPayment(Guid.NewGuid(), invoice.Id, new(D("-10000"), R("usd")), due.AddDays(-1));
        CrudeReceiptCashResult Value(ImmutableArray<CrudeAcceptedReceipt> receipts, ImmutableArray<CrudeReceiptDisposition> dispositions,
            ImmutableArray<CrudeReceiptInvoice> invoices, ImmutableArray<CrudeReceiptPayment> payments) => CrudeReceiptCash.Reconcile(terms, receipts, dispositions, [], invoices, payments, due.AddDays(1));
        var partial = Value([receipt], [inventory, onward], [invoice, feeInvoice], [payment]);
        Equal(partial.AcceptedQuantity.Amount, "400"); Equal(partial.RemainingQuantity.Amount, "600");
        Equal(partial.UndisposedQuantity.Amount, "0"); Equal(partial.OwnedInventoryQuantity.Amount, "250"); Equal(partial.OnwardQuantity.Amount, "150");
        var cash = partial.Invoices.Single(i => i.InvoiceId == invoice.Id);
        Equal(cash.Amount.Amount, "-31200"); Equal(cash.Paid.Amount, "-10000"); Equal(cash.Open.Amount, "-21200");
        if (cash.State != CrudeInvoiceState.PartPaidOverdue) throw new Exception("Unpaid cash must remain overdue independently of physical receipt.");
        Equal(partial.Invoices.Single(i => i.InvoiceId == feeInvoice.Id).Amount.Amount, "200");
        var completion = receipt with { Id = Guid.NewGuid(), Quantity = Q("600") };
        var completed = Value([receipt, completion], [], [], []);
        Equal(completed.RemainingQuantity.Amount, "0"); Equal(completed.UndisposedQuantity.Amount, "1000");
        if (!completed.Invoices.IsEmpty) throw new Exception("Full receipt must not infer disposition, invoicing or payment.");
        Reject("crude_receipt_quantity", () => Value([receipt, completion with { Quantity = Q("601") }], [], [], []));
        Reject("crude_disposition_quantity", () => Value([receipt], [inventory with { Quantity = Q("401") }], [], []));
        Reject("crude_receipt_title", () => Value([receipt with { Title = CrudeReceiptTitle.NotTransferred }], [inventory], [], []));
        Reject("crude_invoice_duplicate", () => Value([receipt], [], [invoice, invoice with { Id = Guid.NewGuid() }], []));
        Reject("crude_payment_amount", () => Value([receipt], [], [invoice], [payment with { Amount = new(D("-31201"), R("usd")) }]));
        Reject("crude_payment_amount", () => Value([receipt], [], [invoice], [payment with { Amount = new(D("100"), R("usd")) }]));
        Reject("crude_payment_amount", () => Value([receipt], [], [invoice], [payment with { Amount = new(D("-100"), R("eur")) }]));
        Reject("crude_payment_amount", () => Value([receipt], [], [invoice], [payment with { Amount = new(D("-100.001"), R("usd")) }]));
        Reject("crude_receipt_time", () => Value([receipt with { AcceptedAt = start.AddTicks(-10) }], [], [], []));
        Reject("crude_receipt_reference", () => Value([receipt with { Grade = R("other_grade") }], [], [], []));
        Reject("crude_receipt_time", () => Value([receipt with { AcceptedAt = terms.Delivery.To }], [], [], []));
        var earlierReceipt = receipt with { Id = Guid.NewGuid(), Quantity = Q("100") };
        var backdatedInvoice = invoice with { Id = Guid.NewGuid(), ReceiptId = earlierReceipt.Id, ComponentOrdinal = 2,
            PriorInvoiceId = invoice.Id, IssuedAt = invoice.IssuedAt.AddTicks(-10) };
        Reject("crude_invoice_time", () => Value([receipt, earlierReceipt], [], [invoice, backdatedInvoice], []));
        var paid = Value([receipt], [], [invoice], [payment with { Amount = new(D("-31200"), R("usd")) }]);
        if (paid.Invoices.Single().State != CrudeInvoiceState.Paid) throw new Exception("An explicit full payment discharges only its selected invoice.");
        Equal(partial.Cash.Single(c => c.Component == CrudeCashComponent.Consideration).Original.Amount, "-78000");
        Equal(partial.Cash.Single(c => c.Component == CrudeCashComponent.Consideration).Outstanding.Amount, "-68000");
        Equal(partial.Cash.Single(c => c.Component == CrudeCashComponent.Consideration).Uninvoiced.Amount, "-46800");
        var dispatch = new CrudeReceiptDispatch(Guid.NewGuid(), onward.Id, Q("50"), start.AddDays(5), CrudeReceiptTitle.Transferred);
        var dispatched = CrudeReceiptCash.Reconcile(terms, [receipt], [inventory, onward], [dispatch], [], [], due);
        Equal(dispatched.OwnedQuantity.Amount, "350"); Equal(dispatched.ReservedQuantity.Amount, "100"); Equal(dispatched.DispatchedQuantity.Amount, "50");
        Equal(dispatched.Cash.Single(c => c.Component == CrudeCashComponent.Consideration).Outstanding.Amount, "-78000");
        Reject("crude_dispatch_time", () => CrudeReceiptCash.Reconcile(terms, [receipt], [onward],
            [dispatch with { DispatchedAt = onward.Sale!.Delivery.To }], [], [], due));
        Reject("crude_onward_quantity", () => Value([receipt], [onward with { Sale = onward.Sale! with { ContractQuantity = Q("149") } }], [], []));
        Reject("crude_dispatch_quantity", () => CrudeReceiptCash.Reconcile(terms, [receipt], [onward],
            [dispatch with { Quantity = Q("151") }], [], [], due));
        Reject("crude_invoice_chain", () => Value([receipt], [], [invoice with { ComponentOrdinal = 2 }], []));
        Reject("crude_inventory_title", () => Value([receipt], [inventory with { OwnerLegalEntityId = Guid.NewGuid() }], [], []));
        var scaledQuote = terms with { Consideration = new(new UnitPrice(D("7800"), new PriceUnit(R("usd"), quantity.Key, D("0.01"))), D("-1")) };
        Reject("crude_receipt_terms", () => CrudeReceiptCash.Reconcile(scaledQuote, [], [], [], [], [], due));
        var cents = new UnitPrice(D("0.01"), new PriceUnit(R("usd"), quantity.Key, D("1")));
        var first = CrudeReceiptCash.AllocateInvoice(cents, D("-1"), Q("0"), Q("0.5"), terms.Rounding);
        var second = CrudeReceiptCash.AllocateInvoice(cents, D("-1"), first.CumulativeQuantity, Q("0.5"), terms.Rounding);
        Equal(first.Amount.Amount, "0"); Equal(second.Amount.Amount, "-0.01");
        if (first.ExactAmount.ToExactString() != "-0.005" || second.ExactAmount.ToExactString() != "-0.005")
            throw new Exception("Invoice allocation retains exact contributions independently of cumulative rounding residuals.");
        if (first.RoundingEffect.ToExactString() != "0.005" || second.RoundingEffect.ToExactString() != "-0.005")
            throw new Exception("Cumulative allocations must preserve separately auditable rounding effects.");
        var pennyTerms = terms with { ContractQuantity = Q("1"), Consideration = new(cents, D("-1")), HandlingFee = null };
        var halfReceipt = receipt with { Quantity = Q("0.5") };
        var pennyState = CrudeReceiptCash.Reconcile(pennyTerms, [halfReceipt], [], [], [invoice], [], due);
        var pennyCash = pennyState.Cash.Single();
        if (pennyCash.ExactOriginal.ToExactString() != "-0.01" || pennyCash.ExactInvoiced.ToExactString() != "-0.005" ||
            pennyCash.ExactUninvoiced.ToExactString() != "-0.005")
            throw new Exception("Exact contractual remainder must stay separate from rounded payable cash.");
        Equal(pennyCash.Uninvoiced.Amount, "-0.01");
        if (pennyState.Invoices.Single().State != CrudeInvoiceState.NoPaymentDue)
            throw new Exception("A zero rounded allocation is not evidence of a payment.");
        var remainderInvoice = invoice with { Id = Guid.NewGuid(), ReceiptId = completion.Id, ComponentOrdinal = 2, PriorInvoiceId = invoice.Id };
        var allInvoiced = Value([receipt, completion], [], [remainderInvoice, invoice], []);
        Equal(allInvoiced.Cash.Single(c => c.Component == CrudeCashComponent.Consideration).Uninvoiced.Amount, "0");
        Equal(allInvoiced.Cash.Single(c => c.Component == CrudeCashComponent.Consideration).Outstanding.Amount, "-78000");
        Reversals(terms, receipt, completion, invoice, payment, due);
        Console.WriteLine("PASS crude receipt cash: partial quantities, explicit title/disposition, independent signed invoices, partial payments and overdue reconciliation");
    }
    private static void Reversals(CrudeReceiptCashTerms terms, CrudeAcceptedReceipt receipt, CrudeAcceptedReceipt completion,
        CrudeReceiptInvoice invoice, CrudeReceiptPayment payment, DateTimeOffset due)
    {
        invoice = invoice with { EventVersion = 1 };
        payment = payment with { EventVersion = 2 };
        var reversePayment = new CrudeCashRecordReversal(Guid.NewGuid(), CrudeCashReversalKind.PaymentRecord,
            payment.Id, due, 3, CrudeReceiptCash.ReversalImplementation);
        CrudeReceiptCashResult Value(ImmutableArray<CrudeReceiptInvoice> invoices, ImmutableArray<CrudeReceiptPayment> payments,
            ImmutableArray<CrudeCashRecordReversal> reversals) =>
            CrudeReceiptCash.Reconcile(terms, [receipt, completion], [], [], invoices, payments, reversals, due.AddDays(2));
        var restored = Value([invoice], [payment], [reversePayment]);
        Equal(restored.Invoices.Single().Paid.Amount, "0"); Equal(restored.Invoices.Single().Open.Amount, "-31200");
        Equal(restored.Cash.Single(c => c.Component == CrudeCashComponent.Consideration).Outstanding.Amount, "-78000");
        if (!restored.ReversedPaymentIds.SequenceEqual([payment.Id])) throw new Exception("Reversal must retain its original payment identity.");
        var voidInvoice = new CrudeCashRecordReversal(Guid.NewGuid(), CrudeCashReversalKind.InvoiceRecord,
            invoice.Id, due.AddDays(1), 4, CrudeReceiptCash.ReversalImplementation);
        Reject("crude_invoice_active_payment", () => Value([invoice], [payment], [voidInvoice]));
        var voided = Value([invoice], [payment], [reversePayment, voidInvoice]);
        Equal(voided.Cash.Single(c => c.Component == CrudeCashComponent.Consideration).Uninvoiced.Amount, "-78000");
        if (!voided.Invoices.IsEmpty || !voided.ReversedInvoiceIds.SequenceEqual([invoice.Id])) throw new Exception("Voiding must preserve history without active payable cash.");
        var second = invoice with { Id = Guid.NewGuid(), ReceiptId = completion.Id, ComponentOrdinal = 2,
            PriorInvoiceId = invoice.Id, EventVersion = 2 };
        Reject("crude_invoice_reversal_order", () => Value([invoice, second], [], [voidInvoice]));
        var voidSecond = voidInvoice with { Id = Guid.NewGuid(), TargetId = second.Id, EventVersion = 3 };
        var reissue = second with { Id = Guid.NewGuid(), ComponentOrdinal = 3, EventVersion = 4, IssuedAt = due.AddDays(1) };
        var reissued = Value([invoice, second, reissue], [], [voidSecond]);
        Equal(reissued.Cash.Single(c => c.Component == CrudeCashComponent.Consideration).Invoiced.Amount, "-78000");
        Equal(reissued.Invoices.Single(i => i.InvoiceId == reissue.Id).Amount.Amount, "-46800");
        var pennyTerms = terms with { ContractQuantity = new(ExactDecimal.Parse("1"), terms.Quantity.Key),
            Consideration = new(new UnitPrice(ExactDecimal.Parse("0.01"), terms.Consideration.Price.Unit), ExactDecimal.Parse("-1")), HandlingFee = null };
        var halves = new[] { receipt with { Quantity = new(ExactDecimal.Parse("0.5"), terms.Quantity.Key) },
            completion with { Quantity = new(ExactDecimal.Parse("0.5"), terms.Quantity.Key) } }.ToImmutableArray();
        var halfState = CrudeReceiptCash.Reconcile(pennyTerms, halves, [], [], [invoice, second, reissue], [], [voidSecond], due.AddDays(2));
        Equal(halfState.Invoices.Single(i => i.InvoiceId == invoice.Id).Amount.Amount, "0");
        Equal(halfState.Invoices.Single(i => i.InvoiceId == reissue.Id).Amount.Amount, "-0.01");
        Equal(halfState.Cash.Single().Invoiced.Amount, "-0.01");
        var corrected = receipt with { Id = Guid.NewGuid(), Quantity = new(ExactDecimal.Parse("450"), terms.Quantity.Key) };
        var correctedInvoice = invoice with { Id = Guid.NewGuid(), ReceiptId = corrected.Id, ComponentOrdinal = 2,
            PriorInvoiceId = null, EventVersion = 5, IssuedAt = due.AddDays(1) };
        var correctedState = CrudeReceiptCash.Reconcile(terms, [corrected], [], [], [invoice, correctedInvoice], [payment],
            [reversePayment, voidInvoice], due.AddDays(2));
        Equal(correctedState.Cash.Single(c => c.Component == CrudeCashComponent.Consideration).Invoiced.Amount, "-35100");
        Equal(correctedState.AcceptedQuantity.Amount, "450");
        Reject("crude_reversal_target", () => Value([invoice], [payment], [reversePayment, reversePayment with { Id = Guid.NewGuid(), EventVersion = 4 }]));
        Reject("crude_payment_invoice", () => Value([invoice], [payment with { EventVersion = 5, PaidAt = due.AddDays(2) }], [voidInvoice]));
        Reject("crude_payment_amount", () => Value([invoice], [payment with { Amount = new(ExactDecimal.Parse("10000"), payment.Amount.Currency) }], [reversePayment]));
        Reject("crude_payment_amount", () => Value([invoice], [payment with { Amount = new(ExactDecimal.Parse("-100"), terms.Quantity.Key) }], [reversePayment]));
        Reject("crude_payment_amount", () => Value([invoice], [payment with { Amount = new(ExactDecimal.Parse("-100.001"), payment.Amount.Currency) }], [reversePayment]));
        Reject("crude_reversal_policy", () => Value([invoice], [payment], [reversePayment with { Implementation = "unknown" }]));
        Reject("crude_cash_history", () => Value([invoice], [payment with { EventVersion = 1 }], []));
        Reject("crude_reversal_time", () => Value([invoice], [payment], [reversePayment with { ReversedAt = due.AddDays(3) }]));
    }
    private static void Equal(ExactDecimal value, string expected)
    { if (value != ExactDecimal.Parse(expected)) throw new Exception($"Expected {expected}, received {value}."); }
    private static void Reject(string code, Action action)
    { try { action(); } catch (MarketDataException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
}
