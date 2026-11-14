using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record EconomicPhysicalBindingChoice(string Value,string Label,JsonElement Selection);
internal sealed record EconomicPhysicalBindingGroup(Guid TradeId,Guid BindingId,string Label,ImmutableArray<EconomicPhysicalBindingChoice> Choices)
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public string? UnavailableReason{get;init;}
}
internal sealed record EconomicPhysicalBindingGroups(ImmutableArray<EconomicPhysicalBindingGroup> Futures,
    ImmutableArray<EconomicPhysicalBindingGroup> Options);

// Compatibility metadata only. No option calibration, price evaluation or
// hypothetical financial state is constructed to offer a model selection.
internal static class EconomicPhysicalBindings
{
    internal static async Task<EconomicPhysicalBindingGroups> Read(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,
        InputFrame frame,ScheduleContext context,ImmutableArray<Guid> books,ImmutableArray<Guid>? tradeIds,CancellationToken ct)
    {
        var exact=PinnedContext.Exact(context);var catalog=database.Manifest.CatalogId;
        await TradingContext.AdmitSource(database,connection,exact,frame.At,ct);
        var selected=await RiskTradeScope.Resolve(connection,catalog,books,tradeIds,ct);
        var store=new FuturesStore(database);var directory=await store.Books(connection,ct);
        var holdings=await store.ReadTrades(connection,data,directory,books,[],ct,trades:selected?.Ids);
        var futures=ImmutableArray.CreateBuilder<EconomicPhysicalBindingGroup>();
        var options=ImmutableArray.CreateBuilder<EconomicPhysicalBindingGroup>();
        var supplied=MarketInputSelection.WithRoot(data,frame,[]);
        var sources=new Dictionary<DefinitionRevision,PreviewData>{[data.DefinitionRevision]=data};
        async Task<PreviewData?> Asset(FuturesPhysicalDeliveryProfile profile)
        {
            var definition=profile.Terms.Mark.Curve.Revision;
            if(sources.TryGetValue(definition,out var available))return available;
            if(frame.SourceInputs?.GetValueOrDefault(definition.RevisionId) is not {} part)return null;
            var captured=await MarketInputSelection.Resolve(database,connection,frame,part.Source,supplied,false,ct);
            if(captured.Data.DefinitionRevision!=definition)
                throw new MarketDataException("physical_mark_interpretation","The selected asset source has different exact native definition pins.");
            sources.Add(definition,captured.Data);return captured.Data;
        }
        EconomicPhysicalBindingGroup Group(Guid trade,Guid binding,string label,string reason)=>new(trade,binding,label,[]){UnavailableReason=reason};
        foreach(var source in (await store.Inputs(connection,frame,holdings,supplied,false,ct)).Values)
        {
            var snapshot=MarketSnapshotBinding.Bind(source.Data.Snapshot(source.Frame),exact.ContextId!.Value,SchedulePreview.ParseAt(exact.KnownAt));
            foreach(var trade in FuturesStore.InSource(holdings.Trades,source.Data.DefinitionRevision))
            foreach(var held in trade.Instruments)
            {
                var market=FuturesExecution.Require(held).MarketBinding;if(market.SettlementMethod!="physical")continue;
                var label=TradingReference.Trade(holdings.Rows.Single(r=>r.Id("trade_id")==trade.Id&&r.Id("binding_id")==held.Id))+" · "+market.Series.Key.Code;
                FuturesMarginRead actual;
                try{actual=await FuturesMarginStore.Read(connection,catalog,source.Data,source.Frame,snapshot,trade,held.Id,ct);}
                catch(MarketDataException error)when(error.Code=="futures_margin_unavailable")
                {futures.Add(Group(trade.Id,held.Id,label,"Save an exact native margin enrollment and delivery profile in Lifecycle."));continue;}
                database.RegisterCommitAdmission(connection,new FuturesMarginReadAdmission(catalog,exact.ContextId.Value,frame.At,actual.AuthorityUntil));
                if(actual.PhysicalAdmission is {} admission)database.RegisterCommitAdmission(connection,admission);
                if(!FuturesMarginPortfolio.RequiresPhysicalBasis(market,actual.State.Status))continue;
                var profile=await FuturesPhysicalDeliveryStore.ReadForPolicy(connection,database,source.Frame,exact,actual.History.Terms.Policy,
                    trade.BookId,trade.Booking!.LegalEntity.Key,ct);
                var asset=await Asset(profile);
                if(asset is null){futures.Add(Group(trade.Id,held.Id,label,"Select a source matrix containing the saved delivery mark definition."));continue;}
                futures.Add(new(trade.Id,held.Id,label,[..PhysicalMarkInterpretation.Permitted(market.Market,source.Data,profile,asset).Select(meaning=>
                    new EconomicPhysicalBindingChoice(meaning,Meaning(meaning),JsonSerializer.SerializeToElement(
                        new FuturesPhysicalValuationSelection(trade.Id,held.Id,FuturesPhysicalBasisValueBinding.Version,meaning),RiskJobs.Json)))]));
            }
        }
        var positions=await FuturesOptionPositions.Read(connection,database.Manifest,books,[],ct,trades:selected?.Ids);
        foreach(var source in await FuturesOptionPortfolio.Inputs(connection,database,frame,positions,supplied,false,ct))
        foreach(var position in FuturesOptionPhysicalPortfolio.LivePhysical(source.Positions,source.Data,source.Frame.At))
        {
            var label=TradingReference.Trade(position.Head)+" · "+position.SeriesKey.Code;
            var saved=await FuturesOptionProjectionProfileStore.ReadAvailable(connection,database,source.Frame,exact,position,ct);
            if(saved is null){options.Add(Group(position.Trade,position.OptionBinding,label,"Save an exact expiry and linked-future delivery profile in Lifecycle."));continue;}
            CurveRequest reference;
            try{reference=FuturesOptionPricing.Requests(source.Data.Model,source.Data.FuturesOptionSeries.Single(s=>s.Key==position.SeriesKey),FuturesOptionPricing.DailySettlement).Underlying;}
            catch(MarketDataException error)when(error.Code is "valuation_binding" or "option_observation_role" or "option_underlying_missing" or "option_settlement_missing")
            {options.Add(Group(position.Trade,position.OptionBinding,label,"The bounded reference model requires one exact unambiguous native calibration source: "+error.Message));continue;}
            var profile=await FuturesOptionPhysicalPortfolio.ReadProfile(connection,database,source.Data,source.Frame,exact,position,reference,ct,saved);
            var asset=await Asset(profile.Delivery);
            if(asset is null){options.Add(Group(position.Trade,position.OptionBinding,label,"Select a source matrix containing the saved option delivery mark definition."));continue;}
            var model=new PhysicalOptionPolicy(PhysicalOptionPolicy.Version,PhysicalOptionPolicy.FrozenBasis,PhysicalOptionPolicy.ReferenceStopping,
                PhysicalOptionPolicy.ReferenceCalibration,PhysicalOptionPolicy.IntrinsicStrategy);
            options.Add(new(position.Trade,position.OptionBinding,label,[..PhysicalMarkInterpretation.Permitted(reference,source.Data,profile.Delivery,asset).Select(meaning=>
                new EconomicPhysicalBindingChoice(meaning,"Frozen quoted basis · reference stopping · "+Meaning(meaning),JsonSerializer.SerializeToElement(
                    new FuturesOptionPhysicalValuationSelection(position.Trade,position.OptionBinding,model,meaning),RiskJobs.Json)))]));
        }
        return new([..futures.OrderBy(p=>p.TradeId).ThenBy(p=>p.BindingId)],[..options.OrderBy(p=>p.TradeId).ThenBy(p=>p.BindingId)]);
    }
    private static string Meaning(string meaning)=>meaning switch{
        FuturesPhysicalBasisValueBinding.SameDeliverable=>"Same contractual deliverable",
        FuturesPhysicalBasisValueBinding.ValuationProxy=>"Explicitly selected delivery-mark proxy",
        _=>throw new MarketDataException("physical_mark_interpretation","Select one qualified explicit native mark interpretation.")};
}
