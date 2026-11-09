using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Curves;
using OpenEtrm.MarketData.Publications;

namespace OpenEtrm.Api;

internal sealed partial class ContractPriceTermsStore
{
    sealed record PublicationTarget(Guid PublicationId,int Version,string Index,DateOnly From,DateOnly To);
    sealed record ReleaseRow(Guid Id,Guid ObservationId,string SourceCode,DateTimeOffset PublishedAt,DateTimeOffset ReceivedAt,
        DateTimeOffset KnownAt,string VendorRevision,Guid? CorrectsId,ExactDecimal Value);
    sealed record OccurrenceRow(Guid Id,PricePublicationOccurrence Occurrence,ImmutableArray<ReleaseRow> Releases);
    sealed record PublicationRow(PublicationTarget Target,ImmutableArray<OccurrenceRow> Occurrences);

    internal async Task<Dictionary<CurveRequest,ContractPriceTermsBinding>> PublicationPrices(NpgsqlConnection connection,PreviewData data,
        IReadOnlyDictionary<CurveRequest,ContractPriceTermsBinding> bindings,DateTimeOffset marketCutoff,DateTimeOffset knownAt,CancellationToken ct)
    {
        var selected=bindings.Where(p=>p.Value.Terms.Settlement==ContractPriceTerms.PublishedRelease).ToArray();
        var result=bindings.ToDictionary(p=>p.Key,p=>p.Value);if(selected.Length==0)return result;
        PublicationTarget Target(ContractPriceTermsBinding binding)=>new(binding.Publication!.Key.Id,binding.Publication.Key.Version,
            binding.Fixing!.Index.Code,binding.Fixing.From,binding.Fixing.To);
        var targets=selected.Select(p=>Target(p.Value)).Distinct().ToArray();
        await using var command=ScheduleStore.Command(connection,"SELECT market_data.read_contract_publication_prices($1,$2,$3::jsonb,$4,$5)::text",
            database.Manifest.CatalogId,data.DefinitionRevision.RevisionId,JsonSerializer.Serialize(targets,PricePublicationStore.Json),marketCutoff,knownAt);
        var rows=JsonSerializer.Deserialize<ImmutableArray<PublicationRow>>((string)(await command.ExecuteScalarAsync(ct))!,PricePublicationStore.Json);
        if(rows.IsDefault||rows.Length!=targets.Length||!rows.Select(r=>r.Target).ToHashSet().SetEquals(targets))throw Error("Publication evidence did not retain the exact requested targets.");
        foreach(var pair in selected)
        {
            var binding=pair.Value;var row=rows.Single(r=>r.Target==Target(binding));
            if(row.Occurrences.Length>1)throw new MarketDataException("publication_occurrence_ambiguity","Multiple visible publication occurrences cover this exact index period.");
            if(row.Occurrences.IsEmpty)continue;
            var occurrence=row.Occurrences.Single();var fixing=binding.Fixing!;var publication=binding.Publication!;
            var quote=data.QuoteRows(fixing.Quote.Code).Single();
            var releases=occurrence.Releases.Select(r=>new PublicationPriceEvidence(new(r.Id,publication.Key,fixing.Index,
                new(data.ReferenceRevision,r.SourceCode),occurrence.Occurrence,PublicationState.Final,r.PublishedAt,r.ReceivedAt,r.KnownAt,
                r.VendorRevision,r.CorrectsId,new(r.Value,quote)),new(database.Manifest.CatalogId,r.ObservationId))).ToImmutableArray();
            result[pair.Key]=binding with{PublicationPrices=new(occurrence.Id,occurrence.Occurrence,knownAt,releases)};
        }
        return result;
    }
}
