using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class PhysicalLifecycleProjectionChecks
{
    internal static void Run()
    {
        var explicitPolicy=new PhysicalFixture("1").Policy;
        var policyJson=System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(explicitPolicy))!.AsObject();
        policyJson.Remove("PhysicalDelivery");
        try{System.Text.Json.JsonSerializer.Deserialize<LifecycleProjectionPolicy>(policyJson.ToJsonString());throw new Exception("An omitted physical-delivery assumption was accepted.");}
        catch(System.Text.Json.JsonException){}
        foreach(var signed in new[]{"1","-1"})
        foreach(var path in new[]{LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Linear})
        {
            var f=new PhysicalFixture(signed);
            var prepared=LifecycleProjection.Prepare(f.Model,f.Source,f.Horizon,f.Policy with{PricePath=path},[f.Component],[]);
            var shifts=prepared.Factors.Select(p=>new QuoteShift(p.Input,p.Quote,p.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)).ToImmutableArray();
            var value=prepared.Evaluate("physical_retained",shifts);
            var result=value.Values.Single();var sign=signed=="1"?"":"-";
            Equal(result.OpeningValue,"0");Equal(result.EconomicPnl,sign+"160");
            Equal(result.PeriodCash,sign+(path==LifecycleProjectionPolicy.Hold?"80":"20"));
            Equal(result.ResidualValue,sign+(path==LifecycleProjectionPolicy.Hold?"80":"140"));
            var remaining=value.States.Single().Residuals.Single();
            if(remaining.Instruments.Single() is not GasPhysicalDeliveryObligationInstrument||
                remaining.Positions.Single().Owner!=f.Component.Owner.Owner||remaining.Positions.Single().BindingId!=f.Component.Owner.BindingId||
                remaining.Setup.BindingStates.ContainsKey(new(f.Component.Owner.Owner,f.Component.Owner.BindingId)))
                throw new Exception("Final margin must leave exactly the ordinary signed delivery obligation under its original economic root, without title or accepted state.");
            if(prepared.Factors.Length!=2)throw new Exception("The future and surviving delivery mark must both enter the complete prepared factor union.");
        }
        var discounted=new PhysicalFixture("1",GasHoldingCarrying.CurrencyCurve);
        var discountedPath=LifecycleProjection.Prepare(discounted.Model,discounted.Source,discounted.Horizon,discounted.Policy,[discounted.Component],[]);
        var discountResult=discountedPath.Evaluate("dated_delivery_carry",[..discountedPath.Factors.Select(p=>new QuoteShift(p.Input,p.Quote,p.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention))]);
        // At H the physical mark is seven; the fixed five-unit consideration
        // is still due later, with conditional factor .6/.8=.75.
        Equal(discountResult.Values.Single().ResidualValue,"130");Equal(discountResult.Values.Single().EconomicPnl,"210");
        var missingCurve=new PhysicalFixture("1",GasHoldingCarrying.CurrencyCurve,provideCurve:false);
        Reject("projection_delivery_discount",()=>LifecycleProjection.Prepare(missingCurve.Model,missingCurve.Source,missingCurve.Horizon,missingCurve.Policy,[missingCurve.Component],[]));
        var cross=new PhysicalFixture("1",crossDefinition:true);
        Reject("projection_delivery_source",()=>LifecycleProjection.Prepare(cross.Model,cross.Source,cross.Horizon,cross.Policy,[cross.Component],[]));
        var beforeFinal=cross.Horizon with{Closes=[new(new(2040,12,27),cross.Source.ValuationAt.AddHours(1),0)]};
        var before=LifecycleProjection.Prepare(cross.Model,cross.Source,beforeFinal,cross.Policy,[cross.Component],[]);
        if(before.Factors.Length!=1)throw new Exception("An unconsumed cross-definition final branch read or invented its delivery input.");
        Reject("projection_policy",()=>LifecycleProjection.Prepare(cross.Model,cross.Source,beforeFinal,cross.Policy with{PhysicalDelivery=""},[cross.Component],[]));
        var actualFinal=new PhysicalFixture("1",nativeFinal:true);
        Reject("futures_delivery_transition",()=>LifecycleProjection.Prepare(actualFinal.Model,actualFinal.Source,actualFinal.Horizon,actualFinal.Policy,[actualFinal.Component],[]));
        Console.WriteLine("PASS physical lifecycle projection: signed final VM plus ordinary retained delivery wealth, full branch factors and no assumed title");
    }
    private static void Equal(ExactMoney value,string expected)
    {if(value!=ExactMoney.Parse(expected,value.Currency))throw new Exception($"Expected physical wealth {expected}, got {value.ToExactString()}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected physical projection refusal "+code);}

    private sealed class PhysicalFixture
    {
        public CurveModel Model{get;}
        public MarketSnapshot Source{get;}
        public LifecycleProjectionHorizon Horizon{get;}
        public LifecycleProjectionPolicy Policy{get;}
        public FuturesLifecycleProjection Component{get;}
        public PhysicalFixture(string signed,GasHoldingCarrying carrying=GasHoldingCarrying.Nominal,bool crossDefinition=false,bool provideCurve=true,bool nativeFinal=false)
        {
            var f=new Fixture();var quote=f.Quotes[0];var contract=f.Contracts[0];var at=contract.Expiry;
            var series=new FuturesSeriesSpecification(f.R("future_jan"),quote.Quantity,quote.Currency,quote.Multiplier,quote.TradingTick,
                f.R("location"),f.R("uniform"),contract.DeliveryFrom,contract.DeliveryTo,at,quote.Quantity,ExactDecimal.Parse("40"),
                LotBasis.FixedTotal,Fixture.Utc("2040-01-01T00:00:00Z"),"physical");
            var inventory=f.Direct("inventory",f.K(2),quote.Key,SettlementKind.Index);
            Model=new(f.Revision,f.Reference,f.Quantities,[quote],[contract,f.Contracts[1]],[f.Curves[0],inventory],[series],[],
                [new(f.S(1),contract.Key,quote.Key,null),new(f.S(8),f.K(2),quote.Key,null)]);
            var sourceAt=at.AddHours(-12);var selected=f.Select(1,QuoteKind.Outright,"gas_out","3");
            var mark=new SelectedObservation(new(f.S(8),InputUse.Variable),new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(8),f.R("source"),
                ObservationRole.TraderMark,PublicationState.Final,sourceAt,ExactDecimal.Parse("5")));
            Source=(f.Snapshot() with{ValuationAt=sourceAt,MarketCutoff=sourceAt,Selections=[selected,mark]}).WithKnowledge(Guid.NewGuid(),nativeFinal?at.AddHours(36):sourceAt);
            var market=new FuturesMarketBinding(Guid.NewGuid(),new(f.C("future"),contract.Key),contract,series,quote,"physical",FuturesMarketBinding.Implementation);
            var binding=Guid.NewGuid();var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[FuturesExecution.Bind(binding,market,ExactDecimal.Parse(signed),
                new(ExactDecimal.Parse("3"),quote),sourceAt.AddDays(-1),FuturesSettlementState.Unprocessed)]);
            var holding=trade.Instruments.Single();
            var deliveryRevision=crossDefinition?new DefinitionRevision(f.Revision.CatalogId,Guid.NewGuid()):f.Revision;
            var terms=new GasPhysicalDeliveryTerms(Guid.NewGuid(),series.Key,quote.Quantity,quote.Currency,series.Location,at,at.AddDays(30),
                at.AddDays(35),new(new(deliveryRevision,inventory.Key.Code),new(deliveryRevision,f.K(2).Id)),carrying,GasPhysicalDeliveryTerms.Version);
            var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,contract.Key,f.C("future"),at.AddDays(-3),at.AddDays(2),
                [new(Guid.NewGuid(),at,at,FuturesMarginPhase.Final)],new(quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),
                FuturesMargin.HeldBindingRounding,FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.DeliveryVersion){Delivery=new(Guid.NewGuid(),1,terms)};
            var state=new FuturesMarginState(trade.Id,binding,trade.BookId,holding.Quantity,market.Series.Key,ExactUnitPrice.FromPrice(new(ExactDecimal.Parse("3"),quote)),
                sourceAt.AddDays(-1),sourceAt,FuturesMarginState.Version,FuturesMarginStatus.Open,null,Source.Knowledge!.ContextId,Source.Knowledge.KnownAt,1,1);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(market.Instrument.Id,market),null){
                BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)};
            if(carrying==GasHoldingCarrying.CurrencyCurve&&provideCurve)
                setup=setup with{InstrumentDiscounting=setup.InstrumentDiscounting.Add(market.Instrument.Id,new HeldCurve(Source,quote.Currency,at.AddHours(36),terms.CashDueAt))};
            var history=new FuturesMarginHistory(market.Instrument.ProposeMarginTerms(trade,binding,margin,Model,Source),Source.Knowledge.ContextId,Source.Knowledge.KnownAt,[]);
            var accepted=nativeFinal?new FuturesKnownHistory(new(trade.Id,binding,trade.BookId),history.Terms,Source.Knowledge.ContextId,Source.Knowledge.KnownAt,
                [new(margin.Occurrences.Single(),Guid.NewGuid(),null,null,2,2,at.AddHours(1),state.SettledBasis,
                    new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(1),f.R("source"),ObservationRole.FinalContractSettlement,PublicationState.Final,at,ExactDecimal.Parse("3")),
                    at.AddHours(1),ExactMoney.Parse("0",quote.Currency),new(default,quote.Currency))],[]):null;
            Component=new(trade,binding,state,history,setup,market.Market,Model,Source,accepted);
            Policy=new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,LifecycleProjectionPolicy.Nominal,
                LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
            Horizon=new(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,29),at.AddHours(36),0)]);
        }
    }
    private sealed class HeldCurve(MarketSnapshot source,ReferenceKey currency,DateTimeOffset horizon,DateTimeOffset due):IProjectionCurrencyCurve
    {
        public ReferenceKey Currency=>currency;
        public MarketSnapshot SourceSnapshot=>source;
        public ProjectionCurrencyCurveKey CurveIdentity{get;}=new(new(source.Definitions,"synthetic_rates"),Guid.NewGuid(),Guid.NewGuid(),1);
        public string TimingPolicy=>RateDiscountTiming.Version;
        private ExactDecimal Factor(DateTimeOffset at)=>ExactDecimal.Parse(at==due?"0.6":at==horizon?"0.8":"1");
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt),selected,DiscountFactorSource.Explicit))];
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected,DateTimeOffset at,string? clockPolicy)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt).DivideExact(Factor(at)),selected,DiscountFactorSource.Explicit))];
    }
}
