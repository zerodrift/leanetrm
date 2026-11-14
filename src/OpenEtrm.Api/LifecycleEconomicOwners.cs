using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// Presentation for a source-known economic owner that has no opening position.
// These descriptors establish no title, quantity, value or native membership.
internal sealed record LifecycleEconomicOwnerDescription(AccountingPositionKey Owner,string Reference,string Instrument);

internal static class LifecycleEconomicOwners
{
    internal static ImmutableDictionary<AccountingPositionKey,LifecycleEconomicOwnerDescription> Describe(
        ImmutableArray<PortfolioPositionEvidence> opening,ImmutableArray<AccountingPositionKey> owners,
        ImmutableArray<LifecycleEconomicOwnerDescription> additional)
    {
        if(opening.IsDefault||owners.IsDefault||additional.IsDefault||owners.Any(o=>!o.IsValid)||owners.Distinct().Count()!=owners.Length||
            opening.Any(p=>p is null)||additional.Any(p=>p is null||!p.Owner.IsValid||string.IsNullOrWhiteSpace(p.Reference)||string.IsNullOrWhiteSpace(p.Instrument)))
            throw Error();
        var result=ImmutableDictionary.CreateBuilder<AccountingPositionKey,LifecycleEconomicOwnerDescription>();
        foreach(var position in opening)
        {
            var owner=new AccountingPositionKey(position.Owner,position.BindingId,position.BookId);
            if(!owner.IsValid||!result.TryAdd(owner,new(owner,position.TradeReference,position.InstrumentReference)))throw Error();
        }
        foreach(var description in additional)if(!result.TryAdd(description.Owner,description))throw Error();
        if(!result.Keys.ToHashSet().SetEquals(owners))throw Error();
        return result.ToImmutable();
    }
    internal static PreparedLifecyclePortfolio Attach(PreparedLifecyclePortfolio prepared,ImmutableArray<LifecycleEconomicOwnerDescription> additional)
    {
        var owners=prepared.Projections.SelectMany(p=>p.Components).SelectMany(c=>c.EconomicOwners).ToImmutableArray();
        _=Describe(prepared.HistoryPortfolio.Positions,owners,additional);
        if(additional.IsEmpty)return prepared;
        if(!prepared.AdditionalOwners.IsEmpty)throw Error();
        var ordered=additional.OrderBy(p=>p.Owner.Owner.Kind).ThenBy(p=>p.Owner.Owner.Id).ThenBy(p=>p.Owner.BindingId).ThenBy(p=>p.Owner.BookId).ToImmutableArray();
        return prepared with{AdditionalOwners=ordered,ProductEvidence=prepared.ProductEvidence.Add(JsonSerializer.SerializeToElement(new{
            implementation="native_additional_economic_owner_descriptions_v1",owners=ordered},RiskJobs.Json))};
    }
    private static MarketDataException Error()=>new("risk_economic_owners","Every economic owner requires one exact original or source-known future owner description in the selected book scope.");
}
