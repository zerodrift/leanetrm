using System.Text.Json.Serialization;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.MarketData.Trading;

public sealed record FuturesPhysicalBasisValueBinding(string Implementation,string MarkInterpretation,Guid ProfileId,int ProfileRevision,
    CapturedPriceSource Asset,[property:JsonIgnore]ISideDiscounting? Carrying)
{
    public const string Version="vm_plus_declared_delivery_basis_v1";
    public const string SameDeliverable="same_contractual_deliverable_v1",ValuationProxy="explicit_valuation_proxy_v1";
}

public sealed partial record FuturesContract
{
    private GasUnfixedPhysicalDeliveryInstrument? PhysicalExchange(UnitValuationContext context,FuturesMarketBinding market,FuturesMarginValueBinding selected)
    {
        if(selected.Physical is not {} physical)return null;
        var configuration=selected.Terms.Policy.Delivery;
        if(Series.SettlementMethod!="physical"||physical.Implementation!=FuturesPhysicalBasisValueBinding.Version||
            physical.MarkInterpretation is not(FuturesPhysicalBasisValueBinding.SameDeliverable or FuturesPhysicalBasisValueBinding.ValuationProxy)||
            configuration is null||physical.ProfileId!=configuration.ProfileId||physical.ProfileRevision!=configuration.ProfileRevision||
            configuration.Terms is not GasPhysicalDeliveryTerms gas||physical.Asset is null||physical.Asset.Market!=gas.Mark||
            physical.Asset.Snapshot.Knowledge!=context.Snapshot.Knowledge||physical.Asset.Snapshot.ValuationAt!=context.Snapshot.ValuationAt||
            physical.Asset.Snapshot.MarketCutoff!=context.Snapshot.MarketCutoff||
            physical.Asset.Valuation.Scenario.Plan.GetPoint(physical.Asset.Point).Unit!=PriceUnit.FromQuote(market.Quote)||
            (gas.Carrying==GasHoldingCarrying.CurrencyCurve)!=(physical.Carrying is not null))
            throw new MarketDataException("futures_physical_value","Select the exact adopted delivery profile, explicit asset-mark meaning, captured asset source and carrying inputs.");
        return GasUnfixedPhysicalDeliveryInstrument.Create(gas,market.Market,PriceUnit.FromQuote(market.Quote));
    }
}
