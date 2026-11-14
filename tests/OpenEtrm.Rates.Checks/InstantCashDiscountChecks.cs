using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class InstantCashDiscountChecks
{
    public static int Run()
    {
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());var refs=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());
        ReferenceKey R(string name)=>new(refs,name);
        var midnight=new DateTimeOffset(2031,1,1,0,0,0,TimeSpan.Zero);var at=midnight.AddHours(6);var due=midnight.AddHours(18);
        var series=new SeriesKey(revision,Guid.NewGuid());
        var observation=new MarketObservation(new(revision.CatalogId,Guid.NewGuid()),series,R("source"),ObservationRole.Parameter,
            PublicationState.Final,midnight,ExactDecimal.Parse("0.36"));
        var snapshot=new MarketSnapshot(Guid.NewGuid(),revision,at,at,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
            [new(new(series,InputUse.Variable),observation)]).WithKnowledge(Guid.NewGuid(),midnight.AddDays(1));
        var conventions=RateConventionSet.Parse("actual_360","simple","log_linear_discount","none","decimal_per_year");
        var definition=new RateCurveDefinition(new(revision,"rates"),R("usd"),R("source"),conventions,
            [new("1D",series,new(2031,1,2))],Guid.NewGuid(),1);
        var cash=new TimedCashPayment(R("usd"),due);var binding=Guid.NewGuid();
        var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(binding,cash,new(ExactDecimal.Parse("100"),cash.Currency))]);
        var state=new RecordedCashValuation(trade,binding,[],RecordedCashValuation.Policy,snapshot.Knowledge!.ContextId,snapshot.Knowledge.KnownAt);
        var model=new CurveModel(revision,refs,[],[],[],[],[],[new(R("rate"),NumericKind.Dimensionless,null,null,ExactDecimal.Parse("-0.1"),ExactDecimal.Parse("1"))],
            [new(series,null,null,R("rate"))]);
        var discounting=new InstantCurrencyDiscounting([cash.Payment],snapshot,new(Guid.NewGuid(),R("usd"),definition),RateDiscountTiming.Version);
        string Value(ISideDiscounting provider)
        {
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,provider)
                {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,binding),state)};
            return RiskEngine.CalculateExact(RiskEngine.Prepare(model,snapshot,[ValuationPortfolio.FromTrades([trade],setup)]),[],RiskEngine.ExactNumericalPolicy)
                .Groups.Single().BaseValues.Single().Value.ToExactString();
        }
        var actual=Value(discounting);
        if(actual!="99.9500374718960779")throw new Exception($"Timed cash PV lost exact intraday carry: {actual}.");
        var factors=discounting.Resolve([cash.Payment],snapshot);
        if(factors.Single().RateDefinition?.Pillars.Single().Observation!=observation)
            throw new Exception("Timed cash must retain its exact native rate evidence.");
        Reject("discount_curve",()=>Value(new ChangedFactor(factors.Single() with{DiscountFactor=ExactDecimal.Parse("1")})));
        var supplied=factors.Single();
        Reject("discount_curve",()=>Value(new ChangedFactor(supplied with{DiscountFactor=ExactDecimal.Parse("1"),RateDefinition=supplied.RateDefinition! with{Timing=null}})));
        Reject("discount_curve",()=>Value(new ChangedFactor(supplied with{DiscountFactor=ExactDecimal.Parse("1"),RateDefinition=supplied.RateDefinition! with{Timing=null,CurveTerms=null}})));
        Reject("discount_curve",()=>Value(new ChangedFactor(supplied with{RateDefinition=null})));
        Reject("discount_roll_unsupported",()=>((ISideDiscounting)discounting).ResolveForValuation([cash.Payment],snapshot,at.AddMinutes(1),ClockChange.FrozenInputs));
        var otherKnowledge=new MarketSnapshot(snapshot.Id,revision,at,at,snapshot.SelectionPolicy,snapshot.Selections)
            .WithKnowledge(Guid.NewGuid(),snapshot.Knowledge.KnownAt);
        Reject("discount_context",()=>discounting.Resolve([cash.Payment],otherKnowledge));
        Reject("discount_context",()=>discounting.Resolve([cash.Payment],snapshot with{Selections=[snapshot.Selections[0] with{
            Observation=observation with{Value=ExactDecimal.Parse("0.37")}}]}));
        Console.WriteLine("PASS timed cash currency discounting: same-day cash PV uses the shared black-box pricer and exact retained rate evidence");return 1;
    }
    private sealed class ChangedFactor(SidePaymentFactor factor):ISideDiscounting
    {public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)=>[factor];}
    static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
