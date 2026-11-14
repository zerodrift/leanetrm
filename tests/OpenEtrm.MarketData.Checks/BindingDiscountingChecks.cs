using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Risk;

internal static class BindingDiscountingChecks
{
    internal static void Run()
    {
        var f=new Fixture();var original=f.Snapshot();
        var observation=new MarketObservation(new(f.Revision.CatalogId,Guid.NewGuid()),new(f.Revision,Guid.NewGuid()),f.R("source"),ObservationRole.Parameter,
            PublicationState.Final,original.ValuationAt,ExactDecimal.Parse("0.25"));
        var source=(original with{Selections=[new(new(observation.Series,InputUse.Variable),observation)]})
            .WithKnowledge(Guid.NewGuid(),original.ValuationAt.AddDays(2));
        var specification=new ValueSpecification(f.R("annual_rate"),NumericKind.Dimensionless,null,null,ExactDecimal.Parse("-0.9"),ExactDecimal.Parse("1"));
        var model=new CurveModel(f.Revision,f.Reference,f.Quantities,[],[],[],[],[specification],[new(observation.Series,null,null,specification.Key)]);
        var cash=new TimedCashPayment(f.R("currency"),source.ValuationAt.AddDays(365));var book=Guid.NewGuid();
        var nominal=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        var curved=new Curve(source,cash.Currency);var fee=new NativeFee(source,cash.Currency,observation);
        var owners=Enumerable.Range(0,3).Select(_=>new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,Guid.NewGuid()),Guid.NewGuid(),book)).ToArray();
        owners[2]=new(new AccountingOwnerKey(AccountingOwnerKind.Trade,owners[0].Owner.Id),owners[0].BindingId,book);
        Quantity Q(string n)=>new(ExactDecimal.Parse(n),cash.Currency);
        RecordedCashValuation State(int i,string amount,string paid)=>new(owners[i],cash,Q(amount),paid=="0"?[]:[new(Guid.NewGuid(),1,owners[i],source.ValuationAt,
            source.Knowledge!.KnownAt,ExactMoney.Parse(paid,cash.Currency),AccountingFlowKind.EconomicCash)],RecordedCashValuation.Policy,source.Knowledge!.ContextId,source.Knowledge.KnownAt);
        var states=new[]{State(0,"100","40"),State(1,"100","33"),State(2,"10","0")};
        var positions=owners.Select((o,i)=>new RiskPosition(o.Owner,o.BindingId,book,cash.Id,states[i].Quantity,
            new(new Money(default,cash.Currency),cash.Currency),new(default,cash.Currency))).ToImmutableArray();
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,fee){
            BindingStates=states.ToImmutableDictionary(s=>new MeasureBinding(s.Owner,s.BindingId),s=>(IBindingValuationState)s),
            BindingDiscounting=ImmutableDictionary<MeasureBinding,ISideDiscounting>.Empty.Add(new(owners[0].Owner,owners[0].BindingId),nominal)
                .Add(new(owners[1].Owner,owners[1].BindingId),curved)};
        var portfolio=new ValuationPortfolio([cash],positions,setup);
        var prepared=RiskEngine.Prepare(model,source,[portfolio]);
        var values=RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy,detail:RiskDetail.Holdings).Groups.Single();
        Equal(values.BasePositions.Single(p=>p.Owner==owners[0].Owner).Value,"60");
        Equal(values.BasePositions.Single(p=>p.Owner==owners[1].Owner).Value,"60.3");
        Equal(values.BasePositions.Single(p=>p.Owner==owners[2].Owner).Value,"8");Equal(values.BaseValues.Single().Value,"128.3");
        var declarations=portfolio.Declare(source);
        if(declarations.Length!=3||declarations.SelectMany(d=>d.DiscountInputs).Select(d=>d.Provider).Distinct().Count()!=3||
            declarations[0].SameInputFence(declarations[1]))throw new Exception("Shared cash definitions combined different held carrying inputs.");
        var subset=setup.ForPosition(owners[0],cash.Id);
        if(subset.BindingDiscounting.Count!=1||!ReferenceEquals(subset.BindingDiscounting.Single().Value,nominal))
            throw new Exception("Position subset preparation lost its exact carrying selection.");
        if(JsonSerializer.SerializeToElement(new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null))
            .TryGetProperty(nameof(ValuationSetup.BindingDiscounting),out _))
            throw new Exception("Legacy setup serialization gained provider internals.");
        Reject(()=>new ValuationPortfolio([cash],positions,setup with{BindingStates=setup.BindingStates.Remove(new(owners[0].Owner,owners[0].BindingId))}));
        Reject(()=>new ValuationPortfolio([cash],positions,setup with{BindingDiscounting=setup.BindingDiscounting.Add(new(Guid.NewGuid(),Guid.NewGuid()),nominal)}));
        Reject(()=>new ValuationPortfolio([cash],positions,setup with{BindingDiscounting=setup.BindingDiscounting.SetItem(new(owners[1].Owner,owners[1].BindingId),new Curve(source,f.R("other_currency")))}));
        Reject("payment_state",()=>RiskEngine.Prepare(model,source,[new ValuationPortfolio([cash],positions,setup with{
            BindingStates=setup.BindingStates.SetItem(new(owners[0].Owner,owners[0].BindingId),new OtherState(owners[0],states[0].Quantity))})]));
        Reject("risk_dependency",()=>RiskEngine.CalculateExact(RiskEngine.Prepare(model,source,[new ValuationPortfolio([cash],positions,setup with{
            BindingDiscounting=setup.BindingDiscounting.SetItem(new(owners[1].Owner,owners[1].BindingId),new Curve(source with{Id=Guid.NewGuid()},cash.Currency,false))})]),[],RiskEngine.ExactNumericalPolicy));
        Console.WriteLine("PASS per-binding cash carrying: shared payment definition, exact native owners, independent nominal/curve/fee policies and subset fences");
    }
    private static void Equal(ExactMoney actual,string expected)
    {if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected held cash {expected}, got {actual.ToExactString()}.");}
    private static void Reject(Action action)=>Reject("payment_discount_scope",action);
    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected held cash carrying refusal "+code);}
    private sealed record OtherState(AccountingPositionKey Position,Quantity Quantity):IBindingValuationState
    {public Guid TradeId=>Position.TradeId;public AccountingOwnerKey Owner=>Position.Owner;public Guid BindingId=>Position.BindingId;public Guid? BookId=>Position.BookId;}
    private sealed class Curve(MarketSnapshot source,ReferenceKey currency,bool checkInput=true):IProjectionCurrencyCurve
    {
        public ReferenceKey Currency=>currency;
        public MarketSnapshot SourceSnapshot=>source;
        public ProjectionCurrencyCurveKey CurveIdentity{get;}=new(new(source.Definitions,"synthetic_currency_curve"),Guid.NewGuid(),Guid.NewGuid(),1);
        public string TimingPolicy=>RateDiscountTiming.Version;
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)
        {
            if(checkInput&&!snapshot.HasSameInputs(source))throw new Exception("The held provider lost its exact original source.");
            return [..payments.Select(p=>new SidePaymentFactor(p,ExactDecimal.Parse("0.9"),source,DiscountFactorSource.Explicit))];
        }
    }
    private sealed class NativeFee(MarketSnapshot source,ReferenceKey currency,MarketObservation observation):ISideDiscounting
    {
        private readonly Guid mapping=Guid.NewGuid(),definition=Guid.NewGuid();
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> payments,MarketSnapshot snapshot)=>[..payments.Select(payment=>
        {
            if(!snapshot.HasSameInputs(source))throw new Exception("The fee currency-default provider lost its original source.");
            var day=DateOnly.FromDateTime(source.ValuationAt.UtcDateTime);var maturity=day.AddDays(365);
            var selected=new RateDiscountDefinition(mapping,new(source.Definitions,"synthetic_fee_rates"),currency,observation.Source,day,maturity,
                RateConventionSet.Parse("actual_365_fixed","simple","linear_zero","flat_zero","decimal_per_year"),RateCurveMath.Policy,[new(maturity,observation)]){
                Timing=new(source.ValuationAt,payment.PaymentAt,RateDiscountTiming.Version),CurveTerms=new(definition,1,[new(observation.Series,maturity)])};
            return new SidePaymentFactor(payment,ExactDecimal.Parse("0.8"),source,DiscountFactorSource.CurrencyDefault){RateDefinition=selected};
        })];
    }
}
