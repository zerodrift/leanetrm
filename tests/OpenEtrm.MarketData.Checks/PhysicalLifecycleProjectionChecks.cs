using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
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
            var prepared=f.Prepare(f.Policy with{PricePath=path});
            var shifts=prepared.Factors.Select(p=>new QuoteShift(p.Input,p.Quote,p.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)).ToImmutableArray();
            var value=prepared.Evaluate("physical_retained",shifts);
            var result=value.Values.Single();var sign=signed=="1"?"":"-";
            Equal(result.OpeningValue,sign+"80");Equal(result.EconomicPnl,sign+"80");
            Equal(result.PeriodCash,sign+(path==LifecycleProjectionPolicy.Hold?"80":"20"));
            Equal(result.ResidualValue,sign+(path==LifecycleProjectionPolicy.Hold?"80":"140"));
            var unchanged=prepared.Evaluate("unchanged_wealth",[]).Values.Single();
            Equal(unchanged.OpeningValue,sign+"80");Equal(unchanged.ResidualValue,sign+"80");Equal(unchanged.EconomicPnl,"0");
            var a=prepared.Factors.Single(p=>p.Curves.Contains(f.Component.SettlementProjection.Curve));
            Equal(prepared.Evaluate("futures_only",[new(a.Input,a.Quote,a.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]).Values.Single().EconomicPnl,"0");
            var remaining=value.States.Single().Residuals.Single();
            if(remaining.Instruments.Single() is not GasPhysicalDeliveryObligationInstrument||
                remaining.Positions.Single().Owner!=f.Component.Owner.Owner||remaining.Positions.Single().BindingId!=f.Component.Owner.BindingId||
                remaining.Setup.BindingStates.ContainsKey(new(f.Component.Owner.Owner,f.Component.Owner.BindingId)))
                throw new Exception("Final margin must leave exactly the ordinary signed delivery obligation under its original economic root, without title or accepted state.");
            if(prepared.Factors.Length!=2)throw new Exception("The future and surviving delivery mark must both enter the complete prepared factor union.");
        }
        foreach(var path in new[]{LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.Linear})
        {
            var foreign=new PhysicalFixture("1",GasHoldingCarrying.CurrencyCurve,crossDefinition:true,prepareForeign:true);
            var b=foreign.DeliveryMarket!;
            var prepared=foreign.Prepare(foreign.Policy with{PricePath=path});
            if(prepared.Factors.Length!=2||prepared.Markets.Length!=2)throw new Exception("Both native source graphs and all physical branch factors must be retained before sampling.");
            var value=prepared.Evaluate("foreign_physical",[..prepared.Factors.Select(p=>new QuoteShift(p.Input,p.Quote,p.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention))]).Values.Single();
            // B's conditional consideration factor is .6/.8; its original
            // price/rate evidence is never relabelled as an A observation.
            Equal(value.ResidualValue,path==LifecycleProjectionPolicy.Hold?"130":"175");
            Equal(value.PeriodCash,path==LifecycleProjectionPolicy.Hold?"80":"20");
            Equal(value.OpeningValue,"128");
            Equal(value.EconomicPnl,path==LifecycleProjectionPolicy.Hold?"82":"67");
        }
        var conversion=new PhysicalFixture("1",GasHoldingCarrying.CurrencyCurve,execution:"2",delayedVm:true);
        var finalAt=conversion.Component.Events.Single(e=>e.Kind=="final_margin").At;
        var terminal=conversion.Horizon with{Closes=[new(new(2040,12,27),finalAt,0)]};
        var atFinal=conversion.Prepare(horizon:terminal).Evaluate("unchanged_final_conversion",[]).Values.Single();
        // Opening and final: VM40*.95 plus (B5-A3*.6)*40.
        // The accepted/path margin cash survives unpaid at its own later due.
        Equal(atFinal.OpeningValue,"166");Equal(atFinal.ResidualValue,"166");Equal(atFinal.PeriodCash,"0");Equal(atFinal.EconomicPnl,"0");
        var discounted=new PhysicalFixture("1",GasHoldingCarrying.CurrencyCurve);
        var discountedPath=discounted.Prepare();
        var discountResult=discountedPath.Evaluate("dated_delivery_carry",[..discountedPath.Factors.Select(p=>new QuoteShift(p.Input,p.Quote,p.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention))]);
        // At H the physical mark is seven; the fixed five-unit consideration
        // is still due later, with conditional factor .6/.8=.75.
        Equal(discountResult.Values.Single().OpeningValue,"128");
        Equal(discountResult.Values.Single().ResidualValue,"130");Equal(discountResult.Values.Single().EconomicPnl,"82");
        var missingCurve=new PhysicalFixture("1",GasHoldingCarrying.CurrencyCurve,provideCurve:false);
        Reject("projection_delivery_discount",()=>LifecycleProjection.Prepare(missingCurve.Model,missingCurve.Source,missingCurve.Horizon,missingCurve.Policy,[missingCurve.Component],[]));
        var cross=new PhysicalFixture("1",crossDefinition:true);
        Reject("projection_delivery_source",()=>LifecycleProjection.Prepare(cross.Model,cross.Source,cross.Horizon,cross.Policy,[cross.Component],[]));
        var beforeFinal=cross.Horizon with{Closes=[new(new(2040,12,27),cross.Source.ValuationAt.AddHours(1),0)]};
        Reject("projection_delivery_source",()=>LifecycleProjection.Prepare(cross.Model,cross.Source,beforeFinal,cross.Policy,[cross.Component],[]));
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
        public FuturesDeliveryProjectionMarket? DeliveryMarket{get;}
        private IProjectionCurrencyCurve CashCurve{get;}
        public PreparedLifecycleProjection Prepare(LifecycleProjectionPolicy? policy=null,LifecycleProjectionHorizon? horizon=null)
        {
            if(DeliveryMarket is not {} selected)return LifecycleProjection.Prepare(Model,Source,horizon??Horizon,policy??Policy,[Component],[],cashCurves:ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(CashCurve.Currency,CashCurve));
            var a=Model.Build(Source,[..Component.Dependencies.Where(d=>d.Curve.Revision==Source.Definitions)]);
            var b=selected.Model.Build(selected.Source,[selected.Terms.Mark]);var context=a.Graph.Plan.Context;
            var exportA=Model.Compile(Source,[..a.Values.Select(v=>v.Request)],false,context);
            var exportB=selected.Model.Compile(selected.Source,[selected.Terms.Mark],false,context);
            var bindings=exportA.Points.Concat(exportB.Points).ToImmutableDictionary(p=>p.Key.Request,p=>p.Value);
            var plan=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(exportA.Definitions.Concat(exportB.Definitions)),[..bindings.Values],context,new Visible());
            var valuation=ConnectedValuation.Prepare(ConnectedScenario.Base(plan),Source,[selected.Source]);
            return LifecycleProjection.Prepare(valuation,bindings,a,horizon??Horizon,policy??Policy,[Component],[],cashCurves:ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(CashCurve.Currency,CashCurve),additionalMarkets:[new(b,
                selected.Discounting is null?ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty:ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(selected.Terms.Currency,selected.Discounting),[])]);
        }
        public PhysicalFixture(string signed,GasHoldingCarrying carrying=GasHoldingCarrying.Nominal,bool crossDefinition=false,bool provideCurve=true,bool nativeFinal=false,bool prepareForeign=false,string execution="3",bool delayedVm=false)
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
                new(ExactDecimal.Parse(execution),quote),sourceAt.AddDays(-1),FuturesSettlementState.Unprocessed)]);
            var holding=trade.Instruments.Single();
            var deliveryRevision=crossDefinition?new DefinitionRevision(f.Revision.CatalogId,Guid.NewGuid()):f.Revision;
            var terms=new GasPhysicalDeliveryTerms(Guid.NewGuid(),series.Key,quote.Quantity,quote.Currency,series.Location,at,at.AddDays(30),
                at.AddDays(35),new(new(deliveryRevision,inventory.Key.Code),new(deliveryRevision,f.K(2).Id)),carrying,GasPhysicalDeliveryTerms.Version);
            var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,contract.Key,f.C("future"),at.AddDays(-3),at.AddDays(2),
                [new(Guid.NewGuid(),at,delayedVm?at.AddDays(1):at,FuturesMarginPhase.Final)],new(quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),
                FuturesMargin.HeldBindingRounding,FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.DeliveryVersion){Delivery=new(Guid.NewGuid(),1,terms)};
            var state=new FuturesMarginState(trade.Id,binding,trade.BookId,holding.Quantity,market.Series.Key,ExactUnitPrice.FromPrice(new(ExactDecimal.Parse(execution),quote)),
                sourceAt.AddDays(-1),sourceAt,FuturesMarginState.Version,FuturesMarginStatus.Open,null,Source.Knowledge!.ContextId,Source.Knowledge.KnownAt,1,1);
            CashCurve=new HeldCurve(Source,quote.Currency,at.AddHours(36),terms.CashDueAt,delayedVm?at.AddDays(1):null);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(market.Instrument.Id,market),
                CashCurve){
                BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)};
            var history=new FuturesMarginHistory(market.Instrument.ProposeMarginTerms(trade,binding,margin,Model,Source),Source.Knowledge.ContextId,Source.Knowledge.KnownAt,[]);
            var accepted=nativeFinal?new FuturesKnownHistory(new(trade.Id,binding,trade.BookId),history.Terms,Source.Knowledge.ContextId,Source.Knowledge.KnownAt,
                [new(margin.Occurrences.Single(),Guid.NewGuid(),null,null,2,2,at.AddHours(1),state.SettledBasis,
                    new(new(f.Revision.CatalogId,Guid.NewGuid()),f.S(1),f.R("source"),ObservationRole.FinalContractSettlement,PublicationState.Final,at,ExactDecimal.Parse("3")),
                    at.AddHours(1),ExactMoney.Parse("0",quote.Currency),new(default,quote.Currency))],[]):null;
            if(prepareForeign)
            {
                var selectedContract=f.Contracts[1] with{Key=terms.Mark.Contract};var selectedSeries=new SeriesKey(deliveryRevision,Guid.NewGuid());
                var selectedCurve=inventory with{Key=terms.Mark.Curve,Constituents=[selectedContract.Key]};
                var selectedModel=new CurveModel(deliveryRevision,f.Reference,f.Quantities,[quote],[selectedContract],[selectedCurve],[],[],[new(selectedSeries,selectedContract.Key,quote.Key,null)]);
                var selectedMark=mark with{Input=new(selectedSeries,InputUse.Variable),Observation=mark.Observation with{Series=selectedSeries,Key=new(deliveryRevision.CatalogId,Guid.NewGuid())}};
                var selectedSource=(Source with{Id=Guid.NewGuid(),Definitions=deliveryRevision,Selections=[selectedMark]}).WithKnowledge(Source.Knowledge!.ContextId,Source.Knowledge.KnownAt);
                DeliveryMarket=new(terms,selectedModel,selectedSource,carrying==GasHoldingCarrying.CurrencyCurve?new HeldCurve(selectedSource,quote.Currency,at.AddHours(36),terms.CashDueAt):null);
            }
            if(!crossDefinition||DeliveryMarket is not null)
            {
                var selectedModel=DeliveryMarket?.Model??Model;var selectedSource=DeliveryMarket?.Source??Source;
                var captured=selectedModel.Build(selectedSource,[terms.Mark]);
                setup=setup with{FuturesValueBindings=setup.FuturesValueBindings.Add(new(trade.Id,binding),new(history.Terms,[],FuturesMarginValueBinding.FlatReference){
                    Physical=new(FuturesPhysicalBasisValueBinding.Version,FuturesPhysicalBasisValueBinding.ValuationProxy,margin.Delivery.ProfileId,margin.Delivery.ProfileRevision,
                        new(terms.Mark,captured.Connected,captured.PricingBindings[terms.Mark]),carrying==GasHoldingCarrying.CurrencyCurve&&provideCurve?
                            DeliveryMarket?.Discounting??setup.Discounting:null)})};
            }
            Component=new(trade,binding,state,history,setup,market.Market,Model,Source,accepted,DeliveryMarket);
            Policy=new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,LifecycleProjectionPolicy.Nominal,
                LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
            Horizon=new(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,29),at.AddHours(36),0)]);
        }
    }
    private sealed class Visible:ICurveBindingResolver
    {public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new NotSupportedException();public void RequireVisible(CurveKey key,ResolvedCalculationContext context){}}
    private sealed class HeldCurve(MarketSnapshot source,ReferenceKey currency,DateTimeOffset horizon,DateTimeOffset due,DateTimeOffset? vmDue=null):IProjectionCurrencyCurve
    {
        public ReferenceKey Currency=>currency;
        public MarketSnapshot SourceSnapshot=>source;
        public ProjectionCurrencyCurveKey CurveIdentity{get;}=new(new(source.Definitions,"synthetic_rates"),Guid.NewGuid(),Guid.NewGuid(),1);
        public string TimingPolicy=>RateDiscountTiming.Version;
        private ExactDecimal Factor(DateTimeOffset at)=>ExactDecimal.Parse(at==due?"0.6":at==vmDue?"0.95":at==horizon?"0.8":"1");
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt),selected,DiscountFactorSource.Explicit))];
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected,DateTimeOffset at,string? clockPolicy)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt).DivideExact(Factor(at)),selected,DiscountFactorSource.Explicit))];
    }
}
