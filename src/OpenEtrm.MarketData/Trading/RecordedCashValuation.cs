using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

// Paid cash is held-occurrence state, not a property of a reusable cash instrument or its due date.
// The complete native allocation history resolves by knowledge time before effective-time recognition.
public sealed class RecordedCashValuation:ICashPaymentValuation
{
    public Guid TradeId{get;}
    public AccountingOwnerKey Owner=>Position.Owner;
    public Guid BindingId{get;}
    public Guid InstrumentId{get;}
    public Guid? BookId=>Position.BookId;
    public Quantity Quantity{get;}
    public Guid SourceContextId{get;}
    public DateTimeOffset KnownThrough{get;}
    public AccountingPositionKey Position{get;}
    public ImmutableArray<AccountingFlow> Allocations{get;}
    public const string Policy=PaymentSideState.Policy;
    public RecordedCashValuation(Trade trade,Guid binding,ImmutableArray<AccountingFlow> allocations,string policy,Guid sourceContextId,DateTimeOffset knownThrough)
        :this(Resolve(trade,binding),allocations,policy,sourceContextId,knownThrough){}
    private RecordedCashValuation((AccountingPositionKey Position,Instrument Instrument,Quantity Quantity) holding,
        ImmutableArray<AccountingFlow> allocations,string policy,Guid sourceContextId,DateTimeOffset knownThrough)
        :this(holding.Position,holding.Instrument,holding.Quantity,allocations,policy,sourceContextId,knownThrough){}
    private static (AccountingPositionKey,Instrument,Quantity) Resolve(Trade trade,Guid binding)
    {
        var holding=trade?.Instruments.SingleOrDefault(p=>p.Id==binding);
        if(trade is null||holding is null)throw Error("Recorded cash requires its exact held trade binding.");
        return(new(trade.Id,binding,trade.BookId),holding.Instrument,holding.Quantity);
    }
    public RecordedCashValuation(AccountingPositionKey position,Instrument instrument,Quantity quantity,
        ImmutableArray<AccountingFlow> allocations,string policy,Guid sourceContextId,DateTimeOffset knownThrough)
    {
        if(sourceContextId==Guid.Empty||!TradingInstant.IsSupported(knownThrough)||policy!=Policy||!position.IsValid||instrument is null||
            instrument is not (CashPayment or TimedCashPayment)||quantity.Specification!=instrument.Currency||allocations.IsDefault)
            throw Error("Recorded cash requires the exact currency-held occurrence, complete allocation history and explicit paid-cash policy.");
        TradeId=position.TradeId;BindingId=position.BindingId;InstrumentId=instrument.Id;Quantity=quantity;Position=position;
        if(allocations.Any(a=>a is null||a.AllocationId==Guid.Empty||a.Revision<=0||a.Position!=Position||a.Kind!=AccountingFlowKind.EconomicCash||
                a.Amount is null||a.Amount.Currency!=instrument.Currency||!TradingInstant.IsSupported(a.KnownAt)||!TradingInstant.IsSupported(a.EffectiveAt))||
            allocations.Select(a=>(a.AllocationId,a.Revision)).Distinct().Count()!=allocations.Length)
            throw Error("Cash allocations require stable native identities, exact owner/currency, explicit times and distinct revisions.");
        foreach(var allocation in allocations.GroupBy(a=>a.AllocationId))
        {
            var revisions=allocation.OrderBy(a=>a.Revision).ToArray();
            if(revisions.Zip(revisions.Skip(1)).Any(pair=>pair.First.KnownAt>=pair.Second.KnownAt))
                throw Error("Cash allocation revisions require strictly increasing knowledge times.");
        }
        if(allocations.Any(a=>a.KnownAt>knownThrough))throw Error("An allocation cannot follow the source reader’s certified knowledge coverage.");
        SourceContextId=sourceContextId;KnownThrough=knownThrough;Allocations=allocations;
    }
    public ImmutableArray<AccountingFlow> KnownFlows(MarketSnapshot snapshot)
    {
        var cutoff=snapshot?.Knowledge?.KnownAt??throw Error("Cash state requires an exact resolved knowledge cutoff.");
        if(cutoff>KnownThrough)throw Error("Cash history cannot be reused beyond its explicit certified knowledge coverage.");
        return [..Allocations.Where(a=>a.KnownAt<=cutoff).GroupBy(a=>a.AllocationId)
            .Select(g=>g.MaxBy(a=>a.Revision)!).OrderBy(a=>a.AllocationId)];
    }
    public ExactMoney Paid(MarketSnapshot snapshot)=>Paid(snapshot,snapshot.ValuationAt);
    private ExactMoney Paid(MarketSnapshot snapshot,DateTimeOffset calculationAt)=>KnownFlows(snapshot).Where(a=>a.EffectiveAt<=calculationAt)
        .Select(a=>a.Amount).Aggregate(ExactMoney.Parse("0",Quantity.Specification),(a,b)=>a.Add(b));
    public ExactMoney Outstanding(MarketSnapshot snapshot)=>ExactMoney.FromMoney(new(Quantity.Amount,Quantity.Specification)).Subtract(Paid(snapshot));
    public ImmutableArray<DateTimeOffset> FutureChanges(MarketSnapshot source)=>[..KnownFlows(source).Where(f=>f.EffectiveAt>source.ValuationAt).Select(f=>f.EffectiveAt).Distinct().Order()];
    public ExactMoney UnitOutstanding(MarketSnapshot snapshot)=>UnitOutstanding(snapshot,snapshot.ValuationAt);
    public ExactMoney UnitOutstanding(MarketSnapshot snapshot,DateTimeOffset calculationAt)
    {
        var outstanding=ExactMoney.FromMoney(new(Quantity.Amount,Quantity.Specification)).Subtract(Paid(snapshot,calculationAt));var sign=Quantity.Amount.CompareTo(default);
        if(sign==0)
        {
            if(outstanding.Numerator!="0")throw Error("Nonzero recorded cash cannot be normalized by a zero holding quantity.");
            return outstanding;
        }
        var quantity=sign<0?-Quantity.Amount:Quantity.Amount;
        return outstanding.DivideBy(quantity).Scale(ExactDecimal.Parse(sign<0?"-1":"1"));
    }
    private static MarketDataException Error(string message)=>new("payment_state",message);
}
