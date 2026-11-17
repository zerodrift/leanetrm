using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

// Customer stock remains under its native ownership policy. This consumer owns
// only the conditional service fee and, after acceptance, its ordinary cash.
internal static class GasParkCustodyPortfolio
{
    internal static async Task<ImmutableArray<GasOwnedPortfolioInput>> PrepareAgreements(NpgsqlConnection connection,PreviewDatabase database,
        InputFrame frame,MarketSnapshot snapshot,ImmutableArray<JsonElement> agreements,ImmutableArray<(PreviewData Data,InputFrame Frame)> supplied,
        bool requirePinnedSources,bool draft,CancellationToken ct)
    {
        RequireSource(frame,snapshot);
        if(agreements.IsDefault||supplied.IsDefault||agreements.Select(a=>a.Id("tradeId")).Distinct().Count()!=agreements.Length)throw Invalid();
        var selected=new List<(JsonElement Agreement,GasParkCustodyState State,ImmutableArray<GasParkAndLoanComponent> Components)>();
        foreach(var agreement in agreements)
        {
            RequireAgreement(agreement,frame,snapshot,draft);
            var state=GasParkCustodyAdapter.Read(database.Manifest.CatalogId,agreement,draft);
            var components=GasParkCustodyAdapter.Components(state);
            if(!components.IsEmpty||state.Cash is not null)selected.Add((agreement,state,components));
        }
        var result=ImmutableArray.CreateBuilder<GasOwnedPortfolioInput>();
        foreach(var group in selected.GroupBy(e=>e.Agreement.GetProperty("terms").Id("definitionRevisionId")).OrderBy(g=>g.Key))
        {
            var sources=group.Select(e=>new CatalogueSource(group.Key,e.State.Terms.Currency.Revision.CatalogId,e.State.Terms.Currency.Revision.RevisionId)).Distinct().ToArray();
            if(sources.Length!=1)throw Invalid();
            var source=await MarketInputSelection.Resolve(database,connection,frame,sources[0],supplied,requirePinnedSources,ct);
            var market=source.Data.Snapshot(source.Frame).WithKnowledge(frame.ContextId!.Value,snapshot.Knowledge!.KnownAt);
            var trades=ImmutableArray.CreateBuilder<Trade>();var states=ImmutableDictionary.CreateBuilder<MeasureBinding,IBindingValuationState>();
            var evidence=ImmutableArray.CreateBuilder<PortfolioPositionEvidence>();var payments=ImmutableArray.CreateBuilder<SidePaymentKey>();
            foreach(var entry in group)
            {
                var agreement=entry.Agreement;var trade=agreement.Id("tradeId");var book=agreement.Id("bookId");var service=agreement.Id("instrumentId");
                var bindings=ImmutableArray.CreateBuilder<TradeInstrument>();
                void Cash(Guid binding,TimedCashPayment cash,Quantity quantity,RecordedCashValuation paid)
                {
                    bindings.Add(new(binding,cash,quantity));states.Add(new(trade,binding),paid);
                    if(!cash.Due(market.ValuationAt)&&paid.Outstanding(market).Numerator!="0")payments.Add(cash.Payment);
                    evidence.Add(new(trade,1,binding,service,cash.Id,book,GasParkCustodyTicket.Measure,group.Key,agreement.GetProperty("publicationOrder").GetInt32(),quantity)
                    {InstrumentType=GasParkCustodyTicket.Product,TradeReference=draft?"":TradingReference.Trade(agreement.GetProperty("tradeReference").GetInt32()),
                        InstrumentReference=draft?"":TradingReference.Instrument(agreement,"instrumentReference")});
                }
                foreach(var component in entry.Components)
                {
                    if(component.Instrument is not TimedCashPayment cash)throw Invalid();
                    var binding=GasParkAndLoanValuation.ComponentBinding(service,component.Role);
                    Cash(binding,cash,component.Quantity,new(new AccountingPositionKey(trade,binding,book),cash,component.Quantity,[],RecordedCashValuation.Policy,frame.ContextId.Value,market.Knowledge!.KnownAt));
                }
                if(entry.State.Cash is {} actual)Cash(actual.BindingId,actual.Instrument,actual.Quantity,actual.State);
                trades.Add(new(trade,book,bindings.ToImmutable()));
            }
            var providers=ImmutableDictionary.CreateBuilder<Guid,ISideDiscounting>();var discounts=ImmutableArray.CreateBuilder<PortfolioInstantDiscountEvidence>();
            foreach(var currency in payments.Distinct().GroupBy(p=>p.Currency))
            {
                var mapping=await SwapStore.CurrencyDefault(connection,database.Manifest.CatalogId,source.Data,source.Frame,currency.Key,ct)??
                    throw new MarketDataException("discount_curve","The independent custody fee requires its captured currency discount mapping.");
                var provider=new InstantCurrencyDiscounting(currency.ToImmutableArray(),market,mapping,RateDiscountTiming.Version);
                foreach(var id in currency.Select(p=>p.SideId).Distinct())providers.Add(id,provider);
                discounts.AddRange(provider.Inputs.Select(p=>new PortfolioInstantDiscountEvidence(p.Key.SideId,p.Value)));
            }
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
                {BindingStates=states.ToImmutable(),InstrumentDiscounting=providers.ToImmutable()};
            result.Add(new(source.Data,source.Frame,market,ValuationPortfolio.FromTrades(trades.ToImmutable(),setup),evidence.ToImmutable(),discounts.ToImmutable())
                {Measure=GasParkCustodyTicket.Measure});
        }
        return result.ToImmutable();
    }

    internal static void RequireSource(InputFrame frame,MarketSnapshot snapshot)
    {
        if(frame.ContextId is null||snapshot.Knowledge?.ContextId!=frame.ContextId||snapshot.ValuationAt!=frame.At)throw Invalid();
    }
    internal static void RequireAgreement(JsonElement agreement,InputFrame frame,MarketSnapshot snapshot,bool draft)
    {
        RequireSource(frame,snapshot);
        if(agreement.Id("contextId")!=frame.ContextId||agreement.Instant("asOf")!=frame.At||agreement.Instant("knownAt")!=snapshot.Knowledge!.KnownAt||
            DateWorkspace.Date(agreement.Text("effectiveOn"))!=frame.BusinessDate||agreement.Instant("effectiveAt")>frame.At||
            draft&&agreement.GetProperty("acceptance").ValueKind!=JsonValueKind.Null)throw Invalid();
    }
    static MarketDataException Invalid()=>new("gas_custody_source","Retain the complete exact custody source, cash memberships and explicitly selected market inputs.");
}
