using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IndexFixingImport(Guid DefinitionRevisionId,ReferenceKey Index,ReferenceKey Quote,ReferenceKey Source,
 string From,string To,string PublishedAt,string State,int Revision,Guid? CorrectsId,string Value,string SourceEventId,string SourceRevisionId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record IndexFixingRead(Guid ContextId,Guid DefinitionRevisionId,string ValuationAt);

public sealed class IndexFixingStore(PreviewManifest manifest)
{
 public async Task<object> Import(NpgsqlConnection connection,IndexFixingImport body,CancellationToken ct)
 {
  if(body.DefinitionRevisionId==Guid.Empty||string.IsNullOrWhiteSpace(body.Index.Code)||string.IsNullOrWhiteSpace(body.Quote.Code)||string.IsNullOrWhiteSpace(body.Source.Code)||
     string.IsNullOrWhiteSpace(body.Value)||string.IsNullOrWhiteSpace(body.SourceEventId)||body.SourceEventId.Length>200||string.IsNullOrWhiteSpace(body.SourceRevisionId)||body.SourceRevisionId.Length>200)
   throw new MarketDataException("index_fixing_input","Supply the exact fixing identities, decimal value and source event/revision identifiers.");
  if(body.Index.Revision!=body.Quote.Revision||body.Index.Revision!=body.Source.Revision||
     body.Index.Revision.CatalogId!=manifest.ReferenceCatalogId||body.Index.Revision.RevisionId!=manifest.ReferenceRevisionId||body.State is not("preliminary" or "final"))
   throw new MarketDataException("index_fixing_definition","Choose exact compatible index, quote and source references and an explicit publication state.");
  await using var command=ScheduleStore.Command(connection,"SELECT security.import_index_fixing($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14::numeric,$15,$16)",
   manifest.CatalogId,body.DefinitionRevisionId,body.Index.Revision.CatalogId,body.Index.Revision.RevisionId,body.Index.Code,body.Quote.Code,body.Source.Code,
   DateWorkspace.Date(body.From),DateWorkspace.Date(body.To),SchedulePreview.ParseAt(body.PublishedAt),body.State,body.Revision,body.CorrectsId is {} prior?prior:DBNull.Value,
   ExactDecimal.Parse(body.Value).ToString(),body.SourceEventId,body.SourceRevisionId);
  return new {id=(Guid)(await command.ExecuteScalarAsync(ct))!,body.Revision};
 }

 public async Task<MarketSnapshot> Bind(NpgsqlConnection connection,PreviewData data,InputFrame frame,CancellationToken ct)
 {
  if(frame.ContextId is not {} contextId)throw new MarketDataException("index_fixing_context","Supply the selected certified fixing context.");
  var selected=(await new KnowledgeStore(manifest.CatalogId).Read(connection,contextId,ct)).Context;
  var rows=await PreviewDatabase.Rows(connection,"""
   SELECT row_to_json(r)::text FROM(
    SELECT f.*,k.known_from FROM security.read_index_fixings_at($1,$5) f
    JOIN market_data.knowledge_events e ON(e.catalog_id,e.fixing_id)=(f.catalog_id,f.id)
    JOIN market_data.market_knowledge_receipts k ON(k.catalog_id,k.event_order,k.publication_xid)=(e.catalog_id,e.event_order,e.publication_xid)
    WHERE f.catalog_id=$1 AND f.reference_catalog_id=$2 AND f.reference_revision_id=$3 AND f.published_at<=$4 ORDER BY f.index_code,f.observation_from,f.revision
   )r
   """,[manifest.CatalogId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,frame.At,contextId],ct);
  var fixings=rows.Select(r=>new IndexFixingObservation(new(manifest.CatalogId,r.Id("id")),new(data.ReferenceRevision,r.Text("index_code")),
   new(data.ReferenceRevision,r.Text("source_code")),data.Quotes.Single(q=>q.Key.Code==r.Text("quote_code")),DateWorkspace.Date(r.Text("observation_from")),DateWorkspace.Date(r.Text("observation_to")),
   r.Instant("published_at"),r.Instant("known_from"),r.Text("publication_state")=="final"?PublicationState.Final:PublicationState.Preliminary,
   new UnitPrice(ExactDecimal.Parse(r.Text("value")),data.Quotes.Single(q=>q.Key.Code==r.Text("quote_code"))),int.Parse(r.Text("revision"),CultureInfo.InvariantCulture),
   r.OptionalId("corrects_id") is {} predecessor?new IndexFixingKey(manifest.CatalogId,predecessor):null)).ToImmutableArray();
  await using(var authority=ScheduleStore.Command(connection,"SELECT security.market_access($1,'ReadMarketData') AND security.reference_access($2,'ReadMarketData')",manifest.CatalogId,data.ReferenceRevision.CatalogId))
   if(!Equals(await authority.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
  return data.Snapshot(frame) with{FixingContextId=contextId,FixingKnowledgeCutoff=SchedulePreview.ParseAt(selected.KnownAt),IndexFixings=fixings};
 }
 public async Task<MarketSnapshot> WorkingSnapshot(NpgsqlConnection connection,PreviewData data,InputFrame frame,CancellationToken ct)
 {
  var selected=(await new KnowledgeStore(manifest.CatalogId).Capture(connection,new(frame.At.UtcDateTime.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),null,"current"),ct)).Context;
  return await Bind(connection,data,frame with{ContextId=selected.ContextId},ct);
 }

}
