namespace OpenEtrm.MarketData.Trading;

public enum FuturesMarginStatus { Open=1, FinalMarginDetermined }
public sealed record FuturesSettlementSource(CurveKey Curve,SeriesKey ObservationSeries,ReferenceKey Source);

// Materialized native state: accepted knowledge/publication and certified reader
// coverage are separate from the source settlement's own knowledge coordinate.
public sealed record FuturesMarginState(Guid TradeId,Guid BindingId,Guid? BookId,Quantity Quantity,ReferenceKey Series,
    ExactUnitPrice SettledBasis,DateTimeOffset SettledAt,DateTimeOffset KnownAt,string Policy,FuturesMarginStatus Status,
    FuturesSettlementFact? LatestSettlement,Guid SourceContextId,DateTimeOffset KnownThrough,int StateRevision,int PublicationOrder):IBindingValuationState
{
    public const string Version="futures_variation_margin_v1";
    public AcceptedFuturesFinalDelivery? AcceptedDelivery{get;init;}
}
// Native operations resolve this evidence from the captured observation set;
// it is not a client command/request type.
public sealed record FuturesSettlementFact
{
    public ContractKey Contract {get;}
    public ReferenceKey Series {get;}
    public CurveKey SettlementCurve {get;}
    public MarketObservation NativeObservation {get;}
    public ObservationKey Observation=>NativeObservation.Key;
    public ObservationRole Role=>NativeObservation.Role;
    public ExactUnitPrice Price {get;}
    public DateTimeOffset EffectiveAt=>NativeObservation.ObservedAt;
    public DateTimeOffset KnownAt {get;}
    internal FuturesSettlementFact(FuturesMarketBinding binding,CurveKey settlementCurve,MarketObservation observation,DateTimeOffset knownAt)
    {
        Contract=binding.Contract.Key;Series=binding.Series.Key;SettlementCurve=settlementCurve;
        NativeObservation=observation;KnownAt=knownAt;
        Price=ExactUnitPrice.FromPrice(new(observation.Value,binding.Quote));
    }
}
public sealed record ProposedFuturesMarginBasis(ExactUnitPrice Price,DateTimeOffset At,FuturesMarginStatus Status);
// A proposal has no accepted-state knowledge time or publication identity.
public sealed record FuturesMarginProposal(ProposedFuturesMarginBasis Basis,FuturesSettlementFact Evidence,
    DateTimeOffset DueAt,ExactMoney ExactVariation,Money Variation,ExactMoney RoundingEffect);

public static class FuturesMargin
{
    public const string HeldBindingRounding="held_binding_difference_payment_total_v1";
    public static void Require(FuturesMarginState state,FuturesContract instrument)
    {
        if(state is null||state.Policy!=FuturesMarginState.Version||state.TradeId==Guid.Empty||state.BindingId==Guid.Empty||state.BookId is null||state.BookId==Guid.Empty||
            state.SourceContextId==Guid.Empty||state.StateRevision<1||state.PublicationOrder<1||
            state.Series!=instrument.SeriesKey||state.Quantity is null||state.Quantity.Specification!=instrument.UnitQuantity.Specification||
            state.Quantity.Amount==default||!state.Quantity.Amount.IsMultipleOf(instrument.LotSize.Amount)||
            state.SettledBasis is null||state.SettledBasis.Unit.Currency!=instrument.Currency||
            state.SettledBasis.Unit.Quantity!=instrument.UnitQuantity.Specification||state.SettledBasis.Unit.Multiplier!=instrument.Series.Multiplier||
            !TradingInstant.IsSupported(state.SettledAt)||!TradingInstant.IsSupported(state.KnownAt)||!TradingInstant.IsSupported(state.KnownThrough)||
            state.KnownAt>state.KnownThrough||!Enum.IsDefined(state.Status))
            throw Error("futures_margin_state","Supply exact held terms, accepted state and certified context coverage.");
        if(state.LatestSettlement is {} fact&&(fact.Series!=state.Series||fact.Price!=state.SettledBasis||fact.EffectiveAt!=state.SettledAt||
            fact.KnownAt>state.KnownAt||fact.Role!=(state.Status==FuturesMarginStatus.FinalMarginDetermined?ObservationRole.FinalContractSettlement:ObservationRole.DailySettlement)))
            throw Error("futures_margin_state","The settlement basis must retain its accepted source fact.");
        if(state.Status==FuturesMarginStatus.FinalMarginDetermined&&
            (state.SettledAt<instrument.Series.LastTradingAt||state.LatestSettlement is null))
            throw Error("futures_margin_state","Final margin requires the terminal milestone and accepted final-settlement evidence.");
        if(state.Status==FuturesMarginStatus.FinalMarginDetermined&&instrument.Series.SettlementMethod=="physical")
        {
            if(state.AcceptedDelivery is not {} delivery||delivery.PositionId==Guid.Empty||delivery.ProfileId==Guid.Empty||delivery.ProfileRevision<1||
                delivery.Plan is null||!TradingInstant.IsSupported(delivery.KnownAt)||delivery.KnownAt!=state.KnownAt||
                delivery.PublicationOrder!=state.PublicationOrder||delivery.Plan.Terms.From<state.SettledAt)
                throw Error("futures_margin_state","Accepted physical final margin requires its exact surviving native delivery obligation.");
            delivery.Plan.Require(instrument,state.Quantity,state.SettledBasis);
        }
        else if(state.AcceptedDelivery is not null)throw Error("futures_margin_state","A delivery obligation belongs only to accepted physical final margin.");
    }

    public static FuturesMarginProposal Determine(FuturesMarginState state,FuturesMarketBinding binding,FuturesSettlementFact fact,
        DateTimeOffset valuationAt,DateTimeOffset knownAt,DateTimeOffset dueAt,CashRoundingConvention rounding,string roundingScope,bool final)
    {
        Require(state,binding.Instrument);
        if(roundingScope!=HeldBindingRounding)
            throw Error("futures_margin_policy","Select the supported explicit held-binding rounding scope.");
        var sameInstantFinal=fact is not null&&final&&state.LatestSettlement?.Role==ObservationRole.DailySettlement&&state.SettledAt==fact.EffectiveAt;
        if(fact is null||fact.Contract!=binding.Contract.Key||fact.Series!=state.Series||fact.Observation.CatalogId!=binding.Contract.Key.Revision.CatalogId||fact.Observation.Id==Guid.Empty||
            fact.Price is null||fact.Price.Unit!=PriceUnit.FromQuote(binding.Quote)||!TradingInstant.IsSupported(fact.EffectiveAt)||
            !TradingInstant.IsSupported(fact.KnownAt)||!TradingInstant.IsSupported(valuationAt)||!TradingInstant.IsSupported(knownAt)||
            !TradingInstant.IsSupported(dueAt)||dueAt<fact.EffectiveAt||state.Status!=FuturesMarginStatus.Open||
            (state.SettledAt>=fact.EffectiveAt&&!sameInstantFinal)||state.KnownAt>knownAt||state.KnownThrough!=knownAt||fact.KnownAt>knownAt||fact.EffectiveAt>valuationAt||
            fact.Role!=(final?ObservationRole.FinalContractSettlement:ObservationRole.DailySettlement)||
            (final?fact.EffectiveAt<binding.Contract.Expiry:fact.EffectiveAt>binding.Contract.Expiry))
            throw Error("futures_settlement_evidence","Select the exact eligible official settlement, occurrence and resolved knowledge context.");
        UnitPrice price;
        try{price=fact.Price.ToUnitPriceExact();}
        catch(MarketDataException error){throw new MarketDataException("futures_settlement_evidence","An actual settlement requires a qualified working-decimal quote.",error);}
        if(price.Value.CompareTo(binding.Quote.Minimum)<0||price.Value.CompareTo(binding.Quote.Maximum)>0||!price.Value.IsMultipleOf(binding.Quote.ClearingIncrement))
            throw Error("futures_settlement_evidence","The official settlement must satisfy its exact quotation domain and clearing increment.");
        if(final&&binding.SettlementMethod!="cash")
            throw Error("futures_delivery_transition","Physical final settlement requires its qualified delivery adapter.");
        var(exact,cash,effect)=Difference(state.SettledBasis,fact.Price,state.Quantity,rounding,roundingScope);
        return new(new(fact.Price,fact.EffectiveAt,final?FuturesMarginStatus.FinalMarginDetermined:FuturesMarginStatus.Open),
            fact,dueAt,exact,cash,effect);
    }
    internal static (ExactMoney Exact,Money Rounded,ExactMoney Effect) Difference(ExactUnitPrice basis,ExactUnitPrice settlement,
        Quantity quantity,CashRoundingConvention rounding,string scope)
    {
        if(scope!=HeldBindingRounding)throw Error("futures_margin_policy","Select the explicit supported held-binding rounding scope.");
        var exact=settlement.Subtract(basis).ExactTimes(quantity);
        var rounded=exact.Round(rounding,CashRoundingStage.PaymentTotal);
        return(exact,rounded,ExactMoney.FromMoney(rounded).Subtract(exact));
    }
    static MarketDataException Error(string code,string message)=>new(code,message);
}
