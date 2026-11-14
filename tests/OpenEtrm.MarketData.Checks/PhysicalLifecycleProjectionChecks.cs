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
        var native=new PhysicalFixture("1",nativeFinal:true,nativeLink:true);
        var nativeValue=native.Prepare().Evaluate("accepted_native_delivery",[]);
        Equal(nativeValue.Values.Single().OpeningValue,"80");Equal(nativeValue.Values.Single().ResidualValue,"80");Equal(nativeValue.Values.Single().EconomicPnl,"0");
        if(nativeValue.States.Single().Residuals.Single().Positions.Single().Owner!=native.NativeOwner!.Value.Owner||
            !nativeValue.States.Single().KnownResidualOwners.Contains(native.NativeOwner.Value))
            throw new Exception("An accepted final must activate its exact predeclared native delivery owner, without another hypothetical obligation.");
        var probe=new FinalProbe(new(Guid.NewGuid(),Guid.NewGuid(),native.Component.Owner.BookId),native.Source,native.Component.Currency,
            native.Component.SettlementProjection,native.Horizon.Closes[0].Instant);
        var withProbe=native.Prepare(extra:[probe]);var primitive=withProbe.Factors.Single(f=>f.Curves.Contains(native.Component.SettlementProjection.Curve));
        var fixedInput=withProbe.Evaluate("shared_native_actual_final",[new(primitive.Input,primitive.Quote,primitive.Unit,ExactDecimal.Parse("2"),QuoteShift.Convention)]);
        Equal(fixedInput.Values.Single(v=>v.Owner==probe.Owner).PeriodCash,"3");
        var originalNative=native.NativeAttachment!;var prior=originalNative.CurrentFinal with{DeterminationId=Guid.NewGuid(),MarginRevision=2,
            Receipt=new(1,originalNative.CurrentFinal.Receipt.KnownAt.AddMinutes(-1))};
        var current=originalNative.CurrentFinal with{MarginRevision=3,PreviousDeterminationId=prior.DeterminationId};
        var corrected=native.NativeFact! with{RunRevision=3,PreviousDeterminationId=prior.DeterminationId};
        var ancestry=originalNative with{InitialDeterminationId=prior.DeterminationId,CurrentFinal=current,Finals=[current,prior],Receipt=prior.Receipt};
        var correction=KnownFuturesPhysicalFinal.Create(ancestry,corrected);
        if(correction.InitialDeterminationId!=prior.DeterminationId||correction.Determination!=corrected)
            throw new Exception("Native final ancestry must preserve the exact initial link and selected corrected head independently of array order.");
        Reject("projection_native_delivery",()=>KnownFuturesPhysicalFinal.Create(originalNative with{Finals=originalNative.Finals.Add(prior)},native.NativeFact!));
        Reject("projection_native_delivery",()=>KnownFuturesPhysicalFinal.Create(originalNative,native.NativeFact! with{
            Observation=native.NativeFact!.Observation with{Role=ObservationRole.DailySettlement}}));
        foreach(var invalid in new Func<GasPhysicalKnownAttachment,GasPhysicalKnownAttachment>[] {
            a=>a with{DeliveryOwner=new(a.FutureOwner.TradeId,Guid.NewGuid(),a.FutureOwner.BookId)},
            a=>a with{DeliveryOwner=a.DeliveryOwner with{BookId=Guid.NewGuid()}},
            a=>a with{InitialDeterminationId=Guid.NewGuid()},
            a=>a with{Receipt=a.Receipt with{PublicationOrder=7},CurrentFinal=a.CurrentFinal with{Receipt=a.CurrentFinal.Receipt with{PublicationOrder=7}},
                Finals=[a.CurrentFinal with{Receipt=a.CurrentFinal.Receipt with{PublicationOrder=7}}]},
            a=>a with{OriginalQuantity=new(ExactDecimal.Parse("80"),a.OriginalQuantity.Specification)},
            a=>a with{CurrentFinal=a.CurrentFinal with{Price=ExactUnitPrice.Parse(a.CurrentFinal.Price.ToExactString(),new PriceUnit(a.CurrentFinal.Price.Unit.Currency,a.CurrentFinal.Price.Unit.Quantity,ExactDecimal.Parse("2")))},
                Finals=[..a.Finals.Select(f=>f with{Price=ExactUnitPrice.Parse(f.Price.ToExactString(),new PriceUnit(f.Price.Unit.Currency,f.Price.Unit.Quantity,ExactDecimal.Parse("2")))})]},
            a=>a with{ProfileId=Guid.NewGuid()},
            a=>a with{SourceSnapshot=a.SourceSnapshot with{ValuationAt=a.SourceSnapshot.ValuationAt.AddHours(1)}}})
            Reject("projection_native_delivery",()=>new PhysicalFixture("1",nativeFinal:true,nativeLink:true,nativeTransform:invalid));
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
        public AccountingPositionKey? NativeOwner{get;}
        public GasPhysicalKnownAttachment? NativeAttachment{get;}
        public KnownFuturesMargin? NativeFact{get;}
        private IProjectionCurrencyCurve CashCurve{get;}
        public PreparedLifecycleProjection Prepare(LifecycleProjectionPolicy? policy=null,LifecycleProjectionHorizon? horizon=null,ImmutableArray<ILifecycleProjection> extra=default)
        {
            var components=ImmutableArray.Create<ILifecycleProjection>(Component).AddRange(extra.IsDefault?[]:extra);
            if(DeliveryMarket is not {} selected)return LifecycleProjection.Prepare(Model,Source,horizon??Horizon,policy??Policy,components,[],cashCurves:ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(CashCurve.Currency,CashCurve));
            var a=Model.Build(Source,[..Component.Dependencies.Where(d=>d.Curve.Revision==Source.Definitions)]);
            var b=selected.Model.Build(selected.Source,[selected.Terms.Mark]);var context=a.Graph.Plan.Context;
            var exportA=Model.Compile(Source,[..a.Values.Select(v=>v.Request)],false,context);
            var exportB=selected.Model.Compile(selected.Source,[selected.Terms.Mark],false,context);
            var bindings=exportA.Points.Concat(exportB.Points).ToImmutableDictionary(p=>p.Key.Request,p=>p.Value);
            var plan=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(exportA.Definitions.Concat(exportB.Definitions)),[..bindings.Values],context,new Visible());
            var valuation=ConnectedValuation.Prepare(ConnectedScenario.Base(plan),Source,[selected.Source]);
            return LifecycleProjection.Prepare(valuation,bindings,a,horizon??Horizon,policy??Policy,components,[],cashCurves:ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(CashCurve.Currency,CashCurve),additionalMarkets:[new(b,
                selected.Discounting is null?ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty:ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(selected.Terms.Currency,selected.Discounting),[])]);
        }
        public PhysicalFixture(string signed,GasHoldingCarrying carrying=GasHoldingCarrying.Nominal,bool crossDefinition=false,bool provideCurve=true,bool nativeFinal=false,bool prepareForeign=false,string execution="3",bool delayedVm=false,bool nativeLink=false,
            Func<GasPhysicalKnownAttachment,GasPhysicalKnownAttachment>? nativeTransform=null)
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
            KnownFuturesPhysicalFinal? native=null;
            if(nativeLink)
            {
                var fact=accepted!.Determinations.Single();var receipt=new GasPhysicalKnownReceipt(fact.PublicationOrder,fact.KnownAt);
                var final=new GasPhysicalKnownFinal(fact.DeterminationId,fact.RunRevision,fact.PreviousDeterminationId,fact.Observation.Key.Id,
                    ExactUnitPrice.FromPrice(new(fact.Observation.Value,quote)),receipt);
                NativeOwner=new(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),trade.BookId);
                var attachment=new GasPhysicalKnownAttachment(Source,new(trade.Id,binding,trade.BookId),NativeOwner.Value,1,margin.Id,margin.Revision,
                    margin.Delivery!.ProfileId,margin.Delivery.ProfileRevision,terms,margin.Rounding,GasDeliveryPrincipal.Scope,"contractual_principal_due_v1",
                    margin.Occurrences.Single(),holding.Quantity,fact.DeterminationId,final,[final],at,receipt,[],[],[],[]);
                NativeAttachment=attachment;NativeFact=fact;
                native=KnownFuturesPhysicalFinal.Create(nativeTransform?.Invoke(attachment)??attachment,fact);
            }
            Component=new(trade,binding,state,history,setup,market.Market,Model,Source,accepted,DeliveryMarket,native);
            Policy=new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,LifecycleProjectionPolicy.Nominal,
                LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
            Horizon=new(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,29),at.AddHours(36),0)]);
        }
    }
    private sealed class Visible:ICurveBindingResolver
    {public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new NotSupportedException();public void RequireVisible(CurveKey key,ResolvedCalculationContext context){}}
    private sealed record FinalProbe(AccountingPositionKey Owner,MarketSnapshot SourceSnapshot,ReferenceKey Currency,CurveRequest Request,DateTimeOffset At):ILifecycleProjection
    {
        public string Implementation=>"synthetic_known_final_probe_v1";
        public ImmutableArray<CurveRequest> Dependencies=>[Request];
        public LifecycleProjectionState Opening=>new([],[]);
        public ImmutableArray<LifecycleProjectionEvent> Events=>[new(LifecycleProjectionEvent.ScopedId(Owner,Guid.Parse("f0000000-0000-0000-0000-000000000001")),At,[],"synthetic_final_read")];
        public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)=>[..Events.Where(e=>e.At<=horizon)];
        public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
        {
            var price=context.Fix(new(Request,ObservationRole.IndexFixing,At)).Price;
            return state with{Cash=state.Cash.Add(new(occurrence.Id,At,At,price.ExactTimes(new(ExactDecimal.Parse("1"),price.Unit.Quantity))))};
        }
    }
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
