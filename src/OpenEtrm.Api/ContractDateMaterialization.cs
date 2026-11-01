using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContractMaterializationRow(Guid ContractId,int Ordinal,string Label,string Kind,string? FuturesSeriesCode);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContractMaterializationRequest(Guid RequestId,Guid RevisionId,Guid MappingId,int MappingVersion,
    ScheduleContext Context,ContractMaterializationRow[] Contracts);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContractAdmissionRead(Guid RevisionId,Guid ContractId,ScheduleContext Context);
public sealed record MaterializedContract(Guid ContractId,Guid AdmissionId,bool Created);
public sealed record ContractMaterializationReceipt(Guid RequestId,ImmutableArray<MaterializedContract> Contracts);
public sealed record ContractDateAdmission(Guid Id,Guid MappingId,int MappingVersion,int Ordinal,ScheduleContext Context,DateTimeOffset KnownFrom);

// Published date mappings construct new draft contracts or verify identical
// existing contracts. An admission records this verification now; it never
// claims that an earlier contract was originally produced by this mapping.
public sealed class ContractDateMaterialization(Guid catalog,ContractDateStore mappings)
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    static MarketDataException Invalid()=>new("contract_materialization","Choose explicit contract identities, mapping occurrences and matching labels from an exact published date-list mapping.");
    static MarketDataException Conflict()=>new("contract_materialization_conflict","The request or contract has different retained terms. Use the original request or a new definition revision.");

    public async Task<ContractMaterializationReceipt> Materialize(NpgsqlConnection connection,ContractMaterializationRequest body,CancellationToken ct)
    {
        if(body.RequestId==Guid.Empty||body.RevisionId==Guid.Empty||body.MappingId==Guid.Empty||body.MappingVersion<1||body.Context is null||
            body.Contracts is null||body.Contracts.Length is <1 or >120||body.Contracts.Any(r=>r is null||r.ContractId==Guid.Empty||r.Ordinal<0||
                string.IsNullOrWhiteSpace(r.Label)||r.Kind is not ("future" or "forward")||(r.Kind=="future")!=!string.IsNullOrWhiteSpace(r.FuturesSeriesCode))||
            body.Contracts.Select(r=>r.ContractId).Distinct().Count()!=body.Contracts.Length)throw Invalid();
        await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102)"))
            await gate.ExecuteNonQueryAsync(ct);
        await using(var authority=ScheduleStore.Command(connection,"SELECT security.current_write_context() AND security.market_access($1,'WriteMarketDefinitions') AND security.market_access($1,'ReadMarketData')",catalog))
            if(!Equals(await authority.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        Guid referenceCatalog,referenceRevision;string state;
        await using(var target=ScheduleStore.Command(connection,"SELECT reference_catalog_id,reference_revision_id,state FROM market_data.revisions WHERE(catalog_id,id)=($1,$2) FOR UPDATE",catalog,body.RevisionId))
        await using(var reader=await target.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct))throw new PreviewDeniedException();
            referenceCatalog=reader.GetGuid(0);referenceRevision=reader.GetGuid(1);state=reader.GetString(2);
        }
        // Resolve under the caller's retained source context, then restore the
        // current-write binding. Historical read authority never grants a write.
        BoundContractDefinition mapping;
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT contract_date_source"))await save.ExecuteNonQueryAsync(ct);
        try{mapping=await mappings.Resolve(connection,new(body.MappingId,body.MappingVersion,body.Context),null,ct);}
        finally
        {
            await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT contract_date_source;RELEASE SAVEPOINT contract_date_source");
            await restore.ExecuteNonQueryAsync(CancellationToken.None);
        }
        foreach(var row in body.Contracts)
        {
            if(row.Ordinal>=mapping.Contracts.Length||mapping.Contracts[row.Ordinal].Label!=row.Label)throw Invalid();
            foreach(var field in new[]{mapping.Contracts[row.Ordinal].StartReference,mapping.Contracts[row.Ordinal].EndReference,mapping.Contracts[row.Ordinal].LastTradingReference})
                if(field.Source is not PublishedDateSource {Selection:"absolute_index",EngineVersion:"date_schedule_v3"}||field.BusinessDays is not null)throw Invalid();
        }
        var payload=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body,Json))));
        await using(var prior=ScheduleStore.Command(connection,"SELECT payload_sha256 FROM market_data.contract_date_materialization_requests WHERE catalog_id=$1 AND actor_id=security.custom_curve_actor($1,'WriteMarketDefinitions') AND request_id=$2",catalog,body.RequestId))
            if(await prior.ExecuteScalarAsync(ct) is string old)
            {
                if(old!=payload)throw Conflict();
                return await Receipt(connection,body.RequestId,body.Contracts.Length,ct);
            }
        await using(var request=ScheduleStore.Command(connection,"INSERT INTO market_data.contract_date_materialization_requests VALUES($1,security.custom_curve_actor($1,'WriteMarketDefinitions'),$2,$3)",catalog,body.RequestId,payload))
            await request.ExecuteNonQueryAsync(ct);
        for(var index=0;index<body.Contracts.Length;index++)
        {
            var row=body.Contracts[index];var dates=mapping.Contracts[row.Ordinal];var created=false;
            await using(var existing=ScheduleStore.Command(connection,"SELECT reference_catalog_id,reference_revision_id,kind,delivery_from,delivery_to,expiry_at,futures_series_code FROM market_data.contracts WHERE(catalog_id,revision_id,id)=($1,$2,$3)",catalog,body.RevisionId,row.ContractId))
            await using(var reader=await existing.ExecuteReaderAsync(ct))
            {
                if(await reader.ReadAsync(ct))
                {
                    if(reader.GetGuid(0)!=referenceCatalog||reader.GetGuid(1)!=referenceRevision||reader.GetString(2)!=row.Kind||
                        reader.GetFieldValue<DateOnly>(3)!=dates.Start||reader.GetFieldValue<DateOnly>(4)!=dates.End||
                        reader.GetFieldValue<DateTimeOffset>(5)!=dates.LastTradingAt||(reader.IsDBNull(6)?null:reader.GetString(6))!=row.FuturesSeriesCode)throw Conflict();
                }
                else {if(state!="draft")throw Conflict();created=true;}
            }
            if(created)
            {
                await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.contracts VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10)",
                    catalog,body.RevisionId,row.ContractId,referenceCatalog,referenceRevision,row.Kind,dates.Start,dates.End,dates.LastTradingAt,(object?)row.FuturesSeriesCode??DBNull.Value);
                await insert.ExecuteNonQueryAsync(ct);
            }
            Guid admission;
            await using(var existing=ScheduleStore.Command(connection,"SELECT id FROM market_data.contract_date_admissions WHERE(catalog_id,revision_id,contract_id,mapping_id,mapping_version,ordinal)=($1,$2,$3,$4,$5,$6)",
                catalog,body.RevisionId,row.ContractId,body.MappingId,body.MappingVersion,row.Ordinal))
                admission=await existing.ExecuteScalarAsync(ct) is Guid id?id:Guid.Empty;
            if(admission==Guid.Empty)
            {
                admission=Guid.NewGuid();
                await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.contract_date_admissions(catalog_id,id,revision_id,contract_id,mapping_id,mapping_version,ordinal,context_id,actor_id) VALUES($1,$2,$3,$4,$5,$6,$7,$8,security.custom_curve_actor($1,'WriteMarketDefinitions'))",
                    catalog,admission,body.RevisionId,row.ContractId,body.MappingId,body.MappingVersion,row.Ordinal,mapping.Context.ContextId!.Value);
                await insert.ExecuteNonQueryAsync(ct);
            }
            await using var item=ScheduleStore.Command(connection,"INSERT INTO market_data.contract_date_materialization_items VALUES($1,security.custom_curve_actor($1,'WriteMarketDefinitions'),$2,$3,$4,$5)",catalog,body.RequestId,index,admission,created);
            await item.ExecuteNonQueryAsync(ct);
        }
        return await Receipt(connection,body.RequestId,body.Contracts.Length,ct);
    }

    async Task<ContractMaterializationReceipt> Receipt(NpgsqlConnection connection,Guid request,int expectedCount,CancellationToken ct)
    {
        var rows=ImmutableArray.CreateBuilder<MaterializedContract>();
        // Resolve only this request's admission IDs. A full admission scan would
        // repeat source-authority checks for unrelated contracts on each replay.
        await using var command=ScheduleStore.Command(connection,"""
SELECT a.contract_id,i.admission_id,i.created FROM market_data.contract_date_materialization_items i
JOIN LATERAL(SELECT contract_id FROM market_data.contract_date_admissions a
 WHERE(a.catalog_id,a.id)=(i.catalog_id,i.admission_id) OFFSET 0) a ON true
WHERE i.catalog_id=$1 AND i.actor_id=security.custom_curve_actor($1,'WriteMarketDefinitions') AND i.request_id=$2 ORDER BY i.ordinal
""",catalog,request);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))rows.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetBoolean(2)));
        if(rows.Count!=expectedCount)throw new PreviewDeniedException();
        return new(request,rows.ToImmutable());
    }

    public async Task<ImmutableArray<ContractDateAdmission>> Read(NpgsqlConnection connection,ContractAdmissionRead body,ScheduleContext? selectedContext,CancellationToken ct)
    {
        if(body.Context is null||body.RevisionId==Guid.Empty||body.ContractId==Guid.Empty)throw Invalid();
        var context=await new KnowledgeStore(catalog).Retained(connection,body.Context,ct);
        if(selectedContext is null)await MarketContextStore.Bind(connection,catalog,context,ct);
        else if(selectedContext.ContextId!=context.ContextId)throw Invalid();
        var rows=ImmutableArray.CreateBuilder<ContractDateAdmission>();
        await using var command=ScheduleStore.Command(connection,"""
SELECT a.id,a.mapping_id,a.mapping_version,a.ordinal,t.effective_on,t.known_through,t.known_at,t.id,k.known_from
FROM market_data.contract_date_admissions a JOIN market_data.contract_date_admission_receipts k ON(k.catalog_id,k.admission_id)=(a.catalog_id,a.id)
JOIN market_data.temporal_contexts t ON(t.catalog_id,t.id)=(a.catalog_id,a.context_id)
WHERE(a.catalog_id,a.revision_id,a.contract_id)=($1,$2,$3) ORDER BY k.known_from,a.id
""",catalog,body.RevisionId,body.ContractId);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))rows.Add(new(reader.GetGuid(0),reader.GetGuid(1),reader.GetInt32(2),reader.GetInt32(3),
            new(reader.GetFieldValue<DateOnly>(4).ToString("yyyy-MM-dd"),reader.GetInt32(5),reader.GetFieldValue<DateTimeOffset>(6).ToString("O"),reader.GetGuid(7)),reader.GetFieldValue<DateTimeOffset>(8)));
        return rows.ToImmutable();
    }
}
