using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasCashoutProjectionChecks
{
    internal static void Run()
    {
        var f=new Fixture();var source=f.Snapshot().WithKnowledge(Guid.NewGuid(),Fixture.Utc("2040-12-18T00:00:00Z"));
        var at=source.ValuationAt;var fix=at.AddHours(2);var due=at.AddHours(4);var mark=new CurveRequest(f.C("future"),f.K(1));var quote=f.Quotes[0];
        var owner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),Guid.NewGuid());
        var instrument=new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,due,GasHoldingCarrying.Nominal);
        var terms=new GasCashoutProjectionTerms(Guid.NewGuid(),1,new(mark,ObservationRole.DailySettlement,fix),due,
            GasCashoutProjectionTerms.SelectedMarkExpectation,GasCashoutProjectionTerms.NominalCash,
            new(quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal));
        GasCashoutLifecycleProjection Component(string quantity,GasCashoutProjectionTerms? selected=null)=>
            new(owner,instrument,new(ExactDecimal.Parse(quantity),quote.Quantity),selected??terms,null,source);
        PreparedLifecycleProjection Prepare(ILifecycleProjection component,DateTimeOffset horizon,string cash=LifecycleProjectionPolicy.Outstanding)=>LifecycleProjection.Prepare(f.Model(),source,
            new(new(Guid.NewGuid(),"synthetic_cashout_close",1),f.R("calendar"),[new(new(2040,12,18),horizon,0)]),
            new(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,cash,LifecycleProjectionPolicy.Nominal,LifecycleProjectionPolicy.Numerics,
                LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical),[component],[]);
        foreach(var sign in new[]{"2","-2"})
        {
            var prepared=Prepare(Component(sign),at.AddHours(3));var factor=prepared.Factors.Single();
            var value=prepared.Evaluate("cashout_unpaid",[new(factor.Input,factor.Quote,factor.Unit,ExactDecimal.Parse("1"),QuoteShift.Convention)]);
            var result=value.Values.Single();Equal(result.OpeningValue,sign=="2"?"6":"-6");Equal(result.ResidualValue,sign=="2"?"8":"-8");
            Equal(result.PeriodCash,"0");Equal(result.EconomicPnl,sign=="2"?"2":"-2");
            if(value.States.Single().Residuals.Single().Instruments.Single() is not TimedCashPayment||value.Fixings.Length!=1)
                throw new Exception("A hypothetical cashout must retain ordinary unpaid cash and a path-local fixing without stock or actual facts.");
        }
        foreach(var cash in new[]{LifecycleProjectionPolicy.Outstanding,LifecycleProjectionPolicy.PayAtDue})
        {
            var result=Prepare(Component("2"),at.AddHours(8),cash).Evaluate("cashout_due",[]).Values.Single();
            Equal(result.OpeningValue,"6");Equal(result.ResidualValue,cash==LifecycleProjectionPolicy.Outstanding?"6":"0");
            Equal(result.PeriodCash,cash==LifecycleProjectionPolicy.Outstanding?"0":"6");Equal(result.EconomicPnl,"0");
        }
        var before=Prepare(Component("2"),at.AddHours(1)).Evaluate("before_fixing",[]);
        if(before.Fixings.Length!=0||before.States.Single().Residuals.Single().Instruments.Single()!=instrument)throw new Exception("Cashout occurred before its fixing.");
        Equal(Prepare(Component("0.005"),at.AddHours(3)).Evaluate("positive_tie",[]).Values.Single().EconomicPnl,"0.005");
        Equal(Prepare(Component("-0.005"),at.AddHours(3)).Evaluate("negative_tie",[]).Values.Single().EconomicPnl,"-0.005");
        var negative=Prepare(Component("0.5"),at.AddHours(3));var input=negative.Factors.Single();
        Equal(negative.Evaluate("negative_fixing",[new(input.Input,input.Quote,input.Unit,ExactDecimal.Parse("-4"),QuoteShift.Convention)]).Values.Single().ResidualValue,"-0.5");
        Reject(()=>Component("2",terms with{PriceInterpretation=""}));
        Reject(()=>Component("2",terms with{Fixing=terms.Fixing with{Market=new(f.C("bridge_basis"),f.K(2))}}));
        Reject(()=>Component("2",terms with{CashCarrying=GasCashoutProjectionTerms.CurrencyCurveCash}));
        Reject(()=>Component("2",terms with{Fixing=terms.Fixing with{At=at}}));Reject(()=>Component("2",terms with{CashDueAt=fix.AddHours(-1)}));
        var zero=Prepare(Component("2"),at.AddHours(8)).Evaluate("zero_cashout",[new(input.Input,input.Quote,input.Unit,ExactDecimal.Parse("-3"),QuoteShift.Convention)]);
        Equal(zero.Values.Single().ResidualValue,"0");
        var curve=new HeldCurve(source,quote.Currency,due,at.AddHours(3));
        var carriedInstrument=new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,due,GasHoldingCarrying.CurrencyCurve);
        var carried=new GasCashoutLifecycleProjection(owner,carriedInstrument,new(ExactDecimal.Parse("2"),quote.Quantity),
            terms with{CashCarrying=GasCashoutProjectionTerms.CurrencyCurveCash},curve,source);
        var carry=Prepare(carried,at.AddHours(3)).Evaluate("dated_cashout_carry",[]).Values.Single();
        // 2*3*.6 at opening; the same six-unit cash at H has .6/.8 carrying.
        Equal(carry.OpeningValue,"3.6");Equal(carry.ResidualValue,"4.5");Equal(carry.EconomicPnl,"0.9");
        var copiedSource=source with{Id=Guid.NewGuid()};
        Reject(()=>new GasCashoutLifecycleProjection(owner,carriedInstrument,new(ExactDecimal.Parse("2"),quote.Quantity),
            terms with{CashCarrying=GasCashoutProjectionTerms.CurrencyCurveCash},new HeldCurve(copiedSource,quote.Currency,due,at.AddHours(3)),source));
        try{RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),source,Prepare(Component("2"),at.AddHours(3)).Evaluate("projected_cash",[]).States.Single().Residuals),[],RiskEngine.ExactNumericalPolicy);throw new Exception("Projected cash entered actual valuation.");}
        catch(MarketDataException e)when(e.Code=="projection_cash_state"){}
        NonunitQuotation();
        Console.WriteLine("PASS operator cashout projection: explicit expectation, signed and negative-price cash, native rounding, unpaid/paid branches and no title");
    }
    private static void NonunitQuotation()
    {
        var f=new Fixture();var quote=f.Quotes[0] with{Multiplier=ExactDecimal.Parse("2.5")};
        var curve=f.Direct("synthetic_cashout",f.K(2),quote.Key,SettlementKind.Index);
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,[quote],[f.Contracts[1]],[curve],[],[],[new(f.S(8),f.K(2),quote.Key,null)]);
        var source=(f.Snapshot() with{Selections=[f.Select(8,QuoteKind.Outright,"gas_out","-1.01")]}).WithKnowledge(Guid.NewGuid(),Fixture.Utc("2040-12-18T00:00:00Z"));
        var owner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),Guid.NewGuid());
        var fix=source.ValuationAt.AddHours(1);var mark=new CurveRequest(curve.Key,f.K(2));
        var instrument=new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,fix,GasHoldingCarrying.Nominal);
        var terms=new GasCashoutProjectionTerms(Guid.NewGuid(),1,new(mark,ObservationRole.DailySettlement,fix),fix,
            GasCashoutProjectionTerms.SelectedMarkExpectation,GasCashoutProjectionTerms.NominalCash,new(quote.Currency,ExactDecimal.Parse("0.01"),CashTieRule.ToEven,CashRoundingStage.PaymentTotal));
        var component=new GasCashoutLifecycleProjection(owner,instrument,new(ExactDecimal.Parse("0.5"),quote.Quantity),terms,null,source);
        var policy=new LifecycleProjectionPolicy(LifecycleProjectionPolicy.Version,LifecycleProjectionPolicy.Hold,LifecycleProjectionPolicy.PayAtDue,LifecycleProjectionPolicy.Nominal,
            LifecycleProjectionPolicy.Numerics,LifecycleProjectionPolicy.HeldNonPrice,LifecycleProjectionPolicy.DeclaredCurveExpiry,LifecycleProjectionPolicy.RetainPhysical);
        var result=LifecycleProjection.Prepare(model,source,new(new(Guid.NewGuid(),"synthetic_close",1),f.R("calendar"),[new(new(2040,12,18),fix.AddHours(1),0)]),policy,[component],[]).Evaluate("signed_scaled_cash",[]);
        Equal(result.Values.Single().OpeningValue,"-1.2625");Equal(result.Values.Single().PeriodCash,"-1.26");Equal(result.Values.Single().EconomicPnl,"0.0025");
    }
    private sealed class HeldCurve(MarketSnapshot source,ReferenceKey currency,DateTimeOffset due,DateTimeOffset horizon):IProjectionCurrencyCurve
    {
        public ReferenceKey Currency=>currency;public MarketSnapshot SourceSnapshot=>source;
        public ProjectionCurrencyCurveKey CurveIdentity{get;}=new(new(source.Definitions,"synthetic_rates"),Guid.NewGuid(),Guid.NewGuid(),1);
        public string TimingPolicy=>RateDiscountTiming.Version;
        private ExactDecimal Factor(DateTimeOffset at)=>ExactDecimal.Parse(at==due?"0.6":at==horizon?"0.8":"1");
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected)=>[..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt),selected,DiscountFactorSource.Explicit))];
        public ImmutableArray<SidePaymentFactor> ResolveForValuation(ImmutableArray<SidePaymentKey> payments,MarketSnapshot selected,DateTimeOffset at,string? policy)=>
            [..payments.Select(p=>new SidePaymentFactor(p,Factor(p.PaymentAt).DivideExact(Factor(at)),selected,DiscountFactorSource.Explicit))];
    }
    private static void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected cashout {expected}; got {actual.ToExactString()}.");}
    private static void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="gas_projection_cashout"){return;}throw new Exception("Expected explicit cashout policy refusal.");}
}
