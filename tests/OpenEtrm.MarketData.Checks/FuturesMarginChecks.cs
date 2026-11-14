using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

internal static class FuturesMarginChecks
{
    public static void Run()
    {
        var f=new Fixture();var quote=f.Quotes[0];var contract=f.Contracts[0];var at=f.Snapshot().ValuationAt;
        var series=new FuturesSeriesSpecification(f.R("future_jan"),quote.Quantity,quote.Currency,quote.Multiplier,quote.TradingTick,
            f.R("location"),f.R("uniform"),contract.DeliveryFrom,contract.DeliveryTo,contract.Expiry,quote.Quantity,
            ExactDecimal.Parse("40"),LotBasis.FixedTotal,Fixture.Utc("2040-01-01T00:00:00Z"),"cash");
        var market=new FuturesMarketBinding(Guid.NewGuid(),new(f.C("future"),f.K(1)),contract,series,quote,"cash",FuturesMarketBinding.Implementation);
        var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[FuturesExecution.Bind(binding,market,ExactDecimal.Parse("2"),new UnitPrice(ExactDecimal.Parse("3"),quote),at.AddDays(-1),FuturesSettlementState.Unprocessed)]);
        var contextId=Guid.NewGuid();
        var original=new FuturesMarginState(trade.Id,binding,trade.BookId,trade.Instruments[0].Quantity,series.Key,
            ExactUnitPrice.Parse("3",PriceUnit.FromQuote(quote)),at.AddDays(-1),at.AddDays(-1),FuturesMarginState.Version,FuturesMarginStatus.Open,null,contextId,at,1,1);
        var round=new CashRoundingConvention(quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal);
        ExactMoney Value(FuturesMarginState state,string mark,DateTimeOffset? valuationAt=null)
        {
            var snapshot=f.Snapshot() with{Selections=[..f.Snapshot().Selections.Select(s=>s.Input.Series==f.S(1)?s with{Observation=s.Observation with{Value=ExactDecimal.Parse(mark)}}:s)]};
            if(valuationAt is {} selectedAt)snapshot=snapshot with{ValuationAt=selectedAt};
            snapshot=snapshot.WithKnowledge(state.SourceContextId,state.KnownThrough);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(market.Instrument.Id,market),null)
                {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)};
            var portfolio=ValuationPortfolio.FromTrades([new Trade(trade.Id,trade.BookId,[FuturesExecution.Bind(binding,market,state.Quantity.Amount.DivideExact(market.LotSize.Amount),new UnitPrice(ExactDecimal.Parse("3"),quote),at.AddDays(-1),FuturesSettlementState.Unprocessed)])],setup);
            return RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),snapshot,[portfolio]),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
        }
        FuturesMarginState Accept(FuturesMarginState before,FuturesMarginProposal proposal,DateTimeOffset recorded)=>before with{
            SettledBasis=proposal.Basis.Price,SettledAt=proposal.Basis.At,Status=proposal.Basis.Status,LatestSettlement=proposal.Evidence,
            KnownAt=recorded,KnownThrough=recorded,StateRevision=before.StateRevision+1,PublicationOrder=before.PublicationOrder+1};
        void Equal(ExactMoney actual,string expected){if(actual.ToExactString()!=expected)throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
        Equal(Value(original,"3.2"),"16");
        var captured=f.Snapshot().WithKnowledge(contextId,at);
        captured=captured with{Selections=[..captured.Selections.Select(s=>s.Input.Series==f.S(1)?s with{Observation=s.Observation with{Value=ExactDecimal.Parse("3.2")}}:s)]};
        var capturedKey=captured.Selections.Single(s=>s.Input.Series==f.S(1)).Observation.Key;
        FuturesMarginProposal Plan(MarketSnapshot source,ObservationKey key)=>market.Instrument.PlanSettlement(original,market,f.C("future"),f.Model(),source,key,at,at,at,round,FuturesMargin.HeldBindingRounding,false);
        Equal(Plan(captured,capturedKey).ExactVariation,"16");
        Reject("futures_settlement_evidence",()=>Plan(captured,new(capturedKey.CatalogId,Guid.NewGuid())));
        Reject("futures_settlement_evidence",()=>Plan(captured with{Selections=[..captured.Selections.Select(s=>s.Input.Series==f.S(1)?s with{
            Observation=s.Observation with{State=PublicationState.Preliminary}}:s)]},capturedKey));
        Reject("futures_settlement_evidence",()=>Plan(captured with{Selections=[..captured.Selections.Select(s=>s.Input.Series==f.S(1)?s with{
            Observation=s.Observation with{Source=f.R("other_source")}}:s)]},capturedKey));
        foreach(var use in new[]{InputUse.FrozenFact,(InputUse)999})
            Reject("futures_settlement_evidence",()=>Plan(captured with{Selections=[..captured.Selections.Select(s=>s.Input.Series==f.S(1)?s with{
                Input=new(s.Input.Series,use)}:s)]},capturedKey));
        Reject("futures_settlement_evidence",()=>Plan(captured with{MarketCutoff=at.AddDays(1)},capturedKey));
        var independentSources=f.Model([..f.Curves.Select(curve=>curve.Key==f.C("future")?
            curve with{DirectRole=ObservationRole.TraderMark,DirectSource=f.R("trader_source")}:curve),
            f.Curves[0] with{Key=f.C("official_settlement")}]);
        Equal(market.Instrument.PlanSettlement(original,market,f.C("official_settlement"),independentSources,captured,capturedKey,at,at,at,
            round,FuturesMargin.HeldBindingRounding,false).ExactVariation,"16");
        Reject("futures_settlement_evidence",()=>market.Instrument.PlanSettlement(original,market,f.C("future"),independentSources,
            captured,capturedKey,at,at,at,round,FuturesMargin.HeldBindingRounding,false));
        FuturesSettlementFact Fact(string price,DateTimeOffset effectiveAt,DateTimeOffset knownAt,bool final=false)
        {
            var selected=f.Select(1,QuoteKind.Outright,"gas_out",price);
            selected=selected with{Observation=selected.Observation with{ObservedAt=effectiveAt,
                Role=final?ObservationRole.FinalContractSettlement:ObservationRole.DailySettlement},
                Input=new(selected.Input.Series,final?InputUse.FrozenFact:InputUse.Variable)};
            var source=new MarketSnapshot(Guid.NewGuid(),f.Revision,effectiveAt,effectiveAt,
                InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[selected]).WithKnowledge(contextId,knownAt);
            return market.Instrument.ResolveSettlementFact(market,f.C("future"),f.Model(),source,selected.Observation.Key,knownAt,effectiveAt,final);
        }
        var fact=Fact("3.2",at,at);
        var laterCapture=captured.WithKnowledge(Guid.NewGuid(),at.AddDays(10));
        var oldFact=market.Instrument.ResolveSettlementFact(market,f.C("future"),f.Model(),captured,capturedKey,at,at,false);
        var sameFact=market.Instrument.ResolveSettlementFact(market,f.C("future"),f.Model(),laterCapture,capturedKey,at,at,false);
        if(oldFact!=sameFact)throw new Exception("A later reader cutoff must preserve original source publication identity.");
        var finalInput=f.Select(1,QuoteKind.Outright,"gas_out","3.2");
        finalInput=finalInput with{Observation=finalInput.Observation with{Role=ObservationRole.FinalContractSettlement,ObservedAt=contract.Expiry}};
        var invalidFinal=new MarketSnapshot(Guid.NewGuid(),f.Revision,contract.Expiry,contract.Expiry,
            InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[finalInput]).WithKnowledge(contextId,contract.Expiry);
        Reject("futures_settlement_evidence",()=>market.Instrument.ResolveSettlementFact(market,f.C("future"),f.Model(),invalidFinal,
            finalInput.Observation.Key,contract.Expiry,contract.Expiry,true));
        var settled=FuturesMargin.Determine(original,market,fact,at,at,at,round,FuturesMargin.HeldBindingRounding,false);
        Equal(settled.ExactVariation,"16");Equal(ExactMoney.FromMoney(settled.Variation),"16");Equal(Value(Accept(original,settled,at),"3.2"),"0");
        Equal(Value(Accept(original,settled,at),"3.3").Add(ExactMoney.FromMoney(settled.Variation)),"24");
        var negative=original with{Quantity=new(ExactDecimal.Parse("-80"),quote.Quantity)};
        Equal(Value(negative,"3.2"),"-16");
        Equal(ExactMoney.FromMoney(ExactMoney.Parse("0.025",quote.Currency).Round(round,CashRoundingStage.PaymentTotal)),"0.02");
        var quantum=FuturesMargin.Determine(original,market,Fact("3.025",at,at),at,at,at,
            round with{Increment=ExactDecimal.Parse("0.03")},FuturesMargin.HeldBindingRounding,false);
        Equal(quantum.ExactVariation,"2");Equal(ExactMoney.FromMoney(quantum.Variation),"2.01");Equal(quantum.RoundingEffect,"0.01");
        Reject("futures_settlement_evidence",()=>Fact("3.0003125",at,at));
        Reject("futures_margin_state",()=>Value(original with{Status=FuturesMarginStatus.FinalMarginDetermined},"3.2"));
        Reject("futures_margin_state",()=>FuturesMargin.Require(original with{BookId=null},market.Instrument));
        Reject("futures_settlement_evidence",()=>Plan(captured with{Selections=[..captured.Selections.Select(s=>s.Input.Series==f.S(1)?s with{
            Observation=s.Observation with{Role=ObservationRole.TraderMark}}:s)]},capturedKey));
        Reject("futures_settlement_evidence",()=>FuturesMargin.Determine(original,market,Fact("3.2",at,at.AddHours(1)),at,at,at,round,FuturesMargin.HeldBindingRounding,false));
        Reject("futures_settlement_evidence",()=>FuturesMargin.Determine(original,market,fact,at,at,at,round,FuturesMargin.HeldBindingRounding,true));
        var lastDaily=FuturesMargin.Determine(original with{KnownThrough=contract.Expiry},market,Fact("3.2",contract.Expiry,contract.Expiry),
            contract.Expiry,contract.Expiry,contract.Expiry,round,FuturesMargin.HeldBindingRounding,false);
        var lastFinal=FuturesMargin.Determine(Accept(original,lastDaily,contract.Expiry),market,Fact("3.2",contract.Expiry,contract.Expiry,true),
            contract.Expiry,contract.Expiry,contract.Expiry,round,FuturesMargin.HeldBindingRounding,true);
        Equal(lastFinal.ExactVariation,"0");
        var terminal=Accept(original,lastFinal,contract.Expiry);
        Equal(Value(terminal,"3.2",contract.Expiry),"0");
        Reject("futures_margin_state",()=>Value(terminal with{SettledBasis=ExactUnitPrice.Parse("3.3",PriceUnit.FromQuote(quote))},"3.2",contract.Expiry));
        if(lastFinal.Basis.Status!=FuturesMarginStatus.FinalMarginDetermined)throw new Exception("Same-instant final settlement must complete after daily settlement.");
        Console.WriteLine("PASS futures margin: shared PV, signed lots, margin reset, continuing exposure, exact rounding and evidence fences");
    }
    static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception($"Expected {code}.");}
}
