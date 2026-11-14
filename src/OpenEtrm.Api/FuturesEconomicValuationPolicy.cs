using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesPhysicalValuationSelection([property:JsonRequired]Guid TradeId,[property:JsonRequired]Guid BindingId,
    [property:JsonRequired]string Implementation,[property:JsonRequired]string MarkInterpretation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FuturesEconomicValuationPolicy([property:JsonRequired]string Implementation,[property:JsonRequired]string VariationMargin,
    [property:JsonRequired]ImmutableArray<FuturesPhysicalValuationSelection> Physical)
{
    public const string Version="native_futures_economic_remaining_value_v2";
    public void Validate()
    {
        if(Implementation!=Version||VariationMargin!=FuturesMarginValueBinding.FlatReference||Physical.IsDefault||
            Physical.Any(p=>p is null||p.TradeId==Guid.Empty||p.BindingId==Guid.Empty||p.Implementation!=FuturesPhysicalBasisValueBinding.Version||
                p.MarkInterpretation is not(FuturesPhysicalBasisValueBinding.SameDeliverable or FuturesPhysicalBasisValueBinding.ValuationProxy))||
            Physical.Select(p=>(p.TradeId,p.BindingId)).Distinct().Count()!=Physical.Length)
            throw new MarketDataException("portfolio_policy","Select the explicit pending-VM funding law and distinct exact physical holdings with their declared asset-mark interpretation.");
    }
}
