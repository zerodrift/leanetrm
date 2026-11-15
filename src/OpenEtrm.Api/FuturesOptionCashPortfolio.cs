using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionCashValuationSelection([property:JsonRequired]Guid TradeId,[property:JsonRequired]Guid BindingId,
    [property:JsonRequired]CashFuturesOptionPolicy Model);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesOptionCashValuationPolicy([property:JsonRequired]string Implementation,
    [property:JsonRequired]ImmutableArray<FuturesOptionCashValuationSelection> Options)
{
    public const string Version="cash_futures_option_reference_value_v1";
    internal void Validate()
    {
        if(Implementation!=Version||Options.IsDefault||Options.Any(p=>p is null||p.TradeId==Guid.Empty||p.BindingId==Guid.Empty||p.Model is null)||
            Options.Select(p=>(p.TradeId,p.BindingId)).Distinct().Count()!=Options.Length)
            throw new MarketDataException("cash_option_policy","Select one explicit complete cash funding model for every exact held cash-underlying option binding.");
        foreach(var option in Options)CashFuturesOptionModel.RequirePolicy(option.Model);
    }
}

internal static class FuturesOptionCashPortfolio
{
    internal sealed record Prepared(ValuationPortfolio Portfolio,ImmutableArray<PortfolioInstantDiscountEvidence> Discounts);
    internal static async Task<FuturesMarginPolicy> ReadProfile(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,InputFrame frame,
        ScheduleContext context,FuturesOptionPosition position,CurveRequest reference,CancellationToken ct,JsonElement? retained=null)
    {
        var source=retained??await FuturesOptionProjectionProfileStore.Read(connection,database,frame,context,position,ct);
        var margin=await FuturesPhysicalDeliveryStore.ReadPolicy(connection,database,frame,source.GetProperty("policy"),source.GetProperty("occurrences"),ct);
        var profile=source.GetProperty("profile");
        var mark=new CurveRequest(new(data.DefinitionRevision,profile.Text("future_mark_curve")),new(data.DefinitionRevision,profile.Id("underlying_contract_id")));
        if(mark!=reference||margin.Contract!=reference.Contract||margin.SettlementCurve!=reference.Curve||margin.Delivery is not null||
            profile.Text("expiry_curve")!=reference.Curve.Code)
            throw Error("The selected cash option model requires one exact calibrated future, saved official expiry and cash-margin source.");
        return margin;
    }
    internal static async Task<Prepared> Read(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,InputFrame frame,
        MarketSnapshot snapshot,ScheduleContext context,ImmutableArray<FuturesOptionPosition> positions,FuturesOptionMarket calibrated,
        ValuationPortfolio portfolio,FuturesOptionCashValuationPolicy policy,CancellationToken ct)
    {
        policy.Validate();var bindings=portfolio.Setup.CashOptionValueBindings.ToBuilder();
        var curves=new Dictionary<ReferenceKey,LifecycleCurrencyDiscounting>();
        foreach(var position in LiveCash(positions,data,snapshot.ValuationAt))
        {
            var selected=policy.Options.SingleOrDefault(p=>p.TradeId==position.Trade&&p.BindingId==position.OptionBinding)??
                throw Error("Select a complete cash funding model for every exact live cash-underlying option binding.");
            if(!calibrated.Bindings.TryGetValue(position.OptionBinding,out var reference))
                throw Error("The selected cash option requires a calibrated explicit reference measure.");
            var margin=await ReadProfile(connection,database,data,frame,context,position,reference.Underlying,ct);
            var currency=reference.Instrument.Currency;
            if(!curves.TryGetValue(currency,out var curve))
            {
                var mapping=await SwapStore.CurrencyDefault(connection,database.Manifest.CatalogId,data,frame,currency,ct)??
                    throw new MarketDataException("discount_curve","Select the captured native currency mapping for saved margin-due funding.");
                curves.Add(currency,curve=new(mapping,snapshot,RateDiscountTiming.Version,ClockChange.FrozenInputs));
            }
            var history=await FuturesOptionKnownHistoryStore.Read(connection,database,data,frame,context,position,ct);
            bindings.Add(new(position.Trade,position.OptionBinding),new(selected.Model,
                FuturesOptionProjectionProfileStore.AutomaticRule(data,reference.Instrument),
                FuturesOptionProjectionProfileStore.Terminal(history,snapshot.ValuationAt),margin,curve,
                FuturesStore.Binding(data,reference.Underlying.Curve.Code,reference.Underlying.Contract.Id,position.OptionBinding,FuturesMarketBinding.Implementation)));
        }
        var updated=new ValuationPortfolio(portfolio.Instruments,portfolio.Positions,portfolio.Setup with{CashOptionValueBindings=bindings.ToImmutable()});
        var evidence=updated.Declare(snapshot).Where(c=>c.ModelVersion==CashFuturesOptionPolicy.Version).SelectMany(c=>c.DiscountInputs)
            .SelectMany(d=>d.Provider.Resolve([d.Payment],d.Source??snapshot)).Distinct().Select(d=>new PortfolioInstantDiscountEvidence(d.Payment.SideId,d)).ToImmutableArray();
        return new(updated,evidence);
    }
    internal static ImmutableArray<FuturesOptionPosition> LiveCash(ImmutableArray<FuturesOptionPosition> positions,PreviewData data,DateTimeOffset at)
    {
        if(positions.IsDefault||!TradingInstant.IsSupported(at)||positions.Any(p=>p is null||p.Definition!=data.DefinitionRevision.RevisionId||p.Reference!=data.ReferenceRevision))
            throw new MarketDataException("cash_option_source","Retain the exact cash-underlying option source and live clock.");
        return [..positions.Where(p=>p.OpenLots!=default).Where(p=>
        {
            var option=data.FuturesOptionSeries.SingleOrDefault(s=>s.Key==p.SeriesKey)??
                throw new MarketDataException("cash_option_source","Retain the exact option series.");
            var future=data.FuturesSeries.SingleOrDefault(s=>s.Key==option.UnderlyingSeries)??
                throw new MarketDataException("cash_option_source","Retain the option's exact declared future series.");
            return option.Terms.ExpiryAt>at&&future.SettlementMethod=="cash";
        })];
    }
    private static MarketDataException Error(string message)=>new("cash_option_source",message);
}
