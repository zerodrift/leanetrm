using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Api.Presentation;

namespace OpenEtrm.Api;

public sealed record ReferenceDisplayData(ReferenceRevision SourceRevision,ReferenceRevision DisplayRevision,int Version,
    ImmutableDictionary<string,ImmutableArray<JsonElement>> References);
public sealed record ReferenceEditField(string Key,string Label,string Kind,[property:JsonRequired]int MaxLength,bool Nullable=false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReferenceSaveRequest(Guid OperationId,string Table,string Code,Guid SourceRevisionId,Guid BaseRevisionId,
    Dictionary<string,string?> Values);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReferenceApplyRequest(Guid OperationId,string Table,Guid SourceRevisionId,Guid RevisionId,int ExpectedVersion);

// Only display metadata is editable. The economic reference rows supplied to
// pricers are never replaced by these rows or by their new revision keys.
public static class ReferenceEditors
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static ReferenceRevision Source(PreviewManifest manifest,string table)
    {
        if(!PreviewDatabase.ReferenceTables.Contains(table))throw Error("reference_edit","Choose an available reference category.");
        return new(manifest.ReferenceCatalogId,PreviewDatabase.UnitTables.Contains(table)&&manifest.UnitCatalog is {} units?units.ReferenceRevisionId:manifest.ReferenceRevisionId);
    }
    public static async Task<ReferenceDisplayData?> Display(NpgsqlConnection connection,ReferenceRevision source,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.reference_display_state($1,$2)::text",source.CatalogId,source.RevisionId);
        var display=JsonSerializer.Deserialize<ReferenceDisplayData>((string)(await command.ExecuteScalarAsync(ct))!,Json);
        return display is null?null:display with{References=display.References.ToImmutableDictionary(p=>p.Key,
            p=>p.Value.Select(row=>ReferenceJson.PreserveNumericLexemes(row.GetRawText())).ToImmutableArray())};
    }
    public static async Task<object> Read(NpgsqlConnection connection,PreviewManifest manifest,string table,CancellationToken ct,string? code=null)
    {
        var source=Source(manifest,table);var display=await Display(connection,source,ct);
        await using var access=ScheduleStore.Command(connection,"SELECT security.reference_access($1,'WriteReferenceData'),security.reference_access($1,'SealReferenceData'),security.reference_display_editor_fields($2)::text",source.CatalogId,table);
        bool write,seal;ImmutableArray<ReferenceEditField> fields;
        await using(var reader=await access.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);write=reader.GetBoolean(0);seal=reader.GetBoolean(1);
            try{fields=JsonSerializer.Deserialize<ImmutableArray<ReferenceEditField>>(reader.GetString(2),Json);}
            catch(JsonException){throw Error("reference_edit","The reference editor field contract is incomplete. Reload after its metadata is qualified.");}
            if(fields.IsDefault||fields.Any(f=>f is null||f.MaxLength is <1 or >300))throw Error("reference_edit","The reference editor field bounds are invalid.");
        }
        var versions=display is not null&&(write||seal)?await PreviewDatabase.Rows(connection,"""
            SELECT row_to_json(t)::text FROM(
             SELECT e.revision_id,e.base_revision_id,e.table_name,e.item_code,e.request->'values' AS changes,r.state,e.recorded_at
             FROM ref_data.display_edits e JOIN ref_data.revisions r ON(r.catalog_id,r.id)=(e.catalog_id,e.revision_id)
             WHERE e.catalog_id=$1 AND e.source_revision_id=$2 AND e.table_name=$3 ORDER BY e.recorded_at DESC,e.revision_id LIMIT 20)t
            """,[source.CatalogId,source.RevisionId,table],ct):ImmutableArray<JsonElement>.Empty;
        JsonElement? item=null;
        if(code is not null&&fields.Length>0)
        {
            if(code.Length is <1 or >64)throw Error("reference_edit","Choose an available reference item.");
            var found=await PreviewDatabase.Rows(connection,$"SELECT row_to_json(t)::text FROM(SELECT * FROM ref_data.{table} WHERE catalog_id=$1 AND revision_id=$2 AND code=$3)t",[source.CatalogId,display?.DisplayRevision.RevisionId??source.RevisionId,code],ct);
            if(found.Length!=1)throw Error("reference_edit","Choose an available reference item.");
            item=found[0];
        }
        return new{table,sourceRevision=source,baseRevisionId=display?.DisplayRevision.RevisionId??source.RevisionId,
            displayVersion=display?.Version??0,fields,canEdit=display is not null&&write&&fields.Length>0,
            canApply=display is not null&&seal,versions,display,item,
            reason=display is null?"Historical references retain their original labels and are read-only.":fields.Length==0?"This category contains financial terms or provenance. Its current consumer is retained; qualified economic authoring is not available in this editor.":"Save an immutable metadata version, review it, then explicitly apply its display changes to current curves. Financial terms and trade references retain their original versions."};
    }
    public static async Task<JsonElement> Save(NpgsqlConnection connection,PreviewManifest manifest,ReferenceSaveRequest request,CancellationToken ct)
    {
        var source=Source(manifest,request.Table);
        if(request.OperationId==Guid.Empty||request.SourceRevisionId!=source.RevisionId||request.BaseRevisionId==Guid.Empty||
            string.IsNullOrWhiteSpace(request.Code)||request.Code.Length>64||request.Values is null||request.Values.Count is <1 or >64||
            // Transport bound: one Unicode scalar may occupy two UTF-16 code
            // units. The canonical SQL field policy enforces exact scalar limits.
            request.Values.Any(p=>p.Key.Length>64||p.Value?.Length>600))
            throw Error("reference_edit","Choose a reference row and its supported display fields. Identity and financial terms are read-only.");
        return await Execute(connection,"SELECT security.save_reference_display_edit($1,$2,$3,$4,$5,$6,$7::jsonb)::text",
            [source.CatalogId,source.RevisionId,request.BaseRevisionId,request.OperationId,request.Table,request.Code,JsonSerializer.Serialize(request.Values,Json)],ct);
    }
    public static async Task<JsonElement> Apply(NpgsqlConnection connection,PreviewManifest manifest,ReferenceApplyRequest request,CancellationToken ct)
    {
        var source=Source(manifest,request.Table);
        if(request.OperationId==Guid.Empty||request.SourceRevisionId!=source.RevisionId||request.RevisionId==Guid.Empty||request.ExpectedVersion<0)
            throw Error("reference_edit","Review a saved metadata version and its current display version before applying it.");
        return await Execute(connection,"SELECT security.apply_reference_display($1,$2,$3,$4,$5)::text",
            [source.CatalogId,source.RevisionId,request.RevisionId,request.OperationId,request.ExpectedVersion],ct);
    }
    static async Task<JsonElement> Execute(NpgsqlConnection connection,string sql,object[] values,CancellationToken ct)
    {
        try{
            await using var command=ScheduleStore.Command(connection,sql,values);
            return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
        }
        catch(PostgresException e)when(e.SqlState is "23514" or "23503" or "22P02" or "22003")
        {throw Error("reference_edit","The metadata is invalid. Supply nonempty text and an optional complete location point within the supported bounds.");}
        catch(PostgresException e)when(e.SqlState=="23505"){throw Error("reference_conflict","This request or version already exists. Reload before editing.");}
    }
    static MarketDataException Error(string code,string message)=>new(code,message);
}
