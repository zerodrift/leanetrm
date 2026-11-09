using OpenEtrm.MarketData.Publications;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Curves;

public sealed record PublicationPriceStatus(string State,PublicationPriceEvidence? Final)
{
    public const string Projected="projected",Awaiting="awaiting_publication",Fixed="final";
}

// This opt-in contract uses issued and certified release facts. The existing
// published_index_v1 economic boundary and its dated fixing resolver are unchanged.
public static class PublicationPriceResolution
{
    public static PublicationPriceStatus Inspect(MarketContract contract,ContractPriceTermsBinding binding,
        DateTimeOffset valuationAt,DateTimeOffset marketCutoff,DateTimeOffset knownAt)
    {
        binding.Terms.Validate();
        var publication=binding.Publication;var fixing=binding.Fixing;var prices=binding.PublicationPrices;
        if(binding.Terms.Settlement!=ContractPriceTerms.PublishedRelease||publication is null||fixing is null||prices is null||
            publication.Key!=binding.Terms.Publication||fixing.Index!=binding.Terms.Index||
            fixing.From!=contract.DeliveryFrom||fixing.To!=contract.DeliveryTo||prices.OccurrenceId==Guid.Empty||
            prices.Releases.IsDefault||prices.Releases.Any(r=>r is null||r.Release is null)||
            prices.Releases.Select(r=>r.Release.Id).Distinct().Count()!=prices.Releases.Length||
            prices.Releases.Select(r=>r.Observation).Distinct().Count()!=prices.Releases.Length||
            prices.KnownAt!=knownAt||new[]{valuationAt,marketCutoff,knownAt}.Any(t=>!TradingInstant.IsSupported(t))||marketCutoff>valuationAt)
            throw new MarketDataException("publication_price_binding","Resolve an exact occurrence, fixing target, release evidence and knowledge context.");
        var behavior=PricePublicationBehavior.Create(publication);
        if(!behavior.CoveredPeriods(prices.Occurrence).Contains(new(fixing.From,fixing.To)))
            throw new MarketDataException("publication_coverage","The publication occurrence must cover this exact contract period.");
        var eligible=prices.Releases.Where(e=>e.Release.PublishedAt<=marketCutoff&&e.Release.KnownAt<=knownAt).ToArray();
        foreach(var evidence in eligible)
        {
            var release=evidence.Release;
            if(evidence.Observation.CatalogId!=contract.Key.Revision.CatalogId||evidence.Observation.Id==Guid.Empty||
                release.Index!=fixing.Index||release.Source!=fixing.Source||!SameOccurrence(release.Occurrence,prices.Occurrence))
                throw new MarketDataException("publication_price_binding","Release facts must retain the exact occurrence, index, source and observation identity.");
            var prior=release.CorrectsId is {} id?eligible.SingleOrDefault(e=>e.Release.Id==id)?.Release:null;
            behavior.ValidateRelease(new(publication.Key,fixing.Source),release,new(marketCutoff,knownAt),prior);
        }
        var final=eligible.Where(e=>e.Release.Stage==PublicationState.Final).ToArray();
        var corrected=final.Select(e=>e.Release.CorrectsId).OfType<Guid>().ToHashSet();
        final=final.Where(e=>!corrected.Contains(e.Release.Id)).ToArray();
        if(final.Length>1)throw new MarketDataException("publication_price_ambiguity","The exact index period has multiple final releases without one correction chain.");
        if(final.Length==1)return new(PublicationPriceStatus.Fixed,final[0]);
        return new(valuationAt<contract.Expiry?PublicationPriceStatus.Projected:PublicationPriceStatus.Awaiting,null);
    }
    static bool SameOccurrence(PricePublicationOccurrence a,PricePublicationOccurrence b)=>a.Publication==b.Publication&&
        a.AssessmentFrom==b.AssessmentFrom&&a.AssessmentTo==b.AssessmentTo&&a.AssessmentDate==b.AssessmentDate&&
        a.ExpectedReleaseAt==b.ExpectedReleaseAt&&a.Periods.SequenceEqual(b.Periods);
}
