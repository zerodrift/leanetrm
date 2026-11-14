using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// Source-known payments are immutable actuals. Only the uncommitted remainder
// may discharge under the selected hypothetical paid-at-due policy.
public sealed class CashLifecycleProjection:ILifecycleProjection
{
    public string Implementation=>"native_cash_economic_projection_v1";
    public AccountingPositionKey Owner{get;}
    public ReferenceKey Currency=>instrument.Currency;
    public MarketSnapshot SourceSnapshot=>source;
    public ImmutableArray<CurveRequest> Dependencies=>[];
    public LifecycleProjectionState Opening{get;}
    public ImmutableArray<LifecycleProjectionEvent> Events{get;}
    private readonly CashPaymentInstrument instrument;
    private readonly RecordedCashValuation actual;
    private readonly ISideDiscounting? discounting;
    private readonly MarketSnapshot source;
    public CashLifecycleProjection(AccountingPositionKey owner,CashPaymentInstrument instrument,RecordedCashValuation actual,
        ISideDiscounting? discounting,MarketSnapshot source)
    {
        if(!owner.IsValid||actual.Position!=owner||actual.InstrumentId!=instrument.Id||actual.Quantity.Specification!=instrument.Currency||
            source.Knowledge is not {} knowledge||actual.SourceContextId!=knowledge.ContextId||actual.KnownThrough!=knowledge.KnownAt)
            throw new MarketDataException("projection_cash_state","Retain this exact native cash owner, due definition, quantity and certified source state.");
        Owner=owner;this.instrument=instrument;this.actual=actual;this.discounting=discounting;this.source=source;
        Opening=new([Portfolio(actual)],[]){KnownPayments=[..actual.KnownFlows(source).Where(p=>p.EffectiveAt>source.ValuationAt)]};
        var key=LifecycleProjectionEvent.ScopedId(owner,ContractIdentity.FromCanonical($"cash_due_projection_v1:{instrument.Id:D}"));
        Events=instrument.PaymentAt>source.ValuationAt?[new(key,instrument.PaymentAt,[],"cash_due")]:[];
    }
    public LifecycleProjectionState Apply(LifecycleProjectionEvent occurrence,LifecycleProjectionState state,LifecycleProjectionContext context)
    {
        if(occurrence.At!=instrument.PaymentAt||!Events.Contains(occurrence))
            throw new MarketDataException("projection_cash_state","Select this exact declared cash due event.");
        if(context.Policy.CashDisposition==LifecycleProjectionPolicy.Outstanding)return state;
        var futureActual=CashAllocationBalance.ReserveFuture(ExactMoney.FromMoney(new(actual.Quantity.Amount,Currency)),
            actual.KnownFlows(source).Where(p=>p.EffectiveAt>occurrence.At).Select(p=>(p.EffectiveAt,p.Amount)));
        var remaining=actual.UnitOutstanding(source,occurrence.At).Scale(actual.Quantity.Amount).Subtract(futureActual);
        if(remaining.Numerator=="0")return state;
        CashAllocationBalance.RequireDirection(ExactMoney.FromMoney(new(actual.Quantity.Amount,Currency)),remaining);
        var projected=new ProjectedCashDischargeValuation(actual,remaining,occurrence.At);
        return state with{Residuals=[Portfolio(projected)],Cash=state.Cash.Add(new(occurrence.Id,occurrence.At,occurrence.At,remaining))};
    }
    public ImmutableArray<LifecycleProjectionEvent> EventsFor(DateTimeOffset horizon)=>[..Events.Where(e=>e.At<=horizon)];
    private ValuationPortfolio Portfolio(ICashPaymentValuation state)
    {
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,discounting){
            BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(Owner.Owner,Owner.BindingId),state)};
        return new([instrument],[new(Owner.Owner,Owner.BindingId,Owner.BookId,instrument.Id,actual.Quantity,
            new UnitMoney(new Money(default,Currency),Currency),new(default,Currency))],setup);
    }
}

public sealed record ProjectedCashDischargeValuation(RecordedCashValuation Actual,ExactMoney Discharged,DateTimeOffset At):IProjectedCashPaymentValuation
{
    public Guid TradeId=>Actual.TradeId;
    public AccountingOwnerKey Owner=>Actual.Owner;
    public Guid BindingId=>Actual.BindingId;
    public Guid? BookId=>Actual.BookId;
    public Quantity Quantity=>Actual.Quantity;
    public Guid InstrumentId=>Actual.InstrumentId;
    public ExactMoney UnitOutstanding(MarketSnapshot source)=>UnitOutstanding(source,source.ValuationAt);
    public ExactMoney UnitOutstanding(MarketSnapshot source,DateTimeOffset calculationAt)
    {
        if(source.Knowledge is not {} knowledge||knowledge.ContextId!=Actual.SourceContextId||knowledge.KnownAt!=Actual.KnownThrough||
            !TradingInstant.IsSupported(calculationAt)||calculationAt<source.ValuationAt||!TradingInstant.IsSupported(At)||At<=source.ValuationAt||
            Discharged.Currency!=Quantity.Specification||Quantity.Amount==default)
            throw new MarketDataException("projection_cash_state","A path-local discharge requires its exact native opening state and future event.");
        var remaining=Actual.UnitOutstanding(source,calculationAt);
        if(calculationAt<At)return remaining;
        var sign=Quantity.Amount.CompareTo(default);var quantity=sign<0?-Quantity.Amount:Quantity.Amount;
        return remaining.Subtract(Discharged.DivideBy(quantity).Scale(ExactDecimal.Parse(sign<0?"-1":"1")));
    }
    public ImmutableArray<DateTimeOffset> FutureChanges(MarketSnapshot source)=>[..Actual.FutureChanges(source).Append(At).Distinct().Order()];
}
