using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Publications;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Curves;

// Authoring conventions materialize concrete pairs; a later roll never rebinds them.
public static class ContractPriceMappings
{
    public const string SameDeliveryPeriod="same_delivery_period_v1";
    public const string ExplicitContracts="explicit_contracts_v1";
    public static ImmutableArray<ConstituentMapping> Resolve(string rule,ImmutableArray<MarketContract> targets,
        ImmutableArray<MarketContract> sources,ImmutableArray<ConstituentMapping> explicitMappings)
    {
        if(targets.IsDefaultOrEmpty||targets.Length>120||sources.IsDefaultOrEmpty||sources.Length>1200||explicitMappings.IsDefault||
            targets.Any(c=>c is null)||sources.Any(c=>c is null)||targets.Select(c=>c.Key).Distinct().Count()!=targets.Length||sources.Select(c=>c.Key).Distinct().Count()!=sources.Length)
            throw Invalid();
        foreach(var c in targets.Concat(sources))if(c.Key.Id==Guid.Empty||c.Key.Revision.CatalogId==Guid.Empty||c.Key.Revision.RevisionId==Guid.Empty||
            c.DeliveryFrom>=c.DeliveryTo||c.Interval is not null)throw Invalid();
        ImmutableArray<ConstituentMapping> result;
        if(rule==SameDeliveryPeriod)
        {
            if(!explicitMappings.IsEmpty)throw Invalid();
            result=[..targets.Select(target=>
            {
                var matches=sources.Where(source=>source.DeliveryFrom==target.DeliveryFrom&&source.DeliveryTo==target.DeliveryTo).ToArray();
                if(matches.Length!=1)throw Invalid();return new ConstituentMapping(target.Key,matches[0].Key);
            })];
        }
        else if(rule==ExplicitContracts)
        {
            if(explicitMappings.Any(m=>m is null)||explicitMappings.Length!=targets.Length||explicitMappings.Select(m=>m.Target).Distinct().Count()!=targets.Length||
                !targets.Select(t=>t.Key).ToHashSet().SetEquals(explicitMappings.Select(m=>m.Target)))throw Invalid();
            result=[..targets.Select(t=>explicitMappings.Single(m=>m.Target==t.Key))];
        }
        else throw Invalid();
        foreach(var map in result)
        {
            var target=targets.Single(c=>c.Key==map.Target);var source=sources.SingleOrDefault(c=>c.Key==map.Source)??throw Invalid();
            if(target.Key.Revision!=source.Key.Revision)throw Invalid();
            // This is an explicit custom-recipe sampling map. Native curve
            // constituent mappings retain their separate equal-period rule.
        }
        return result;
    }
    public static ImmutableArray<RecipeContractWeight> RecipeWeights(ImmutableArray<ConstituentMapping> mappings,string sourcePort,ExactDecimal weight)
    {
        if(mappings.IsDefaultOrEmpty||mappings.Length>120||string.IsNullOrWhiteSpace(sourcePort)||
            !System.Text.RegularExpressions.Regex.IsMatch(sourcePort,@"\A[a-z][a-z0-9_]{0,31}\z")||mappings.Any(m=>m is null||m.Target.Id==Guid.Empty||m.Source.Id==Guid.Empty)||
            mappings.Select(m=>m.Target).Distinct().Count()!=mappings.Length)throw Invalid();
        return [..mappings.Select((m,i)=>new RecipeContractWeight(i+1,sourcePort,m.Source.Id,weight.ToString()))];
    }
    static MarketDataException Invalid()=>new("contract_price_mapping","Select an explicit convention, distinct dated contracts and complete unambiguous exact mapping coverage.");
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContractPriceTerms(string Settlement,PricePublicationKey? Publication,ReferenceKey? Index,
    string ObservationRule,string PendingPolicy,string? ProjectionPolicy)
{
    public const string ExchangeFinal="exchange_final_v1",PublishedIndex="published_index_v1",PublishedRelease="published_release_v1",Derived="derived_inputs_v1";
    [JsonIgnore] public bool UsesPublication=>Settlement is PublishedIndex or PublishedRelease;
    public const string SingleAssessment="single_assessment_v1",RequireFinal="require_final_v1";
    public void Validate()
    {
        if(Settlement is not(ExchangeFinal or PublishedIndex or PublishedRelease or Derived)||PendingPolicy!=RequireFinal||ObservationRule!=SingleAssessment||
            UsesPublication!=(Publication is not null&&Index is not null)||
            !UsesPublication&&(Publication is not null||Index is not null||ProjectionPolicy is not null)||
            UsesPublication&&ProjectionPolicy is not(null or PriceSettlementSelection.ForwardCurveNoBasis))throw Invalid();
        if(Publication is {} publication&&(publication.CatalogId==Guid.Empty||publication.Id==Guid.Empty||publication.Version<1))throw Invalid();
        if(Index is {} index)Money.RequireKey(index);
    }
    internal static MarketDataException Invalid()=>new("contract_price_terms","Choose an explicit supported settlement source, single-assessment observation rule and require-final policy; publication sources require exact publication/index references.");
}
public sealed record ContractPriceTermsOverride(ContractKey Contract,ContractPriceTerms Terms);
public sealed record ContractPriceTermsSet(ContractPriceTerms Default,ImmutableArray<ContractPriceTermsOverride> Replacements,
    string MappingRule,ImmutableArray<ConstituentMapping> Mappings)
{
    public ContractPriceTerms For(ContractKey contract)
    {
        if(Default is null||Replacements.IsDefault||Replacements.Any(r=>r is null||r.Terms is null)||Replacements.Select(r=>r.Contract).Distinct().Count()!=Replacements.Length)throw ContractPriceTerms.Invalid();
        if(MappingRule is not(ContractPriceMappings.SameDeliveryPeriod or ContractPriceMappings.ExplicitContracts)||Mappings.IsDefaultOrEmpty||
            Mappings.Any(m=>m is null||m.Target.Id==Guid.Empty||m.Source.Id==Guid.Empty||m.Target.Revision!=m.Source.Revision)||
            Mappings.Select(m=>m.Target).Distinct().Count()!=Mappings.Length||Replacements.Any(r=>!Mappings.Any(m=>m.Target==r.Contract)))throw ContractPriceTerms.Invalid();
        Default.Validate();foreach(var replacement in Replacements)replacement.Terms.Validate();
        return Replacements.SingleOrDefault(r=>r.Contract==contract)?.Terms??Default;
    }
    public ContractKey SourceFor(ContractKey target)=>Mappings.SingleOrDefault(m=>m.Target==target)?.Source??
        throw new MarketDataException("contract_price_mapping","The exact target contract has no authored source mapping.");
}
public sealed record PublicationPriceEvidence(PricePublicationRelease Release,ObservationKey Observation);
public sealed record PublicationPriceBinding(Guid OccurrenceId,PricePublicationOccurrence Occurrence,
    DateTimeOffset KnownAt,ImmutableArray<PublicationPriceEvidence> Releases);
public sealed record ContractPriceTermsBinding(ContractPriceTerms Terms,PricePublicationDefinition? Publication,ReferenceKey Commodity,IndexFixing? Fixing)
{ public PublicationPriceBinding? PublicationPrices { get; init; } }
