using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class SettlementDeterminationChecks
{
    public static int Run()
    {
        var f = new IndexFixingChecks.FixingFixture(monthly: true);
        var swap = f.Swap(); var snapshot = f.Snapshot(f.Fixings[0].FixingAt, 1);
        var occurrence = new PaymentOccurrenceKey(Guid.NewGuid(), Guid.NewGuid(), 1, f.From, f.To);
        var rounding = new SwapSettlementRounding(swap.Currency, 2, CashTieRule.ToEven, SwapSettlementRounding.Implementation);
        SwapPaymentCalculation Calculate(MarketSnapshot selected) => SwapSettlement.Calculate(swap, 1, 0,
            new(ExactDecimal.Parse("200"), swap.UnitQuantity.Specification), occurrence, f.Model.Build(selected, []), rounding);
        var first = SettlementDetermination.Create(Guid.NewGuid(), Calculate(snapshot), null, null);
        Equal(first.Contribution.Amount.ToString(), "2000"); Equal(first.DueAt, f.Payment);
        var correction = snapshot with
        {
            Id = Guid.NewGuid(), Selections = snapshot.Selections.Select(s => s.Input.Use == InputUse.FrozenFact
                ? s with { Observation = s.Observation with { Key = new(s.Observation.Key.CatalogId, Guid.NewGuid()), Value = ExactDecimal.Parse("11") } } : s).ToImmutableArray()
        };
        var explicitDue = f.Payment.AddDays(4);
        var revised = SettlementDetermination.Create(Guid.NewGuid(), Calculate(correction), first, explicitDue);
        Equal(revised.Total.Amount.ToString(), "2200"); Equal(revised.Contribution.Amount.ToString(), "200");
        Equal(revised.PreviousId, (Guid?)first.Id); Equal(revised.DueAt, explicitDue);
        Equal(first.Total.Amount.ToString(), "2000");
        Reject("settlement_correction_due", () => SettlementDetermination.Create(Guid.NewGuid(), Calculate(correction), first, null));
        Reject("settlement_no_change", () => SettlementDetermination.Create(Guid.NewGuid(), Calculate(snapshot), first, explicitDue));
        SwapPaymentCalculation Tiny(MarketSnapshot selected)=>SwapSettlement.Calculate(swap,1,0,
            new(ExactDecimal.Parse("0.0005"),swap.UnitQuantity.Specification),occurrence,f.Model.Build(selected,[]),rounding);
        var tiny=SettlementDetermination.Create(Guid.NewGuid(),Tiny(snapshot),null,null);
        var twelve=correction with{Id=Guid.NewGuid(),Selections=correction.Selections.Select(s=>s.Input.Use==InputUse.FrozenFact
            ?s with{Observation=s.Observation with{Value=ExactDecimal.Parse("12")}}:s).ToImmutableArray()};
        var tinyRevision=SettlementDetermination.Create(Guid.NewGuid(),Tiny(twelve),tiny,explicitDue);
        Equal(tiny.Total.Amount.ToString(),"0");Equal(tinyRevision.Total.Amount.ToString(),"0.01");
        Equal(tinyRevision.Contribution.Amount.ToString(),"0.01");
        Console.WriteLine("PASS immutable settlement totals, correction differences and explicit correction due terms");
        return 8;
    }
    private static void Equal<T>(T actual, T expected)
    { if (!Equals(actual, expected)) throw new Exception($"Expected {expected}, got {actual}"); }
    private static void Reject(string code, Action work)
    { try { work(); } catch (MarketDataException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
}
