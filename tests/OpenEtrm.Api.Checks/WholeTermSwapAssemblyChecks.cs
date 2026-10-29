using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class WholeTermSwapAssemblyChecks
{
    internal static void Run()
    {
        RequireSettlementChoice();
        var revision=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string code)=>new(revision,code);
        var one=ExactDecimal.Parse("1");
        var quote=new QuoteSpecification(R("synthetic_quote"),QuoteKind.Outright,R("synthetic_currency"),R("synthetic_energy"),one,
            ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
        var unit=new Quantity(one,quote.Quantity);
        if(SwapScheduledAssembly.Spread("0",quote) is not null || SwapScheduledAssembly.Spread(null,quote) is not null ||
            SwapScheduledAssembly.Spread("0.125",quote)?.Value!=ExactDecimal.Parse("0.125"))
            throw new Exception("Explicit zero spread and no spread must have the same retained economic definition; nonzero adjustments remain exact.");
        var first=new DateOnly(2048,1,1);var second=first.AddMonths(1);
        DateTimeOffset Payment(DateOnly month)=>new(month.AddMonths(1).AddDays(10).ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
        var periods=ImmutableArray.Create(
            new ResolvedFixedSwapPeriod(quote,unit,new(ExactDecimal.Parse("40"),quote),new(first,second,Payment(first),ExactRatio.One,null)),
            new ResolvedFixedSwapPeriod(quote,unit,new(ExactDecimal.Parse("50"),quote),new(second,second.AddMonths(1),Payment(second),ExactRatio.One,null)));
        var definition=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());
        var settlement=new PriceSettlementSelection(PriceSettlementMode.Override,R("synthetic_projection_index"),R("synthetic_settlement_index"),PriceSettlementSelection.ForwardCurveNoBasis);
        ResolvedFloatingSwapPeriod Floating(DateOnly month)
        {
            var to=month.AddMonths(1);var fixing=Payment(month).AddDays(-12);
            var contract=new MarketContract(new(definition,Guid.NewGuid()),ContractKind.Forward,month,to,fixing,null);
            var projection=new SwapProjection(new(new(definition,"synthetic_projection"),contract.Key),contract);
            var publication=new IndexFixing(settlement.SelectedIndex,quote.Key,month,to,fixing,new(definition,Guid.NewGuid()),R("synthetic_source"),IndexFixing.SupportedImplementation);
            return new(settlement,quote,unit,new(month,to,fixing,Payment(month),FloatingAveraging.MonthlyIndexPrice,[projection],[publication],
                null,null,null,null,ExactRatio.One,FloatingSide.IndexedImplementation));
        }
        var floatingPeriods=ImmutableArray.Create(Floating(first),Floating(second));
        var floatingSide=SwapScheduledAssembly.Floating(Guid.NewGuid(),floatingPeriods,[new(1,3),new(2,3)]);
        if(floatingSide.Settlement!=settlement||floatingSide.Periods.Length!=2||
            floatingSide.GenerateDatedSchedule().Payments.SelectMany(payment=>payment.Prices).Any(price=>price.Lookup!.Settlement!=settlement))
            throw new Exception("Every whole-term observation must retain the selected fixing index separately from its projection target and explicit unmodelled-basis policy.");
        var projectionEvidence=floatingSide.SettlementProjections;
        if(projectionEvidence.Length!=1||projectionEvidence[0].Selection!=settlement||projectionEvidence[0].Curve.Code!="synthetic_projection")
            throw new Exception("Risk receipts must retain the explicit selected index and projection policy even when multiple periods share a curve.");
        var contract=new OpenEtrm.MarketData.Risk.UnitPricingContract(Guid.NewGuid(),"synthetic_model_v1",[],unit.Specification,quote.Currency)
            {SettlementProjections=projectionEvidence};
        var policyReceipt=ProjectionPolicyReceipts.For([contract]).Single();
        if(policyReceipt.Settlement!=settlement||policyReceipt.Label!="Curve risk with unmodelled basis to synthetic_settlement_index")
            throw new Exception("The backend must state the interpretation of override risk and retain its exact contractual selection.");
        try
        {
            SwapScheduledAssembly.Floating(Guid.NewGuid(),floatingPeriods.SetItem(1,floatingPeriods[1] with{Settlement=PriceSettlementSelection.CurveTarget(settlement.CurveTargetIndex)}),[new(1,3),new(2,3)]);
            throw new Exception("One whole-term floating side cannot silently change settlement policy between periods.");
        }
        catch(MarketDataException error)when(error.Code=="swap_definition"){}
        var side=SwapScheduledAssembly.Fixed(Guid.NewGuid(),periods,[new(1,3),new(2,3)]);
        var schedule=side.GenerateDatedSchedule();
        if(schedule.Payments.Length!=2 || side.Periods.Length!=2 || side.Price.At(first).ToUnitPriceExact().Value.ToString()!="40" ||
            side.Price.At(second).ToUnitPriceExact().Value.ToString()!="50" || schedule.Payments[0].AllocatedQuantity.Numerator!="1" ||
            schedule.Payments[0].AllocatedQuantity.Denominator!="3" || schedule.Payments[1].AllocatedQuantity.Numerator!="2")
            throw new Exception("Authored monthly parameters must form one whole-term side with every exact price and quantity allocation.");
        try{SwapScheduledAssembly.Fixed(Guid.NewGuid(),periods,[ExactRatio.One]);}
        catch(MarketDataException error)when(error.Code=="scheduled_quantity")
        {Console.WriteLine("PASS whole-term swap assembly: direct period parameters, stepped agreed prices and exact allocations");return;}
        throw new Exception("Missing period allocations must be refused before assembling a side.");
    }
    private static void RequireSettlementChoice()
    {
        try
        {
            System.Text.Json.JsonSerializer.Deserialize<SwapFloatingInput>("{\"curve\":\"synthetic_curve\"}",
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch(System.Text.Json.JsonException){return;}
        throw new Exception("A floating booking cannot infer the settlement selection when its required field is absent.");
    }
}
