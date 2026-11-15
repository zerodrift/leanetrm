using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Source compatibility only: offering a strategy neither calibrates a price nor
// creates an exercise, linked future or margin determination.
internal static class EconomicCashOptionBindings
{
    internal const string ModelHelp="Reference stopping values the option. The selected event policy retains the right until a native event or expiry; holding may produce decision or time P&L.";
    internal static async Task<ImmutableArray<EconomicPhysicalBindingGroup>> Read(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,
        InputFrame frame,ScheduleContext context,ImmutableArray<Guid> books,ImmutableArray<Guid>? tradeIds,CancellationToken ct)
    {
        var exact=PinnedContext.Exact(context);await TradingContext.AdmitSource(database,connection,exact,frame.At,ct);
        var scope=await RiskTradeScope.Resolve(connection,database.Manifest.CatalogId,books,tradeIds,ct);
        var positions=await FuturesOptionPositions.Read(connection,database.Manifest,books,[],ct,trades:scope?.Ids);
        var groups=ImmutableArray.CreateBuilder<EconomicPhysicalBindingGroup>();
        var model=new CashFuturesOptionPolicy(CashFuturesOptionPolicy.Version,CashFuturesOptionPolicy.ReferenceStopping,
            CashFuturesOptionPolicy.ReferenceCalibration,CashFuturesOptionPolicy.IntrinsicStrategy,FuturesMarginValueBinding.FlatReference);
        foreach(var source in await FuturesOptionPortfolio.Inputs(connection,database,frame,positions,MarketInputSelection.WithRoot(data,frame,[]),false,ct))
        foreach(var position in FuturesOptionCashPortfolio.LiveCash(source.Positions,source.Data,source.Frame.At))
        {
            var label=TradingReference.Trade(position.Head)+" · "+position.SeriesKey.Code;
            void Unavailable(string reason)=>groups.Add(new(position.Trade,position.OptionBinding,label,[]){UnavailableReason=reason});
            var saved=await FuturesOptionProjectionProfileStore.ReadAvailable(connection,database,source.Frame,exact,position,ct);
            if(saved is null){Unavailable("Save an exact expiry source and linked cash-future margin profile in Lifecycle.");continue;}
            CurveRequest reference;
            try{reference=FuturesOptionPricing.Requests(source.Data.Model,source.Data.FuturesOptionSeries.Single(s=>s.Key==position.SeriesKey),FuturesOptionPricing.DailySettlement).Underlying;}
            catch(MarketDataException error)when(error.Code is "valuation_binding" or "option_observation_role" or "option_underlying_missing" or "option_settlement_missing")
            {Unavailable("The reference model requires one exact unambiguous native calibration source: "+error.Message);continue;}
            try{_=await FuturesOptionCashPortfolio.ReadProfile(connection,database,source.Data,source.Frame,exact,position,reference,ct,saved);}
            catch(MarketDataException error)when(error.Code=="cash_option_source")
            {Unavailable("The saved native sources do not support this bounded cash reference model: "+error.Message);continue;}
            groups.Add(new(position.Trade,position.OptionBinding,label,[new(CashFuturesOptionPolicy.Version,
                "Reference stopping · cash future · saved margin-due funding",JsonSerializer.SerializeToElement(
                    new FuturesOptionCashValuationSelection(position.Trade,position.OptionBinding,model),RiskJobs.Json))]));
        }
        return [..groups.OrderBy(g=>g.TradeId).ThenBy(g=>g.BindingId)];
    }
}
