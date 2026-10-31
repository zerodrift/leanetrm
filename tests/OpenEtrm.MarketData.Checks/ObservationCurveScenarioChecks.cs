using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

// Cross-curve acceptance cases for the shared observation-price integration.
// Synthetic contracts/prices; no exchange specification or physical lifecycle is inferred.
internal static class ObservationCurveScenarioChecks
{
    public static void Run()
    {
        ContractObservationSideChecks.Run();
        DailySettlementAndFinalCarry();
        SyntheticPartiallyFixed();
        GasObservationCadence();
        PowerIntervalLifecycle();
        PowerRepeatedHour();
        OtherCommodityUnits();
    }
    private static void DailySettlementAndFinalCarry()
    {
        var f=new Fixture();var expiry=f.Contracts[0].Expiry;var at=expiry.AddDays(2);
        var future=f.Curves.Single(c=>c.Key==f.C("future")) with{ConstituentExpiry=ConstituentExpiryRule.FreezeFinalSettlement};
        var projection=f.Direct("projection",f.K(2),f.R("gas_out"),SettlementKind.Index);
        var model=f.Model([future,projection]);var context=new ResolvedCalculationContext(Guid.NewGuid(),"daily-final-carry",at,at,at,at);
        var oldRequest=new CurveRequest(future.Key,f.K(1));var liveRequest=new CurveRequest(projection.Key,f.K(2));
        var daily=f.Select(1,QuoteKind.Outright,"gas_out","10");
        daily=daily with{Observation=daily.Observation with{ObservedAt=expiry.AddDays(-1)}};
        var final=daily with{Input=daily.Input with{Use=InputUse.FrozenFact},Observation=daily.Observation with{
            Key=new(f.Revision.CatalogId,Guid.NewGuid()),Role=ObservationRole.FinalContractSettlement,ObservedAt=expiry,Value=ExactDecimal.Parse("40")}};
        var live=new SelectedObservation(new(f.S(8),InputUse.Variable),new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(8),f.R("source"),
            ObservationRole.TraderMark,PublicationState.Final,at,ExactDecimal.Parse("30")));
        MarketSnapshot Snapshot(DateTimeOffset sampleAt,params SelectedObservation[] selections)=>new(Guid.NewGuid(),f.Revision,sampleAt,sampleAt,
            InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[..selections]);
        // These are admitted inputs. ObservedAt is the market observation timestamp;
        // publication/knowledge admission belongs to the authorized reader and is not certified by this check.
        var dailySnapshot=Snapshot(expiry.AddDays(-1),daily);
        var carrySnapshot=Snapshot(expiry.AddDays(1),final);
        var a=model.Compile(dailySnapshot,[oldRequest],true,context);
        var b=model.Compile(carrySnapshot,[oldRequest],true,context);
        var c=model.Compile(Snapshot(at,live),[liveRequest],false,context);
        var points=new[]{a.Points[(oldRequest,InputUse.Variable)],b.Points[(oldRequest,InputUse.Variable)],c.Points[(liveRequest,InputUse.Variable)]};
        var mean=new CurvePointKey(f.C("observation_mean"),Guid.NewGuid());
        var output=new ConnectedCurvePoint(mean,f.Contracts[0].DeliveryFrom,f.Contracts[0].DeliveryTo,PriceUnit.FromQuote(f.Quotes[0]),
            new ConnectedMean([..points.Select(CurvePointReference.Exact)],ConnectedCurves.UniformMean));
        var definitions=ConnectedCurves.MergeDefinitions(a.Definitions.Concat(b.Definitions).Concat(c.Definitions).Append(new(mean.Curve,[output])));
        var plan=ConnectedCurves.Compile(definitions,[mean],context,new ExactBindings());var baseline=ConnectedScenario.Base(plan);
        Equal(baseline.Price(mean).Numerator,"80");Equal(baseline.Price(mean).Denominator,"3");
        var shifted=baseline.WithShifts([new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(liveRequest.Curve,[points[2].PointId]),
            output.Unit,ExactDecimal.Parse("3"),CurveShift.ParentAdditive)]);
        Equal(shifted.Price(mean).Numerator,"83");Equal(shifted.Price(mean).Denominator,"3");
        Equal(baseline.Price(mean).Numerator,"80");
        for(var i=0;i<2;i++)Equal(shifted.Price(points[i]),baseline.Price(points[i]));
        var retainedDaily=(ConnectedSource)plan.GetPoint(points[0]).Expression;
        var retainedFinal=(ConnectedSource)plan.GetPoint(points[1]).Expression;
        Equal(retainedDaily.Observation,daily.Observation);Equal(retainedDaily.Sample!.OriginalUse,InputUse.Variable);
        Equal(retainedFinal.Observation,final.Observation);Equal(retainedFinal.Sample!.OriginalUse,InputUse.FrozenFact);
        Equal(retainedDaily.Request.Contract,retainedFinal.Request.Contract);
        Equal(plan.Primitives.Count(p=>p.Source.Input.Use==InputUse.Variable),1);
        Reject("source_expired",()=>f.Model([future with{ConstituentExpiry=ConstituentExpiryRule.RequireLive},projection]).Compile(carrySnapshot,[oldRequest],true,context));
        Reject("missing_observation",()=>model.Compile(carrySnapshot with{Selections=[]},[oldRequest],true,context));
        Reject("frozen_fact",()=>model.Compile(carrySnapshot with{Selections=[final with{Observation=final.Observation with{Role=ObservationRole.DailySettlement}}]},[oldRequest],true,context));
        Reject("selection_mismatch",()=>model.Compile(dailySnapshot with{Selections=[daily with{Observation=daily.Observation with{Role=ObservationRole.FinalContractSettlement}}]},[oldRequest],true,context));
        Console.WriteLine("PASS observation curves: historical daily10, explicit later final carry40 and live projection30 retain separate sources; mean80/3 shocks to83/3 without replacing earlier fixes");
    }
    private static void SyntheticPartiallyFixed()
    {
        var f=new Fixture();var expiry=f.Contracts.Single(c=>c.Key==f.K(1)).Expiry;
        var curves=f.Curves.Select(curve=>curve.Key==f.C("base_forward")?curve with{
            Inputs=curve.Inputs.SetItem(0,curve.Inputs[0] with{ExpiryRule=SourceExpiryRule.FreezeFinalFuture})}:curve).ToImmutableArray();
        var model=f.Model(curves);var at=expiry.AddDays(1);
        var final=f.Select(1,QuoteKind.Outright,"gas_out","3.2");
        final=final with{Input=final.Input with{Use=InputUse.FrozenFact},Observation=final.Observation with{
            Role=ObservationRole.FinalContractSettlement,ObservedAt=expiry}};
        var snapshot=f.Snapshot() with{ValuationAt=at,MarketCutoff=at};
        snapshot=snapshot with{Selections=snapshot.Selections.Add(final)};
        var request=new CurveRequest(f.C("local_gas"),f.K(3));
        var contract=f.Contracts.Single(c=>c.Key==request.Contract);var unit=PriceUnit.FromQuote(f.Quotes[0]);
        var context=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic-observation-scenarios",at,at,at,at);
        RecipeNode Node(string id,string kind,(string,string)[]? parameters=null,(string,string)[]? ports=null)=>new(id,kind,
            (parameters??[]).ToImmutableDictionary(p=>p.Item1,p=>p.Item2),(ports??[]).ToImmutableDictionary(p=>p.Item1,p=>p.Item2));
        var definition=new RecipeDefinition("assembled_gas","Synthetic assembled gas",[
            Node("source","source",[("curve","local_gas")]),
            Node("select","select",[("mode","positions"),("first","1"),("count","1"),("binding","each_sample")],[("source","source")]),
            Node("dates","dates",[("mode","context")]),
            Node("sample","sample",ports:[("selection","select"),("dates","dates")]),
            Node("out","output",ports:[("values","sample")])],"out");
        var day=DateOnly.FromDateTime(at.UtcDateTime);
        var inputs=new RecipeInputs(day,at,ImmutableDictionary<string,RecipeSource>.Empty.Add("source",
            new(request.Curve,unit,[new(contract,at.AddYears(-1))])),
            ImmutableDictionary<string,ImmutableArray<DateOnly>>.Empty.Add("dates",[day]));
        var identity=new ConnectedRecipeIdentity(f.Revision.CatalogId,Guid.NewGuid(),1,RecipeEngine.Version);
        var graph=RecipeEngine.Connect(definition,inputs,identity,[new(request.Curve,day,request.Contract,model,snapshot,false)],context);
        var plan=ConnectedCurves.Compile(graph.Definitions,[graph.Outputs.Single().Point],context,new ExactBindings());
        var baseline=ConnectedScenario.Base(plan);
        // Independently: final futures 3.2 + bridge basis 0.1 + regional basis 0.2 = 3.5.
        Equal(graph.Result(baseline).Points.Single().Price.Numerator,"7");
        Equal(graph.Result(baseline).Points.Single().Price.Denominator,"2");
        Equal(plan.Primitives.Count(p=>p.Source.Input.Series==f.S(1)&&p.Source.Input.Use==InputUse.Variable),0);
        Equal(plan.Primitives.Count(p=>p.Source.Input.Series==f.S(1)&&p.Source.Input.Use==InputUse.FrozenFact),1);
        var basis=plan.Points.Values.Single(p=>p.Expression is ConnectedSource source&&source.Input.Series==f.S(3));
        var shifted=baseline.WithShifts([new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(basis.Key.Curve,[basis.Key.PointId]),
            basis.Unit,ExactDecimal.Parse("0.5"),CurveShift.ParentAdditive)]);
        Equal(graph.Result(shifted).Points.Single().Price.Numerator,"4");
        Equal(graph.Result(shifted).Points.Single().Price.Denominator,"1");
        Equal(graph.Result(baseline).Points.Single().Price.Numerator,"7");
        Reject("missing_observation",()=>model.Build(snapshot with{Selections=snapshot.Selections.Where(s=>s.Input!=final.Input).ToImmutableArray()},[request]));
        Console.WriteLine("PASS observation curves: assembled curve retains final futures and live regional basis under a shared scenario");
    }
    private static void GasObservationCadence()
    {
        var monthly=new IndexFixingChecks.FixingFixture(monthly:true,delayedMonthlyPublication:true);
        var monthlySide=monthly.Floating();var intramonth=monthly.Snapshot(Fixture.Utc("2041-01-15T12:00:00Z"));
        var monthlyRows=monthlySide.GenerateDatedSchedule();
        Equal(monthlyRows.Payments.Single().Prices.Length,1);
        Equal(monthlySide.DependenciesAt(intramonth).Length,1);
        Equal(monthlyRows.Payments.Single().Prices.Single().Lookup!.Contract.DeliveryFrom,new DateOnly(2041,1,1));
        Equal(monthlyRows.Payments.Single().Prices.Single().Lookup!.Contract.DeliveryTo,new DateOnly(2041,2,1));
        var fixedMonthly=monthly.Snapshot(monthly.Fixings[0].FixingAt,1);
        Equal(monthlySide.DependenciesAt(fixedMonthly).Length,0);
        var monthlyContext=new SideValuationContext(monthly.Model.Build(fixedMonthly,[]),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
        Equal(monthlyContext.Resolve(monthlyRows.Payments.Single().Prices.Single()).Price.Value.ToString(),"10");
        var daily=new IndexFixingChecks.FixingFixture();var dailySide=daily.Floating();
        var snapshot=daily.Snapshot(daily.Fixings[1].FixingAt,2);
        var context=new SideValuationContext(daily.Model.Build(snapshot,dailySide.DependenciesAt(snapshot)),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
        var prices=dailySide.GenerateDatedSchedule().Payments.Single().Prices.Select(context.Resolve).ToArray();
        Equal(string.Join(',',prices.Select(p=>p.State)),"Actual,Actual,Projected");
        Equal(string.Join(',',prices.Select(p=>p.Price.Value.ToString())),"10,20,33");
        Equal(monthlySide.GenerateDatedSchedule().Payments.Single().Prices.Length,1);
        Console.WriteLine("PASS observation curves: monthly gas remains monthly inside delivery; explicit daily gas retains mixed fixed and projected rows");
    }
    private static void PowerIntervalLifecycle()
    {
        var f=new Fixture();var start=Fixture.Utc("2041-01-01T00:00:00Z");
        var clock=PowerClock.Capture(TimeZoneInfo.Utc);var month=new DateOnly(2041,1,1);
        var quote=f.Quotes.Single(q=>q.Key.Code=="power_out");var unit=PriceUnit.FromQuote(quote);
        var contracts=Enumerable.Range(0,24).Select(i=>f.IntervalContract(9200+i,Fixture.Interval(TimeZoneInfo.Utc,start.AddHours(i),60))).ToImmutableArray();
        var curve=f.Direct("hourly_lifecycle",contracts[0].Key,quote.Key,SettlementKind.Index) with{Constituents=[..contracts.Select(c=>c.Key)]};
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,f.Quotes,contracts,[curve],[],[],
            [..contracts.Select((c,i)=>new MarketSeries(f.S(9200+i),c.Key,quote.Key,null))]);
        var marked=new MarkedIntervalCurve(curve.Key,unit,new("interval_end",60,IntervalActualRule.SupportedVersion),clock,60,contracts);
        var selections=contracts.Select((c,i)=>new SelectedObservation(new(f.S(9200+i),InputUse.Variable),
            new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(9200+i),f.R("source"),ObservationRole.TraderMark,
                PublicationState.Final,start.AddHours(-1),ExactDecimal.Parse("30")))).ToImmutableArray();
        ImmutableArray<IntervalPriceResolution> Resolve(int actualCount)
        {
            var at=actualCount==0?start.AddMinutes(-1):start.AddHours(actualCount);
            var actuals=contracts.Take(actualCount).Select((c,i)=>new IntervalActual(c.Interval!.Start,c.Interval.End,
                new(ExactDecimal.Parse(i<12?"10":"20"),unit),c.Interval.End,Guid.NewGuid(),1)).ToArray();
            var prepared=PreparedCurve.Prepare(marked,[(start,start.AddDays(1))],[],actuals,at,at);
            Equal(prepared.Dependencies(month).Length,24-actualCount);
            var snapshot=new MarketSnapshot(Guid.NewGuid(),f.Revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,selections);
            var resolver=new IntervalPriceResolver(model.Build(snapshot,prepared.Dependencies(month)),
                new Dictionary<CurveKey,PreparedCurve>{{curve.Key,prepared}});
            var rows=resolver.Prices(curve.Key,month);
            Equal(rows.Count(r=>r.State==IntervalPriceState.Actual),actualCount);
            Equal(rows.Count(r=>r.State==IntervalPriceState.Projected),24-actualCount);
            Equal(rows.Where(r=>r.State==IntervalPriceState.Actual).All(r=>r.ForwardLineage.IsEmpty),true);
            return rows;
        }
        var projected=Resolve(0);var partial=Resolve(12);var settled=Resolve(24);
        foreach(var (rows,mean) in new[]{(projected,"30"),(partial,"20"),(settled,"15")})
        {
            var average=IntervalPricing.Aggregate(rows,IntervalAggregation.Day,clock).Single().Mean!;
            Equal(average.Numerator,mean);Equal(average.Denominator,"1");
        }
        // Actuals replace only their own interval. Remaining forward cells keep 30, not a rescaled whole-day mark.
        Equal(partial.Skip(12).All(r=>r.Price!.Numerator=="30"),true);
        Equal(projected.All(r=>r.Price!.Numerator=="30"),true);
        var energy=new QuantitySpecification(unit.Quantity,f.R("power"),f.R("delivered"),f.R("megawatt_hour"),f.R("energy"));
        var rate=new QuantitySpecification(f.R("rate"),f.R("power"),f.R("delivery_rate"),f.R("megawatt"),f.R("power"));
        var flat=PowerQuantitySchedule.Compile(new(ExactDecimal.Parse("1"),rate.Key),PowerQuantityBasis.FlatPower,
            rate,energy,month,month.AddDays(1),clock,TimeOfUse.All(60),[],60,null);
        var index=new PowerIndex(f.R("index"),unit.Currency,energy.Unit,energy.Key,"synthetic","interval_end");
        var fixings=settled.Select(r=>new PowerFixing(r.Start,r.End,index,r.Price!.ToUnitPriceExact().Value,null)).ToArray();
        var flatCash=PowerSettlement.Calculate(flat,fixings,index,PowerSettlementPolicy.Preview);
        // Same 24 MWh, but 1.5 MWh in each low-price hour and 0.5 MWh in each high-price hour.
        var shaped=flat.Intervals.Select((r,i)=>r with{Energy=ExactQuantity.FromQuantity(new(ExactDecimal.Parse(i<12?"1.5":"0.5"),energy.Key))}).ToArray();
        var shapedCash=PowerSettlement.Calculate(shaped,energy,fixings,index,PowerSettlementPolicy.Preview);
        Equal(flatCash.Cash.Amount.ToString(),"360");Equal(shapedCash.Cash.Amount.ToString(),"300");
        Equal(flatCash.Energy,shapedCash.Energy);
        Equal(flatCash.Summary.AveragePrice,shapedCash.Summary.AveragePrice);
        Reject("interval_actual_missing",()=>PreparedCurve.Prepare(marked,[(start,start.AddDays(1))],[],[],start.AddDays(1),start.AddDays(1)));
        Console.WriteLine("PASS observation curves: native hourly resolver moves projected/partial/actual with means 30/20/15; equal energy shapes settle 360/300; due missing actuals refused");
    }
    private static void PowerRepeatedHour()
    {
        var r=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());ReferenceKey R(string code)=>new(r,code);
        var energy=new QuantitySpecification(R("energy"),R("power"),R("delivered"),R("megawatt_hour"),R("energy"));
        var rate=new QuantitySpecification(R("rate"),R("power"),R("delivery_rate"),R("megawatt"),R("power"));
        var clock=PowerClock.Capture(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        foreach(var minutes in new[]{60,15})
        {
            var schedule=PowerQuantitySchedule.Compile(new(ExactDecimal.Parse("1"),rate.Key),PowerQuantityBasis.FlatPower,
                rate,energy,new(2026,11,1),new(2026,11,2),clock,TimeOfUse.All(minutes),[],minutes,null);
            Equal(schedule.Total.ToQuantity().Amount.ToString(),"25");
            var index=new PowerIndex(R("index"),R("usd"),energy.Unit,energy.Key,"synthetic","interval_end");
            // The two occurrences of local 01:00 are different UTC intervals, with different prices.
            var fixings=schedule.Intervals.Select(row=>new PowerFixing(row.Interval.Start,row.Interval.End,index,
                ExactDecimal.Parse(row.Interval.Start.Hour==5?"100":row.Interval.Start.Hour==6?"-20":"0"),null)).ToArray();
            var settlement=PowerSettlement.Calculate(schedule,fixings,index,PowerSettlementPolicy.Preview);
            Equal(settlement.Summary.ExactAmountNumerator,"80");Equal(settlement.Summary.ExactAmountDenominator,"1");
            Equal(schedule.Intervals.Select(row=>row.Interval.Start).Distinct().Count(),25*60/minutes);
        }
        Console.WriteLine("PASS observation curves: hourly and quarter-hour power retain both repeated hours, negative prices and exact energy-weighted cash");
    }
    private static void OtherCommodityUnits()
    {
        var r=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());ReferenceKey R(string code)=>new(r,code);
        var oil=new PriceUnit(R("usd"),R("oil_barrels"),ExactDecimal.Parse("1"));
        var metal=new PriceUnit(R("usd"),R("metal_tonnes"),ExactDecimal.Parse("1"));
        Equal(ExactQuantity.FromQuantity(new(ExactDecimal.Parse("2"),oil.Quantity)).Times(new UnitPrice(ExactDecimal.Parse("75"),oil)).ToMoney().Amount.ToString(),"150");
        Equal(ExactQuantity.FromQuantity(new(ExactDecimal.Parse("0.5"),metal.Quantity)).Times(new UnitPrice(ExactDecimal.Parse("2000"),metal)).ToMoney().Amount.ToString(),"1000");
        Reject("quantity_mismatch",()=>ExactQuantity.FromQuantity(new(ExactDecimal.Parse("2"),oil.Quantity)).Times(new UnitPrice(ExactDecimal.Parse("75"),metal)));
        Console.WriteLine("PASS observation curves: other commodity price units retain explicit quantity bases and refuse implicit barrel-to-tonne conversion");
    }
    private static void Equal<T>(T actual,T expected){if(!Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}");}
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
    private sealed class ExactBindings:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context){}
    }
}
