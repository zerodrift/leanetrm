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
public sealed record ReferenceCommitRequest(Guid OperationId,string Table,string Code,Guid SourceRevisionId,Guid BaseRevisionId,
    [property:JsonRequired]int ExpectedVersion,Dictionary<string,string?> Values);

// Only display metadata is editable. The economic reference rows supplied to
// pricers are never replaced by these rows or by their new revision keys.
public static class ReferenceEditors
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public static ReferenceRevision Source(PreviewManifest manifest,string table)
    {
        if(!PreviewDatabase.ReferenceTables.Contains(table))throw Error("reference_edit","Choose an available reference category.");
        return PreviewDatabase.ReferenceSource(manifest,table).Revision;
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
        // History: the item's latest applied display versions, which only those who may write or seal reference data read (D261).
        var history=display is not null&&code is not null&&(write||seal)
            ?await Execute(connection,"SELECT security.reference_display_history($1,$2,$3,$4)::text",[source.CatalogId,source.RevisionId,table,code],ct)
            :JsonSerializer.SerializeToElement(new{versions=Array.Empty<object>(),truncated=false},Json);
        JsonElement? item=null;
        if(code is not null&&fields.Length>0)
        {
            if(code.Length is <1 or >64)throw Error("reference_edit","Choose an available reference item.");
            var found=await PreviewDatabase.Rows(connection,$"SELECT row_to_json(t)::text FROM(SELECT * FROM ref_data.{table} WHERE catalog_id=$1 AND revision_id=$2 AND code=$3)t",[source.CatalogId,display?.DisplayRevision.RevisionId??source.RevisionId,code],ct);
            if(found.Length!=1)throw Error("reference_edit","Choose an available reference item.");
            item=found[0];
        }
        return new{table,sourceRevision=source,baseRevisionId=display?.DisplayRevision.RevisionId??source.RevisionId,
            displayVersion=display?.Version??0,fields,canEdit=display is not null&&write&&seal&&fields.Length>0,
            versions=history.GetProperty("versions"),historyTruncated=history.GetProperty("truncated").GetBoolean(),display,item,
            reason=display is null?"Historical references retain their original labels and are read-only.":fields.Length==0?"This category contains financial terms or provenance. Its current consumer is retained; qualified economic authoring is not available in this editor.":"Confirm the listed display changes to save and apply them to current curves in one step; this needs both Write and Seal reference data. Financial terms and trade references retain their original versions."};
    }
    // Save and apply share one transaction and operation id, so an exact retry
    // replays both first outcomes and any refusal leaves no unapplied draft.
    public static async Task<JsonElement> Commit(NpgsqlConnection connection,PreviewManifest manifest,ReferenceCommitRequest request,CancellationToken ct)
    {
        var source=Source(manifest,request.Table);
        if(request.OperationId==Guid.Empty||request.SourceRevisionId!=source.RevisionId||request.BaseRevisionId==Guid.Empty||request.ExpectedVersion<0||
            string.IsNullOrWhiteSpace(request.Code)||request.Code.Length>64||request.Values is null||request.Values.Count is <1 or >64||
            // Transport bound: one Unicode scalar may occupy two UTF-16 code
            // units. The canonical SQL field policy enforces exact scalar limits.
            request.Values.Any(p=>p.Key.Length>64||p.Value?.Length>600))
            throw Error("reference_edit","Choose a reference row, its supported display fields and its current display version. Identity and financial terms are read-only.");
        if(await Unchanged(connection,source,request,ct) is {} current)return current;
        await Execute(connection,"SELECT security.save_reference_display_edit($1,$2,$3,$4,$5,$6,$7::jsonb)::text",
            [source.CatalogId,source.RevisionId,request.BaseRevisionId,request.OperationId,request.Table,request.Code,JsonSerializer.Serialize(request.Values,Json)],ct);
        var applied=await Execute(connection,"SELECT security.apply_reference_display($1,$2,$3,$4,$5)::text",
            [source.CatalogId,source.RevisionId,request.OperationId,request.OperationId,request.ExpectedVersion],ct);
        return JsonSerializer.SerializeToElement(new{revisionId=request.OperationId,displayRevisionId=applied.GetProperty("displayRevisionId").GetGuid(),
            version=applied.GetProperty("version").GetInt32()},Json);
    }
    // A new request whose display fields already hold its values at the current display version records nothing and
    // returns that version (D238). A used id, a stale version or any other field goes on to save and apply, which decide it.
    static async Task<JsonElement?> Unchanged(NpgsqlConnection connection,ReferenceRevision source,ReferenceCommitRequest request,CancellationToken ct)
    {
        if(await Display(connection,source,ct) is not {} display||display.DisplayRevision.RevisionId!=request.BaseRevisionId||display.Version!=request.ExpectedVersion)return null;
        await using(var check=ScheduleStore.Command(connection,"SELECT security.reference_access($1,'WriteReferenceData') AND security.reference_access($1,'SealReferenceData'),"+
            "EXISTS(SELECT 1 FROM ref_data.display_edits WHERE catalog_id=$1 AND revision_id=$2),security.reference_display_editor_fields($3)::text",source.CatalogId,request.OperationId,request.Table))
        await using(var reader=await check.ExecuteReaderAsync(ct))
        {
            await reader.ReadAsync(ct);
            if(!reader.GetBoolean(0))throw new PreviewDeniedException();
            var fields=JsonSerializer.Deserialize<ImmutableArray<ReferenceEditField>>(reader.GetString(2),Json);
            if(reader.GetBoolean(1)||fields.IsDefaultOrEmpty||request.Values.Keys.Any(key=>!fields.Any(f=>f.Key==key)))return null;
        }
        // The table is a supported category and the code a bound value.
        var found=await PreviewDatabase.Rows(connection,$"SELECT to_jsonb(r)::text FROM ref_data.{request.Table} r WHERE catalog_id=$1 AND revision_id=$2 AND code=$3",
            [source.CatalogId,display.DisplayRevision.RevisionId,request.Code],ct);
        if(found.Length!=1||request.Values.Any(p=>!found[0].TryGetProperty(p.Key,out var held)||held.ValueKind switch
            {JsonValueKind.Null=>p.Value is not null,JsonValueKind.String=>held.GetString()!=p.Value,JsonValueKind.Number=>held.GetRawText()!=p.Value,_=>true}))return null;
        return JsonSerializer.SerializeToElement(new{revisionId=display.DisplayRevision.RevisionId,displayRevisionId=display.DisplayRevision.RevisionId,version=display.Version},Json);
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
