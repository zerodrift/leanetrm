using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PowerLocationBindingRequest([property:JsonRequired]Guid RequestId,[property:JsonRequired]Guid DefinitionRevisionId,
    [property:JsonRequired]string Location,[property:JsonRequired]string Market,[property:JsonRequired]DateTimeOffset DeliveryFrom,[property:JsonRequired]DateTimeOffset DeliveryTo,
    [property:JsonRequired]Guid CurveId,[property:JsonRequired]string Policy,[property:JsonRequired]string Reason,[property:JsonRequired]ImmutableArray<CurveActivationSegment> Segments);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PowerLocationHistoryRequest([property:JsonRequired]Guid RequestId,[property:JsonRequired]Guid DefinitionRevisionId,
    [property:JsonRequired]string Location,[property:JsonRequired]Guid CurveId,[property:JsonRequired]int ExpectedActivationRevision,
    [property:JsonRequired]DateTimeOffset ActualFrom,[property:JsonRequired]string Reason,[property:JsonRequired]ImmutableArray<CurveActivationSegment> Segments);
internal sealed record PowerLocationEvidence(int BindingRevision,string Location,string Market,string Policy,DateTimeOffset DeliveryFrom,DateTimeOffset DeliveryTo,
    CurveActivationPlan Activation,PublishedRecipeReference Output,PowerIntervalSettlement? Settlement)
{
    public DeliveryCoverage? ActualPrefix{get;init;}
    public int? PrefixActivationRevision{get;init;}
    public string? PrefixPolicy{get;init;}
}
internal sealed record PowerLocationDemand(string Location,DateTimeOffset From,DateTimeOffset To);
internal sealed record ResolvedPowerLocation(LocationAssignment Assignment,PowerIntervalCurves.Definition Definition,PowerLocationEvidence Evidence);

internal static class PowerLocationGraphs
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    internal static async Task<JsonElement> PublishHistory(NpgsqlConnection connection,Guid catalog,PowerLocationHistoryRequest request,CancellationToken ct)
    {
        if(request.Segments.IsDefault||request.ActualFrom.Offset!=TimeSpan.Zero)
            throw new MarketDataException("power_prefix_input","Supply explicit activated outputs and an exact UTC actual-prefix boundary.");
        var contract=JsonSerializer.Serialize(new{policy=CurveActivations.Policy,request.Reason,request.Segments},Json);
        await using var command=ScheduleStore.Command(connection,"SELECT security.publish_power_location_history($1,$2,$3,$4,$5,$6,$7,$8::jsonb)::text",
            catalog,request.DefinitionRevisionId,request.Location,request.CurveId,request.RequestId,request.ExpectedActivationRevision,request.ActualFrom,contract);
        try{return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct)??throw new PreviewDeniedException()));}
        catch(PostgresException error)when(error.SqlState=="P0001"&&(error.MessageText=="power_location_conflict"||error.MessageText.StartsWith("power_prefix_",StringComparison.Ordinal)||error.MessageText.StartsWith("curve_activation_",StringComparison.Ordinal)))
        {throw new MarketDataException(error.MessageText,"An actual-only history prefix requires a compatible DA release-replay output, local month boundary and explicit activation revision.");}
    }
    internal static async Task<JsonElement> Publish(NpgsqlConnection connection,Guid catalog,PowerLocationBindingRequest request,CancellationToken ct)
    {
        if(request.Segments.IsDefault)throw new MarketDataException("power_location_input","Supply exact activated outputs and the follow-location policy.");
        var contract=JsonSerializer.Serialize(new{policy=CurveActivations.Policy,request.Reason,request.Segments},Json);
        await using var command=ScheduleStore.Command(connection,"SELECT security.publish_power_location($1,$2,$3,$4,$5,$6,$7,$8,$9,$10::jsonb)",
            catalog,request.DefinitionRevisionId,request.Location,request.Market,request.DeliveryFrom,request.DeliveryTo,request.CurveId,request.RequestId,request.Policy,contract);
        try{return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct)??throw new PreviewDeniedException()));}
        catch(PostgresException error)when(error.SqlState=="P0001"&&error.MessageText is "power_location_input" or "power_location_conflict" or "power_location_output" or "curve_activation_input" or "curve_activation_conflict" or "curve_activation_overlap")
        {throw new MarketDataException(error.MessageText,"Supply one unambiguous power location binding, exact compatible interval outputs and a stable request identity. Existing bindings retain their logical graph family.");}
    }
    internal static async Task<ImmutableArray<ResolvedPowerLocation>> Resolve(NpgsqlConnection connection,PreviewData data,Guid context,ImmutableArray<PowerLocationDemand> demands,CancellationToken ct)
    {
        var rows=await PreviewDatabase.Rows(connection,"SELECT security.read_power_locations($1,$2,$3,$4::jsonb)::text",[data.DefinitionRevision.CatalogId,data.DefinitionRevision.RevisionId,context,JsonSerializer.Serialize(demands,Json)],ct);
        if(rows.IsEmpty)return [];
        var selected=(await new KnowledgeStore(data.DefinitionRevision.CatalogId).Read(connection,context,ct)).Context;
        var versions=await CustomCurvesStore.ReadVersions(connection,data.DefinitionRevision.CatalogId,ct);
        var products=await PowerIntervalCurves.Products(connection,data,ct);
        var result=ImmutableArray.CreateBuilder<ResolvedPowerLocation>();
        foreach(var row in rows)
        {
            var plan=JsonSerializer.Deserialize<CurveActivationPlan>(row.GetProperty("activation"),Json)!;
            var segment=CurveActivations.Resolve(plan,DateWorkspace.Date(selected.EffectiveOn!),CurveActivations.Policy);
            var saved=versions.SingleOrDefault(v=>v.Id==plan.CurveId&&v.Version==segment.Version&&v.State=="published"&&v.Definition.Output==segment.OutputNode)
                ??throw new MarketDataException("power_location_output","The selected interval output is unavailable in this context.");
            RecipeEngine.Validate(saved.Definition);
            var output=new PublishedRecipeReference(data.DefinitionRevision.CatalogId,saved.Id,saved.Version,segment.OutputNode);
            var definition=PowerIntervalCurves.Read(data,saved.Definition,products,output,versions);
            DeliveryCoverage? prefix=row.OptionalText("actual_only_from") is {} prefixFrom
                ?new(SchedulePreview.ParseAt(prefixFrom),row.Instant("actual_only_to")):null;
            if(prefix is not null)definition=PowerIntervalCurves.WithActualPrefix(data,definition,prefix);
            var location=row.Text("location_code");var market=row.Text("market");
            if(definition.Location!=location||definition.Terms.Market!=market||saved.Sources.Values.Any(s=>s.CatalogId!=data.DefinitionRevision.CatalogId||s.RevisionId!=data.DefinitionRevision.RevisionId))
                throw new MarketDataException("power_location_output","The activated graph must retain this exact location, market and source bundle.");
            var from=row.Instant("delivery_from");var to=row.Instant("delivery_to");
            if(data.Definitions["location_valuation_assignments"].Any(a=>a.Text("location_code")==location&&a.Text("attribute")=="energy"&&a.Instant("effective_from")<to&&from<a.Instant("effective_to")))
                throw new MarketDataException("power_location_conflict","Native and graph assignments cannot own the same physical delivery.");
            var assignment=new LocationAssignment("power_"+location,new(data.ReferenceRevision,location),ValuedAttribute.Energy,from,to,definition.Curve.Key,definition.Curve.Unit,ExactDecimal.Parse("0"));
            result.Add(new(assignment,definition,new(1,location,market,row.Text("policy"),from,to,plan,output,definition.Settlement){ActualPrefix=prefix,
                PrefixActivationRevision=prefix is null?null:int.Parse(row.Text("prefix_activation_revision"),System.Globalization.CultureInfo.InvariantCulture),PrefixPolicy=row.OptionalText("prefix_policy")}));
        }
        return result.ToImmutable();
    }
}
