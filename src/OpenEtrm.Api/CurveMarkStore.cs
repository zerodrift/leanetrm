using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VolatilityMarkInput(string Month,[property:JsonRequired]int ExpectedRevision,string Volatility);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CorrelationMarkInput(string Month,[property:JsonRequired]int ExpectedRevision,string Correlation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record VolatilityMarksRecordRequest(FuturesReadRequest Context,Guid OperationId,Guid DefinitionRevisionId,
    string Curve,string Reason,[property:JsonRequired]ImmutableArray<VolatilityMarkInput> Marks);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CorrelationMarksRecordRequest(FuturesReadRequest Context,Guid OperationId,Guid DefinitionRevisionId,
    string FirstCurve,string SecondCurve,string Reason,[property:JsonRequired]ImmutableArray<CorrelationMarkInput> Marks);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveMarksReadRequest(FuturesReadRequest Context,Guid DefinitionRevisionId,
    ImmutableArray<string> Curves,ImmutableArray<string> Months);

internal sealed class CurveMarkStore(Guid catalog)
{
    internal Task<JsonElement> RecordVolatility(NpgsqlConnection connection,InputFrame frame,VolatilityMarksRecordRequest request,CancellationToken ct) =>
        Record(connection,frame,"SELECT security.record_volatility_marks($1,$2::jsonb,$3)::text",
            new {request.OperationId,request.DefinitionRevisionId,request.Curve,request.Reason,request.Marks,convention=ExerciseMarks.HeldLogReturn},ct);

    internal Task<JsonElement> RecordCorrelation(NpgsqlConnection connection,InputFrame frame,CorrelationMarksRecordRequest request,CancellationToken ct) =>
        Record(connection,frame,"SELECT security.record_correlation_marks($1,$2::jsonb,$3)::text",
            new {request.OperationId,request.DefinitionRevisionId,request.FirstCurve,request.SecondCurve,request.Reason,request.Marks,convention=ExerciseMarks.HeldLogReturn},ct);

    private Task<JsonElement> Record(NpgsqlConnection connection,InputFrame frame,string sql,object command,CancellationToken ct) =>
        Result(connection,sql,ct,catalog,JsonSerializer.Serialize(command,RiskJobs.Json),frame.At);

    internal Task<JsonElement> Read(NpgsqlConnection connection,CurveMarksReadRequest request,CancellationToken ct)
    {
        if(request.Curves.IsDefaultOrEmpty||request.Months.IsDefaultOrEmpty)throw Invalid();
        return Read(connection,request.DefinitionRevisionId,[..request.Curves],[..request.Months.Select(Month)],ct);
    }

    private Task<JsonElement> Read(NpgsqlConnection connection,Guid definition,string[] curves,DateOnly[] months,CancellationToken ct) =>
        Result(connection,"SELECT security.read_curve_marks($1,$2,$3::text[],$4::date[])::text",ct,catalog,definition,curves,months);

    private static async Task<JsonElement> Result(NpgsqlConnection connection,string sql,CancellationToken ct,params object[] values)
    {
        await using var query=ScheduleStore.Command(connection,sql,values);
        try{return JsonSerializer.Deserialize<JsonElement>((string)(await query.ExecuteScalarAsync(ct))!);}
        catch(PostgresException error) when(error.SqlState=="42501"&&error.MessageText=="spread_mark_denied")
        {throw new PreviewDeniedException();}
    }

    private static DateOnly Month(string text) =>
        DateOnly.TryParseExact(text+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)?date:throw Invalid();
    private static MarketDataException Invalid()=>new("curve_mark_input","Select exact curves and delivery months in yyyy-MM format.");

    // Batch months with the same required curve set. A swing point needs its monthly volatility alone.
    internal async Task<RetainedGasInstrument> Bind(NpgsqlConnection connection,RetainedGasInstrument instrument,
        MarketSnapshot snapshot,PhysicalCashState? state,CancellationToken ct)
    {
        if(instrument.Rights.IsEmpty && !instrument.HasSwing)return instrument;
        if(state is null)throw new MarketDataException("gas_composition_input","Exercise valuation requires held quantities.");
        var scope=instrument.RequiredMarkSets(snapshot,state);
        var volatilities=new HashSet<CurveVolatilityMark>();
        var correlations=new HashSet<CurveCorrelationMark>();
        var groups=scope.Select(set=>new {set.Month,set.Curves[0].Revision,Curves=set.Curves.Select(c=>c.Code).ToArray()});
        foreach(var batch in groups.GroupBy(g=>(g.Revision,Curves:string.Join('/',g.Curves))))
        {
            var revision=batch.Key.Revision;
            if(revision.CatalogId!=catalog)throw Invalid();
            foreach(var months in batch.Select(g=>g.Month).Distinct().Chunk(240))
            {
                var curves=batch.First().Curves;
                var result=await Read(connection,revision.RevisionId,curves,months,ct);
                var volatilityRows=result.GetProperty("volatilities");
                var correlationRows=result.GetProperty("correlations");
                foreach(var row in volatilityRows.EnumerateArray())
                    volatilities.Add(new(new(revision,row.Text("curve")),Month(row.Text("month")),
                        new(row.Id("markId"),row.Text("volatility"),VolatilityInput.Annualized)));
                foreach(var row in correlationRows.EnumerateArray())
                    correlations.Add(new(new(revision,row.Text("firstCurve")),new(revision,row.Text("secondCurve")),
                        Month(row.Text("month")),row.Text("correlation"),row.Id("markId")));
            }
        }
        if(volatilities.Count==0&&correlations.Count==0)return instrument;
        var marks=new ExerciseMarks([..volatilities],[..correlations],ExerciseMarks.HeldLogReturn);
        if(instrument.QuantityRight is {} swing)
            return new(instrument.Id,instrument.Members,instrument.Assignments,swing,instrument.SwingPricingBindings.Single(),
                instrument.PriceLookups,instrument.Rights,marks,instrument.PhysicalSources);
        return new(instrument.Id,instrument.Members,instrument.Assignments,instrument.PriceLookups,rights:instrument.Rights,
            marks:marks,physicalSources:instrument.PhysicalSources);
    }
}
