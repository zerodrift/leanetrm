using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;
using OpenEtrm.Shared.Temporal;

internal static class OperatingBookChecks
{
    internal static int Run(Action<SidedInstrument, SideValuationContext>? inspect = null)
    {
        var f=new Fixture();var one=ExactDecimal.Parse("1");var minus=ExactDecimal.Parse("-1");
        var date=new DateOnly(2041,1,1);var end=date.AddMonths(1);var payment=Fixture.Utc("2041-02-18T00:00:00Z");
        var power=f.Quotes[2];var gas=f.Quotes[0];var index=f.R("power_index");
        var direct=f.Direct("operating_power",f.K(5),power.Key,SettlementKind.Index) with{Settlement=new(SettlementKind.Index,index,null)};
        var model=f.Model(curves:f.Curves.Add(direct).Add(f.Direct("operating_fuel",f.K(2),gas.Key,SettlementKind.Index)));
        var fuelMark=f.Select(2,QuoteKind.Outright,"gas_out","3");fuelMark=fuelMark with{Input=new(f.S(8),InputUse.Variable),Observation=fuelMark.Observation with{Series=f.S(8)}};
        var raw=f.Snapshot();var snapshot=(raw with{Selections=raw.Selections.Add(f.Select(5,QuoteKind.Outright,"power_out","60")).Add(fuelMark)}).WithKnowledge(Guid.NewGuid(),raw.ValuationAt);
        var resource=new AssetKey(f.Revision,"synthetic_thermal");var plan=new OperatingPlanReference(f.Revision,Guid.NewGuid(),1);
        var energyPort=new OperatingOutputPort(plan,resource,OperatingOutputKind.EnergyOutput,power.Quantity);
        var fuelPort=new OperatingOutputPort(plan,resource,OperatingOutputKind.FuelInput,gas.Quantity);
        var interval=new DeliveryInterval(Fixture.Utc("2041-01-01T00:00:00Z"),Fixture.Utc("2041-01-01T01:00:00Z"),date,0,0,1);
        var rightQuantity=new Quantity(one,f.R("right_count"));
        var rule=new PaymentRule(PaymentFrequency.Monthly,PaymentDateRule.DaysAfterPeriodEnd,18,null,PaymentDayBasis.CalendarDays,null,DateRoll.Unadjusted,null,PaymentRule.Implementation);
        var calendar=new SideCalendarPolicy(CalendarBinding.NotApplicable(),CalendarBinding.NotApplicable(),CalendarBinding.NotApplicable(),SideCalendarPolicy.Implementation);
        OperatingSideTerms Terms(QuoteSpecification quote,OperatingOutputPort port,CurveRequest? curve)=>new(rightQuantity,quote,rule,ComponentKind.Payment,
            [new(interval,date,end,new(port,null),curve)],OperatingSideTerms.SupportedImplementation,calendar);
        var output=new FloatingSide(Guid.NewGuid(),Terms(power,energyPort,new(f.C("operating_power"),f.K(5))));
        var fuel=new FloatingSide(Guid.NewGuid(),Terms(gas,fuelPort,new(f.C("operating_fuel"),f.K(2))));
        var variable=new FixedSide(Guid.NewGuid(),Terms(power,energyPort,null),new FlatPrice(new(ExactDecimal.Parse("2"),power)));
        var right=new OperatingRightInstrument(Guid.NewGuid(),resource,rightQuantity,power.Currency,[new(output,one),new(fuel,minus),new(variable,minus)]);
        if(right.SupportsQuantityScaling)throw new Exception("A complete constrained operating right must not declare arbitrary linear quantity scaling.");
        _=new TradeInstrument(Guid.NewGuid(),right,rightQuantity);
        foreach(var amount in new[]{"0","-1","0.5","2"})Reject("operating_right_quantity",()=>new TradeInstrument(Guid.NewGuid(),right,new(ExactDecimal.Parse(amount),rightQuantity.Specification)));
        var fixedHedge=new FixedSide(Guid.NewGuid(),new UnitPrice(ExactDecimal.Parse("60"),power),new(one,power.Quantity),date,end,payment,FixedSide.Implementation,power,SyntheticSideCalendars.Dated(false));
        var floatingHedge=new FloatingSide(Guid.NewGuid(),PriceSettlementSelection.CurveTarget(index),power,new(one,power.Quantity),date,end,
            [new(index,power.Key,date,end,Fixture.Utc("2041-01-31T12:00:00Z"),f.S(7),f.R("actual_source"),IndexFixing.SupportedImplementation)],payment,
            FloatingAveraging.MonthlyIndexPrice,[new(new(f.C("operating_power"),f.K(5)),f.Contracts.Single(c=>c.Key==f.K(5)))],FloatingSide.IndexedImplementation,SyntheticSideCalendars.Dated(true));
        var hedge=new FinancialSwapInstrument(Guid.NewGuid(),[new(fixedHedge,one),new(floatingHedge,minus)],FinancialSwapInstrument.Implementation,false);
        var book=Guid.NewGuid();var operatingTrade=new Trade(Guid.NewGuid(),book,[new(Guid.NewGuid(),right,rightQuantity)]);
        var hedgeTrade=new Trade(Guid.NewGuid(),book,[new(Guid.NewGuid(),hedge,new(ExactDecimal.Parse("5"),power.Quantity))]);
        var cells=OperatingQuantityInputs.Scenario(snapshot,[new(energyPort,interval,ExactQuantity.FromQuantity(new(ExactDecimal.Parse("5"),power.Quantity))),
            new(fuelPort,interval,ExactQuantity.FromQuantity(new(ExactDecimal.Parse("35"),gas.Quantity)))],OperatingQuantityInputs.ScenarioImplementation);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,new ExplicitDiscounting(one,DiscountFactorSource.Undiscounted))
            {OperatingQuantities=ImmutableDictionary<Guid,OperatingQuantityInputs>.Empty.Add(right.Id,cells)};
        var retained=ScheduleDefinitionCompiler.Generate(output,operatingInputs:cells);
        var program=BookedSideProgram.Compile(retained);
        if(retained.Family!="operating"||program.Document.Family!="operating")
            throw new Exception("A booked operating Side must retain its own executable program family.");
        var loaded=BookedSideProgram.Read(program.Write(),program.Identity);
        var market=model.Build(snapshot,right.Dependencies);
        var sources=ScheduleMarketSources.ForOperating(market);
        var retainedValue=sources.Evaluate(loaded,output.Id,market,setup.Discounting!);
        if(retainedValue.PresentValue.ToExactString()!="300"||retainedValue.SourceReceipt!.Cells.Values.OfType<ScheduleOperatingPriceEvidence>().Single().Lineage.Count==0)
            throw new Exception("Retained operating instructions must reopen exact quantity/contract sources and genuine scenario lineage.");
        var inspection=SideScheduleSnapshot.FromProgram(output.Id,snapshot.Id,new("synthetic_output",retainedValue,loaded,one,new(1,1),[]),d=>new(d.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero));
        var inspected=inspection.Read(0,4);
        if(inspection.Calculation(inspected.Rows.Single().Key,0,16).Rows.Single(r=>r.Operation==OperatingContractScheduleSource.Operation).Lineage.IsEmpty)
            throw new Exception("An ordinary operating schedule drill-down must preserve projected contract source lineage.");
        var deliveryTerms=Terms(power,energyPort,new(f.C("operating_power"),f.K(5))) with{Payment=null,Kind=ComponentKind.Delivery};
        var delivery=new FloatingSide(Guid.NewGuid(),deliveryTerms);
        var deliveryProgram=BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(delivery,operatingInputs:cells));
        var deliveryValue=sources.Evaluate(deliveryProgram,delivery.Id,market,setup.Discounting!);
        if(delivery.GenerateSchedule().Rows[0].Key.Timing!=ValueTimingKind.Instant||deliveryValue.PresentValue.ToExactString()!="300"||
            deliveryProgram.Document.Roots.Any(r=>r.Measure=="contractual_amount"))
            throw new Exception("Physical delivery marks retain instant boundaries without a cash obligation.");
        Reject("operating_terms",()=>new FloatingSide(Guid.NewGuid(),deliveryTerms with{Payment=rule}));
        Reject("operating_inputs",()=>ScheduleDefinitionCompiler.Generate(output));
        Reject("side_schedule_program",()=>BookedSideProgram.Compile(new ScheduleDefinition(retained.SideId,"floating",retained.EconomicIdentity,
            retained.PricingBindingIdentity,retained.SchemaVersion,retained.GeneratorVersion,retained.Rows,retained.Edges,retained.Bindings,retained.Roots,retained.Ports,retained.Grids)));
        var portfolio=ValuationPortfolio.FromTrades([operatingTrade,hedgeTrade],setup);
        Reject("risk_fixed_unit_binding",()=>ValuationPortfolio.FromTrades([operatingTrade,
            new Trade(Guid.NewGuid(),book,[new(Guid.NewGuid(),right,rightQuantity)])],setup));
        if(!setup.ForPositions([portfolio.Positions.Single(p=>p.TradeId==hedgeTrade.Id)]).OperatingQuantities.IsEmpty)
            throw new Exception("A selected hedge scope must not retain unrelated operating inputs.");
        var reopened=new RetainedOperatingInstrument(Guid.NewGuid(),resource,rightQuantity,power.Currency,
            [..right.Sides.Select(member=>new RetainedOperatingMember(member.Side.Id,member.Coefficient,
                BookedSideProgram.Compile(ScheduleDefinitionCompiler.Generate(member.Side,operatingInputs:cells))))]);
        RetainedInstrumentContractChecks.Sides(reopened,[..reopened.Members.Select(m=>(m.SideId,m.Program,m.Coefficient,(ExactRatio?)null))]);
        var reopenedPortfolio=ValuationPortfolio.FromTrades([new Trade(Guid.NewGuid(),book,[new(Guid.NewGuid(),reopened,rightQuantity)])],
            setup with{OperatingQuantities=ImmutableDictionary<Guid,OperatingQuantityInputs>.Empty});
        var powerBump=new QuoteBump(new(f.S(7),InputUse.Variable),power.Key,PriceUnit.FromQuote(power),ExactDecimal.Parse("1"),QuoteBump.Convention);
        RetainedProgramSideChecks.Operating(reopened,model,snapshot,powerBump);
        inspect?.Invoke(right, new SideValuationContext(market, setup.Discounting!).WithOperatingQuantities(cells));
        inspect?.Invoke(reopened, SideValuationContext.ForRetainedPrograms(market, market, setup.Discounting!));
        var result=RiskEngine.Calculate(model,snapshot,portfolio,[powerBump],RiskEngine.NumericalPolicy);
        var reopenedRisk=RiskEngine.Calculate(model,snapshot,reopenedPortfolio,[powerBump],RiskEngine.NumericalPolicy);
        Equal(reopenedRisk.Factors.Single().Totals.Single().Base,"185");
        Equal(reopenedRisk.Factors.Single().Totals.Single().Up,"190");
        Equal(reopenedRisk.Factors.Single().Totals.Single().Down,"180");
        var started=(snapshot with{ValuationAt=interval.Start}).WithKnowledge(Guid.NewGuid(),interval.Start);
        Reject("operating_delivery_lifecycle",()=>reopened.PV(new UnitValuationContext(started,setup)));
        Reject("operating_delivery_lifecycle",()=>reopened.PV(new SideValuationContext(started,setup.Discounting!)));
        var totals=result.Factors.Single().Totals.Single();
        Equal(totals.Base,"185");Equal(totals.Up,"185");Equal(totals.Down,"185");
        var physical=result.Factors.Single().Positions.Single(p=>p.TradeId==operatingTrade.Id);
        var financial=result.Factors.Single().Positions.Single(p=>p.TradeId==hedgeTrade.Id);
        Equal(physical.UpPnl,"5");Equal(financial.UpPnl,"-5");
        var changed=OperatingQuantityInputs.Scenario(snapshot,cells.ScenarioCells.SetItem(0,cells.ScenarioCells[0] with
            {Quantity=ExactQuantity.FromQuantity(new(ExactDecimal.Parse("10"),power.Quantity))}),OperatingQuantityInputs.ScenarioImplementation);
        var changedPortfolio=ValuationPortfolio.FromTrades([operatingTrade],setup with{OperatingQuantities=setup.OperatingQuantities.SetItem(right.Id,changed)});
        Equal(RiskEngine.Calculate(model,snapshot,changedPortfolio,[powerBump],RiskEngine.NumericalPolicy).Factors.Single().Totals.Single().Base,"475");
        Reject("operating_inputs",()=>RiskEngine.Calculate(model,snapshot,ValuationPortfolio.FromTrades([operatingTrade],setup with{OperatingQuantities=ImmutableDictionary<Guid,OperatingQuantityInputs>.Empty}),[],RiskEngine.NumericalPolicy));
        Console.WriteLine("PASS operating book: complete fixed-unit right and ordinary hedge use shared base/up/down Instrument/Side PV with explicit plan inputs");
        return 1;
    }
    private static void Equal(Money value,string amount)
    {if(value.Amount!=ExactDecimal.Parse(amount))throw new Exception($"Expected {amount}; got {value.Amount}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
