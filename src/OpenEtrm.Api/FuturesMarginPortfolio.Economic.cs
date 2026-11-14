using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

// A source needed by a selected residual branch before any inventory exists.
// This retains inputs; it creates no holding, cash or native lifecycle fact.
internal sealed record PortfolioInputMarket(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot,CustomCurves.PreparedMarket Market,
    ImmutableArray<CurveRequest> Dependencies);
internal sealed record FuturesFullValuationInputs(ImmutableDictionary<MeasureBinding,FuturesMarginValueBinding> Bindings,
    ImmutableArray<PortfolioInputMarket> Inputs,ImmutableArray<(Guid Instrument,SidePaymentKey Payment)> Pending,
    ImmutableArray<PortfolioInstantDiscountEvidence> Discounts);

internal static partial class FuturesMarginPortfolio
{
    private static async Task<FuturesFullValuationInputs> ReadFullInputs(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,InputFrame frame,
        MarketSnapshot snapshot,ScheduleContext context,ImmutableArray<Trade> trades,ImmutableDictionary<MeasureBinding,FuturesMarginRead> accepted,
        FuturesMarginValuationPolicy policy,CancellationToken ct)
    {
        var selected=policy.Economic??throw new MarketDataException("portfolio_policy","Select the complete full native futures model.");
        selected.Validate();var common=CustomCurves.CompleteContext(frame,snapshot,context);
        var profiles=new Dictionary<MeasureBinding,FuturesPhysicalDeliveryProfile>();
        var sources=new Dictionary<DefinitionRevision,(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot)>{[snapshot.Definitions]=(data,frame,snapshot)};
        foreach(var trade in trades)
        foreach(var holding in trade.Instruments)
        {
            var market=FuturesExecution.Require(holding).MarketBinding;if(market.SettlementMethod!="physical")continue;
            var key=new MeasureBinding(trade.Id,holding.Id);
            if(!selected.Physical.Any(p=>p.TradeId==trade.Id&&p.BindingId==holding.Id))
                throw new MarketDataException("portfolio_policy","Select the asset-mark interpretation for every exact held physical future.");
            var profile=await FuturesPhysicalDeliveryStore.ReadForPolicy(connection,database,frame,context,accepted[key].History.Terms.Policy,trade.BookId,
                trade.Booking?.LegalEntity.Key??throw new MarketDataException("trade_party","Retain the native held legal owner."),ct);
            profiles.Add(key,profile);var definition=profile.Terms.Mark.Curve.Revision;
            if(sources.ContainsKey(definition))continue;
            if(frame.SourceInputs?.GetValueOrDefault(definition.RevisionId) is not {} part)
                throw new MarketDataException("market_inputs_missing","The selected source matrix omits the physical delivery mark's exact native definition.");
            var resolved=await MarketInputSelection.Resolve(database,connection,frame,part.Source,[(data,frame)],false,ct);
            if(resolved.Data.DefinitionRevision!=definition)
                throw new MarketDataException("projection_delivery_source","The selected physical mark source has different exact native definition pins.");
            var inputs=MarketSnapshotBinding.Bind(resolved.Data.Snapshot(resolved.Frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
            sources.Add(definition,(resolved.Data,resolved.Frame,inputs));
        }
        var markets=new Dictionary<DefinitionRevision,PortfolioInputMarket>();
        foreach(var group in profiles.Values.GroupBy(p=>p.Terms.Mark.Curve.Revision))
        {
            var source=sources[group.Key];var dependencies=group.Select(p=>p.Terms.Mark).Distinct().ToImmutableArray();
            var market=await CustomCurves.PrepareMarket(connection,database,source.Data,source.Frame,source.Snapshot,context,[],dependencies,
                CustomCurves.MarketPreparation.ScenarioUniverse,ct,calculationContext:common);
            markets.Add(group.Key,new(source.Data,source.Frame,source.Snapshot,market,dependencies));
        }
        var curves=new Dictionary<(DefinitionRevision,ReferenceKey),LifecycleCurrencyDiscounting>();
        var bindings=ImmutableDictionary.CreateBuilder<MeasureBinding,FuturesMarginValueBinding>();
        var pending=ImmutableArray.CreateBuilder<(Guid,SidePaymentKey)>();var discounts=ImmutableArray.CreateBuilder<PortfolioInstantDiscountEvidence>();
        foreach(var trade in trades)
        foreach(var holding in trade.Instruments)
        {
            var market=FuturesExecution.Require(holding).MarketBinding;var key=new MeasureBinding(trade.Id,holding.Id);var actual=accepted[key];
            var completed=actual.History.Determinations.Select(d=>d.OccurrenceId).ToImmutableArray();
            var next=FuturesMarginSchedule.NextUnsettled(actual.History.Terms.Policy,actual.History.Terms.ExecutedAt,completed,snapshot.ValuationAt);
            if(next is not null&&next.DueAt>snapshot.ValuationAt)pending.Add((market.Instrument.Id,new(market.Instrument.Id,0,next.DueAt,market.Instrument.Currency)));
            var binding=new FuturesMarginValueBinding(actual.History.Terms,completed,selected.VariationMargin);
            if(profiles.TryGetValue(key,out var profile))
            {
                var input=markets[profile.Terms.Mark.Curve.Revision];LifecycleCurrencyDiscounting? carrying=null;
                if(profile.Terms.Carrying==GasHoldingCarrying.CurrencyCurve)
                {
                    var curveKey=(input.Snapshot.Definitions,profile.Terms.Currency);
                    if(!curves.TryGetValue(curveKey,out carrying))
                    {
                        var mapping=await SwapStore.CurrencyDefault(connection,database.Manifest.CatalogId,input.Data,input.Frame,profile.Terms.Currency,ct)??
                            throw new MarketDataException("discount_curve","The exact delivery-mark source requires its selected native currency carrying mapping.");
                        curves.Add(curveKey,carrying=new(mapping,input.Snapshot,policy.TimingPolicy,ClockChange.FrozenInputs));
                    }
                }
                var choice=selected.Physical.Single(p=>p.TradeId==trade.Id&&p.BindingId==holding.Id);
                binding=binding with{Physical=new(choice.Implementation,choice.MarkInterpretation,profile.Id,profile.Revision,
                    new(profile.Terms.Mark,input.Market.Valuation,input.Market.Bindings[profile.Terms.Mark]),carrying)};
                if(carrying is not null)
                {
                    var exchange=GasUnfixedPhysicalDeliveryInstrument.Create(profile.Terms,market.Market,PriceUnit.FromQuote(market.Quote));
                    discounts.AddRange(carrying.Resolve(exchange.RequiredDiscounts(snapshot.ValuationAt),input.Snapshot).Select(f=>new PortfolioInstantDiscountEvidence(f.Payment.SideId,f)));
                }
            }
            bindings.Add(key,binding);
        }
        return new(bindings.ToImmutable(),[..markets.Values.Where(m=>m.Snapshot.Definitions!=snapshot.Definitions).OrderBy(m=>m.Snapshot.Definitions.RevisionId)],
            pending.ToImmutable(),discounts.ToImmutable());
    }
}
