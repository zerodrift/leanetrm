using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionPhysicalValuationSelection(
    [property:JsonRequired]Guid TradeId,[property:JsonRequired]Guid BindingId,
    [property:JsonRequired]PhysicalOptionPolicy Model,[property:JsonRequired]string MarkInterpretation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionPhysicalValuationPolicy(
    [property:JsonRequired]string Implementation,[property:JsonRequired]ImmutableArray<FuturesOptionPhysicalValuationSelection> Options)
{
    public const string Version="physical_futures_option_reference_value_v1";
    internal void Validate()
    {
        if(Implementation!=Version||Options.IsDefault||Options.Any(p=>p is null||p.TradeId==Guid.Empty||p.BindingId==Guid.Empty||p.Model is null||
            p.MarkInterpretation is not(FuturesPhysicalBasisValueBinding.SameDeliverable or FuturesPhysicalBasisValueBinding.ValuationProxy))||
            Options.Select(p=>(p.TradeId,p.BindingId)).Distinct().Count()!=Options.Length)
            throw new MarketDataException("physical_option_policy","Select an explicit complete physical option model for every exact held option binding.");
        foreach(var option in Options)PhysicalFuturesOptionModel.RequirePolicy(option.Model);
    }
}

internal static class FuturesOptionPhysicalPortfolio
{
    internal sealed record Profile(FuturesMarginPolicy Margin,FuturesPhysicalDeliveryProfile Delivery);
    internal sealed record Prepared(ValuationPortfolio Portfolio,ImmutableArray<PortfolioInputMarket> Inputs,
        ImmutableArray<PortfolioInstantDiscountEvidence> Discounts);
    internal static async Task<Profile> ReadProfile(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,
        InputFrame frame,ScheduleContext context,FuturesOptionPosition position,CurveRequest reference,CancellationToken ct,JsonElement? retained=null)
    {
        var source=retained??await FuturesOptionProjectionProfileStore.Read(connection,database,frame,context,position,ct);
        var margin=await FuturesPhysicalDeliveryStore.ReadPolicy(connection,database,frame,source.GetProperty("policy"),source.GetProperty("occurrences"),ct);
        var selected=source.GetProperty("profile");
        var mark=new CurveRequest(new(data.DefinitionRevision,selected.Text("future_mark_curve")),
            new(data.DefinitionRevision,selected.Id("underlying_contract_id")));
        if(mark!=reference||margin.Delivery is null||margin.Contract!=reference.Contract||
            margin.SettlementCurve!=new CurveKey(data.DefinitionRevision,selected.Text("expiry_curve")))
            throw Error("The bounded physical option model requires its calibrated native future mark and the saved official expiry/margin source.");
        var owner=new PartyKey(database.Manifest.CatalogId,position.Head.Id("legal_entity_id"),Integer(position.Head,"legal_entity_revision"));
        var delivery=await FuturesPhysicalDeliveryStore.ReadForPolicy(connection,database,frame,context,margin,position.Book.Id("id"),owner,ct);
        return new(margin,delivery);
    }
    internal static async Task<Prepared> Read(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,InputFrame frame,
        MarketSnapshot snapshot,ScheduleContext context,ImmutableArray<FuturesOptionPosition> positions,FuturesOptionMarket calibrated,
        ValuationPortfolio portfolio,FuturesOptionPhysicalValuationPolicy policy,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> supplied,bool requirePinned,CancellationToken ct)
    {
        policy.Validate();var live=LivePhysical(positions,data,snapshot.ValuationAt);
        var bindings=portfolio.Setup.PhysicalOptionValueBindings.ToBuilder();
        var inputs=ImmutableArray.CreateBuilder<PortfolioInputMarket>();
        var sources=new Dictionary<DefinitionRevision,(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot)>{[data.DefinitionRevision]=(data,frame,snapshot)};
        var markets=new Dictionary<CurveRequest,PortfolioInputMarket>();
        var curves=new Dictionary<(DefinitionRevision,ReferenceKey),LifecycleCurrencyDiscounting>();
        foreach(var position in live)
        {
            var selected=policy.Options.SingleOrDefault(p=>p.TradeId==position.Trade&&p.BindingId==position.OptionBinding)??
                throw Error("Select a complete physical option model for every exact live held physical-underlying binding.");
            if(!calibrated.Bindings.TryGetValue(position.OptionBinding,out var reference))
                throw Error("The selected physical option requires a successfully calibrated explicit reference measure.");
            var profile=await ReadProfile(connection,database,data,frame,context,position,reference.Underlying,ct);
            var gas=profile.Delivery.Terms;var definition=gas.Mark.Curve.Revision;
            if(!sources.TryGetValue(definition,out var source))
            {
                if(frame.SourceInputs?.GetValueOrDefault(definition.RevisionId) is not {} part)
                    throw new MarketDataException("market_inputs_missing","The selected source matrix omits the physical option's exact delivery mark source.");
                var resolved=await MarketInputSelection.Resolve(database,connection,frame,part.Source,supplied,requirePinned,ct);
                var captured=MarketSnapshotBinding.Bind(resolved.Data.Snapshot(resolved.Frame),context.ContextId!.Value,SchedulePreview.ParseAt(context.KnownAt));
                if(captured.Definitions!=definition)throw Error("The physical asset requires its original exact native source definition.");
                sources.Add(definition,source=(resolved.Data,resolved.Frame,captured));
            }
            PhysicalMarkInterpretation.Require(selected.MarkInterpretation,reference.Underlying,data,profile.Delivery,source.Data);
            if(!markets.TryGetValue(gas.Mark,out var market))
            {
                var prepared=await CustomCurves.PrepareMarket(connection,database,source.Data,source.Frame,source.Snapshot,context,[],[gas.Mark],
                    CustomCurves.MarketPreparation.ScenarioUniverse,ct,calculationContext:CustomCurves.CompleteContext(frame,snapshot,context));
                market=new(source.Data,source.Frame,source.Snapshot,prepared,[gas.Mark]);markets.Add(gas.Mark,market);inputs.Add(market);
            }
            async Task<LifecycleCurrencyDiscounting> CurrencyCurve(PreviewData selectedData,InputFrame selectedFrame,MarketSnapshot selectedSource)
            {
                var key=(selectedSource.Definitions,gas.Currency);
                if(curves.TryGetValue(key,out var found))return found;
                var mapping=await SwapStore.CurrencyDefault(connection,database.Manifest.CatalogId,selectedData,selectedFrame,gas.Currency,ct)??
                    throw new MarketDataException("discount_curve","Select the exact captured native currency mapping for physical option carrying.");
                var curve=new LifecycleCurrencyDiscounting(mapping,selectedSource,RateDiscountTiming.Version,ClockChange.FrozenInputs);curves.Add(key,curve);return curve;
            }
            var carrying=gas.Carrying==GasHoldingCarrying.Nominal?null:await CurrencyCurve(source.Data,source.Frame,source.Snapshot);
            var marginDiscounting=await CurrencyCurve(data,frame,snapshot);
            var history=await FuturesOptionKnownHistoryStore.Read(connection,database,data,frame,context,position,ct);
            var instruction=history.Events.Where(e=>e.EffectiveAt<=snapshot.ValuationAt&&(e.Kind is "exercise_instruction" or "abandon_instruction"))
                .OrderBy(e=>e.PublicationOrder).LastOrDefault();
            var terminal=instruction?.Kind switch{"exercise_instruction"=>PhysicalOptionTerminal.Exercise,
                "abandon_instruction"=>PhysicalOptionTerminal.Abandon,_=>PhysicalOptionTerminal.Automatic};
            var option=reference.Instrument;
            var rule=data.References["futures_option_rules"].Single(r=>r.Text("code")==option.Series.AutomaticExerciseRule);
            var automatic=new FuturesOptionAutomaticRule(new(data.ReferenceRevision,rule.Text("code")),Integer(rule,"version"),rule.Text("evaluation"),
                rule.Number("in_the_money_threshold"),rule.Text("at_the_money_call"),rule.Text("at_the_money_put"));
            bindings.Add(new(position.Trade,position.OptionBinding),new(selected.Model,automatic,terminal,profile.Margin,
                new(gas.Mark,market.Market.Valuation,market.Market.Bindings[gas.Mark]),carrying,marginDiscounting,selected.MarkInterpretation,
                FuturesStore.Binding(data,reference.Underlying.Curve.Code,reference.Underlying.Contract.Id,position.OptionBinding,FuturesMarketBinding.Implementation)));
        }
        var updated=new ValuationPortfolio(portfolio.Instruments,portfolio.Positions,portfolio.Setup with{PhysicalOptionValueBindings=bindings.ToImmutable()});
        var evidence=updated.Declare(snapshot).Where(c=>c.ModelVersion==PhysicalOptionPolicy.Version).SelectMany(c=>c.DiscountInputs)
            .SelectMany(d=>d.Provider.Resolve([d.Payment],d.Source??snapshot)).Distinct().Select(d=>new PortfolioInstantDiscountEvidence(d.Payment.SideId,d)).ToImmutableArray();
        return new(updated,inputs.ToImmutable(),evidence);
    }
    internal static ImmutableArray<FuturesOptionPosition> LivePhysical(ImmutableArray<FuturesOptionPosition> positions,PreviewData data,DateTimeOffset at)
    {
        if(positions.IsDefault||!TradingInstant.IsSupported(at)||positions.Any(p=>p is null||p.Definition!=data.DefinitionRevision.RevisionId||p.Reference!=data.ReferenceRevision))
            throw new MarketDataException("physical_option_source","Retain the exact held option definition, reference version and source clock.");
        return [..positions.Where(p=>p.OpenLots!=default).Where(p=>
        {
            var option=data.FuturesOptionSeries.SingleOrDefault(s=>s.Key==p.SeriesKey)??
                throw new MarketDataException("physical_option_source","The held option requires its exact retained series.");
            var future=data.FuturesSeries.SingleOrDefault(s=>s.Key==option.UnderlyingSeries)??
                throw new MarketDataException("physical_option_source","The option requires its exact declared underlying future series.");
            return option.Terms.ExpiryAt>at&&future.SettlementMethod=="physical";
        })];
    }
    private static int Integer(JsonElement row,string field)=>int.Parse(row.Text(field),CultureInfo.InvariantCulture);
    private static MarketDataException Error(string message)=>new("physical_option_source",message);
}
