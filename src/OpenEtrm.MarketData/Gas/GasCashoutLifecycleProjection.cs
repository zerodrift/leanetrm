using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasCashoutProjectionTerms(Guid ProfileId,int Revision,ProjectedFixingKey Fixing,DateTimeOffset CashDueAt,
    string PriceInterpretation,string CashCarrying,CashRoundingConvention Rounding)
{
    public const string Version="gas_quantity_and_cashout_lifecycle_projection_v2";
    public const string SelectedMarkExpectation="selected_valuation_mark_is_cashout_expectation_v1";
    public const string NominalCash="explicit_nominal_cashout_cash_v1",CurrencyCurveCash="captured_currency_curve_cashout_cash_v1";
}

public sealed class GasCashoutLifecycleProjection:ILifecycleProjection
{
    public string Implementation=>GasCashoutProjectionTerms.Version;
    public AccountingPositionKey Owner{get;}
    public ReferenceKey Currency{get;}
    public MarketSnapshot SourceSnapshot{get;}
    public ImmutableArray<CurveRequest> Dependencies{get;}
    public LifecycleProjectionState Opening{get;}
    public ImmutableArray<LifecycleProjectionEvent> Events{get;}
    private readonly GasCommodityHoldingInstrument claim;
    private readonly Quantity quantity;
    private readonly GasCashoutProjectionTerms terms;
    private readonly ISideDiscounting cashDiscounting;
    private readonly TimedCashPayment cash;
    private readonly Guid cashBinding;
    public GasCashoutLifecycleProjection(AccountingPositionKey owner,GasCommodityHoldingInstrument instrument,Quantity quantity,
        GasCashoutProjectionTerms terms,ISideDiscounting? discounting,MarketSnapshot source)
    {
        if(!owner.IsValid||instrument is null||quantity is null||quantity.Amount==default||quantity.Specification!=instrument.QuantitySpecification||
            source?.Knowledge is null||terms is null||terms.ProfileId==Guid.Empty||terms.Revision<1||terms.Fixing is null||
            terms.PriceInterpretation!=GasCashoutProjectionTerms.SelectedMarkExpectation||terms.Fixing.Market!=instrument.Mark||
            terms.Fixing.Role is not(ObservationRole.DailySettlement or ObservationRole.FinalContractSettlement)||
            !TradingInstant.IsSupported(terms.Fixing.At)||terms.Fixing.At<=source.ValuationAt||!TradingInstant.IsSupported(terms.CashDueAt)||
            terms.CashDueAt<terms.Fixing.At||instrument.ReturnAt!=terms.CashDueAt||terms.Rounding is null||
            terms.CashCarrying is not(GasCashoutProjectionTerms.NominalCash or GasCashoutProjectionTerms.CurrencyCurveCash))
            throw Error("Select exact native cashout terms, a future official fixing, the explicit valuation-mark expectation and cash carrying.");
        terms.Rounding.Require(instrument.Currency,CashRoundingStage.PaymentTotal);
        if(terms.CashCarrying==GasCashoutProjectionTerms.NominalCash)
        {
            if(instrument.Carrying!=GasHoldingCarrying.Nominal||discounting is not null)
                throw Error("Explicit nominal cashout cash must retain the claim's nominal carrying without an alternative curve.");
            cashDiscounting=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
        }
        else
        {
            if(instrument.Carrying!=GasHoldingCarrying.CurrencyCurve||discounting is not IProjectionCurrencyCurve curve||
                curve.Currency!=instrument.Currency||!curve.SourceSnapshot.HasSameInputs(source))
                throw Error("Currency-carried cashout requires the same captured dated curve for the claim and its resulting cash.");
            cashDiscounting=discounting;
        }
        Owner=owner;Currency=instrument.Currency;SourceSnapshot=source;claim=instrument;this.quantity=quantity;this.terms=terms;
        cash=new(Currency,terms.CashDueAt);
        var fixingId=LifecycleProjectionEvent.ScopedId(owner,ContractIdentity.FromCanonical($"gas_cashout_fixing_v1:{terms.ProfileId:D}:{terms.Revision}"));
        cashBinding=LifecycleProjectionEvent.ScopedId(owner,ContractIdentity.FromCanonical($"gas_cashout_cash_v1:{terms.ProfileId:D}:{terms.Revision}"));
        Events=[new(fixingId,terms.Fixing.At,[],"gas_cashout_fixing_v1"),new(cashBinding,terms.CashDueAt,[fixingId],"gas_cashout_cash_due_v1")];
        Dependencies=[instrument.Mark];Opening=new([Portfolio(claim,quantity,Owner.BindingId,discounting)],[]);
    }
    public ImmutableArray<LifecycleMarketUse> MarketUsesFor(DateTimeOffset horizon)=>[new(claim.Mark,horizon<terms.Fixing.At?horizon:terms.Fixing.At)];
    public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)
    {
        if(!TradingInstant.IsSupported(horizon)||horizon<=SourceSnapshot.ValuationAt)throw Error("Select a common cashout horizon after opening.");
        return [..Events.Where(e=>e.At<=horizon)];
    }
    public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
    {
        if(!Events.Contains(occurrence)||!SourceSnapshot.HasSameInputs(context.SourceSnapshot)||context.CalculationAt!=occurrence.At||!state.Members.IsEmpty)
            throw Error("Apply only this admitted cashout occurrence under its exact source and event clock.");
        if(occurrence==Events[0])
        {
            if(state.Residuals.Length!=1||state.Residuals[0].Positions is not [{} held]||held.Owner!=Owner.Owner||held.BindingId!=Owner.BindingId||
                held.ValuationId!=claim.Id||held.Quantity!=quantity)throw Error("Retain this exact outstanding native cashout claim before fixing.");
            var price=context.Fix(terms.Fixing).Price;
            if(price.Unit.Currency!=Currency||price.Unit.Quantity!=quantity.Specification)throw Error("The selected cashout expectation must keep exact contractual quotation units.");
            var amount=price.ExactTimes(quantity).Round(terms.Rounding,CashRoundingStage.PaymentTotal);
            return state with{Residuals=amount.Amount==default?[]:[Portfolio(cash,new(amount.Amount,Currency),cashBinding,cashDiscounting)]};
        }
        if(context.Policy.CashDisposition==LifecycleProjectionPolicy.Outstanding||state.Residuals.IsEmpty)return state;
        if(state.Residuals.Length!=1||state.Residuals[0].Positions is not [{} unpaid]||unpaid.Owner!=Owner.Owner||unpaid.BindingId!=cashBinding||
            unpaid.ValuationId!=cash.Id||unpaid.Quantity.Specification!=Currency)throw Error("Retain this cashout's exact unpaid cash before projected payment.");
        return state with{Residuals=[],Cash=state.Cash.Add(new(occurrence.Id,occurrence.At,terms.CashDueAt,ExactMoney.FromMoney(new(unpaid.Quantity.Amount,Currency))))};
    }
    private ValuationPortfolio Portfolio(Instrument instrument,Quantity amount,Guid binding,ISideDiscounting? discounting)
    {
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discounting);
        if(instrument is CashPaymentInstrument)
        {
            var state=new ProjectedCashValuation(new AccountingPositionKey(Owner.Owner,binding,Owner.BookId),amount,instrument.Id,
                ExactMoney.Parse("1",Currency),SourceSnapshot.Knowledge!.ContextId,SourceSnapshot.Knowledge.KnownAt);
            setup=setup with{BindingStates=setup.BindingStates.Add(new(Owner.Owner,binding),state)};
        }
        return new([instrument],[new(Owner.Owner,binding,Owner.BookId,instrument.Id,amount,
            new(new Money(default,Currency),amount.Specification),new(default,Currency))],setup);
    }
    private static MarketDataException Error(string message)=>new("gas_projection_cashout",message);
}
