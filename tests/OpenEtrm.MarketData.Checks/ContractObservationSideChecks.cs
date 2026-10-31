using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;
using OpenEtrm.Shared.Temporal;

internal static class ContractObservationSideChecks
{
    public static void Run()
    {
        var f=new Fixture();var expiry=f.Contracts[0].Expiry;var at=expiry.AddDays(2);
        var future=f.Curves.Single(c=>c.Key==f.C("future")) with{ConstituentExpiry=ConstituentExpiryRule.FreezeFinalSettlement};
        var projection=f.Direct("projection",f.K(2),f.R("gas_out"),SettlementKind.Index);
        var model=f.Model([future,projection]);var quote=f.Quotes[0];
        ContractObservationLookup Lookup(int ordinal,DateTimeOffset fixing,Curve curve,int contract,int series,ObservationRole role)=>
            new(new(curve.Key,f.K(contract)),f.Contracts[contract-1],quote,new(ordinal,DateOnly.FromDateTime(fixing.UtcDateTime),DateOnly.FromDateTime(fixing.UtcDateTime).AddDays(1),fixing),
                fixing,f.R("source"),f.S(series),role,ContractObservationExpiry.CarryFinalSettlement,ContractObservationLookup.SupportedImplementation);
        var lookups=ImmutableArray.Create(Lookup(0,expiry.AddDays(-1),future,1,1,ObservationRole.DailySettlement),
            Lookup(1,expiry.AddDays(1),future,1,1,ObservationRole.DailySettlement),Lookup(2,at.AddDays(1),projection,2,8,ObservationRole.TraderMark));
        var from=new DateOnly(2041,1,1);var to=from.AddMonths(1);
        var period=new FloatingSidePeriod(from,to,lookups[0].Observation.FixingAt,new DateTimeOffset(2041,2,3,0,0,0,TimeSpan.Zero),
            FloatingAveraging.ObservationArithmetic,[..lookups.Select(l=>new SwapProjection(l.Market,l.Contract))],[],null,null,null,null,ExactRatio.One,FloatingSide.ContractObservationImplementation)
            {ContractObservations=lookups};
        var side=new FloatingSide(Guid.NewGuid(),quote,new(ExactDecimal.Parse("1"),quote.Quantity),[period]);
        var schedule=side.GenerateDatedSchedule();Equal(schedule.Payments.Single().Prices.Length,3);
        Equal(schedule.Payments.Single().DeliveryFrom,from);Equal(schedule.Payments.Single().Prices[0].ContractObservation,lookups[0]);
        var context=new ResolvedCalculationContext(Guid.NewGuid(),"contract-observation-test",at,at,at,at);
        MarketSnapshot Snapshot(int n,string value,InputUse use,ObservationRole role)
        {
            var lookup=lookups[n];var sampleAt=lookup.Observation.FixingAt<at?lookup.Observation.FixingAt:at;
            var observed=role==ObservationRole.FinalContractSettlement?expiry:sampleAt;
            return new(Guid.NewGuid(),f.Revision,sampleAt,sampleAt,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
                [new(new(lookup.Series,use),new(new(f.Revision.CatalogId,Guid.NewGuid()),lookup.Series,lookup.Source,role,PublicationState.Final,observed,ExactDecimal.Parse(value)))]);
        }
        var snapshots=new[]{Snapshot(0,"10",InputUse.Variable,ObservationRole.DailySettlement),Snapshot(1,"40",InputUse.FrozenFact,ObservationRole.FinalContractSettlement),Snapshot(2,"30",InputUse.Variable,ObservationRole.TraderMark)};
        var pricing=ContractObservationPricing.Compile([..lookups.Select((l,n)=>new ContractObservationSample(l,model,snapshots[n]))],context,new Bindings());
        var baseline=ConnectedScenario.Base(pricing.Plan);
        var valuation=new SideValuationContext(snapshots[2],new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)).WithContractObservations(pricing,baseline);
        Equal(side.PV(valuation).PresentValue.Numerator,"80");Equal(side.PV(valuation).PresentValue.Denominator,"3");
        var point=pricing.Points[lookups[2]];
        var shifted=baseline.WithShifts([new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(point.Curve,[point.PointId]),PriceUnit.FromQuote(quote),ExactDecimal.Parse("3"),CurveShift.ParentAdditive)]);
        Equal(side.PV(valuation.WithContractObservations(pricing,shifted)).PresentValue.Numerator,"83");
        Equal(side.PV(valuation).PresentValue.Numerator,"80");
        Reject("contract_observation_source",()=>ContractObservationPricing.Compile([new(lookups[0],model,snapshots[2])],context,new Bindings()));
        Reject("contract_observation_source",()=>ContractObservationPricing.Compile([new(lookups[0] with{Series=f.S(8)},model,snapshots[0])],context,new Bindings()));
        Reject("missing_observation",()=>ContractObservationPricing.Compile([new(lookups[1],model,snapshots[1] with{Selections=[]})],context,new Bindings()));
        Equal(side.DependenciesAt(snapshots[2]).Single(),lookups[2].Market);
        var equalLookup=lookups[2] with{Observation=new(0,DateOnly.FromDateTime(at.UtcDateTime),DateOnly.FromDateTime(at.UtcDateTime).AddDays(1),at),SelectionAt=at};
        var equalPricing=ContractObservationPricing.Compile([new(equalLookup,model,snapshots[2])],context,new Bindings());
        Equal(equalPricing.Plan.Primitives.Count(p=>p.Source.Input.Use==InputUse.Variable),0);
        Equal(equalPricing.Resolve(equalLookup,ConnectedScenario.Base(equalPricing.Plan)).State,SchedulePriceState.Actual);
        var coexist=ContractObservationPricing.Compile([new(equalLookup,model,snapshots[2]),new(lookups[2],model,snapshots[2])],context,new Bindings());
        Equal(coexist.Points[equalLookup]!=coexist.Points[lookups[2]],true);
        var coexistBase=ConnectedScenario.Base(coexist.Plan);var livePoint=coexist.Points[lookups[2]];
        var coexistShift=coexistBase.WithShifts([new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(livePoint.Curve,[livePoint.PointId]),PriceUnit.FromQuote(quote),ExactDecimal.Parse("3"),CurveShift.ParentAdditive)]);
        Equal(coexist.Resolve(equalLookup,coexistShift).Price.Numerator,"30");
        Equal(coexist.Resolve(lookups[2],coexistShift).Price.Numerator,"33");
        var admitted=ConnectedValuation.Prepare(baseline,snapshots[2],[..snapshots]).WithContractObservations(pricing);
        var authority=model.Build(snapshots[2],[lookups[2].Market]);
        var bindings=ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(lookups[2].Market,pricing.Points[lookups[2]]);
        var paymentContext=admitted.WithScenario(shifted).PaymentContext(bindings,authority,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
        Equal(side.PV(paymentContext).PresentValue.Numerator,"83");Equal(paymentContext.ScenarioId,shifted.Id);
        // Execute serialized instructions against production source callbacks, with
        // neither the Side nor a replacement payment graph supplied to evaluation.
        var definition=ScheduleDefinitionCompiler.Generate(side);var bytes=BookedSideProgram.Compile(definition).Write();
        var stored=BookedSideProgram.Read(bytes,new(BookedSideProgram.Digest(bytes),definition.Manifest,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity));
        var certified=snapshots.Select(s=>s.WithKnowledge(context.ContextId,context.KnownAt)).ToImmutableArray();
        var storedValuation=ConnectedValuation.Prepare(baseline,certified[2],certified).WithContractObservations(pricing);
        var native=model.Build(certified[2],[lookups[2].Market]);
        var baseMarket=native.BindConnected(storedValuation,bindings,default);
        var shiftedMarket=native.BindConnected(storedValuation.WithScenario(shifted),bindings,default);
        var sources=new ScheduleMarketSources(context,baseMarket,null,null,IntervalKnowledgePolicy.ResolvedCutoff,
            PendingActualsPolicy.RequireActuals,[],[],pricing);
        var undiscounted=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var storedBase=sources.Evaluate(stored,side.Id,baseMarket,undiscounted);
        var storedShift=sources.Evaluate(stored,side.Id,shiftedMarket,undiscounted);
        var receipts=storedShift.SourceReceipt?.Cells.Values.OfType<ScheduleContractObservationEvidence>().ToArray()
            ??throw new Exception("Contract observation inputs require source evidence.");
        if(receipts.Length!=3||receipts.Any(receipt=>receipt.Resolution.Lineage.IsEmpty)||
            receipts.Count(receipt=>receipt.Resolution.State==SchedulePriceState.Actual)!=2||
            !receipts.Select(receipt=>receipt.Lookup).ToHashSet().SetEquals(lookups))
            throw new Exception("Retained observation receipts must preserve every exact sample and actual/projected state.");
        Equal(storedBase.PresentValue.Numerator,"80");Equal(storedBase.PresentValue.Denominator,"3");
        Equal(storedShift.PresentValue.Numerator,"83");Equal(storedShift.PresentValue.Denominator,"3");
        Equal(sources.Evaluate(stored,side.Id,baseMarket,undiscounted).PresentValue.Numerator,"80");
        Reject("side_market_sources",()=>new ScheduleMarketSources(context,baseMarket,null,null,IntervalKnowledgePolicy.ResolvedCutoff,
            PendingActualsPolicy.RequireActuals,[],[]).Evaluate(stored,side.Id,baseMarket,undiscounted));

        Reject("contract_observation_source",()=> (lookups[0] with{Observation=lookups[0].Observation with{To=lookups[0].To.AddDays(1)}}).Validate());
        var changedLookup=lookups[0] with{SelectionAt=lookups[0].SelectionAt.AddSeconds(1)};
        Reject("contract_observation_source",()=>changedLookup.Validate());
        var changedPeriod=period with{ContractObservations=lookups.SetItem(0,lookups[0] with{Source=f.R("other_source")})};
        Equal(DefinitionIdentity.For(period).Digest!=DefinitionIdentity.For(changedPeriod).Digest,true);
        var changedSide=new FloatingSide(Guid.NewGuid(),quote,new(ExactDecimal.Parse("1"),quote.Quantity),[changedPeriod]);
        Equal(DefinitionIdentity.For(side.PricingBinding).Digest!=DefinitionIdentity.For(changedSide.PricingBinding).Digest,true);
        // Cash determination binds the actual retained contract terms, while a
        // published price correction changes evidence only, never the obligation.
        var cashSwap=new FinancialSwapInstrument(Guid.NewGuid(),[new(side,ExactDecimal.Parse("-1")),new(side,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,false);
        var cashQuantity=new Quantity(ExactDecimal.Parse("3"),quote.Quantity);
        var occurrence=new PaymentOccurrenceKey(Guid.NewGuid(),Guid.NewGuid(),1,from,to);
        var rounding=new SwapSettlementRounding(quote.Currency,2,CashTieRule.ToEven,SwapSettlementRounding.Implementation);
        var originalHash=SwapPaymentCalculation.ContractIdentity(cashSwap,cashSwap.Sides[1],schedule.Payments[0],cashQuantity);
        var changedHash=SwapPaymentCalculation.ContractIdentity(cashSwap,cashSwap.Sides[1],changedSide.GenerateDatedSchedule().Payments[0],cashQuantity);
        Equal(originalHash!=changedHash,true);
        Reject("settlement_not_final",()=>SwapSettlement.Calculate(cashSwap,1,0,cashQuantity,occurrence,valuation,rounding));
        var futureCarry=lookups[1] with{Observation=lookups[2].Observation,SelectionAt=lookups[2].SelectionAt};
        var carryLookups=lookups.SetItem(2,futureCarry);
        var carrySide=new FloatingSide(Guid.NewGuid(),quote,new(ExactDecimal.Parse("1"),quote.Quantity),
            [period with{ContractObservations=carryLookups,Projections=[..carryLookups.Select(l=>new SwapProjection(l.Market,l.Contract))]}]);
        var carrySwap=new FinancialSwapInstrument(Guid.NewGuid(),[new(carrySide,ExactDecimal.Parse("-1")),new(carrySide,ExactDecimal.Parse("1"))],FinancialSwapInstrument.Implementation,false);
        var carrySamples=ContractObservationPricing.Compile([new(lookups[0],model,snapshots[0]),new(lookups[1],model,snapshots[1]),
            new(futureCarry,model,snapshots[1] with{Id=Guid.NewGuid(),ValuationAt=at,MarketCutoff=at})],context,new Bindings());
        var carryContext=new SideValuationContext(snapshots[2],new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))
            .WithContractObservations(carrySamples,ConnectedScenario.Base(carrySamples.Plan));
        Equal(carrySide.GenerateDatedSchedule().Payments[0].Prices.Select(carryContext.Resolve).All(p=>p.State==SchedulePriceState.Actual),true);
        Reject("settlement_not_final",()=>SwapSettlement.Calculate(carrySwap,1,0,cashQuantity,occurrence,carryContext,rounding));
        var settledAt=lookups[2].FixingAt;
        var settledContext=new ResolvedCalculationContext(Guid.NewGuid(),"contract-observation-cash",settledAt,settledAt,settledAt,settledAt);
        var lastActual=snapshots[2] with{Id=Guid.NewGuid(),ValuationAt=settledAt,MarketCutoff=settledAt,
            Selections=[snapshots[2].Selections[0] with{Observation=snapshots[2].Selections[0].Observation with{ObservedAt=settledAt}}]};
        SideValuationContext CashContext(MarketSnapshot first)
        {
            var samples=ContractObservationPricing.Compile([new(lookups[0],model,first),new(lookups[1],model,snapshots[1]),new(lookups[2],model,lastActual)],settledContext,new Bindings());
            return new SideValuationContext(lastActual,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted))
                .WithContractObservations(samples,ConnectedScenario.Base(samples.Plan));
        }
        var determined=SwapSettlement.Calculate(cashSwap,1,0,cashQuantity,occurrence,CashContext(snapshots[0]),rounding);
        Equal(determined.ExactAmount.ToExactString(),"80");Equal(determined.Amount.Amount.ToString(),"80");
        Equal(determined.Evidence.Prices.All(p=>p.State==SchedulePriceState.Actual&&p.Lineage.Count==1),true);
        var corrected=snapshots[0] with{Id=Guid.NewGuid(),Selections=[snapshots[0].Selections[0] with{Observation=snapshots[0].Selections[0].Observation with{
            Key=new(f.Revision.CatalogId,Guid.NewGuid()),Value=ExactDecimal.Parse("13")}}]};
        var restated=SwapSettlement.Calculate(cashSwap,1,0,cashQuantity,occurrence,CashContext(corrected),rounding);
        Equal(restated.ExactAmount.ToExactString(),"83");Equal(restated.ContractHash,determined.ContractHash);
        Equal(restated.InputHash!=determined.InputHash,true);Equal(restated.Occurrence,determined.Occurrence);
        var zeroCash=ExactMoney.FromMoney(new(ExactDecimal.Parse("0"),quote.Currency));
        var due=period.PaymentAt;
        var contributions=ImmutableArray.Create(new SettlementContribution(Guid.NewGuid(),1,from,to,1,due,determined.ExactAmount,zeroCash));
        var completions=ImmutableArray.Create(new SwapPeriodCompletion(0,from,to,new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)),"date_only_utc_v1"),
            new SwapPeriodCompletion(1,from,to,new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)),"date_only_utc_v1"));
        SwapSettlementInputs State(ImmutableArray<SettlementContribution> selected)=>new(Guid.NewGuid(),Guid.NewGuid(),cashQuantity,selected,zeroCash,zeroCash,completions,
            "swap_due_nominal_v1","swap_completed_period_trading_pnl_v1");
        var lifecycle=State([]).Evaluate(cashSwap,valuation);
        Equal(lifecycle.RemainingValue.ToExactString(),"0");
        Equal(lifecycle.Payments[1].ContractualAmount.ToExactString(),"80");
        Equal(State([]).RequiredPrices(cashSwap).Length,6);
        Equal(State(contributions).RequiredPrices(cashSwap).Length,3);
        Equal(State(contributions.Add(contributions[0] with{Id=Guid.NewGuid(),MembershipOrdinal=0})).RequiredPrices(cashSwap).Length,0);
        var indexedPeriod=period with{ContractObservations=[]};
        Equal(DefinitionIdentity.For(indexedPeriod).Version,"floating_side_period_v1");
        Equal(DefinitionIdentity.For(indexedPeriod).Canonical.Contains("ContractObservations"),false);
        Equal(DefinitionIdentity.For(period).Version,"floating_side_period_v2");
        Reject("connected_curve_source",()=>model.Compile(snapshots[0],[lookups[0].Market],true,context,freezeObservation:true));
        Reject("connected_curve_source",()=>model.Compile(snapshots[2] with{Selections=[snapshots[2].Selections[0] with{Observation=snapshots[2].Selections[0].Observation with{ObservedAt=at.AddTicks(-1)}}]},[lookups[2].Market],false,context,freezeObservation:true));
        Console.WriteLine("PASS contract observation side: independent delivery and observation periods, retained contracts, connected historical/final/live cash and immutable shocks");
    }
    private static void Equal<T>(T actual,T expected){if(!Equals(actual,expected))throw new Exception($"Expected {expected}; got {actual}");}
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
    private sealed class Bindings:ICurveBindingResolver
    {
        public CurveKey Resolve(LogicalCurveKey curve,ResolvedCalculationContext context)=>throw new NotSupportedException();
        public void RequireVisible(CurveKey curve,ResolvedCalculationContext context){}
    }
}
