using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class PhysicalOptionProjectionChecks
{
    internal static void Run()
    {
        var f=new FuturesOptionChecks.OptionMarket();var option=f.Option(13);var model=f.Model();
        var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),FuturesOptionChecks.OptionMarket.At);
        var requests=FuturesOptionPricing.Requests(model,option.Series,FuturesOptionPricing.DailySettlement);
        var basis=model.Build(source,[requests.Underlying,requests.Settlement]);
        var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var reference=FuturesOptionPricing.Prepare(option,basis,source,discount,FuturesOptionPricing.DailySettlement,
            OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein),true,default);
        var future=model.BindFutures(Guid.NewGuid(),requests.Underlying,"physical",FuturesMarketBinding.Implementation);
        var daily=option.Series.Terms.ExpiryAt.AddHours(1);var from=future.Contract.Expiry;
        var asset=Asset(f,source,from.AddMonths(4));
        var gas=new GasPhysicalDeliveryTerms(Guid.NewGuid(),future.Series.Key,future.Quote.Quantity,option.Currency,future.Series.Location,
            from,from.AddDays(30),from.AddDays(35),asset.Request,GasHoldingCarrying.CurrencyCurve,GasPhysicalDeliveryTerms.Version);
        var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,requests.Underlying.Contract,requests.Underlying.Curve,source.ValuationAt,from.AddDays(1),
            [new(Guid.NewGuid(),daily,daily.AddDays(2),FuturesMarginPhase.Daily),new(Guid.NewGuid(),from,from.AddDays(2),FuturesMarginPhase.Final)],
            new(option.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),FuturesMargin.HeldBindingRounding,
            FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.DeliveryVersion){Delivery=new(Guid.NewGuid(),1,gas)};
        var aCurve=new HeldCurve(source,option.Currency,[(daily.AddDays(2),"0.9"),(from.AddDays(2),"0.9")]);
        var bCurve=new HeldCurve(asset.Source,option.Currency,[(gas.From,"0.8"),(gas.CashDueAt,"0.7")]);
        var rule=new FuturesOptionAutomaticRule(f.R(option.Series.AutomaticExerciseRule),1,"settlement_moneyness_v1",default,"abandon","exercise");
        var policy=new PhysicalOptionPolicy(PhysicalOptionPolicy.Version,PhysicalOptionPolicy.FrozenBasis,PhysicalOptionPolicy.ReferenceStopping,
            PhysicalOptionPolicy.ReferenceCalibration,PhysicalOptionPolicy.IntrinsicStrategy);
        var selected=new FuturesOptionPhysicalValueBinding(policy,rule,PhysicalOptionTerminal.Automatic,margin,
            new(asset.Request,asset.Build.Connected,asset.Build.PricingBindings[asset.Request]),bCurve,aCurve,FuturesPhysicalBasisValueBinding.ValuationProxy,future);
        var resolved=basis.Graph.Plan.Context;var a=model.Compile(source,[requests.Underlying,requests.Settlement],false,resolved);
        var b=asset.Model.Compile(asset.Source,[asset.Request],false,resolved);
        var bindings=a.Points.Concat(b.Points).ToImmutableDictionary(p=>p.Key.Request,p=>p.Value);
        var plan=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(a.Definitions.Concat(b.Definitions)),[..bindings.Values],resolved,new Visible());
        var valuation=ConnectedValuation.Prepare(ConnectedScenario.Base(plan),source,[asset.Source]);
        var global=ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty.Add(option.Currency,aCurve);
        var other=new LifecycleProjectionMarket(asset.Build.Connected,asset.Build.PricingBindings,asset.Build,ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty,[]);
        var projectionPolicy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,
            LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
        foreach(var lots in new[]{"2","-2"})
        {
            var trade=FuturesOptionChecks.OptionMarket.Holding(lots=="2"?91:92,1,option,lots);var owner=new AccountingPositionKey(trade.Id,trade.Instruments[0].Id,trade.BookId);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){
                OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(option.Id,reference),
                PhysicalOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionPhysicalValueBinding>.Empty.Add(new(trade.Id,owner.BindingId),selected)};
            var delivery=new FuturesDeliveryProjectionMarket(gas,asset.Model,asset.Source,bCurve);
            var scalar=new ExplicitDiscounting(ExactDecimal.Parse("0.8"),DiscountFactorSource.Explicit);
            var scalarSetup=setup with{PhysicalOptionValueBindings=setup.PhysicalOptionValueBindings.SetItem(new(trade.Id,owner.BindingId),selected with{Carrying=scalar})};
            try{_=new FuturesOptionLifecycleProjection(trade,owner.BindingId,scalarSetup,future,margin,rule,null,
                requests.Underlying,requests.Underlying,model,source,deliveryMarket:delivery with{Discounting=scalar});
                throw new Exception("The physical projection admitted a scalar factor instead of its complete captured carrying curve.");}
            catch(MarketDataException e)when(e.Code=="projection_delivery_discount"){}
            var component=new FuturesOptionLifecycleProjection(trade,owner.BindingId,setup,future,margin,rule,null,
                requests.Underlying,requests.Underlying,model,source,deliveryMarket:delivery);
            foreach(var at in new[]{daily.AddMinutes(-30),daily.AddHours(1)})
            {
                var horizon=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(DateOnly.FromDateTime(at.UtcDateTime),at,0)]);
                var prepared=LifecycleProjection.Prepare(valuation,bindings,basis,horizon,projectionPolicy,[component],[],global,additionalMarkets:[other]);
                var sign=lots=="2"?"":"-";
                // q=20,000: pending VM(3-2)*.9 + exchange(5*.8-3*.7) = 56,000.
                var unchanged=prepared.Evaluate("unchanged",[]).Values.Single();
                Equal(unchanged.OpeningValue,sign+"56000");Equal(unchanged.ResidualValue,sign+"56000");Equal(unchanged.PeriodCash,"0");Equal(unchanged.EconomicPnl,"0");
                foreach(var(request,expected)in new[]{(requests.Underlying,"400"),(asset.Request,"1600")})
                {
                    var factor=prepared.Factors.Single(p=>p.Curves.Contains(request.Curve));
                    var shifted=prepared.Evaluate("independent_native_shock",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("0.1"),QuoteShift.Convention)]).Values.Single();
                    Equal(shifted.EconomicPnl,sign+expected);
                }
                if(prepared.Factors.Length!=2||!component.DependencySource(asset.Request).HasSameInputs(asset.Source))
                    throw new Exception("The option's independent delivery source was omitted or relabelled.");
            }
        }
        ClosedParent();
        Console.WriteLine("PASS physical option economic routes: independent A/B, signed full value, exact next VM due, separate carrying and no-gain handoff");
    }
    private static void ClosedParent()
    {
        var f=new FuturesOptionChecks.OptionMarket();var option=f.Option(13);var model=f.Model();var from=f.Underlying.LastTradingAt;
        var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),from.AddMonths(3));
        var a=f.UnderlyingRequest;var basis=model.Build(source,[a,f.SettlementRequest(13)]);
        var discount=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var reference=FuturesOptionPricing.Prepare(option,basis,source,discount,FuturesOptionPricing.DailySettlement,
            OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein),true,default);
        var future=model.BindFutures(Guid.NewGuid(),a,"physical",FuturesMarketBinding.Implementation);
        var priorAsset=Asset(f,source,from.AddDays(40));var childAsset=Asset(f,source,from.AddMonths(6));
        GasPhysicalDeliveryTerms Terms(AssetMarket asset)=>new(Guid.NewGuid(),future.Series.Key,future.Quote.Quantity,option.Currency,future.Series.Location,
            from,from.AddDays(30),from.AddDays(35),asset.Request,GasHoldingCarrying.Nominal,GasPhysicalDeliveryTerms.Version);
        var priorTerms=Terms(priorAsset);var childTerms=Terms(childAsset);var daily=option.Series.Terms.ExpiryAt.AddHours(1);
        var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,a.Contract,a.Curve,source.ValuationAt,from.AddDays(1),
            [new(Guid.NewGuid(),daily,daily,FuturesMarginPhase.Daily),new(Guid.NewGuid(),from,from,FuturesMarginPhase.Final)],
            new(option.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),FuturesMargin.HeldBindingRounding,
            FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.DeliveryVersion){Delivery=new(Guid.NewGuid(),1,priorTerms)};
        var childMargin=margin with{Id=Guid.NewGuid(),Delivery=new(Guid.NewGuid(),1,childTerms)};
        var rule=new FuturesOptionAutomaticRule(f.R(option.Series.AutomaticExerciseRule),1,"settlement_moneyness_v1",default,"abandon","exercise");
        var policy=new PhysicalOptionPolicy(PhysicalOptionPolicy.Version,PhysicalOptionPolicy.FrozenBasis,PhysicalOptionPolicy.ReferenceStopping,
            PhysicalOptionPolicy.ReferenceCalibration,PhysicalOptionPolicy.IntrinsicStrategy);
        var trade=FuturesOptionChecks.OptionMarket.Holding(93,1,option,"2");var binding=trade.Instruments[0].Id;
        CapturedPriceSource Price(AssetMarket asset)=>new(asset.Request,asset.Build.Connected,asset.Build.PricingBindings[asset.Request]);
        var selected=new FuturesOptionPhysicalValueBinding(policy,rule,PhysicalOptionTerminal.Automatic,margin,Price(priorAsset),null,discount,
            FuturesPhysicalBasisValueBinding.ValuationProxy,future);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){
            OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(option.Id,reference),
            PhysicalOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionPhysicalValueBinding>.Empty.Add(new(trade.Id,binding),selected)};
        var executed=source.ValuationAt.AddHours(1);var nativeBinding=Guid.NewGuid();
        var nativeTrade=new Trade(Guid.NewGuid(),trade.BookId,[FuturesExecution.Bind(nativeBinding,future,ExactDecimal.Parse("2"),new(option.Series.Terms.Strike,f.FuturesQuote),executed,FuturesSettlementState.Unprocessed)]);
        var accepted=new KnownFuturesOptionEvent(Guid.NewGuid(),"exercise",ExactDecimal.Parse("2"),default,executed,executed,source.Knowledge!.KnownAt,1,
            null,null,null,null,null,new(nativeTrade,nativeBinding,childMargin,Guid.NewGuid(),1,executed,source.Knowledge.KnownAt));
        var history=new FuturesOptionKnownHistory(new(trade.Id,binding,trade.BookId),1,option.SeriesKey,ExactDecimal.Parse("2"),source.Knowledge.ContextId,source.Knowledge.KnownAt,[accepted]);
        var nativeTerms=future.Instrument.RetainKnownMarginTerms(nativeTrade,nativeBinding,childMargin,model,source);
        var known=new FuturesKnownHistory(new(nativeTrade.Id,nativeBinding,trade.BookId),nativeTerms,source.Knowledge.ContextId,source.Knowledge.KnownAt,[],[]);
        var childSetup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(future.Instrument.Id,future),null){
            InstrumentDiscounting=ImmutableDictionary<Guid,ISideDiscounting>.Empty.Add(future.Instrument.Id,discount),
            FuturesValueBindings=ImmutableDictionary<MeasureBinding,FuturesMarginValueBinding>.Empty.Add(new(nativeTrade.Id,nativeBinding),
                new(nativeTerms,[],FuturesMarginValueBinding.FlatReference){Physical=new(FuturesPhysicalBasisValueBinding.Version,selected.MarkInterpretation,
                    childMargin.Delivery!.ProfileId,childMargin.Delivery.ProfileRevision,Price(childAsset),null)})};
        var child=new FuturesLifecycleProjection(accepted,known,childSetup,a,model,source,new(childTerms,childAsset.Model,childAsset.Source,null),parent:history);
        var parent=new FuturesOptionLifecycleProjection(trade,binding,setup,future,margin,rule,null,a,a,model,source,history,
            deliveryMarket:new(priorTerms,priorAsset.Model,priorAsset.Source,null),nativeContinuations:ImmutableDictionary<Guid,FuturesLifecycleProjection>.Empty.Add(accepted.EventId,child));
        var resolved=basis.Graph.Plan.Context;var exports=new[]{model.Compile(source,[a,f.SettlementRequest(13)],false,resolved),
            priorAsset.Model.Compile(priorAsset.Source,[priorAsset.Request],false,resolved),childAsset.Model.Compile(childAsset.Source,[childAsset.Request],false,resolved)};
        var bindings=exports.SelectMany(p=>p.Points).ToImmutableDictionary(p=>p.Key.Request,p=>p.Value);
        var plan=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(exports.SelectMany(p=>p.Definitions)),[..bindings.Values],resolved,new Visible());
        var valuation=ConnectedValuation.Prepare(ConnectedScenario.Base(plan),source,[priorAsset.Source,childAsset.Source]);
        var at=from.AddMonths(2);var horizon=new LifecycleProjectionHorizon(new(Guid.NewGuid(),"synthetic_closed_parent",1),f.R("calendar"),[new(DateOnly.FromDateTime(at.UtcDateTime),at,0)]);
        var projectionPolicy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,
            LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
        var others=new[]{priorAsset,childAsset}.Select(asset=>new LifecycleProjectionMarket(asset.Build.Connected,asset.Build.PricingBindings,asset.Build,
            ImmutableDictionary<ReferenceKey,ISideDiscounting>.Empty,[])).ToImmutableArray();
        var result=LifecycleProjection.Prepare(valuation,bindings,basis,horizon,projectionPolicy,[parent,child],[],additionalMarkets:others).Evaluate("native_full_early_exercise",[]);
        Equal(result.Values.Single(v=>v.Owner==parent.Owner).ResidualValue,"0");Equal(result.Values.Single(v=>v.Owner==child.Owner).ResidualValue,"40000");
        Equal(result.Values.Single(v=>v.Owner==child.Owner).PeriodCash,"20000");Equal(result.Values.Select(v=>v.EconomicPnl).Aggregate((a,b)=>a.Add(b)),"0");
        if(parent.Events.Any(e=>e.Kind=="option_expiry")||parent.MarketUsesFor(at).Any(use=>use.Through!=source.ValuationAt)||!parent.InputFinals.IsEmpty)
            throw new Exception("The fully exercised parent demanded its expired current-profile asset instead of the child's own original profile.");
        Console.WriteLine("PASS closed physical option parent: full native early exercise, independent child profile and no unused expired parent mark");
    }
    private sealed record AssetMarket(CurveRequest Request,CurveModel Model,MarketSnapshot Source,CurveBuild Build);
    private static AssetMarket Asset(FuturesOptionChecks.OptionMarket f,MarketSnapshot opening,DateTimeOffset expiry)
    {
        var revision=new DefinitionRevision(f.Revision.CatalogId,Guid.NewGuid());var contract=new MarketContract(new(revision,Guid.NewGuid()),ContractKind.Forward,
            f.Underlying.DeliveryFrom,f.Underlying.DeliveryTo,expiry,null);var request=new CurveRequest(new(revision,"inventory"),contract.Key);var series=new SeriesKey(revision,Guid.NewGuid());
        var model=new CurveModel(revision,f.Reference,[new(f.R("mmbtu"),f.R("gas"),f.R("energy_basis"),f.R("mmbtu_unit"),f.R("energy"))],
            [f.FuturesQuote],[contract],[new(request.Curve,f.FuturesQuote.Key,f.R("hub"),f.R("uniform"),new(SettlementKind.Index,f.R("synthetic_inventory"),null),CurveRule.Direct,
                [contract.Key],[],ObservationRole.TraderMark,f.R("exchange"),null)],[],[],[new(series,contract.Key,f.FuturesQuote.Key,null)]);
        var source=new MarketSnapshot(Guid.NewGuid(),revision,opening.ValuationAt,opening.MarketCutoff,opening.SelectionPolicy,
            [new(new(series,InputUse.Variable),new(new(revision.CatalogId,Guid.NewGuid()),series,f.R("exchange"),ObservationRole.TraderMark,PublicationState.Final,opening.ValuationAt,ExactDecimal.Parse("5")))])
            .WithKnowledge(opening.Knowledge!.ContextId,opening.Knowledge.KnownAt);
        return new(request,model,source,model.Build(source,[request]));
    }
    private sealed class HeldCurve(MarketSnapshot original,ReferenceKey currency,ImmutableArray<(DateTimeOffset At,string Factor)> factors):IProjectionCurrencyCurve
    {
        public ReferenceKey Currency=>currency;
        public MarketSnapshot SourceSnapshot=>original;
        public ProjectionCurrencyCurveKey CurveIdentity{get;}=new(new(original.Definitions,"synthetic_rates"),Guid.NewGuid(),Guid.NewGuid(),1);
        public string TimingPolicy=>RateDiscountTiming.Version;
        private ExactDecimal Factor(DateTimeOffset at)=>ExactDecimal.Parse(factors.Where(p=>p.At==at).Select(p=>p.Factor).SingleOrDefault()??"1");
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot source)
        {
            if(!source.HasSameInputs(original))throw new Exception("The carrying provider lost its original admitted native source.");
            return [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt),original,DiscountFactorSource.Explicit))];
        }
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot source,DateTimeOffset at,string? clockPolicy)=>
            [..Resolve(payments,source).Select(p=>p with{DiscountFactor=p.DiscountFactor.DivideExact(Factor(at))})];
    }
    private sealed class Visible:ICurveBindingResolver
    {public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new NotSupportedException();public void RequireVisible(CurveKey key,ResolvedCalculationContext context){}}
    private static void Equal(ExactMoney actual,string expected)
    {if(actual.CompareTo(ExactMoney.Parse(expected,actual.Currency))!=0)throw new Exception($"Expected {expected}; got {actual.ToExactString()}.");}
}
