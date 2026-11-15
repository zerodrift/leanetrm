using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

internal static partial class LifecyclePortfolioProjection
{
    internal static void RequireFullOptionValue(FuturesMarketBinding underlying,PortfolioValuationPolicy pricing,FuturesOptionPhysicalValueBinding? physical=null)
    {
        if(underlying.SettlementMethod=="cash"&&pricing.NativeFutures?.Economic is not null)
            throw Error("projection_cash_option_value","This full valuation profile requires a qualified saved-margin-due cash-option funding model, which is unavailable.");
        if(underlying.SettlementMethod=="physical"&&pricing.NativeFutures?.Economic is not null&&physical is null)
            throw Error("projection_physical_option_value","The full valuation profile requires this option's exact selected physical model, native profile and captured asset source.");
    }
    private static FuturesDeliveryProjectionMarket? OptionDeliveryMarket(PortfolioValuation.Prepared opening,PortfolioProductSource source,
        FuturesOptionPhysicalValueBinding? physical,FuturesMarginPolicy margin)
    {
        if(physical is null)return null;
        if((physical.Margin with{Occurrences=margin.Occurrences})!=margin||!physical.Margin.Occurrences.SequenceEqual(margin.Occurrences)||
            margin.Delivery?.Terms is not GasPhysicalDeliveryTerms gas)
            throw Error("projection_option_state","Retain the option's exact selected native expiry and physical delivery profile.");
        var asset=physical.Asset.Snapshot;var data=asset.HasSameInputs(source.Snapshot)?source.Data:OriginalMarketSource(opening,asset).Data;
        return new(gas,data.Model,asset,physical.Carrying);
    }
    private sealed record PreparedOptionChildren(ImmutableDictionary<Guid,FuturesLifecycleProjection> Children,
        ImmutableArray<PortfolioInputMarket> Inputs,ImmutableArray<(MarketSnapshot Source,ISideDiscounting Provider)> Carrying);
    private static async Task<PreparedOptionChildren> OptionChildren(NpgsqlConnection connection,PreviewDatabase database,
        PortfolioValuation.Prepared opening,PortfolioProductSource source,FuturesOptionPhysicalValueBinding selected,
        FuturesOptionKnownHistory parent,ImmutableDictionary<Guid,FuturesKnownHistory> histories,
        ImmutableDictionary<ReferenceKey,ISideDiscounting> curves,GasPhysicalKnownRead? physical,
        ImmutableDictionary<AccountingPositionKey,KnownFuturesPhysicalFinal>.Builder finals,
        ImmutableArray<LifecycleEconomicOwnerDescription>.Builder descriptions,string reference,CancellationToken ct)
    {
        var children=ImmutableDictionary.CreateBuilder<Guid,FuturesLifecycleProjection>();
        var inputs=opening.InputMarkets;var carrying=ImmutableArray.CreateBuilder<(MarketSnapshot,ISideDiscounting)>();
        foreach(var accepted in parent.Events.Where(e=>e.EffectiveAt>source.Snapshot.ValuationAt&&e.Future is not null).OrderBy(e=>e.PublicationOrder))
        {
            var native=accepted.Future!;var holding=native.Trade.Instruments.Single(h=>h.Id==native.BindingId);
            var market=FuturesExecution.Require(holding).MarketBinding;var owner=new AccountingPositionKey(native.Trade.Id,native.BindingId,native.Trade.BookId);
            var history=histories[accepted.EventId];
            if(market.SettlementMethod!="physical"||history.Terms.Policy.Delivery is null)
                throw Error("projection_option_state","The selected physical option must activate its exact adopted physical future policy.");
            if(history.Determinations.Any(d=>d.Occurrence.Phase==FuturesMarginPhase.Final)&&physical?.Attachments.Any(a=>a.FutureOwner==owner)!=true)
                throw Error("projection_gas_scope","Accepted native physical final requires the complete independently owned delivery, title and principal source in the selected book scope.");
            var profile=await FuturesPhysicalDeliveryStore.ReadForPolicy(connection,database,source.Frame,opening.Context,history.Terms.Policy,
                owner.BookId,native.Trade.Booking?.LegalEntity.Key??throw Error("trade_party","Retain the accepted linked future's native legal owner."),ct);
            var asset=await OptionChildMarket(connection,database,opening with{InputMarkets=inputs},source,profile.Terms.Mark,ct);
            inputs=inputs.Where(i=>i.Snapshot.Definitions!=asset.Snapshot.Definitions).ToImmutableArray().Add(asset);
            PhysicalMarkInterpretation.Require(selected.MarkInterpretation,market.Market,source.Data,profile,asset.Data);
            ISideDiscounting? provider=null;
            if(profile.Terms.Carrying==GasHoldingCarrying.CurrencyCurve)
            {
                var existing=curves.GetValueOrDefault(profile.Terms.Currency);
                if(asset.Snapshot.HasSameInputs(source.Snapshot))provider=existing;
                else
                {
                    var mapping=await SwapStore.CurrencyDefault(connection,database.Manifest.CatalogId,asset.Data,asset.Frame,profile.Terms.Currency,ct)??
                        throw Error("discount_curve","The native child requires its exact selected delivery-source currency carrying curve.");
                    provider=new LifecycleCurrencyDiscounting(mapping,asset.Snapshot,selected.MarginDiscounting is IProjectionCurrencyCurve marginCurve?
                        marginCurve.TimingPolicy:throw Error("projection_option_state","Retain the option's complete captured margin curve."),ClockChange.FrozenInputs);
                }
                if(provider is null)throw Error("discount_curve","Retain the native child's complete exact-source delivery carrying curve.");
                carrying.Add((asset.Snapshot,provider));
            }
            var funded=new FuturesMarginValueBinding(history.Terms,[],FuturesMarginValueBinding.FlatReference){Physical=new(
                FuturesPhysicalBasisValueBinding.Version,selected.MarkInterpretation,profile.Id,profile.Revision,
                new(profile.Terms.Mark,asset.Market.Valuation,asset.Market.Bindings[profile.Terms.Mark]),provider)};
            var childSetup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty.Add(market.Instrument.Id,market),null){
                FuturesValueBindings=ImmutableDictionary<MeasureBinding,FuturesMarginValueBinding>.Empty.Add(new(owner.Owner,owner.BindingId),funded),
                InstrumentDiscounting=ImmutableDictionary<Guid,ISideDiscounting>.Empty.Add(market.Instrument.Id,curves[market.Instrument.Currency])};
            KnownFuturesPhysicalFinal? final=null;
            if(physical?.Attachments.SingleOrDefault(a=>a.FutureOwner==owner) is {} attachment)
            {
                final=PhysicalFinal(attachment,history,source.Snapshot,market.Instrument);finals.Add(owner,final);
            }
            children.Add(accepted.EventId,new(accepted,history,childSetup,market.Market,source.Data.Model,source.Snapshot,
                new(profile.Terms,asset.Data.Model,asset.Snapshot,provider),final,parent));
            descriptions.Add(new(owner,reference+" · linked future","Accepted native option continuation"));
        }
        return new(children.ToImmutable(),inputs,carrying.ToImmutable());
    }
    private static async Task<PortfolioInputMarket> OptionChildMarket(NpgsqlConnection connection,PreviewDatabase database,
        PortfolioValuation.Prepared opening,PortfolioProductSource source,CurveRequest mark,CancellationToken ct)
    {
        if(source.Frame.SourceInputs?.GetValueOrDefault(mark.Curve.Revision.RevisionId) is not {} part)
            throw Error("market_inputs_missing","The selected source matrix omits the accepted child's exact delivery market.");
        var candidates=opening.InputMarkets.Where(i=>i.Snapshot.Definitions==mark.Curve.Revision).ToArray();
        var existing=candidates.FirstOrDefault();
        if(candidates.Any(i=>!i.Snapshot.HasSameInputs(existing!.Snapshot)))
            throw Error("projection_delivery_source","One native definition cannot have conflicting child source selections.");
        var selected=existing is null?await MarketInputSelection.Resolve(database,connection,source.Frame,part.Source,
            [(source.Data,source.Frame)],false,ct):(existing.Data,existing.Frame);
        MarketInputSelection.Require(selected,source.Frame,part.Source);
        if(!part.Matches(selected.Item2)||selected.Item1.DefinitionRevision!=mark.Curve.Revision)
            throw Error("projection_delivery_source","Retain the native child's exact complete source inputs and definition pins.");
        var snapshot=MarketSnapshotBinding.Bind(selected.Item1.Snapshot(part.Project(source.Frame)),opening.Context.ContextId!.Value,SchedulePreview.ParseAt(opening.Context.KnownAt));
        if(candidates.Any(i=>!i.Snapshot.HasSameInputs(snapshot)||!i.Market.Valuation.Snapshot.HasSameInputs(snapshot)||
            !i.Market.Authority.Snapshot.HasSameInputs(snapshot)||i.Dependencies.Any(d=>!i.Market.Bindings.ContainsKey(d))))
            throw Error("projection_delivery_source","Retain each native child's original admitted graph and complete declared bindings under its exact source snapshot.");
        var dependencies=candidates.SelectMany(i=>i.Dependencies).Concat(opening.Markets.Where(m=>m.Snapshot.HasSameInputs(snapshot))
            .SelectMany(m=>m.InputMarket?.Bindings.Keys??[])).Append(mark).Distinct().ToImmutableArray();
        if(existing is not null&&dependencies.All(d=>existing.Market.Bindings.ContainsKey(d)))return existing;
        var market=await CustomCurves.PrepareMarket(connection,database,selected.Item1,selected.Item2,snapshot,opening.Context,[],dependencies,
            CustomCurves.MarketPreparation.ScenarioUniverse,ct,calculationContext:CustomCurves.CompleteContext(source.Frame,source.Snapshot,opening.Context));
        return new(selected.Item1,selected.Item2,snapshot,market,dependencies);
    }
}
