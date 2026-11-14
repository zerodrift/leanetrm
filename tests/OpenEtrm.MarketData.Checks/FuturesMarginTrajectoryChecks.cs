using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

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
    }
    static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception($"Expected {code}.");}
}
