using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Catalogue membership is metadata. These exact coordinates never select prices.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogueSource(Guid DefinitionRevisionId,Guid ReferenceCatalogId,Guid ReferenceRevisionId)
{
    internal static CatalogueSource From(PreviewData data)=>new(data.DefinitionRevision.RevisionId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId);
    internal PreviewManifest Apply(PreviewManifest manifest)=>manifest with
    {RevisionId=DefinitionRevisionId,ReferenceCatalogId=ReferenceCatalogId,ReferenceRevisionId=ReferenceRevisionId};
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogueCurve(string Code,string Kind,Guid DefinitionRevisionId,Guid ReferenceCatalogId,Guid ReferenceRevisionId)
{
    internal CatalogueSource Source=>new(DefinitionRevisionId,ReferenceCatalogId,ReferenceRevisionId);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CatalogueDefinition(DateOnly EffectiveFrom,DateOnly EffectiveTo,ImmutableArray<CatalogueCurve> Curves,
    ImmutableDictionary<string,CatalogueSource> Roles);
public sealed record CataloguePublication(Guid Id,Guid? PreviousId,DateOnly EffectiveFrom,DateOnly EffectiveTo,int PublicationOrder,
    ImmutableArray<CatalogueCurve> Curves,ImmutableDictionary<string,CatalogueSource> Roles)
{
    internal CatalogueSource RequireCurve(string code)=>Curves.SingleOrDefault(c=>c.Code==code)?.Source??
        throw new MarketDataException("catalogue_curve","The selected catalogue does not contain this curve.");
    internal CatalogueSource RequireRole(string role)=>Roles.TryGetValue(role,out var source)?source:
        throw new MarketDataException("catalogue_role",$"The selected catalogue does not declare the {role} role.");
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CataloguePublishRequest(Guid RequestId,Guid? ExpectedHead,CatalogueDefinition Definition);

internal sealed class CatalogueStore(Guid catalog)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    // Null explicitly means current published metadata, never a pricing cutoff.
    internal async Task<CataloguePublication> Read(NpgsqlConnection connection,Guid? context,CancellationToken ct)
    {
        await using var command=context is {} id
            ?ScheduleStore.Command(connection,"SELECT security.read_catalogue($1,$2)::text",catalog,id)
            :ScheduleStore.Command(connection,"SELECT security.read_current_catalogue($1,NULL)::text",catalog);
        return await Execute(command,ct);
    }
    internal async Task<CataloguePublication> ReadBound(NpgsqlConnection connection,CancellationToken ct,DateOnly? effectiveOn=null)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.read_request_catalogue($1,$2::date)::text",catalog,(object?)effectiveOn??DBNull.Value);
        return await Execute(command,ct);
    }
    internal async Task<CataloguePublication> Publish(NpgsqlConnection connection,CataloguePublishRequest request,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.publish_catalogue($1,$2,$3::uuid,$4::jsonb)::text",
            catalog,request.RequestId,(object?)request.ExpectedHead??DBNull.Value,JsonSerializer.Serialize(request.Definition,Json));
        return await Execute(command,ct);
    }
    private static async Task<CataloguePublication> Execute(NpgsqlCommand command,CancellationToken ct)
    {
        try{return Parse(await command.ExecuteScalarAsync(ct));}
        catch(PostgresException error)when(error.SqlState=="P0001"&&error.MessageText is
            "catalogue_source" or "catalogue_context" or "catalogue_missing" or "catalogue_conflict" or "catalogue_publication")
        {
            throw new MarketDataException(error.MessageText,Refusal(error.MessageText));
        }
    }
    internal static string Refusal(string code)=>code switch
    {
        "catalogue_source"=>"The selected catalogue requires exact source and reference versions that are unavailable under this context or current authority.",
        "catalogue_context"=>"Use the exact captured catalogue context and its business date.",
        "catalogue_missing"=>"No catalogue publication is known and applicable in this context.",
        "catalogue_conflict"=>"The catalogue head or command changed. Reload or retry the original command.",
        _=>"Publish complete exact catalogue membership with an explicit effective interval."
    };
    private static CataloguePublication Parse(object? value)=>value is string text
        ?JsonSerializer.Deserialize<CataloguePublication>(text,Json)??throw Missing():throw Missing();
    private static MarketDataException Missing()=>new("catalogue_missing","The exact catalogue publication is unavailable.");
}
