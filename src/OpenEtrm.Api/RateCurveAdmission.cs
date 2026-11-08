using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RateCurveAdmissionRequest(Guid RequestId,Guid RevisionId,Guid DefinitionId,int Version,string? ValueSpecification);
public sealed record AdmittedRateTenor(string Label,Guid SeriesId);
public sealed record RateCurveAdmissionReceipt(Guid RequestId,Guid RevisionId,string Code,ImmutableArray<AdmittedRateTenor> Tenors);

// D359: an exact published interest-rate definition version is admitted into a draft definition revision as one rate curve
// with one series per tenor label. A label's series identity is derived from the catalog, curve code and label, so every
// revision and definition version that keeps the label keeps its series and its mark history.
public sealed class RateCurveAdmission(Guid catalog)
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    static MarketDataException Invalid(string message)=>new("rate_admission",message);
    static MarketDataException Conflict()=>new("rate_admission_conflict","The request or the admitted curve has different terms. Retry the original request or admit into a new definition revision.");

    // A label keeps its series, and so its mark history, while the curve's mark unit stays the same.
    public static Guid TenorSeries(Guid catalog,string code,string label,string markUnit)=>AgentTools.NameUuid(catalog,$"rate_tenor:{code}:{label}:{markUnit}");

    public async Task<RateCurveAdmissionReceipt> Admit(NpgsqlConnection connection,RateCurveAdmissionRequest body,CancellationToken ct)
    {
        if(body.RequestId==Guid.Empty||body.RevisionId==Guid.Empty||body.DefinitionId==Guid.Empty||body.Version<1||string.IsNullOrWhiteSpace(body.ValueSpecification))
            throw Invalid("Choose an exact draft definition revision, published interest-rate definition version and tenor value specification.");
        await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102)"))await gate.ExecuteNonQueryAsync(ct);
        await using(var authority=ScheduleStore.Command(connection,"SELECT security.current_write_context() AND security.market_access($1,'WriteMarketDefinitions') AND security.market_access($1,'ReadMarketData')",catalog))
            if(!Equals(await authority.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        var payload=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body,Json))));
        await using(var prior=ScheduleStore.Command(connection,"SELECT payload_sha256,curve_code FROM market_data.rate_curve_admission_requests WHERE catalog_id=$1 AND actor_id=security.custom_curve_actor($1,'WriteMarketDefinitions') AND request_id=$2",catalog,body.RequestId))
        await using(var reader=await prior.ExecuteReaderAsync(ct))
            if(await reader.ReadAsync(ct))
            {
                if(reader.GetString(0)!=payload)throw Conflict();
                var code=reader.GetString(1);await reader.CloseAsync();
                return await Receipt(connection,body.RequestId,body.RevisionId,code,ct);
            }

        Guid referenceCatalog,referenceRevision;
        await using(var target=ScheduleStore.Command(connection,"SELECT reference_catalog_id,reference_revision_id,state FROM market_data.revisions WHERE(catalog_id,id)=($1,$2) FOR UPDATE",catalog,body.RevisionId))
        await using(var reader=await target.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct))throw new PreviewDeniedException();
            if(reader.GetString(2)!="draft")throw Invalid("Admit a rate curve into a draft definition revision.");
            referenceCatalog=reader.GetGuid(0);referenceRevision=reader.GetGuid(1);
        }
        string curve,name,source,unit;Guid mapping;int mappingVersion;object[] terms;
        await using(var definition=ScheduleStore.Command(connection,"""
SELECT d.code,v.name,v.mark_source_code,v.mark_unit,v.mapping_definition_id,v.mapping_version,v.state,v.kind,v.reference_catalog_id,v.reference_revision_id,
 v.currency_code,v.day_count,v.compounding,v.interpolation,v.extrapolation
 FROM market_data.curve_definition_versions v JOIN market_data.curve_definitions d ON(d.catalog_id,d.id)=(v.catalog_id,v.definition_id)
 WHERE(v.catalog_id,v.definition_id,v.version)=($1,$2,$3)
""",catalog,body.DefinitionId,body.Version))
        await using(var reader=await definition.ExecuteReaderAsync(ct))
        {
            if(!await reader.ReadAsync(ct)||reader.GetString(6)!="published"||reader.GetString(7)!="interest_rate")
                throw Invalid("Admit an exact published interest-rate curve definition version.");
            if(reader.GetGuid(8)!=referenceCatalog||reader.GetGuid(9)!=referenceRevision)
                throw Invalid("The definition and the definition revision must name the same reference revision.");
            (curve,name,source,unit,mapping,mappingVersion)=(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),reader.GetGuid(4),reader.GetInt32(5));
            terms=[reader.GetString(10),unit,reader.GetString(11),reader.GetString(12),reader.GetString(13),reader.GetString(14),mapping,mappingVersion];
        }
        await using(var specification=ScheduleStore.Command(connection,"SELECT kind,minimum::text,maximum::text FROM ref_data.value_specifications WHERE(catalog_id,revision_id,code)=($1,$2,$3)",
            referenceCatalog,referenceRevision,body.ValueSpecification))
        await using(var reader=await specification.ExecuteReaderAsync(ct))
        {
            var range=RateCurveMath.MarkRange(unit);
            if(!await reader.ReadAsync(ct)||reader.GetString(0)!="dimensionless"||ExactDecimal.Parse(reader.GetString(1))!=range.Minimum||ExactDecimal.Parse(reader.GetString(2))!=range.Maximum)
                throw Invalid($"Tenor marks need a dimensionless value specification ranging exactly {range.Minimum} to {range.Maximum} ({RateCurveMath.MarkUnitLabel(unit)}).");
        }
        var labels=ImmutableArray.CreateBuilder<string>();
        await using(var rows=ScheduleStore.Command(connection,"SELECT label FROM market_data.contract_date_contracts WHERE(catalog_id,definition_id,version)=($1,$2,$3) ORDER BY ordinal",catalog,mapping,mappingVersion))
        await using(var reader=await rows.ExecuteReaderAsync(ct))
            while(await reader.ReadAsync(ct))labels.Add(reader.GetString(0));
        if(labels.Count==0)throw Invalid("The definition's date list has no tenors.");

        await using(var existing=ScheduleStore.Command(connection,"SELECT definition_id,definition_version FROM market_data.rate_curves WHERE(catalog_id,revision_id,code)=($1,$2,$3)",catalog,body.RevisionId,curve))
        await using(var reader=await existing.ExecuteReaderAsync(ct))
            if(await reader.ReadAsync(ct)&&(reader.IsDBNull(0)||reader.GetGuid(0)!=body.DefinitionId||reader.GetInt32(1)!=body.Version))throw Conflict();
        await using(var insert=ScheduleStore.Command(connection,"""
INSERT INTO market_data.rate_curves(catalog_id,revision_id,code,reference_catalog_id,reference_revision_id,source_code,name,definition_id,definition_version,
 currency_code,mark_unit,day_count,compounding,interpolation,extrapolation,mapping_definition_id,mapping_version)
 VALUES($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17) ON CONFLICT DO NOTHING
""",[catalog,body.RevisionId,curve,referenceCatalog,referenceRevision,source,name,body.DefinitionId,body.Version,..terms]))
            await insert.ExecuteNonQueryAsync(ct);
        for(var ordinal=0;ordinal<labels.Count;ordinal++)
        {
            var series=TenorSeries(catalog,curve,labels[ordinal],unit);
            await using(var insert=ScheduleStore.Command(connection,"""
INSERT INTO market_data.series(catalog_id,revision_id,id,code,reference_catalog_id,reference_revision_id,value_specification_code)
 VALUES($1,$2,$3,$4,$5,$6,$7) ON CONFLICT DO NOTHING
""",catalog,body.RevisionId,series,$"{curve}.tenor.{series:N}"[..Math.Min(64,curve.Length+23)],referenceCatalog,referenceRevision,body.ValueSpecification))
                await insert.ExecuteNonQueryAsync(ct);
            await using(var insert=ScheduleStore.Command(connection,"INSERT INTO market_data.rate_curve_tenors VALUES($1,$2,$3,$4,$5,$6) ON CONFLICT DO NOTHING",
                catalog,body.RevisionId,curve,ordinal,labels[ordinal],series))
                await insert.ExecuteNonQueryAsync(ct);
        }
        var receipt=await Receipt(connection,body.RequestId,body.RevisionId,curve,ct);
        if(!receipt.Tenors.Select(t=>t.Label).SequenceEqual(labels))throw Conflict();
        await using(var verify=ScheduleStore.Command(connection,"SELECT count(*) FROM market_data.series s JOIN market_data.rate_curve_tenors t ON(t.catalog_id,t.revision_id,t.series_id)=(s.catalog_id,s.revision_id,s.id) WHERE(t.catalog_id,t.revision_id,t.curve_code)=($1,$2,$3) AND s.value_specification_code=$4",
            catalog,body.RevisionId,curve,body.ValueSpecification))
            if(Convert.ToInt32(await verify.ExecuteScalarAsync(ct),System.Globalization.CultureInfo.InvariantCulture)!=labels.Count)throw Conflict();
        await using(var request=ScheduleStore.Command(connection,"INSERT INTO market_data.rate_curve_admission_requests VALUES($1,security.custom_curve_actor($1,'WriteMarketDefinitions'),$2,$3,$4,$5)",
            catalog,body.RequestId,payload,body.RevisionId,curve))
            await request.ExecuteNonQueryAsync(ct);
        return receipt;
    }

    async Task<RateCurveAdmissionReceipt> Receipt(NpgsqlConnection connection,Guid request,Guid revision,string code,CancellationToken ct)
    {
        var tenors=ImmutableArray.CreateBuilder<AdmittedRateTenor>();
        await using var command=ScheduleStore.Command(connection,"SELECT label,series_id FROM market_data.rate_curve_tenors WHERE(catalog_id,revision_id,curve_code)=($1,$2,$3) ORDER BY ordinal",catalog,revision,code);
        await using var reader=await command.ExecuteReaderAsync(ct);
        while(await reader.ReadAsync(ct))tenors.Add(new(reader.GetString(0),reader.GetGuid(1)));
        return new(request,revision,code,tenors.ToImmutable());
    }
}
