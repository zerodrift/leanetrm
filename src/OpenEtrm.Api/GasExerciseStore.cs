using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasExerciseReadRequest(FuturesReadRequest Context,Guid TradeId,Guid InstrumentId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasElectionInput([property:JsonRequired]int Right,string FromDate,[property:JsonRequired]string? Outcome,
    string EffectiveAt,string Evidence,[property:JsonRequired]Guid? ExpectedPreviousId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasExerciseRecordRequest(FuturesReadRequest Context,Guid TradeId,Guid InstrumentId,int ExpectedRevision,
    Guid OperationId,[property:JsonRequired]ImmutableArray<GasElectionInput> Elections);

internal static class GasExerciseStore
{
    internal static Task<JsonElement> Record(NpgsqlConnection connection,Guid catalog,GasExerciseRecordRequest request,InputFrame frame,CancellationToken ct)
    {
        var body=new {request.OperationId,request.TradeId,request.InstrumentId,request.ExpectedRevision,request.Elections};
        return Result(connection,"SELECT security.record_gas_exercise_elections($1,$2::jsonb,$3)::text",ct,
            catalog,JsonSerializer.Serialize(body,RiskJobs.Json),frame.At);
    }

    internal static Task<JsonElement> Read(NpgsqlConnection connection,Guid catalog,Guid trade,Guid instrument,CancellationToken ct) =>
        Result(connection,"SELECT security.read_gas_exercise_elections($1,$2,$3)::text",ct,catalog,trade,instrument);

    internal static async Task<(ImmutableArray<ExerciseElection> Elections,DateTimeOffset? Next)> Facts(
        NpgsqlConnection connection,Guid catalog,Guid trade,Guid instrument,CancellationToken ct)
    {
        var result=await Read(connection,catalog,trade,instrument,ct);
        var elections=result.GetProperty("elections").EnumerateArray().Where(e=>e.OptionalText("outcome") is not null)
            .Select(e=>new ExerciseElection(e.GetProperty("right").GetInt32(),DateOnly.ParseExact(e.Text("fromDate"),"yyyy-MM-dd",CultureInfo.InvariantCulture),
                e.Text("outcome"),e.GetProperty("effectiveAt").GetDateTimeOffset(),e.Id("revisionId"),e.Text("evidence")));
        var next=result.GetProperty("nextElectionEffectiveAt");
        return ([..elections],next.ValueKind==JsonValueKind.Null?null:next.GetDateTimeOffset());
    }

    private static async Task<JsonElement> Result(NpgsqlConnection connection,string sql,CancellationToken ct,params object[] values)
    {
        await using var query=ScheduleStore.Command(connection,sql,values);
        return JsonSerializer.Deserialize<JsonElement>((string)(await query.ExecuteScalarAsync(ct))!);
    }
}
