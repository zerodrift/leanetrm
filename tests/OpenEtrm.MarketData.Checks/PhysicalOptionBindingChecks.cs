using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class PhysicalOptionBindingChecks
{
    internal static void Run()
    {
        var f=new FuturesOptionChecks.OptionMarket();var option=f.Option(11);var model=f.Model();
        var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),FuturesOptionChecks.OptionMarket.At);
        var at=source.ValuationAt;var a=f.UnderlyingRequest;
        var basis=model.Build(source,[a,f.SettlementRequest(11)]);
        var numerical=OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein) with{Steps=1};
        var reference=FuturesOptionPricing.Prepare(option,basis,source,new ExplicitDiscounting(ExactDecimal.Parse("0.999"),DiscountFactorSource.Explicit),
            FuturesOptionPricing.DailySettlement,numerical,false,default);
        var asset=new CapturedPriceSource(a,basis.Connected,basis.PricingBindings[a]);
        var from=new DateTimeOffset(2041,1,1,0,0,0,TimeSpan.Zero);
        var gas=new GasPhysicalDeliveryTerms(Guid.NewGuid(),f.Underlying.Key,f.Underlying.LotQuantity,option.Currency,f.R("hub"),from,from.AddDays(1),
            from.AddDays(2),a,GasHoldingCarrying.Nominal,GasPhysicalDeliveryTerms.Version);
        var final=new FuturesMarginOccurrence(Guid.NewGuid(),f.Underlying.LastTradingAt,f.Underlying.LastTradingAt.AddDays(1),FuturesMarginPhase.Final);
        var margin=new FuturesMarginPolicy(Guid.NewGuid(),1,a.Contract,a.Curve,at.AddDays(-1),final.At.AddDays(1),[final],
            new(option.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal),FuturesMargin.HeldBindingRounding,
            FuturesMarginOpening.AfterOpeningInstant,FuturesMarginPolicy.DeliveryVersion){Delivery=new(Guid.NewGuid(),1,gas)};
        var policy=new PhysicalOptionPolicy(PhysicalOptionPolicy.Version,PhysicalOptionPolicy.FrozenBasis,PhysicalOptionPolicy.ReferenceStopping,
            PhysicalOptionPolicy.ReferenceCalibration,PhysicalOptionPolicy.IntrinsicStrategy);
        var selected=new FuturesOptionPhysicalValueBinding(policy,new(f.R(option.Series.AutomaticExerciseRule),1,"settlement_moneyness_v1",ExactDecimal.Parse("0"),"abandon","abandon"),
            PhysicalOptionTerminal.Automatic,margin,asset,null,new ExplicitDiscounting(ExactDecimal.Parse("0.9"),DiscountFactorSource.Explicit),FuturesPhysicalBasisValueBinding.SameDeliverable);
        var first=FuturesOptionChecks.OptionMarket.Holding(71,1,option,"1");
        var second=FuturesOptionChecks.OptionMarket.Holding(72,1,option,"1");
        var vanilla=FuturesOptionChecks.OptionMarket.Holding(73,1,option,"1");
        MeasureBinding Key(Trade trade)=>new(trade.Id,trade.Instruments.Single().Id);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){
            OptionBindings=ImmutableDictionary<Guid,FuturesOptionMarketBinding>.Empty.Add(option.Id,reference),
            PhysicalOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionPhysicalValueBinding>.Empty
                .Add(Key(first),selected).Add(Key(second),selected with{Terminal=PhysicalOptionTerminal.Abandon})};
        var portfolio=ValuationPortfolio.FromTrades([first,second,vanilla],setup);
        if(portfolio.Scopes.Length!=3)throw new Exception("Per-held physical pricing choices were merged through the shared option definition.");
        var declarations=portfolio.Declare(source);
        if(declarations.Count(d=>d.ModelVersion==PhysicalOptionPolicy.Version)!=2||declarations.Count(d=>d.StaticDerivatives is not null)!=2)
            throw new Exception("Selected physical option units did not declare their model and unavailable static derivatives.");
        var prepared=RiskEngine.PrepareConnected(basis.Connected,basis.PricingBindings,[portfolio],basis);
        var values=RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BasePositions.ToDictionary(p=>p.TradeId);
        // Independent one-step martingale oracle. Only the up node exercises;
        // its new VM is carried to the saved final due, distinct from expiry.
        var sigma=((SolvedVolatility)reference.Calibration).Volatility;
        var time=AmericanFuturesOptionModel.YearFraction(at,option.Series.Terms.ExpiryAt,numerical);
        var up=(sigma*time.Sqrt()).Exp();var one=L("1");var probability=(one-one/up)/(up-one/up);
        var vm=probability*(L("3")*up-L("3"))*L("0.9");
        Equal(values[first.Id].Value,ExactMoney.FromMoney(new(vm.ToExact(),option.Currency)).Scale(option.Series.Multiplier));
        Equal(values[second.Id].Value,ExactMoney.Parse("0",option.Currency));
        var vanillaUnit=AmericanFuturesOptionModel.UnitPrice(option.Series.Terms,L("3"),sigma,L("0.999"),at,numerical,default);
        Equal(values[vanilla.Id].Value,ExactMoney.FromMoney(new(vanillaUnit.ToExact(),option.Currency)).Scale(option.Series.Multiplier));
        if(reference.TreePrices!=3)throw new Exception("Physical reference tree prices were omitted from the shared option work counter.");
        if(portfolio.Scopes.Any(s=>s.BindingState is not null)||portfolio.Scopes.Count(s=>s.PricingBinding is not null)!=2)
            throw new Exception("Selected pricing introduced an actual state or lost its distinct pricing scopes.");
        var legacy=ValuationPortfolio.FromTrades([vanilla],setup.ForPosition(new(vanilla.Id,vanilla.Instruments.Single().Id,vanilla.BookId),option.Id));
        if(System.Text.Json.JsonSerializer.SerializeToElement(legacy.Scopes.Single()).TryGetProperty(nameof(UnitValuationScope.PricingBinding),out _))
            throw new Exception("The legacy scope JSON gained a pricing selection.");
        ForeignAsset();
        Console.WriteLine("PASS selected physical option bindings: distinct held policies, ordinary PV and independent next-due funding oracle");

        void ForeignAsset()
        {
            var revisionB=new DefinitionRevision(f.Revision.CatalogId,Guid.NewGuid());var bContract=new MarketContract(new(revisionB,Guid.NewGuid()),ContractKind.Forward,
                f.Underlying.DeliveryFrom,f.Underlying.DeliveryTo,from.AddMonths(1),null);
            var b=new CurveRequest(new(revisionB,"inventory"),bContract.Key);var bSeries=new SeriesKey(revisionB,Guid.NewGuid());
            var modelB=new CurveModel(revisionB,f.Reference,[new(f.R("mmbtu"),f.R("gas"),f.R("energy_basis"),f.R("mmbtu_unit"),f.R("energy"))],
                [f.FuturesQuote],[bContract],[new(b.Curve,f.FuturesQuote.Key,f.R("hub"),f.R("uniform"),new(SettlementKind.Index,f.R("inventory_index"),null),
                    CurveRule.Direct,[bContract.Key],[],ObservationRole.TraderMark,f.R("exchange"),null)],[],[],[new(bSeries,bContract.Key,f.FuturesQuote.Key,null)]);
            var sourceB=new MarketSnapshot(Guid.NewGuid(),revisionB,at,at,source.SelectionPolicy,[new(new(bSeries,InputUse.Variable),
                new(new(revisionB.CatalogId,Guid.NewGuid()),bSeries,f.R("exchange"),ObservationRole.TraderMark,PublicationState.Final,at,ExactDecimal.Parse("5")))])
                .WithKnowledge(source.Knowledge!.ContextId,source.Knowledge.KnownAt);
            var bBuild=modelB.Build(sourceB,[b]);var resolved=basis.Graph.Plan.Context;
            var exportA=model.Compile(source,[a,f.SettlementRequest(11)],false,resolved);var exportB=modelB.Compile(sourceB,[b],false,resolved);
            var bindings=exportA.Points.Concat(exportB.Points).ToImmutableDictionary(p=>p.Key.Request,p=>p.Value);
            var plan=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(exportA.Definitions.Concat(exportB.Definitions)),[..bindings.Values],resolved,new Visible());
            var valuation=ConnectedValuation.Prepare(ConnectedScenario.Base(plan),source,[sourceB]);
            var gasB=new GasPhysicalDeliveryTerms(Guid.NewGuid(),gas.Series,gas.Quantity,gas.Currency,gas.Location,gas.From,gas.To,gas.CashDueAt,b,
                GasHoldingCarrying.CurrencyCurve,GasPhysicalDeliveryTerms.Version);
            var selectedB=selected with{Margin=margin with{Id=Guid.NewGuid(),Delivery=new(Guid.NewGuid(),1,gasB)},
                Asset=new(b,bBuild.Connected,bBuild.PricingBindings[b]),Carrying=new Carrying(gasB,sourceB),MarkInterpretation=FuturesPhysicalBasisValueBinding.ValuationProxy};
            var held=FuturesOptionChecks.OptionMarket.Holding(81,1,option,"2");
            ValuationPortfolio Portfolio(FuturesOptionPhysicalValueBinding terms,Trade? trade=null)=>ValuationPortfolio.FromTrades([trade??held],setup with{
                PhysicalOptionValueBindings=ImmutableDictionary<MeasureBinding,FuturesOptionPhysicalValueBinding>.Empty.Add(Key(trade??held),terms)});
            PreparedConnectedPortfolioRisk Prepare(FuturesOptionPhysicalValueBinding terms,Trade? trade=null)=>RiskEngine.PrepareConnected(valuation,bindings,[Portfolio(terms,trade)],basis);
            var mixed=Prepare(selectedB);
            ExactMoney Expected(string futures,string inventory,string lots)
            {
                var root=L(futures);var node=root*up;var referenceValue=probability*(node-L("3"))*L("0.9");
                var delivery=probability*((node+L(inventory)-root)*L("0.8")-node*L("0.7"));
                return ExactMoney.FromMoney(new((referenceValue+delivery).ToExact(),option.Currency)).Scale(option.Series.Multiplier*ExactDecimal.Parse(lots));
            }
            var baseline=RiskEngine.CalculateExact(mixed,[],RiskEngine.ExactNumericalPolicy).Groups.Single();
            Equal(baseline.BaseValues.Single().Value,Expected("3","5","2"));
            if(baseline.BasePositions.Single().Components.Length!=3||
                baseline.BasePositions.Single().Components.Aggregate(ExactMoney.Parse("0",option.Currency),(sum,c)=>sum.Add(c.Value))!=baseline.BasePositions.Single().Value)
                throw new Exception("The selected components and explicit numerical effect must sum to the once-rounded total.");
            foreach(var(request,futures,inventory,change)in new[]{(a,"3.01","5","0.01"),(b,"3","5.01","0.01"),(b,"3","1","-4")})
            {
                var child=RiskEngine.WithShifts(mixed,[new(CurveShockCoordinate.MarketQuote,new PointCurveTarget(request.Curve,[bindings[request].PointId]),
                    PriceUnit.FromQuote(f.FuturesQuote),ExactDecimal.Parse(change),CurveShift.ParentAdditive)]);
                Equal(RiskEngine.CalculateExact(child,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,Expected(futures,inventory,"2"));
            }
            var shortTrade=FuturesOptionChecks.OptionMarket.Holding(82,1,option,"-2");
            Equal(RiskEngine.CalculateExact(Prepare(selectedB,shortTrade),[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value,Expected("3","5","-2"));
            Reject("risk_static_derivatives_unavailable",()=>RiskEngine.CalculateExact(mixed,[new(CurveShockCoordinate.MarketQuote,
                new PointCurveTarget(a.Curve,[bindings[a].PointId]),PriceUnit.FromQuote(f.FuturesQuote),ExactDecimal.Parse("0.01"),ConnectedRiskBump.Convention)],RiskEngine.ExactNumericalPolicy));
            Reject("valuation_time_unsupported",()=>RiskEngine.CalculateExact(RiskEngine.WithClock(mixed,new(at.AddHours(1),ClockChange.FrozenInputs)),[],RiskEngine.ExactNumericalPolicy));
            void Bad(FuturesOptionPhysicalValueBinding terms)=>Reject("physical_option_binding",()=>Prepare(terms));
            Bad(selectedB with{Carrying=null});Bad(selectedB with{Asset=asset});Bad(selectedB with{MarkInterpretation=""});
            Bad(selectedB with{Rule=selectedB.Rule with{Key=f.R("another_rule")}});
            Bad(selectedB with{Margin=selectedB.Margin with{Implementation=FuturesMarginPolicy.Version,Delivery=null}});
            Reject("physical_option_binding",()=>ValuationPortfolio.FromTrades([held],setup with{PhysicalOptionValueBindings=setup.PhysicalOptionValueBindings}));
        }
    }
    private sealed class Carrying(GasPhysicalDeliveryTerms terms,MarketSnapshot original):ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected)
        {
            if(!selected.HasSameInputs(original))throw new Exception("The physical provider lost its original B source.");
            return [..payments.Select(p=>new SidePaymentFactor(p,ExactDecimal.Parse(p.PaymentAt==terms.From?"0.8":p.PaymentAt==terms.CashDueAt?"0.7":throw new Exception("Unexpected physical due.")),original,DiscountFactorSource.Explicit))];
        }
    }
    private sealed class Visible:ICurveBindingResolver
    {public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new NotSupportedException();public void RequireVisible(CurveKey key,ResolvedCalculationContext context){}}
    private static LatticeDecimal L(string value)=>LatticeDecimal.Parse(value);
    private static void Equal(ExactMoney actual,ExactMoney expected)
    {if(actual!=expected)throw new Exception($"Expected {expected.ToExactString()}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
