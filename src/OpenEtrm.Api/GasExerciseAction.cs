using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasExercisePrepareRequest(FuturesReadRequest Context,Guid? ReadContextId,Guid TradeId,Guid InstrumentId,
    int ExpectedRevision,Guid OperationId,[property:JsonRequired]int Right,DateOnly FirstPeriod,DateOnly LastPeriod,string Outcome,string Evidence,string EffectiveAt)
{
    internal FuturesReadRequest RetainedContext()=>Context is {KnowledgeMode:"current",ContextId:null}&&ReadContextId is {} pin&&pin!=Guid.Empty
        ?Context with{KnowledgeMode="retained",ContextId=pin}:throw GasExerciseAction.Invalid("Select the working ticket and its exact retained exercise read.");
}

// Previous identifies the latest instruction known when the action opened. It may take effect after the ticket's as-of instant.
public sealed record GasExerciseActionPeriod(int Right,DateOnly From,DateOnly To,DateTimeOffset ExerciseAt,
    Guid? PreviousId,string? PreviousOutcome,DateTimeOffset? PreviousEffectiveAt);

public sealed partial record GasExerciseAction(bool Allowed,string? Reason,Guid TradeId,Guid InstrumentId,int ExpectedRevision,
    Guid? ReadContextId,ImmutableArray<GasExerciseActionPeriod> Periods)
{
    public string Label=>"Record exercise";
    public string PreparePath=>"/api/gas/exercise/prepare";
    public string RecordPath=>"/api/gas/exercise/record";
    public ImmutableArray<TicketChoice> Outcomes=>[new("exercised","Exercised"),new("abandoned","Abandoned")];

    internal static MarketDataException Invalid(string message)=>new("exercise_election",message);

    internal static async Task<ImmutableArray<GasExerciseActionPeriod>> ReadPeriods(NpgsqlConnection connection,Guid catalog,
        Guid trade,Guid binding,int revision,CancellationToken ct)
    {
        await using var query=ScheduleStore.Command(connection,"SELECT security.gas_exercise_action_periods($1,$2,$3,$4)::text",catalog,trade,binding,revision);
        return JsonSerializer.Deserialize<ImmutableArray<GasExerciseActionPeriod>>((string)(await query.ExecuteScalarAsync(ct))!,JsonSerializerOptions.Web);
    }

    internal static async Task RequireAuthority(NpgsqlConnection connection,Guid catalog,Guid trade,Guid instrument,CancellationToken ct)
    {
        await using var check=ScheduleStore.Command(connection,
            "SELECT security.trading_access($1,'AmendTrade') AND security.gas_component_sources_visible($1,$3) FROM security.require_gas_quantity_trade($1,$2,false)",
            catalog,trade,instrument);
        if(await check.ExecuteScalarAsync(ct) is not true)throw new PreviewDeniedException();
    }

    [GeneratedRegex(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T([01][0-9]|2[0-3]):[0-5][0-9]:[0-5][0-9](\.[0-9]{1,6})?(Z|\+00:00)\z")]
    private static partial Regex EffectiveInstant();

    public static GasExerciseRecordRequest Build(GasExercisePrepareRequest request,ImmutableArray<GasExerciseActionPeriod> periods)
    {
        _=request.RetainedContext();
        if(request.OperationId==Guid.Empty||request.TradeId==Guid.Empty||request.InstrumentId==Guid.Empty||request.ExpectedRevision<1||
            request.Outcome is not ("abandoned" or "exercised")||string.IsNullOrWhiteSpace(request.Evidence)||request.Evidence.Trim(' ').EnumerateRunes().Count()>500)
            throw Invalid("Choose an outcome, supply evidence of 1 to 500 characters and identify the exact trade, revision and operation.");
        if(request.EffectiveAt is null || !EffectiveInstant().IsMatch(request.EffectiveAt) || !UtcInstant.TryParse(request.EffectiveAt,out var effective)||effective==default)
            throw Invalid("Supply an explicit UTC effective instant with at most microsecond precision.");
        var selected=periods.Where(p=>p.Right==request.Right&&p.From>=request.FirstPeriod&&p.From<=request.LastPeriod).OrderBy(p=>p.From).ToArray();
        if(selected.Length is 0 or >9600||selected[0].From!=request.FirstPeriod||selected[^1].From!=request.LastPeriod)
            throw Invalid("Choose the first and last saved periods of one physical right, in order.");
        if(selected.Any(p=>effective>p.ExerciseAt||effective<p.PreviousEffectiveAt))
            throw Invalid("The effective instant must be at or after each predecessor and at or before every selected deadline.");
        var instant=effective.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFF'Z'",CultureInfo.InvariantCulture);
        return new(request.Context,request.TradeId,request.InstrumentId,request.ExpectedRevision,request.OperationId,
            [..selected.Select(p=>new GasElectionInput(p.Right,p.From.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),request.Outcome,
                instant,request.Evidence,p.PreviousId))]);
    }
}
