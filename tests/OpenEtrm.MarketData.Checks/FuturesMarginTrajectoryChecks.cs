using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

internal static class FuturesMarginTrajectoryChecks
{
    public static void Run()
    {
        var f=new Fixture();var at=f.Snapshot().ValuationAt;var quote=f.Quotes[0];var c=f.Contracts[0];
        var series=new FuturesSeriesSpecification(f.R("future_jan"),quote.Quantity,quote.Currency,quote.Multiplier,quote.TradingTick,
            f.R("location"),f.R("uniform"),c.DeliveryFrom,c.DeliveryTo,c.Expiry,quote.Quantity,ExactDecimal.Parse("40"),LotBasis.FixedTotal,
            Fixture.Utc("2040-01-01T00:00:00Z"),"cash");
        var market=new FuturesMarketBinding(Guid.NewGuid(),new(f.C("future"),c.Key),c,series,quote,"cash",FuturesMarketBinding.Implementation);
        var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[FuturesExecution.Bind(binding,market,ExactDecimal.Parse("2"),
            new(ExactDecimal.Parse("3"),quote),at.AddDays(-1),FuturesSettlementState.Unprocessed)]);
        var a=Guid.NewGuid();var b=Guid.NewGuid();var terminal=Guid.NewGuid();
        var round=new CashRoundingConvention(quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal);
        var policy=new FuturesMarginPolicy(Guid.NewGuid(),1,c.Key,f.C("future"),at.AddDays(-2),c.Expiry.AddDays(1),[
            new(a,at,at.AddDays(1),FuturesMarginPhase.Daily),new(b,at.AddDays(1),at.AddDays(2),FuturesMarginPhase.Daily),
            new(terminal,c.Expiry,c.Expiry,FuturesMarginPhase.Final)],round,FuturesMargin.HeldBindingRounding,FuturesMarginOpening.IncludeOpeningInstant,FuturesMarginPolicy.Version);
        var executedAt=at.AddDays(-1);
        if(FuturesMarginSchedule.NextUnsettled(policy,executedAt,[],at.AddTicks(-10))?.Id!=a||
            FuturesMarginSchedule.NextUnsettled(policy,executedAt,[a],at)?.Id!=b||
            FuturesMarginSchedule.NextUnsettled(policy,executedAt,[a,b],at.AddDays(1))?.Id!=terminal||
            FuturesMarginSchedule.NextUnsettled(policy,executedAt,[a,b,terminal],c.Expiry) is not null)
            throw new Exception("Pending VM must select the next exact saved due after the complete accepted causal prefix.");
        Reject("futures_margin_schedule",()=>FuturesMarginSchedule.NextUnsettled(policy,executedAt,[],at));
        Reject("futures_margin_schedule",()=>FuturesMarginSchedule.NextUnsettled(policy,executedAt,[b],at));
        Reject("futures_margin_schedule",()=>FuturesMarginSchedule.NextUnsettled(policy,executedAt,[a,a],at));
        Reject("futures_margin_schedule",()=>FuturesMarginSchedule.NextUnsettled(policy,executedAt,[a],at.AddTicks(-10)));
        if(FuturesMarginSchedule.NextUnsettled(policy,at,[],at)?.Id!=a)
            throw new Exception("An exact include-opening inception must retain its pending saved due before a separate actual determination.");
        Reject("futures_margin_schedule",()=>FuturesMarginSchedule.NextUnsettled(policy,at,[],at.AddTicks(10)));
        if(FuturesMarginSchedule.NextAfterExecution(policy,at,SignedRatio.Zero).Id!=a||
            FuturesMarginSchedule.NextAfterExecution(policy with{Opening=FuturesMarginOpening.AfterOpeningInstant},at,SignedRatio.Zero).Id!=b||
            FuturesMarginSchedule.NextAfterExecution(policy,at,new SignedRatio(-1,3)).Id!=a||
            FuturesMarginSchedule.NextAfterExecution(policy,at,new SignedRatio(1,3)).Id!=b)
            throw new Exception("Fresh linked futures must preserve the exact opening convention even at fractional lattice ticks.");
        Reject("futures_margin_schedule",()=>FuturesMarginSchedule.NextAfterExecution(policy,c.Expiry,new SignedRatio(1,3)));
        Reject("futures_margin_schedule",()=>FuturesMarginSchedule.NextAfterExecution(policy,at,default));
        Reject("futures_margin_schedule",()=>FuturesMarginSchedule.Eligible(policy,policy.From.AddTicks(-10)));
        Reject("futures_margin_schedule",()=>FuturesMarginSchedule.NextAfterExecution(policy,at.ToOffset(TimeSpan.FromHours(1)),SignedRatio.Zero));
        Reject("futures_margin_schedule",()=>FuturesMarginSchedule.NextAfterExecution(policy with{Occurrences=[]},at,SignedRatio.Zero));
        Reject("futures_margin_schedule",()=>FuturesMarginSchedule.NextAfterExecution(policy with{Occurrences=policy.Occurrences.SetItem(0,
            policy.Occurrences[0] with{DueAt=at.AddTicks(-10)})},at,SignedRatio.Zero));
        if(FuturesMarginSchedule.NextUnsettled(policy with{Occurrences=[..policy.Occurrences.Reverse()]},executedAt,[a],at)?.DueAt!=at.AddDays(2))
            throw new Exception("Transport row order cannot replace the native occurrence order or due.");
        var sameInstant=policy with{Occurrences=[policy.Occurrences[0],new(b,c.Expiry,c.Expiry.AddDays(1),FuturesMarginPhase.Daily),policy.Occurrences[2]]};
        Reject("futures_margin_schedule",()=>FuturesMarginSchedule.NextUnsettled(sameInstant,executedAt,[a,b],c.Expiry));
        if(FuturesMarginSchedule.NextUnsettled(sameInstant,executedAt,[a,b,terminal],c.Expiry) is not null)
            throw new Exception("A fully accepted same-instant daily/final prefix must be complete.");
        var context=(f.Snapshot() with{ValuationAt=at.AddDays(2),MarketCutoff=at.AddDays(2)}).WithKnowledge(Guid.NewGuid(),at.AddDays(2));
        ExactUnitPrice Price(string value)=>ExactUnitPrice.Parse(value,PriceUnit.FromQuote(quote));
        ExactMoney Money(string value)=>ExactMoney.Parse(value,quote.Currency);
        FuturesSettlementFact Fact(string value,DateTimeOffset day)
        {
            var selected=f.Select(1,QuoteKind.Outright,"gas_out",value);
            selected=selected with{Observation=selected.Observation with{ObservedAt=day}};
            var source=context with{Selections=[selected]};
            return market.Instrument.ResolveSettlementFact(market,policy.SettlementCurve,f.Model(),source,selected.Observation.Key,at.AddDays(2),day,false);
        }
        ImmutableDictionary<Guid,FuturesSettlementFact> facts=ImmutableDictionary<Guid,FuturesSettlementFact>.Empty.Add(a,Fact("4",at)).Add(b,Fact("5",at.AddDays(1)));
        var firstDetermination=Guid.NewGuid();
        var prior=ImmutableArray.Create(new RecordedFuturesMargin(a,firstDetermination,null,Price("3"),facts[a],Money("80"),new(ExactDecimal.Parse("80"),quote.Currency),at.AddDays(2),1),
            new RecordedFuturesMargin(b,Guid.NewGuid(),firstDetermination,Price("4"),facts[b],Money("80"),new(ExactDecimal.Parse("80"),quote.Currency),at.AddDays(2),2));
        var dues=ImmutableDictionary<Guid,DateTimeOffset>.Empty.Add(a,at.AddDays(3)).Add(b,at.AddDays(3));
        var terms=new FuturesMarginTerms(new(trade.Id,binding),trade.BookId,trade.Instruments[0].Quantity,new(ExactDecimal.Parse("3"),quote),
            at.AddDays(-1),policy);
        if(market.Instrument.ProposeMarginTerms(trade,binding,policy,f.Model(),context)!=terms)
            throw new Exception("Adoption proposals must derive opening terms from the exact booked execution.");
        var beforeExecution=context with{ValuationAt=at.AddDays(-2),MarketCutoff=at.AddDays(-2),Selections=[]};
        Reject("futures_margin_policy",()=>market.Instrument.ProposeMarginTerms(trade,binding,policy,f.Model(),beforeExecution));
        if(market.Instrument.RetainKnownMarginTerms(trade,binding,policy,f.Model(),beforeExecution)!=terms||beforeExecution.ValuationAt!=at.AddDays(-2))
            throw new Exception("Known future execution terms must remain separate from effective opening state and preserve the original source instant.");
        Reject("futures_margin_policy",()=>market.Instrument.RetainKnownMarginTerms(trade,binding,policy,f.Model(),context));
        var configuration=(context with{ValuationAt=policy.From.AddDays(-1),MarketCutoff=policy.From.AddDays(-1),Selections=[]});
        var declared=market.Instrument.ValidateMarginPolicy(market,policy,f.Model(),configuration);
        if(declared!=new FuturesSettlementSource(policy.SettlementCurve,f.S(1),f.R("source")))
            throw new Exception("Policy configuration declares its exact official source independently of execution and current price availability.");
        Reject("futures_margin_policy",()=>market.Instrument.ProposeMarginTerms(trade,binding,policy,f.Model(),context with{MarketCutoff=context.ValuationAt.AddDays(1)}));
        FuturesMarginHistory History(ImmutableArray<RecordedFuturesMargin> entries,MarketSnapshot? source=null,FuturesMarginPolicy? adopted=null)=>
            new(terms with{Policy=adopted??policy},(source??context).Knowledge!.ContextId,(source??context).Knowledge!.KnownAt,entries);
        FuturesMarginTrajectory Plan(ImmutableDictionary<Guid,FuturesSettlementFact> selected,ImmutableArray<RecordedFuturesMargin> recorded,
            ImmutableDictionary<Guid,DateTimeOffset>? corrections=null)=>market.Instrument.PlanMarginTrajectory(trade,binding,policy,context,b,selected,History(recorded),corrections??ImmutableDictionary<Guid,DateTimeOffset>.Empty);
        void Equal(ExactMoney amount,string expected){if(amount.ToExactString()!=expected)throw new Exception($"Expected {expected}, got {amount.ToExactString()}.");}
        var first=Plan(facts,[]);Equal(first.Steps[0].Adjustment,"80");Equal(first.Steps[1].Adjustment,"80");
        FuturesMarginTrajectory FromHistory(FuturesMarginHistory history)=>market.Instrument.PlanMarginTrajectory(
            trade,binding,policy,context,b,facts,history,ImmutableDictionary<Guid,DateTimeOffset>.Empty);
        foreach(var other in new[]{terms with{Owner=new(Guid.NewGuid(),binding)},terms with{Owner=new(trade.Id,Guid.NewGuid())},
            terms with{BookId=Guid.NewGuid()},terms with{Quantity=new(ExactDecimal.Parse("40"),quote.Quantity)},
            terms with{ExecutionPrice=new(ExactDecimal.Parse("2"),quote)},terms with{ExecutedAt=at.AddDays(-2)},
            terms with{Policy=policy with{Id=Guid.NewGuid()}},terms with{Policy=policy with{Revision=2}},
            terms with{Policy=policy with{Occurrences=policy.Occurrences.SetItem(0,policy.Occurrences[0] with{DueAt=at.AddDays(2)})}},
            terms with{Policy=policy with{Rounding=round with{Increment=ExactDecimal.Parse("0.03")}}}})
            Reject("futures_margin_history",()=>FromHistory(History(prior) with{Terms=other}));
        Reject("futures_margin_history",()=>FromHistory(History(prior) with{SourceContextId=Guid.NewGuid()}));
        Reject("futures_margin_history",()=>FromHistory(History(prior) with{KnownThrough=at.AddDays(3)}));
        Reject("futures_margin_history",()=>FromHistory(History(prior) with{KnownThrough=context.Knowledge!.KnownAt.ToOffset(TimeSpan.FromHours(1))}));
        Reject("futures_margin_history",()=>FromHistory(History(prior) with{Terms=terms with{ExecutedAt=terms.ExecutedAt.ToOffset(TimeSpan.FromHours(1))}}));
        Reject("futures_margin_history",()=>FromHistory(History(prior.SetItem(0,prior[0] with{KnownAt=at.AddDays(3)}))));
        Reject("futures_margin_history",()=>FromHistory(History(prior.SetItem(0,prior[0] with{KnownAt=at.AddDays(1)}))));
        Reject("futures_margin_history",()=>FromHistory(History(prior.SetItem(0,prior[0] with{PublicationOrder=0}))));
        Reject("futures_margin_history",()=>FromHistory(History(prior.SetItem(0,prior[0] with{PublicationOrder=3}))));
        if(FromHistory(History(prior.SetItem(1,prior[1] with{PublicationOrder=1}))).Steps.Any(s=>!s.NoChange))
            throw new Exception("An atomic multi-occurrence determination may share one certified publication.");
        var futureRead=context.WithKnowledge(Guid.NewGuid(),at.AddDays(3));
        var conflictingReceipt=prior.SetItem(1,prior[1] with{PublicationOrder=1,KnownAt=at.AddDays(2).AddHours(1)});
        Reject("futures_margin_history",()=>market.Instrument.PlanMarginTrajectory(trade,binding,policy,futureRead,b,facts,
            History(conflictingReceipt,futureRead),ImmutableDictionary<Guid,DateTimeOffset>.Empty));
        var rereadPolicy=policy with{Occurrences=policy.Occurrences.Reverse().ToImmutableArray(),Rounding=round with{}};
        if(FromHistory(History(prior,adopted:rereadPolicy)).Steps.Any(s=>!s.NoChange))
            throw new Exception("Rematerializing the same exact policy version must preserve financial identity.");
        var same=Plan(facts,prior);if(same.Steps.Any(s=>!s.NoChange))throw new Exception("Identical facts must produce no financial revisions.");
        var otherCurve=f.C("other_official_source");
        var otherModel=f.Model([..f.Curves,f.Curves[0] with{Key=otherCurve}]);
        var selectedOther=f.Select(1,QuoteKind.Outright,"gas_out","4");
        var otherFact=market.Instrument.ResolveSettlementFact(market,otherCurve,otherModel,context with{Selections=[selectedOther]},
            selectedOther.Observation.Key,at.AddDays(2),at,false);
        Reject("futures_settlement_evidence",()=>Plan(facts.SetItem(a,otherFact),prior));
        Reject("futures_settlement_evidence",()=>Plan(facts,prior.SetItem(0,prior[0] with{Evidence=otherFact})));
        var replacedEvidence=Plan(facts.SetItem(a,Fact("4",at)),prior);
        if(replacedEvidence.Steps.Any(s=>s.NoChange)||replacedEvidence.Steps.Any(s=>s.AdjustmentDue is not null))
            throw new Exception("A changed accepted source requires a zero-cash causal restatement, including unchanged successors.");
        foreach(var step in replacedEvidence.Steps)Equal(step.Adjustment,"0");

        var revised=Plan(facts.SetItem(a,Fact("4.5",at)),prior,dues);
        Equal(revised.Steps[0].Adjustment,"40");Equal(revised.Steps[1].Adjustment,"-40");
        Equal(revised.Steps.Select(s=>s.Adjustment).Aggregate((x,y)=>x.Add(y)),"0");
        if(revised.Basis.Price.ToExactString()!="5"||revised.Steps[1].BasisOccurrence!=a)
            throw new Exception("An old-price correction must preserve the latest basis and its causal predecessor.");
        var both=Plan(facts.SetItem(a,Fact("4.5",at)).SetItem(b,Fact("5.5",at.AddDays(1))),prior,dues.Remove(b));
        Equal(both.Steps[1].Adjustment,"0");
        if(both.Steps[1].NoChange||both.Basis.Price.ToExactString()!="5.5")throw new Exception("Zero additional cash cannot suppress a changed settlement basis.");
        Reject("futures_margin_correction_due",()=>Plan(facts.SetItem(a,Fact("4.5",at)),prior));
        Reject("futures_margin_correction_due",()=>Plan(facts.SetItem(a,Fact("4.5",at)),prior,dues.SetItem(a,at.AddHours(-1))));
        Reject("futures_margin_correction_due",()=>Plan(facts,prior,dues));
        Reject("futures_margin_coverage",()=>Plan(facts.Remove(a),prior));
        Reject("futures_margin_coverage",()=>Plan(facts,[prior[1]]));
        Reject("futures_margin_coverage",()=>Plan(facts,prior.SetItem(1,prior[1] with{PreviousBasis=Price("3")})));
        Reject("futures_margin_coverage",()=>Plan(facts,prior.SetItem(1,prior[1] with{Variation=new(ExactDecimal.Parse("81"),quote.Currency)})));
        Reject("futures_margin_coverage",()=>Plan(facts,prior.SetItem(1,prior[1] with{BasisDeterminationId=Guid.NewGuid()})));
        var third=Guid.NewGuid();
        var longer=policy with{Occurrences=policy.Occurrences.Add(new(third,at.AddDays(2),at.AddDays(3),FuturesMarginPhase.Daily))};
        var thirdFact=Fact("6",at.AddDays(2));
        var threeFacts=facts.Add(third,thirdFact).SetItem(a,Fact("4.5",at));
        var threePrior=prior.Add(new(third,Guid.NewGuid(),prior[1].DeterminationId,Price("5"),thirdFact,Money("80"),new(ExactDecimal.Parse("80"),quote.Currency),at.AddDays(2),3));
        FuturesMarginTrajectory Three(ImmutableArray<RecordedFuturesMargin> accepted,bool corrections=true)=>market.Instrument.PlanMarginTrajectory(
            trade,binding,longer,context,third,threeFacts,History(accepted,adopted:longer),corrections?dues:ImmutableDictionary<Guid,DateTimeOffset>.Empty);
        var propagated=Three(threePrior);
        if(propagated.Steps[2].NoChange)throw new Exception("A changed predecessor determination requires a causal restatement even when cash and price are unchanged.");
        Equal(propagated.Steps[2].Adjustment,"0");
        var accepted=ImmutableArray.CreateBuilder<RecordedFuturesMargin>();Guid? predecessor=null;
        foreach(var step in propagated.Steps)
        {
            var id=Guid.NewGuid();accepted.Add(new(step.Occurrence.Id,id,predecessor,step.PreviousBasis,step.Evidence,step.ExactVariation,step.Variation,
                at.AddDays(2),10));predecessor=id;
        }
        if(Three(accepted.ToImmutable(),false).Steps.Any(s=>!s.NoChange))throw new Exception("A fully accepted correction closure must reconcile as a no-op on replan.");
        var learnedLate=policy.Until.AddDays(10);
        var later=new MarketSnapshot(Guid.NewGuid(),context.Definitions,learnedLate,learnedLate,context.SelectionPolicy,context.Selections)
            .WithKnowledge(Guid.NewGuid(),learnedLate);
        var late=market.Instrument.PlanMarginTrajectory(trade,binding,policy,later,b,facts.SetItem(a,Fact("4.5",at)),History(prior,later),
            dues.ToImmutableDictionary(p=>p.Key,p=>learnedLate.AddDays(1)));
        Equal(late.Steps[0].Adjustment,"40");Equal(late.Steps[1].Adjustment,"-40");
        Console.WriteLine("PASS futures margin trajectory: successor corrections, zero-cash basis changes, explicit due terms and complete coverage");
        PhysicalFinal();
    }
    private static void PhysicalFinal()
    {
        var f=new Fixture();var quote=f.Quotes[0];var contract=f.Contracts[0];var at=contract.Expiry;
        var series=new FuturesSeriesSpecification(f.R("future_jan"),quote.Quantity,quote.Currency,quote.Multiplier,quote.TradingTick,
            f.R("location"),f.R("uniform"),contract.DeliveryFrom,contract.DeliveryTo,at,quote.Quantity,ExactDecimal.Parse("40"),LotBasis.FixedTotal,
            Fixture.Utc("2040-01-01T00:00:00Z"),"physical");
        var market=new FuturesMarketBinding(Guid.NewGuid(),new(f.C("future"),contract.Key),contract,series,quote,"physical",FuturesMarketBinding.Implementation);
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,[quote],[contract],[f.Curves[0]],[series],[],[new(f.S(1),contract.Key,quote.Key,null)]);
        var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[FuturesExecution.Bind(binding,market,ExactDecimal.Parse("2"),
            new(ExactDecimal.Parse("3"),quote),at.AddDays(-1),FuturesSettlementState.Unprocessed)]);
        var terms=new OpenEtrm.MarketData.Gas.GasPhysicalDeliveryTerms(Guid.NewGuid(),series.Key,quote.Quantity,quote.Currency,series.Location,
            at,at.AddDays(30),at.AddDays(35),new(f.C("future"),contract.Key),OpenEtrm.MarketData.Gas.GasHoldingCarrying.Nominal,
            OpenEtrm.MarketData.Gas.GasPhysicalDeliveryTerms.Version);
        var occurrence=new FuturesMarginOccurrence(Guid.NewGuid(),at,at.AddDays(1),FuturesMarginPhase.Final);
        var rounding=new CashRoundingConvention(quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal);
        var policy=new FuturesMarginPolicy(Guid.NewGuid(),1,contract.Key,f.C("future"),at.AddDays(-2),at.AddDays(2),[occurrence],rounding,
            FuturesMargin.HeldBindingRounding,FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.DeliveryVersion)
            {Delivery=new(Guid.NewGuid(),1,terms)};
        var selected=f.Select(1,QuoteKind.Outright,"gas_out","5");
        selected=selected with{Input=selected.Input with{Use=InputUse.FrozenFact},Observation=selected.Observation with{ObservedAt=at,Role=ObservationRole.FinalContractSettlement}};
        var context=(f.Snapshot() with{ValuationAt=at,MarketCutoff=at,Selections=[selected]}).WithKnowledge(Guid.NewGuid(),at);
        var fact=market.Instrument.ResolveSettlementFact(market,policy.SettlementCurve,model,context,selected.Observation.Key,at,at,true);
        FuturesMarginTrajectory Plan(FuturesMarginPolicy p)=>market.Instrument.PlanMarginTrajectory(trade,binding,p,context,occurrence.Id,
            ImmutableDictionary<Guid,FuturesSettlementFact>.Empty.Add(occurrence.Id,fact),
            new(new(new(trade.Id,binding),trade.BookId,trade.Instruments[0].Quantity,new(ExactDecimal.Parse("3"),quote),at.AddDays(-1),p),
                context.Knowledge!.ContextId,at,[]),ImmutableDictionary<Guid,DateTimeOffset>.Empty);
        var plan=Plan(policy);
        if(plan.Basis.Status!=FuturesMarginStatus.FinalMarginDetermined||plan.Steps.Single().Variation.Amount!=ExactDecimal.Parse("160")||
            plan.FinalDelivery?.Obligation is not OpenEtrm.MarketData.Gas.GasPhysicalDeliveryObligationInstrument||
            plan.FinalDelivery.Quantity.Amount!=ExactDecimal.Parse("80"))
            throw new Exception("Physical final must preserve final VM and a separately valued full signed delivery obligation.");
        Reject("futures_margin_policy",()=>Plan(policy with{Delivery=null}));
        Reject("futures_margin_policy",()=>Plan(policy with{Delivery=policy.Delivery! with{ProfileRevision=0}}));
        Reject("futures_margin_policy",()=>Plan(policy with{Delivery=policy.Delivery! with{Terms=new OpenEtrm.MarketData.Gas.GasPhysicalDeliveryTerms(Guid.NewGuid(),series.Key,
            quote.Quantity,quote.Currency,series.Location,at.AddHours(-1),at.AddDays(30),at.AddDays(35),terms.Mark,terms.Carrying,terms.Implementation)}}));
        Reject("futures_delivery_transition",()=>Plan(policy with{Implementation=FuturesMarginPolicy.Version,Delivery=null}));
        var native=new FuturesMarginState(trade.Id,binding,trade.BookId,trade.Instruments[0].Quantity,series.Key,fact.Price,at,at,
            FuturesMarginState.Version,FuturesMarginStatus.FinalMarginDetermined,fact,context.Knowledge!.ContextId,at.AddDays(1),2,3)
            {AcceptedDelivery=new(Guid.NewGuid(),policy.Delivery!.ProfileId,1,plan.FinalDelivery!,at,3)};
        FuturesMargin.Require(native,market.Instrument);
        Reject("futures_margin_state",()=>FuturesMargin.Require(native with{AcceptedDelivery=null},market.Instrument));
        Reject("futures_margin_state",()=>FuturesMargin.Require(native with{AcceptedDelivery=native.AcceptedDelivery! with{PublicationOrder=2}},market.Instrument));
        Reject("futures_margin_state",()=>FuturesMargin.Require(native with{AcceptedDelivery=native.AcceptedDelivery! with{KnownAt=at.AddHours(1)}},market.Instrument));
        Console.WriteLine("PASS qualified physical final trajectory: pinned continuation, exact final VM and no title before performance");
    }
    static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception($"Expected {code}.");}
}
