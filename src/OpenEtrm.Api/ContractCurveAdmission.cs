using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContractCurveAdmissionRequest(Guid RequestId,Guid RevisionId,Guid DefinitionId,int Version,Guid? SnapshotId);
public sealed record AdmittedCurveContract(int Position,Guid ContractId,Guid SeriesId,string? FuturesSeries);
public sealed record ContractCurveAdmissionReceipt(Guid RequestId,Guid RevisionId,string Code,ImmutableArray<AdmittedCurveContract> Contracts);

// D362: an exact published futures or forward definition version is admitted into a draft definition revision at a named close: the
// window it resolves there becomes the curve's members. Contract and series identities derive from what they trade, so a later
// close's admission into another revision keeps each kept contract's identity.
public sealed class ContractCurveAdmission(Guid catalog,CurveDefinitionStore definitions,ContractDateMaterialization materialization)
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    static MarketDataException Invalid(string message)=>new("contract_curve_admission",message);
    static MarketDataException Conflict()=>new("contract_curve_admission_conflict","The request or the admitted curve has different terms or another window. Retry the original request or admit into a new definition revision.");

    public async Task<ContractCurveAdmissionReceipt> Admit(NpgsqlConnection connection,ContractCurveAdmissionRequest body,
        Func<CurveDefinitionDraft,Task<(CurveDefinitionPreview Preview,ReferenceRevision Reference)>> preview,CancellationToken ct)
    {
        if(body.RequestId==Guid.Empty||body.RevisionId==Guid.Empty||body.DefinitionId==Guid.Empty||body.Version<1)
            throw Invalid("Choose an exact draft definition revision, published contract-curve definition version and close.");
        await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102)"))await gate.ExecuteNonQueryAsync(ct);
        await using(var authority=ScheduleStore.Command(connection,"SELECT security.current_write_context() AND security.market_access($1,'WriteMarketDefinitions') AND security.market_access($1,'ReadMarketData')",catalog))
            if(!Equals(await authority.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        var payload=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body,Json))));
        await using(var prior=ScheduleStore.Command(connection,"SELECT payload_sha256,curve_code FROM market_data.contract_curve_admission_requests WHERE catalog_id=$1 AND actor_id=security.custom_curve_actor($1,'WriteMarketDefinitions') AND request_id=$2",catalog,body.RequestId))
        await using(var reader=await prior.ExecuteReaderAsync(ct))
            if(await reader.ReadAsync(ct))
            {
                if(reader.GetString(0)!=payload)throw Conflict();
                var code=reader.GetString(1);await reader.CloseAsync();
                return await RetainedReceipt(connection,body,code,ct);
            }

        Guid referenceCatalog,referenceRevision;
        await using(var target=ScheduleStore.Command(connection,"SELECT reference_catalog_id,reference_revision_id,state FROM market_data.revisions WHERE(catalog_id,id)=($1,$2) FOR UPDATE",catalog,body.RevisionId))
        await using(var reader=await target.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct))throw new PreviewDeniedException();
            if(reader.GetString(2)!="draft")throw Invalid("Admit a contract curve into a draft definition revision.");
            referenceCatalog=reader.GetGuid(0);referenceRevision=reader.GetGuid(1);
        }
        await using(var pin=ScheduleStore.Command(connection,"SELECT reference_catalog_id,reference_revision_id FROM market_data.curve_definition_versions WHERE(catalog_id,definition_id,version)=($1,$2,$3)",catalog,body.DefinitionId,body.Version))
        await using(var reader=await pin.ExecuteReaderAsync(ct))
            if(await reader.ReadAsync(ct)&&(reader.GetGuid(0)!=referenceCatalog||reader.GetGuid(1)!=referenceRevision))
                throw Invalid("The definition and the definition revision must name the same reference revision.");
        var version=(await definitions.Read(connection,ct)).SingleOrDefault(v=>v.Id==body.DefinitionId&&v.Version==body.Version);
        if(version is not {State:"published",Definition:{ContractKind:"future" or "forward"}})throw Invalid("Admit an exact published futures or forward curve definition version.");
        var definition=version.Definition;
        var (window,reference)=await preview(definition);
        if(reference.CatalogId!=referenceCatalog||reference.RevisionId!=referenceRevision)throw Invalid("The selected close reads another reference revision than the definition revision.");
        var curve=definition.Code!;

        var identities=ImmutableArray.CreateBuilder<(CurveWindowContract Contract,Guid Id)>(window.Contracts.Length);
        foreach(var contract in window.Contracts)identities.Add((contract,await ContractIdentity(connection,body.RevisionId,curve,contract,ct)));
        await materialization.Materialize(connection,new(AgentTools.NameUuid(body.RequestId,"contract_materialization"),body.RevisionId,definition.Contracts!.MappingId,definition.Contracts.MappingVersion,window.Context,
            [..identities.Select(i=>new ContractMaterializationRow(i.Id,i.Contract.Position-1,i.Contract.Label,definition.ContractKind!,i.Contract.FuturesSeries))]),ct);

        await using(var existing=ScheduleStore.Command(connection,"SELECT definition_id,definition_version FROM market_data.curves WHERE(catalog_id,revision_id,code)=($1,$2,$3)",catalog,body.RevisionId,curve))
        await using(var reader=await existing.ExecuteReaderAsync(ct))
            if(await reader.ReadAsync(ct)&&(reader.IsDBNull(0)||reader.GetGuid(0)!=body.DefinitionId||reader.GetInt32(1)!=body.Version))throw Conflict();
        // A curve code keeps one display name across the catalog's revisions, and a name belongs to one curve in a revision (0026).
        await using(var names=ScheduleStore.Command(connection,"SELECT 1 FROM market_data.curves WHERE catalog_id=$1 AND(code=$3 AND name<>$4 OR revision_id=$2 AND name=$4 AND code<>$3) UNION ALL SELECT 1 FROM market_data.rate_curves WHERE catalog_id=$1 AND(code=$3 AND name<>$4 OR revision_id=$2 AND name=$4 AND code<>$3)",
            catalog,body.RevisionId,curve,definition.Name!))
            if(await names.ExecuteScalarAsync(ct) is not null)
                throw new MarketDataException("contract_curve_admission_name",$"Curve '{curve}' or the name '{definition.Name}' is already held with another display name or code. Publish a version that keeps the curve's name, or choose an unused name.");
        await using(var insert=ScheduleStore.Command(connection,"""
INSERT INTO market_data.curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,commodity_code,location_code,quote_code,profile_code,settlement_target_code,
 rule,direct_source_code,direct_role,name,definition_id,definition_version)
 VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,'direct',$11,$12,$13,$14,$15) ON CONFLICT DO NOTHING
""",catalog,body.RevisionId,curve,referenceCatalog,referenceRevision,definition.Commodity!,definition.Location!,definition.Quote!,definition.Profile!,definition.SettlementTarget!,
            definition.MarkSource!,definition.MarkRole!,definition.Name!,body.DefinitionId,body.Version))
            await insert.ExecuteNonQueryAsync(ct);
        foreach(var (contract,id) in identities)
        {
            await EnsureSeries(connection,body.RevisionId,curve,id,definition.Quote!,referenceCatalog,referenceRevision,ct);
            await using var member=ScheduleStore.Command(connection,"INSERT INTO market_data.curve_members VALUES($1,$2,$3,$4,$5) ON CONFLICT DO NOTHING",catalog,body.RevisionId,curve,id,contract.Position-1);
            await member.ExecuteNonQueryAsync(ct);
        }
        var receipt=await Receipt(connection,body.RequestId,body.RevisionId,curve,ct);
        if(!receipt.Contracts.Select(c=>(c.Position,c.ContractId)).SequenceEqual(identities.Select(i=>(i.Contract.Position,i.Id))))throw Conflict();
        await using(var request=ScheduleStore.Command(connection,"INSERT INTO market_data.contract_curve_admission_requests VALUES($1,security.custom_curve_actor($1,'WriteMarketDefinitions'),$2,$3,$4,$5)",
            catalog,body.RequestId,payload,body.RevisionId,curve))
            await request.ExecuteNonQueryAsync(ct);
        return receipt;
    }

    // A future is its exchange series, which the revision may already hold under another identity; a forward is its curve's delivery and expiry.
    async Task<Guid> ContractIdentity(NpgsqlConnection connection,Guid revision,string curve,CurveWindowContract contract,CancellationToken ct)
    {
        if(contract.FuturesSeries is not {} series)
            return AgentTools.NameUuid(catalog,$"curve_contract:forward:{curve}:{contract.Start:yyyy-MM-dd}:{contract.End:yyyy-MM-dd}:{contract.LastTradingAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}");
        await using var existing=ScheduleStore.Command(connection,"SELECT id FROM market_data.contracts WHERE(catalog_id,revision_id,futures_series_code)=($1,$2,$3)",catalog,revision,series);
        return await existing.ExecuteScalarAsync(ct) is Guid id?id:AgentTools.NameUuid(catalog,$"curve_contract:future:{series}");
    }

    async Task EnsureSeries(NpgsqlConnection connection,Guid revision,string curve,Guid contract,string quote,Guid referenceCatalog,Guid referenceRevision,CancellationToken ct)
    {
        await using(var existing=ScheduleStore.Command(connection,"SELECT 1 FROM market_data.series WHERE(catalog_id,revision_id,contract_id,quote_code)=($1,$2,$3,$4)",catalog,revision,contract,quote))
            if(await existing.ExecuteScalarAsync(ct) is not null)return;
        var series=AgentTools.NameUuid(catalog,$"curve_series:{contract:N}:{quote}");
        await using var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.series(catalog_id,revision_id,id,code,reference_catalog_id,reference_revision_id,contract_id,quote_code) VALUES($1,$2,$3,$4,$5,$6,$7,$8)",
            catalog,revision,series,$"{curve[..Math.Min(curve.Length,31)]}.{series:N}",referenceCatalog,referenceRevision,contract,quote);
        await insert.ExecuteNonQueryAsync(ct);
    }

    async Task<ContractCurveAdmissionReceipt> Receipt(NpgsqlConnection connection,Guid request,Guid revision,string code,CancellationToken ct)
    {
        var contracts=ImmutableArray.CreateBuilder<AdmittedCurveContract>();
        await using var command=ScheduleStore.Command(connection,"""
SELECT m.ordinal,m.contract_id,s.id,k.futures_series_code FROM market_data.curve_members m
 JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code)
 JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
 JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.contract_id,s.quote_code)=(m.catalog_id,m.revision_id,m.contract_id,c.quote_code)
 WHERE(m.catalog_id,m.revision_id,m.curve_code)=($1,$2,$3) ORDER BY m.ordinal
""",catalog,revision,code);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))contracts.Add(new(reader.GetInt32(0)+1,reader.GetGuid(1),reader.GetGuid(2),reader.IsDBNull(3)?null:reader.GetString(3)));
        return new(request,revision,code,contracts.ToImmutable());
    }

    async Task<ContractCurveAdmissionReceipt> RetainedReceipt(NpgsqlConnection connection,ContractCurveAdmissionRequest body,string code,CancellationToken ct)
    {
        var contracts=ImmutableArray.CreateBuilder<AdmittedCurveContract>();var expectedCount=0;
        // A draft may acquire more members after admission. The materialization's original ordinal slots and their exact
        // admissions are immutable; bound them by the published window, and still require every source to be visible now.
        await using var command=ScheduleStore.Command(connection,"""
SELECT i.ordinal,a.contract_id,s.id,k.futures_series_code,v.contract_count FROM market_data.curves c
 JOIN market_data.curve_definition_versions v ON(v.catalog_id,v.definition_id,v.version)=(c.catalog_id,c.definition_id,c.definition_version)
 JOIN market_data.contract_date_materialization_items i ON i.catalog_id=c.catalog_id
  AND i.actor_id=security.custom_curve_actor(c.catalog_id,'WriteMarketDefinitions') AND i.request_id=$4 AND i.ordinal<v.contract_count
 JOIN LATERAL(SELECT revision_id,contract_id,mapping_id,mapping_version,ordinal FROM market_data.contract_date_admissions a
  WHERE(a.catalog_id,a.id)=(i.catalog_id,i.admission_id) OFFSET 0) a
  ON(a.revision_id,a.mapping_id,a.mapping_version,a.ordinal)=(c.revision_id,v.mapping_definition_id,v.mapping_version,i.ordinal)
 JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(c.catalog_id,c.revision_id,a.contract_id)
 JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.contract_id,s.quote_code)=(c.catalog_id,c.revision_id,a.contract_id,c.quote_code)
 WHERE(c.catalog_id,c.revision_id,c.code)=($1,$2,$3) AND(v.definition_id,v.version)=($5,$6) AND v.state='published'
 ORDER BY i.ordinal
""",catalog,body.RevisionId,code,AgentTools.NameUuid(body.RequestId,"contract_materialization"),body.DefinitionId,body.Version);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))
        {
            if(reader.GetInt32(0)!=contracts.Count)throw new PreviewDeniedException();
            expectedCount=reader.GetInt32(4);
            contracts.Add(new(reader.GetInt32(0)+1,reader.GetGuid(1),reader.GetGuid(2),reader.IsDBNull(3)?null:reader.GetString(3)));
        }
        if(expectedCount<1||contracts.Count!=expectedCount)throw new PreviewDeniedException();
        return new(body.RequestId,body.RevisionId,code,contracts.ToImmutable());
    }
}
