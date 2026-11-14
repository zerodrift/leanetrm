using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesMarginValuationPolicy(string CashDiscounting,string TimingPolicy)
{
    public const string Convention="native_margin_remaining_value_v1",CurrencyDefault="captured_currency_default_v1";
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public FuturesEconomicValuationPolicy? Economic{get;init;}
    public static bool IsNative(string convention)=>convention is Convention or FuturesEconomicValuationPolicy.Version;
    public void Validate(string convention)
    {
        Validate();
        if(!IsNative(convention)||(convention==FuturesEconomicValuationPolicy.Version)!=(Economic is not null))
            throw new MarketDataException("portfolio_policy","Select one exact native futures convention and its complete versioned financial inputs.");
    }
    public void Validate()
    {
        if(CashDiscounting!=CurrencyDefault||TimingPolicy!=RateDiscountTiming.Version)
            throw new MarketDataException("portfolio_policy","Select the captured currency discount mapping and exact-instant timing for native futures cash.");
        Economic?.Validate();
    }
}
internal sealed record NativeFuturesPortfolio(ValuationPortfolio Portfolio,ImmutableDictionary<MeasureBinding,FuturesMarginRead> Accepted,
    ImmutableArray<PortfolioInstantDiscountEvidence> DiscountInputs)
{internal ImmutableArray<PortfolioInputMarket> Inputs{get;init;}=[];}

// Native accepted state and ordinary independent cash feed the existing portfolio
// evaluator. This adapter derives neither settlement nor replacement prices.
internal static partial class FuturesMarginPortfolio
{
    internal const string Measure="futures_remaining_value_v1";
    private sealed record ReadAdmission(Guid Catalog,Guid Context,DateTimeOffset At,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await FuturesMarginStore.RequireRead(connection,Catalog,Context,At,Until,ct);
        }
    }
    internal static async Task<NativeFuturesPortfolio> Read(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,InputFrame frame,
        MarketSnapshot snapshot,ImmutableArray<Trade> trades,FuturesMarginValuationPolicy policy,ScheduleContext selectedContext,CancellationToken ct)
    {
        policy.Validate();var catalog=database.Manifest.CatalogId;
        if(frame.ContextId is not {} context||snapshot.Knowledge?.ContextId!=context||snapshot.ValuationAt!=frame.At)
            throw new MarketDataException("futures_margin_source","Retain the exact certified native margin source and opening clock.");
        await using var capture=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_preview_authority($1)",catalog);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(connection,new ReadAdmission(catalog,context,frame.At,until));
        var accepted=ImmutableDictionary.CreateBuilder<MeasureBinding,FuturesMarginRead>();
        var states=ImmutableDictionary.CreateBuilder<MeasureBinding,IBindingValuationState>();
        var held=ImmutableArray.CreateBuilder<Trade>();var cashTrades=ImmutableArray.CreateBuilder<Trade>();
        foreach(var trade in trades)
        {
            var cash=ImmutableArray.CreateBuilder<TradeInstrument>();
            foreach(var binding in trade.Instruments)
            {
                var read=await FuturesMarginStore.Read(connection,catalog,data,frame,snapshot,trade,binding.Id,ct);
                if(read.PhysicalAdmission is {} physical)database.RegisterCommitAdmission(connection,physical);
                if(read.AuthorityUntil<until)until=read.AuthorityUntil;
                var key=new MeasureBinding(trade.Id,binding.Id);accepted.Add(key,read);states.Add(key,read.State);cash.AddRange(read.CashBindings);
            }
            held.Add(new(trade.Id,trade.BookId,trade.Instruments.AddRange(cash),trade.Booking));
            if(cash.Count>0)cashTrades.Add(new(trade.Id,trade.BookId,cash.ToImmutable(),trade.Booking));
        }
        database.RegisterCommitAdmission(connection,new ReadAdmission(catalog,context,frame.At,until));
        var cashStates=await CashPaymentStore.Read(connection,catalog,frame,snapshot,cashTrades.ToImmutable(),ct);
        foreach(var pair in cashStates)states.Add(pair.Key,pair.Value);
        var full=policy.Economic is null?null:await ReadFullInputs(connection,database,data,frame,snapshot,selectedContext,trades,accepted.ToImmutable(),policy,ct);
        var discounts=ImmutableDictionary.CreateBuilder<Guid,ISideDiscounting>();var evidence=ImmutableArray.CreateBuilder<PortfolioInstantDiscountEvidence>();
        if(full is not null)evidence.AddRange(full.Discounts);
        var pending=cashTrades.SelectMany(t=>t.Instruments.Select(b=>(Trade:t,Binding:b,Cash:(CashPaymentInstrument)b.Instrument)))
            .Where(h=>!h.Cash.Due(snapshot.ValuationAt)&&cashStates[new(h.Trade.Id,h.Binding.Id)].Outstanding(snapshot).Numerator!="0");
        var payments=pending.Select(h=>(Instrument:h.Cash.Id,Payment:h.Cash.Payment)).Concat(full?.Pending??[]).Distinct().ToArray();
        foreach(var currency in payments.GroupBy(h=>h.Payment.Currency))
        {
            var instruments=currency.Select(h=>h.Instrument).Distinct().ToArray();
            var mapping=await SwapStore.CurrencyDefault(connection,catalog,data,frame,currency.Key,ct)??
                throw new MarketDataException("discount_curve","Native futures cash requires the exact selected currency discount mapping.");
            var provider=new InstantCurrencyDiscounting([..currency.Select(i=>i.Payment).Distinct()],snapshot,mapping,policy.TimingPolicy);
            foreach(var instrument in instruments)discounts.Add(instrument,provider);
            evidence.AddRange(provider.Inputs.Select(p=>new PortfolioInstantDiscountEvidence(p.Key.SideId,p.Value)));
        }
        return new(Materialize(held.ToImmutable(),states.ToImmutable(),discounts.ToImmutable(),null,snapshot,full?.Bindings),accepted.ToImmutable(),evidence.ToImmutable()){Inputs=full?.Inputs??[]};
    }
    internal static ValuationPortfolio Materialize(ImmutableArray<Trade> trades,ImmutableDictionary<MeasureBinding,IBindingValuationState> states,
        ImmutableDictionary<Guid,ISideDiscounting> discounts,ISideDiscounting? sharedDiscount,MarketSnapshot snapshot,ImmutableDictionary<MeasureBinding,FuturesMarginValueBinding>? valueBindings=null)
    {
        if(snapshot.Knowledge is not {} knowledge||trades.IsDefaultOrEmpty||states is null)
            throw new MarketDataException("futures_margin_state","Supply complete actual futures state and its certified source context.");
        var bindings=trades.SelectMany(t=>t.Instruments).Where(b=>b.Instrument is FuturesContract).Select(b=>FuturesExecution.Require(b).MarketBinding)
            .GroupBy(b=>b.Instrument.Id).ToImmutableDictionary(g=>g.Key,g=>
            {
                var first=g.First();
                if(g.Any(b=>b!=first))throw new MarketDataException("valuation_binding","A unit future must retain one exact market binding.");
                return first;
            });
        var ledger=trades.SelectMany(t=>t.Instruments.Select(b=>
        {
            var key=new MeasureBinding(t.Id,b.Id);
            if(!states.TryGetValue(key,out var state)||b.Instrument is FuturesContract&&state is not FuturesMarginState||
                b.Instrument is CashPaymentInstrument&&state is not RecordedCashValuation||b.Instrument is not (FuturesContract or CashPaymentInstrument))
                throw new MarketDataException("futures_margin_state","Every native futures and cash occurrence requires its exact accepted state.");
            return KeyValuePair.Create(key,new BindingAccountingFacts(new(t.Id,b.Id,t.BookId),knowledge.ContextId,knowledge.KnownAt,
                state is RecordedCashValuation cash?cash.KnownFlows(snapshot):[]));
        })).ToImmutableDictionary();
        return ValuationPortfolio.FromTrades(trades,new(ValuationMeasure.PresentValue,bindings,sharedDiscount)
            {BindingStates=states,InstrumentDiscounting=discounts,AccountingFacts=ledger,FuturesValueBindings=valueBindings??ImmutableDictionary<MeasureBinding,FuturesMarginValueBinding>.Empty});
    }
}
