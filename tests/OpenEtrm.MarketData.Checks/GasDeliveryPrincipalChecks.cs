using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

internal static class GasDeliveryPrincipalChecks
{
    internal static void Run()
    {
        var f=new Fixture();var unit=PriceUnit.FromQuote(f.Quotes[0]);var at=f.Snapshot().ValuationAt;
        var rounding=new CashRoundingConvention(unit.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal);
        Quantity Q(string n)=>new(ExactDecimal.Parse(n),unit.Quantity);
        var first=new GasDeliveryPrincipalPerformance(Guid.NewGuid(),"delivery_a",at.AddHours(1),Q("0.5"));
        // The second performance is accepted in the source journal but remains
        // future-effective when a correction to the final price is determined.
        var second=new GasDeliveryPrincipalPerformance(Guid.NewGuid(),"delivery_b",at.AddDays(2),Q("0.5"));
        ImmutableArray<GasDeliveryPrincipalStep> Plan(string price,ImmutableArray<GasDeliveryPrincipalPerformance> input,ImmutableArray<GasDeliveryPrincipalPrevious> prior)=>
            GasDeliveryPrincipal.Plan(Q("3"),ExactUnitPrice.Parse(price,unit),rounding,GasDeliveryPrincipal.Scope,input,prior);
        var original=Plan("1.01",[second,first],[]);
        if(original.Length!=2||original[0].Performance!=first||original[1].Performance!=second)throw new Exception("Principal needs the complete chronological native performance prefix.");
        Equal(original[0].Contribution,"-0.50");Equal(original[1].Contribution,"-0.51");Equal(original[1].RoundedCumulative,"-1.01");
        var corrected=Plan("1.03",[second,first],[..original.Select(s=>new GasDeliveryPrincipalPrevious(s.Performance.Id,s.Contribution))]);
        Equal(corrected[0].Adjustment,"-0.02");Equal(corrected[1].Adjustment,"0");Equal(corrected[1].Contribution,"-0.51");
        var oldFirst=first with{SignedQuantity=Q("0.25")};var oldLater=second with{SignedQuantity=Q("0.25")};
        var initial=Plan("1.01",[oldFirst,oldLater],[]);Equal(initial[1].Contribution,"-0.25");
        var inserted=new GasDeliveryPrincipalPerformance(Guid.NewGuid(),"delivery_inserted",at.AddDays(1),Q("0.25"));
        var backdated=Plan("1.01",[oldLater,inserted,oldFirst],[..initial.Select(s=>new GasDeliveryPrincipalPrevious(s.Performance.Id,s.Contribution))]);
        Equal(backdated.Single(s=>s.Performance==inserted).Adjustment,"-0.25");Equal(backdated.Single(s=>s.Performance==oldLater).Adjustment,"-0.01");
        Equal(backdated.Last().RoundedCumulative,"-0.76");
        var negative=GasDeliveryPrincipal.Plan(Q("-1"),ExactUnitPrice.Parse("1.01",unit),rounding,GasDeliveryPrincipal.Scope,
            [first with{SignedQuantity=Q("-0.5")},second with{SignedQuantity=Q("-0.5")}],[]);
        Equal(negative[0].Contribution,"0.50");Equal(negative[1].Contribution,"0.51");
        var multiplied=GasDeliveryPrincipal.Plan(Q("1"),ExactUnitPrice.Parse("-1.01",new(unit.Currency,unit.Quantity,ExactDecimal.Parse("2.5"))),rounding,
            GasDeliveryPrincipal.Scope,[first,second],[]);
        Equal(multiplied[0].Contribution,"1.26");Equal(multiplied[1].Contribution,"1.26");Equal(multiplied[1].RoundedCumulative,"2.52");
        var low=new GasDeliveryPrincipalPerformance(Guid.NewGuid(),"\uE000",at,Q("0.25"));var high=low with{Id=Guid.NewGuid(),BusinessReference="\U00010000"};
        var ordered=Plan("1.01",[high,low],[]);
        if(ordered[0].Performance.Id!=low.Id)throw new Exception("Principal tie order must use the native UTF-8 business reference, not UTF-16 or generated row identity.");
        Reject("gas_delivery_principal_capacity",()=>Plan("1",[first with{SignedQuantity=Q("4")}],[]));
        Reject("gas_delivery_principal_scope",()=>Plan("1",[first,first with{Id=Guid.NewGuid()}],[]));
        Reject("gas_delivery_principal_scope",()=>Plan("1",[first],[new(Guid.NewGuid(),new(ExactDecimal.Parse("1"),unit.Currency))]));
        var accepted=original.Select(s=>new GasDeliveryPrincipalQuantityPrior(s.Performance.Id,s.Performance.At,s.Contribution)).ToImmutableArray();
        var retracted=GasDeliveryPrincipal.CorrectQuantities(Q("3"),ExactUnitPrice.Parse("1.01",unit),rounding,GasDeliveryPrincipal.Scope,
            [first with{SignedQuantity=Q("0")},second],[first.Id],accepted);
        if(retracted[0].CumulativeQuantity.Amount!=default)throw new Exception("Retracted performance retained quantity.");
        Equal(retracted[0].Contribution,"0");
        Equal(retracted[0].Adjustment,"0.50");Equal(retracted[1].Contribution,"-0.50");Equal(retracted[1].Adjustment,"0.01");
        var cancelled=GasDeliveryPrincipal.CorrectQuantities(Q("3"),ExactUnitPrice.Parse("1.01",unit),rounding,GasDeliveryPrincipal.Scope,
            [first with{SignedQuantity=Q("0")},second with{SignedQuantity=Q("0")}],[first.Id,second.Id],accepted);
        Equal(cancelled[0].Adjustment,"0.50");Equal(cancelled[1].Adjustment,"0.51");Equal(cancelled[^1].RoundedCumulative,"0");
        Reject("gas_delivery_principal_reconciliation",()=>GasDeliveryPrincipal.CorrectQuantities(Q("3"),ExactUnitPrice.Parse("1.01",unit),rounding,GasDeliveryPrincipal.Scope,
            [first with{At=first.At.AddHours(1)},second],[],accepted));
        Reject("gas_delivery_principal_reconciliation",()=>GasDeliveryPrincipal.CorrectQuantities(Q("3"),ExactUnitPrice.Parse("1.01",unit),rounding,GasDeliveryPrincipal.Scope,
            [second],[],accepted));
        Reject("gas_delivery_principal_scope",()=>GasDeliveryPrincipal.CorrectQuantities(Q("3"),ExactUnitPrice.Parse("1.01",unit),rounding,GasDeliveryPrincipal.Scope,
            [first with{SignedQuantity=Q("0")},second],[],accepted));
        Reject("gas_delivery_principal_scope",()=>GasDeliveryPrincipal.CorrectQuantities(Q("3"),ExactUnitPrice.Parse("1.01",unit),rounding,GasDeliveryPrincipal.Scope,
            [first,second],[first.Id],accepted));
        var replaced=GasDeliveryPrincipal.CorrectQuantities(Q("3"),ExactUnitPrice.Parse("1.01",unit),rounding,GasDeliveryPrincipal.Scope,
            [first with{SignedQuantity=Q("0.25")},second],[],accepted);
        Equal(replaced[0].Adjustment,"0.25");Equal(replaced[1].Adjustment,"0");Equal(replaced[^1].RoundedCumulative,"-0.76");
        var restored=GasDeliveryPrincipal.CorrectQuantities(Q("3"),ExactUnitPrice.Parse("1.01",unit),rounding,GasDeliveryPrincipal.Scope,
            [first,second],[],[..cancelled.Select(s=>new GasDeliveryPrincipalQuantityPrior(s.Performance.Id,s.Performance.At,s.Contribution))]);
        Equal(restored[0].Adjustment,"-0.50");Equal(restored[1].Adjustment,"-0.51");
        var cancelledNegative=GasDeliveryPrincipal.CorrectQuantities(Q("1"),ExactUnitPrice.Parse("-1.01",new(unit.Currency,unit.Quantity,ExactDecimal.Parse("2.5"))),rounding,GasDeliveryPrincipal.Scope,
            [first with{SignedQuantity=Q("0")},second with{SignedQuantity=Q("0")}],[first.Id,second.Id],
            [..multiplied.Select(s=>new GasDeliveryPrincipalQuantityPrior(s.Performance.Id,s.Performance.At,s.Contribution))]);
        Equal(cancelledNegative[0].Adjustment,"-1.26");Equal(cancelledNegative[1].Adjustment,"-1.26");
        Console.WriteLine("PASS quantity reconciliation principal: full retraction refunds, zero prefixes, separate contribution adjustments and explicit timing refusal");
        Console.WriteLine("PASS physical delivery principal: cumulative held rounding, source-known future prefixes, backdated contribution corrections, signed consideration and stable UTF-8 ordering");
        void Equal(Money amount,string expected){if(amount.Amount!=ExactDecimal.Parse(expected))throw new Exception($"Expected {expected}; got {amount.Amount}.");}
    }
    private static void Reject(string code,Action work){try{work();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
