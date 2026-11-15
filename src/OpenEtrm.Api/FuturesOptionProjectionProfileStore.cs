using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

// The same exact saved expiry/margin profile serves ordinary option value and
// hypothetical continuation. Native visibility and original read authority stay
// independent of the financial model selected by either consumer.
internal static class FuturesOptionProjectionProfileStore
{
    internal static FuturesOptionAutomaticRule AutomaticRule(PreviewData data,FuturesOptionInstrument option)
    {
        var rule=data.References["futures_option_rules"].Single(r=>r.Text("code")==option.Series.AutomaticExerciseRule);
        return new(new(data.ReferenceRevision,rule.Text("code")),int.Parse(rule.Text("version"),CultureInfo.InvariantCulture),rule.Text("evaluation"),
            rule.Number("in_the_money_threshold"),rule.Text("at_the_money_call"),rule.Text("at_the_money_put"));
    }
    internal static PhysicalOptionTerminal Terminal(FuturesOptionKnownHistory history,DateTimeOffset at)
    {
        var instruction=history.Events.Where(e=>e.EffectiveAt<=at&&(e.Kind is "exercise_instruction" or "abandon_instruction"))
            .OrderBy(e=>e.PublicationOrder).LastOrDefault();
        return instruction?.Kind switch{"exercise_instruction"=>PhysicalOptionTerminal.Exercise,
            "abandon_instruction"=>PhysicalOptionTerminal.Abandon,_=>PhysicalOptionTerminal.Automatic};
    }
    private sealed record Admission(Guid Catalog,Guid Trade,int Revision,Guid Context,DateTimeOffset At,DateOnly Day,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT security.require_lifecycle_projection_profile($1,$2,$3,$4,$5,$6,$7,$8,$9)",
                Catalog,"listed_option",Trade,DBNull.Value,Revision,Context,At,Day,Until);
            command.Parameters[3].NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid;
            await command.ExecuteNonQueryAsync(ct);
        }
    }
    internal static async Task<JsonElement> Read(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ScheduleContext context,FuturesOptionPosition option,CancellationToken ct)
        =>await ReadAvailable(connection,database,frame,context,option,ct)??throw new MarketDataException("projection_option_policy",
            "Save an exact expiry source and linked-future margin profile for every held option.");
    internal static async Task<JsonElement?> ReadAvailable(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ScheduleContext context,FuturesOptionPosition option,CancellationToken ct)
    {
        var exact=PinnedContext.Exact(context);var day=PublishedSchedules.ContextDate(exact);var catalog=database.Manifest.CatalogId;
        if(frame.ContextId!=exact.ContextId||frame.BusinessDate!=day)
            throw new MarketDataException("projection_option_policy","Retain the exact certified option source frame and business date.");
        await using var capture=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_preview_authority($1)",catalog);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        var read=new FuturesMarginReadAdmission(catalog,exact.ContextId!.Value,frame.At,until);
        database.RegisterCommitAdmission(connection,read);
        await TradingContext.AdmitSource(database,connection,exact,frame.At,ct);
        var value=await LifecycleNative.Read(connection,"SELECT security.read_lifecycle_projection_profiles($1,$2,$3,$4,$5,$6,$7,$8)::text",ct,
            catalog,"listed_option",option.Trade,option.Revision,exact.ContextId!.Value,frame.At,day,until);
        var rows=value.EnumerateArray().ToArray();
        if(rows.Length==0){await read.RequireCommit(connection,ct);return null;}
        if(rows.Length!=1)throw new MarketDataException("projection_option_policy","Retain one exact native expiry profile at this source.");
        var admission=new Admission(catalog,option.Trade,int.Parse(rows[0].GetProperty("profile").Text("revision"),CultureInfo.InvariantCulture),
            exact.ContextId.Value,frame.At,day,until);
        database.RegisterCommitAdmission(connection,admission);await admission.RequireCommit(connection,ct);
        return rows[0];
    }
}
