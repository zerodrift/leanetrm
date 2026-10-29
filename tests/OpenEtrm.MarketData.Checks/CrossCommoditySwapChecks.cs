using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class CrossCommoditySwapChecks
{
    public static int Run()
    {
        var reference=new ReferenceRevision(Guid.Parse("10000000-0000-0000-0000-000000000001"),Guid.Parse("20000000-0000-0000-0000-000000000001"));
        var definition=new DefinitionRevision(Guid.Parse("40000000-0000-0000-0000-000000000001"),Guid.Parse("60000000-0000-0000-0000-000000000001"));
        ReferenceKey R(string code)=>new(reference,code);
        var one=ExactDecimal.Parse("1");var quantity=new Quantity(one,R("gas_quantity"));
        var quote=new QuoteSpecification(R("gas_quote"),QuoteKind.Outright,R("currency"),quantity.Specification,one,ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.001"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
        var from=new DateOnly(2041,1,1);var to=from.AddMonths(1);var payment=Fixture.Utc("2041-02-18T00:00:00Z");
        var contract=new MarketContract(new(definition,Guid.Parse("50000000-0000-0000-0000-000000000001")),ContractKind.Forward,from,to,Fixture.Utc("2041-02-01T00:00:00Z"),null);
        var fixedSide=new FixedSide(Guid.NewGuid(),new(ExactDecimal.Parse("4"),quote),quantity,from,to,payment,FixedSide.Implementation,quote);
        var floating=new FloatingSide(Guid.NewGuid(),PriceSettlementSelection.CurveTarget(R("index")),quote,quantity,from,to,Fixture.Utc("2040-12-31T00:00:00Z"),payment,FloatingAveraging.MonthlyIndexPrice,[new(new(new(definition,"gas"),contract.Key),contract)],FloatingSide.Implementation);
        var first=WholeTermSideFixtures.Fixed([fixedSide],quote,[ExactRatio.One]);
        var second=WholeTermSideFixtures.Floating([floating],[ExactRatio.One]);
        var legacy=new ScheduledFinancialSwap(Guid.NewGuid(),[new(first,ExactDecimal.Parse("-1"),ExactRatio.One),new(second,one,ExactRatio.One)],false,ScheduledFinancialSwap.Implementation);
        Equal(DefinitionIdentity.For(legacy).Version,"ScheduledFinancialSwap_v2");

        var f=new Fixture();var powerQuote=f.Quotes[2];var gasQuote=f.Quotes[0];
        var powerCurve=f.Direct("independent_power",f.K(5),powerQuote.Key,SettlementKind.Index) with{Settlement=new(SettlementKind.Index,f.R("power_index"),null)};
        var model=f.Model(curves:f.Curves.Add(powerCurve));
        var snapshot=f.Snapshot() with{Selections=f.Snapshot().Selections.Add(f.Select(5,QuoteKind.Outright,"power_out","50"))};
        var fixingAt=Fixture.Utc("2040-12-31T00:00:00Z");
        FloatingSide Floating(QuoteSpecification q,CurveKey curve,ContractKey key,ReferenceKey index)=>new(Guid.NewGuid(),PriceSettlementSelection.CurveTarget(index),q,new(one,q.Quantity),from,to,
            [new(index,q.Key,from,to,fixingAt,q.Key==gasQuote.Key?f.S(8):f.S(7),f.R("actual_source"),IndexFixing.SupportedImplementation)],
            payment,FloatingAveraging.MonthlyIndexPrice,[new(new(curve,key),f.Contracts.Single(c=>c.Key==key))],FloatingSide.IndexedImplementation);
        var power=WholeTermSideFixtures.Floating([Floating(powerQuote,powerCurve.Key,f.K(5),f.R("power_index"))],[ExactRatio.One]);
        var gas=WholeTermSideFixtures.Floating([Floating(gasQuote,f.C("base_forward"),f.K(2),f.R("index"))],[ExactRatio.One]);
        var extraGas=new QuantitySpecification(f.R("gas_bundle"),f.R("gas"),f.R("gas_energy"),f.R("thermal_bundle"),f.R("energy"));
        var extraPower=new QuantitySpecification(f.R("power_slice"),f.R("power"),f.R("power_energy"),f.R("power_slice_unit"),f.R("energy"));
        var quantities=f.Quantities.Add(extraGas).Add(extraPower);
        var converter=new UnitConverter(quantities.Select(q=>new UnitDefinition(q.Unit,q.Dimension,q.Unit.Code)).ToImmutableArray(),
            [new(extraGas.Unit,f.Quantities[0].Unit,ExactDecimal.Parse("2"),one),new(extraPower.Unit,f.Quantities[1].Unit,one,ExactDecimal.Parse("1000"))],quantities);
        var factor=new QuantityFactor(ExactDecimal.Parse("7"),gas.UnitQuantity.Specification,power.UnitQuantity.Specification,QuantityFactor.Implementation);
        var members=ImmutableArray.Create(new SideMembership(power,one,ExactRatio.One),new SideMembership(gas,ExactDecimal.Parse("-1"),new(7,1)));
        var cross=new CrossCommodityScheduledSwap(Guid.NewGuid(),members,factor,converter,false,CrossCommodityScheduledSwap.Implementation);
        InstrumentValuation Price(CurveBuild market)=>cross.PV(new SideValuationContext(market,new ExplicitDiscounting(one,DiscountFactorSource.Undiscounted)));
        var build=model.Build(snapshot,cross.DependenciesAt(snapshot));
        // Independent outright power50; gas3+.1. Power1000 minus gas7000.
        Equal(Price(build).PresentValue.Scale(ExactDecimal.Parse("1000")).ToExactString(),"28300");
        var powerBump=new OpenEtrm.MarketData.Risk.QuoteBump(new(f.S(7),InputUse.Variable),powerQuote.Key,PriceUnit.FromQuote(powerQuote),ExactDecimal.Parse("0.1"),OpenEtrm.MarketData.Risk.QuoteBump.Convention);
        var gasBump=new OpenEtrm.MarketData.Risk.QuoteBump(new(f.S(2),InputUse.Variable),f.Quotes[1].Key,PriceUnit.FromQuote(f.Quotes[1]),ExactDecimal.Parse("0.1"),OpenEtrm.MarketData.Risk.QuoteBump.Convention);
        foreach(var (bump,up,down) in new[]{(powerBump,"28400","28200"),(gasBump,"27600","29000")}){
            var pair=model.Scenarios(build,[bump]).Single();Equal(Price(pair.Up).PresentValue.Scale(ExactDecimal.Parse("1000")).ToExactString(),up);Equal(Price(pair.Down).PresentValue.Scale(ExactDecimal.Parse("1000")).ToExactString(),down);
        }
        Reject("quantity_mapping",()=>new CrossCommodityScheduledSwap(Guid.NewGuid(),members.SetItem(1,members[1] with{QuantityRatio=new(8,1)}),factor,converter,false,CrossCommodityScheduledSwap.Implementation));
        Reject("quantity_mapping",()=>new CrossCommodityScheduledSwap(Guid.NewGuid(),members.SetItem(1,members[1] with{QuantityRatio=null}),factor,converter,false,CrossCommodityScheduledSwap.Implementation));
        var oversizedPower=new FloatingSide(Guid.NewGuid(),power.Settlement,power.Quote,power.UnitQuantity,[power.Periods[0] with{Allocation=new(2,1)}]);
        var oversizedGas=new FloatingSide(Guid.NewGuid(),gas.Settlement,gas.Quote,gas.UnitQuantity,[gas.Periods[0] with{Allocation=new(2,1)}]);
        Reject("quantity_mapping",()=>new CrossCommodityScheduledSwap(Guid.NewGuid(),[new(oversizedPower,one,ExactRatio.One),new(oversizedGas,ExactDecimal.Parse("-1"),new(7,1))],factor,converter,false,CrossCommodityScheduledSwap.Implementation));
        Reject("scheduled_swap_instrument",()=>new ScheduledFinancialSwap(Guid.NewGuid(),members,false,ScheduledFinancialSwap.Implementation));
        Reject("quantity_conversion",()=>new CrossCommodityScheduledSwap(Guid.NewGuid(),members,new(ExactDecimal.Parse("7"),power.UnitQuantity.Specification,gas.UnitQuantity.Specification,QuantityFactor.Implementation),converter,false,CrossCommodityScheduledSwap.Implementation));
        var equivalent=new QuantityFactor(ExactDecimal.Parse("0.0035"),extraGas.Key,extraPower.Key,QuantityFactor.Implementation);
        var converted=new CrossCommodityScheduledSwap(Guid.NewGuid(),members,equivalent,converter,false,CrossCommodityScheduledSwap.Implementation);
        Equal(converted.PV(new SideValuationContext(build,new ExplicitDiscounting(one,DiscountFactorSource.Undiscounted))).PresentValue.Scale(ExactDecimal.Parse("1000")).ToExactString(),"28300");
        if(DefinitionIdentity.For(cross)==DefinitionIdentity.For(converted))throw new Exception("Selected factor units were omitted from economic identity.");
        Equal(new Quantity(ExactDecimal.Parse("1000"),power.UnitQuantity.Specification).Times(equivalent,gas.UnitQuantity.Specification,converter).ToQuantity().Amount.ToString(),"7000");
        var third=ExactQuantity.FromQuantity(new(one,power.UnitQuantity.Specification)).Scale(new ExactRatio(1,3)).Times(factor,gas.UnitQuantity.Specification,converter);
        Equal(third.Numerator,"7");Equal(third.Denominator,"3");
        Equal(third.Times(new UnitPrice(ExactDecimal.Parse("3"),gasQuote)).ToExactString(),"7");
        Reject("quantity_factor",()=>new QuantityFactor(default,gas.UnitQuantity.Specification,power.UnitQuantity.Specification,QuantityFactor.Implementation));
        Reject("quantity_factor",()=>new QuantityFactor(ExactDecimal.Parse("-1"),gas.UnitQuantity.Specification,power.UnitQuantity.Specification,QuantityFactor.Implementation));
        Reject("missing_reference",()=>new Quantity(ExactDecimal.Parse("1"),power.UnitQuantity.Specification).Times(new(one,f.R("missing_quantity"),power.UnitQuantity.Specification,QuantityFactor.Implementation),gas.UnitQuantity.Specification,converter));

        SelectedObservation Actual(int series,string price)=>new(new(f.S(series),InputUse.FrozenFact),new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(series),f.R("actual_source"),ObservationRole.IndexFixing,PublicationState.Final,fixingAt,ExactDecimal.Parse(price)));
        var final=snapshot with{ValuationAt=Fixture.Utc("2041-01-31T20:00:00Z"),MarketCutoff=Fixture.Utc("2041-01-31T20:00:00Z"),Selections=snapshot.Selections.Add(Actual(7,"47")).Add(Actual(8,"4"))};
        var finalMarket=model.Build(final,[]);var booked=new Quantity(ExactDecimal.Parse("1000"),power.UnitQuantity.Specification);
        var trade=Guid.NewGuid();var binding=Guid.NewGuid();var rounding=new SwapSettlementRounding(cross.Currency,2,CashTieRule.ToEven,SwapSettlementRounding.Implementation);
        SwapPaymentCalculation Cash(CrossCommodityScheduledSwap instrument,int slot,CurveBuild market)=>SwapSettlement.Calculate(instrument,slot,0,booked,new(trade,binding,slot,from,to),market,rounding);
        var powerCash=Cash(cross,0,finalMarket);var gasCash=Cash(cross,1,finalMarket);
        Equal(powerCash.Amount.Amount.ToString(),"47000");Equal(gasCash.Amount.Amount.ToString(),"-28000");
        Equal(powerCash.ResolvedQuantity.ToQuantity().Amount.ToString(),"1000");Equal(gasCash.ResolvedQuantity.ToQuantity().Amount.ToString(),"7000");
        if(powerCash.ResolvedQuantity.Specification!=power.UnitQuantity.Specification||gasCash.ResolvedQuantity.Specification!=gas.UnitQuantity.Specification)throw new Exception("Gross quantities lost their native units.");
        Equal(Price(finalMarket).PresentValue.Scale(booked.Amount).ToExactString(),"19000");
        var changedMarks=final with{Selections=final.Selections.Select(s=>s.Input.Use==InputUse.Variable?s with{Observation=s.Observation with{Value=ExactDecimal.Parse("99")}}:s).ToImmutableArray()};
        Equal(Price(model.Build(changedMarks,[])).PresentValue.Scale(booked.Amount).ToExactString(),"19000");
        var missing=final with{Selections=final.Selections.Where(s=>s.Input!=new InputKey(f.S(8),InputUse.FrozenFact)).ToImmutableArray()};
        Reject("index_fixing_missing",()=>Cash(cross,1,model.Build(missing,[])));
        if(gasCash.ContractHash==Cash(converted,1,finalMarket).ContractHash)throw new Exception("Settlement evidence lost the selected factor units.");
        Console.WriteLine("PASS cross-commodity native quantities, typed mapping, independent cash and gas/power shocks; whole-term definition versions");
        return 8;
    }
    private static void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}, got {actual}");}
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
