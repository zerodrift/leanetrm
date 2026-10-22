using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveActivationSegment([property:JsonRequired]DateOnly EffectiveFrom,[property:JsonRequired]DateOnly EffectiveTo,
    [property:JsonRequired]int Version,[property:JsonRequired]string OutputNode);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveActivationRequest([property:JsonRequired]Guid RequestId,[property:JsonRequired]Guid CurveId,
    [property:JsonRequired]int ExpectedRevision,[property:JsonRequired]string Policy,[property:JsonRequired]string Reason,
    [property:JsonRequired]ImmutableArray<CurveActivationSegment> Segments);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EffectiveCurveRequest([property:JsonRequired]Guid CurveId,[property:JsonRequired]Guid SnapshotId,[property:JsonRequired]string Policy);
public sealed record CurveActivationPlan(Guid CurveId,int Revision,string Policy,string Reason,
    ImmutableArray<CurveActivationSegment> Segments,DateTimeOffset? KnownFrom=null);

// Resolves a logical selection once at admission. Calculation, booked sides and
// retained workers continue to consume the existing exact recipe contract.
public sealed class CurveActivations(Guid catalog)
{
    public const string Policy="complete_effective_timeline_v1";
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    public async Task<CurveActivationPlan?> Read(NpgsqlConnection connection,Guid curve,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.read_custom_curve_activation($1,$2)",catalog,curve);
        var value=await command.ExecuteScalarAsync(ct);
        return value is string encoded?JsonSerializer.Deserialize<CurveActivationPlan>(encoded,Json):null;
    }
    public async Task<JsonElement> Publish(NpgsqlConnection connection,CurveActivationRequest request,CancellationToken ct)
    {
        if(request.Segments.IsDefault)throw Invalid();
        var contract=JsonSerializer.Serialize(new{request.Policy,request.Reason,request.Segments},Json);
        await using var command=ScheduleStore.Command(connection,"SELECT security.publish_custom_curve_activation($1,$2,$3,$4,$5::jsonb)",
            catalog,request.CurveId,request.RequestId,request.ExpectedRevision,contract);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct)??throw Invalid()));
    }
    public static CurveActivationSegment Resolve(CurveActivationPlan? plan,DateOnly effective,string policy)
    {
        if(policy!=Policy)throw Invalid();
        if(plan is null)throw Missing();
        var selected=plan.Segments.Where(s=>s.EffectiveFrom<=effective&&effective<s.EffectiveTo).ToArray();
        return selected.Length==1?selected[0]:throw Missing();
    }
    static MarketDataException Invalid()=>new("curve_activation_input","Supply the complete effective timeline policy, exact published outputs, a reason and expected revision.");
    static MarketDataException Missing()=>new("curve_activation_missing","The latest timeline known in this context has no active curve version on the selected effective date.");
}
