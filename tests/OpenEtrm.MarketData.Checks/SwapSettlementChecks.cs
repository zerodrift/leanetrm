using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class SwapSettlementChecks
{
    public static int Run()
    {
        var f = new IndexFixingChecks.FixingFixture();
        var swap = f.Swap();
        var final = f.Snapshot(f.Fixings[^1].FixingAt, 3);
        var quantity = new Quantity(ExactDecimal.Parse("3"), swap.UnitQuantity.Specification);
        var trade = Guid.NewGuid(); var binding = Guid.NewGuid();
        var rounding = new SwapSettlementRounding(swap.Currency, 2, CashTieRule.ToEven, SwapSettlementRounding.Implementation);
        SideValuationContext Context(MarketSnapshot snapshot) => new(f.Model.Build(snapshot, swap.DependenciesAt(snapshot)),
            new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted));
        PaymentOccurrenceKey Occurrence(int membership) => new(trade, binding, membership, f.From, f.To);
        SwapPaymentCalculation Calculate(int membership, MarketSnapshot snapshot) =>
            SwapSettlement.Calculate(swap, membership, 0, quantity, Occurrence(membership), Context(snapshot), rounding);

        // Three daily prices 10,20,30 average to 20. Pay 3*15; receive 3*20.
        var fixedCash = Calculate(0, final); var floatingCash = Calculate(1, final);
        Equal(fixedCash.ExactAmount.ToExactString(), "-45");
        Equal(floatingCash.ExactAmount.ToExactString(), "60");
        Equal(floatingCash.Amount.Amount.ToString(), "60");
        Equal(floatingCash.ResolvedQuantity.ToQuantity().Amount.ToString(), "3");
        Equal(floatingCash.Evidence.Prices.Length, 3);
        if (floatingCash.Evidence.Prices.Any(p => p.State != SchedulePriceState.Actual || p.Lineage.Count != 1))
            throw new Exception("Settlement lost final fixing evidence.");
        Equal(fixedCash.ExactAmount.Add(floatingCash.ExactAmount), swap.PV(Context(final)).PresentValue.Scale(quantity.Amount));
        Reject("settlement_not_final", () => Calculate(1, f.Snapshot(f.Fixings[1].FixingAt, 2)));
        Reject("index_fixing_missing", () => Calculate(1, f.Snapshot(f.Fixings[^1].FixingAt, 2)));
        Reject("settlement_occurrence", () => SwapSettlement.Calculate(swap, 1, 0, quantity,
            Occurrence(0), Context(final), rounding));
        Reject("quantity_mismatch", () => SwapSettlement.Calculate(swap, 1, 0,
            new(quantity.Amount, f.Base.R("wrong_quantity")), Occurrence(1), Context(final), rounding));
        Reject("settlement_rounding", () => new SwapSettlementRounding(swap.Currency, 2, CashTieRule.ToEven, ""));
        Reject("settlement_occurrence", () => new PaymentOccurrenceKey(Guid.Empty, binding, 0, f.From, f.To));
        if (Occurrence(0) == Occurrence(1) || Occurrence(0) == new PaymentOccurrenceKey(Guid.NewGuid(), binding, 0, f.From, f.To))
            throw new Exception("Different trade memberships shared an occurrence.");

        // Determination needs no discount factor, even after payment is due.
        var due = final with { Id = Guid.NewGuid(), ValuationAt = f.Payment, MarketCutoff = f.Payment };
        Equal(Calculate(1, due).ExactAmount, floatingCash.ExactAmount);
        Reject("side_payment_due", () => swap.PV(Context(due)));

        // One boundary after all daily applications and booked-quantity scaling:
        // 20 * 0.00025 = 0.005, ties-to-even -> 0.00; pay 15 * 0.00025 -> 0.00.
        var tiny = SwapSettlement.Calculate(swap, 1, 0, new(ExactDecimal.Parse("0.00025"), quantity.Specification),
            Occurrence(1), Context(final), rounding);
        Equal(tiny.ExactAmount.ToExactString(), "0.005"); Equal(tiny.Amount.Amount.ToString(), "0");
        Equal(tiny.RoundingEffect.ToExactString(), "-0.005");
        Reject("settlement_rounding", () => new SwapSettlementRounding(swap.Currency, 2, CashTieRule.AwayFromZero, SwapSettlementRounding.Implementation));
        Equal(SwapSettlement.Calculate(swap, 1, 0, quantity, Occurrence(1), f.Model.Build(final, []), rounding).ExactAmount, floatingCash.ExactAmount);
        var paired=new FinancialSwapInstrument(Guid.NewGuid(),[new(swap.Floating,ExactDecimal.Parse("-1")),new(swap.Floating,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,false);
        var paid=SwapSettlement.Calculate(paired,0,0,quantity,Occurrence(0),f.Model.Build(final,[]),rounding);
        var received=SwapSettlement.Calculate(paired,1,0,quantity,Occurrence(1),f.Model.Build(final,[]),rounding);
        Equal(paid.ExactAmount.ToExactString(),"-60");Equal(received.ExactAmount.ToExactString(),"60");
        Equal(paid.Evidence.Plan.Key,received.Evidence.Plan.Key);
        if(paid.Occurrence==received.Occurrence)throw new Exception("Gross memberships sharing one side acquired one obligation.");
        Console.WriteLine("PASS swap determination: exact gross cash, finality, identity, one-time scaling, rounding and due-date separation");
        return 16;
    }
    private static void Equal<T>(T actual, T expected)
    { if (!Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Reject(string code, Action work)
    { try { work(); } catch (MarketDataException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
}
