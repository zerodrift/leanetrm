using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;

// The owned SQL controller pauses the second nonempty point read until the
// initial grant ends and a pre-existing successor grant becomes current.
internal static class FrameReadAuthorityChecks
{
    public static async Task Run(string fixturePath)
    {
        using var fixture=JsonDocument.Parse(File.ReadAllText(fixturePath));
        var sample=fixture.RootElement;
        var root=Path.GetFullPath(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")!);
        if(!File.Exists(Path.Combine(root,".openetrm-preview")))throw new Exception("Frame authority checks require an owned preview.");
        var runtime=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_RUNTIME")!)
            {CommandTimeout=20,ApplicationName=sample.GetProperty("application").GetString()!};
        if(Path.GetFullPath(runtime.Host!)!=root||runtime.Username!="preview_runtime"||runtime.Database!="postgres")
            throw new Exception("Frame authority checks must not connect outside their owned preview.");
        // This child's bounded wait tests the database authority decision, not the
        // ordinary five-second client deadline. Application defaults are unchanged.
        Environment.SetEnvironmentVariable("OPENETRM_PREVIEW_RUNTIME",runtime.ConnectionString);
        await using var database=new PreviewDatabase(root);
        var pins=sample.GetProperty("pins").EnumerateArray().Select(p=>p.GetGuid()).ToImmutableArray();
        if(pins.Length<2||pins.Distinct().Count()!=pins.Length)throw new Exception("The frame fixture requires distinct native pins.");
        var initialDeadline=DateTimeOffset.Parse(sample.GetProperty("initialDeadline").GetString()!,CultureInfo.InvariantCulture);
        var filtered=sample.GetProperty("filtered").GetBoolean();
        var expectDenied=sample.GetProperty("expectDenied").GetBoolean();
        ImmutableArray<Guid>? series=filtered?[sample.GetProperty("seriesId").GetGuid()]:null;
        var frames=Enumerable.Range(0,2).Select(i=>new InputFrame(Guid.NewGuid(),$"Synthetic deadline frame {i}",
            DateTimeOffset.Parse("2020-12-18T16:00:00Z",CultureInfo.InvariantCulture),pins,
            ImmutableDictionary<string,ImmutableArray<Guid>>.Empty)).ToImmutableArray();
        var yielded=0;var denied=false;
        try
        {
            await database.WithIdentity(sample.GetProperty("subject").GetString()!,"ReadMarketData",async(connection,ct)=>
            {
                await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_market_context($1,$2)",
                    database.Manifest.CatalogId,sample.GetProperty("contextId").GetGuid()))
                    await bind.ExecuteNonQueryAsync(ct);
                await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_close_session($1,$2)",
                    database.Manifest.CatalogId,sample.GetProperty("session").GetString()!))
                    await bind.ExecuteNonQueryAsync(ct);
                var metadata=await database.Read(connection,ct,observationIds:[],includeCalendarDays:false);
                await using(var check=ScheduleStore.Command(connection,"SELECT security.authority_until('ReadMarketData')"))
                {
                    var until=new DateTimeOffset((DateTime)(await check.ExecuteScalarAsync(ct)??throw new Exception("Missing native authority deadline.")));
                    if(until!=initialDeadline)throw new Exception("The frame read did not start under the intended original grant.");
                }
                await using(var limits=ScheduleStore.Command(connection,
                    "SET LOCAL statement_timeout='15s';SET LOCAL openetrm.synthetic_frame_point_calls='0'"))
                    await limits.ExecuteNonQueryAsync(ct);
                await using(var enable=ScheduleStore.Command(connection,"SELECT set_config('openetrm.synthetic_frame_gate',$1,true)",expectDenied?"enabled":"disabled"))
                    await enable.ExecuteNonQueryAsync(ct);
                await foreach(var frame in database.ReadFrameObservations(connection,metadata,frames,ObservationReadScope.ExactPins,ct,series))
                {
                    yielded++;
                    if(frame.Observations.IsEmpty)throw new Exception("The authority fixture lost its visible native frame.");
                }
                return true;
            },CancellationToken.None);
        }
        catch(PreviewDeniedException){denied=true;}
        if(expectDenied)
        {
            if(!denied||yielded>=2)throw new Exception($"Frame history chained a later grant past its original authority deadline: filtered={filtered}, yielded={yielded}.");
            Console.WriteLine($"PASS managed frame authority: filtered={filtered}, original deadline refuses later-grant continuation before both frames can be yielded");
        }
        else
        {
            if(denied||yielded!=2)throw new Exception($"A fresh managed frame read lost valid successor authority: filtered={filtered}, yielded={yielded}.");
            Console.WriteLine($"PASS fresh managed frame authority: filtered={filtered}, both frames complete under the current successor grant");
        }
    }
}
