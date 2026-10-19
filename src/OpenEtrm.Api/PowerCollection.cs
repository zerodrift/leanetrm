using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PowerCollectionConfiguration(string ConnectionFile,PowerProviderSettings[] Providers,int PjmPageSize,int ErcotPageSize,int MaximumRows,int TimeoutSeconds);

// One invocation, one fenced worker connection. The timer only wakes this managed operation.
public static class PowerCollection
{
    public static async Task<int> Run(string[] args,HttpClient? transport=null)
    {
        if(args is not ["scheduled"] && args is not ["now","PJM"] && args is not ["now","ERCOT"])
            throw new ArgumentException("Choose scheduled or now PJM/ERCOT explicitly.");
        var file=Environment.GetEnvironmentVariable("OPENETRM_COLLECTION_CONFIG")??throw new InvalidOperationException("Explicit private collection configuration is required.");
        var settings=JsonSerializer.Deserialize<PowerCollectionConfiguration>(File.ReadAllText(file),new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new InvalidOperationException("Missing collection configuration.");
        if(settings.TimeoutSeconds is <1 or >2700 || settings.Providers.Select(p=>p.Provider).Distinct().Count()!=settings.Providers.Length)throw new InvalidOperationException("Explicit bounded collection settings are required.");
        await using var connection=new NpgsqlConnection(HostedConfiguration.Connection(settings.ConnectionFile).ConnectionString);
        await connection.OpenAsync();
        await using(var gate=ScheduleStore.Command(connection,"SELECT power_data.worker_lock()"))
            if(!Equals(await gate.ExecuteScalarAsync(),true)){Console.WriteLine("COLLECTION_BUSY");return 0;}
        foreach(var provider in settings.Providers)
        {
            await using var rate=ScheduleStore.Command(connection,"SELECT requests_per_minute FROM power_data.request_gates WHERE provider=$1",provider.Provider);
            if(!Equals(await rate.ExecuteScalarAsync(),provider.RequestsPerMinute))throw new InvalidOperationException("The pinned provider request rate differs from the retained pacing contract.");
        }
        var now=DateTimeOffset.UtcNow;var date=DateOnly.FromDateTime(now.UtcDateTime);
        var jobs=new List<(Guid Id,string Provider,int First,int Last)>();
        await using(var command=ScheduleStore.Command(connection,"SELECT j.id,j.provider,j.from_day,j.through_day,j.schedule_enabled,EXISTS(SELECT 1 FROM power_data.attempts a WHERE a.job_id=j.id AND (a.started_at AT TIME ZONE 'UTC')::date=$1) FROM power_data.jobs j WHERE j.enabled ORDER BY j.provider,j.id",date))
        await using(var reader=await command.ExecuteReaderAsync())
            while(await reader.ReadAsync())
            {
                var provider=reader.GetString(1);
                if(args[0]=="now"?provider!=args[1]:(!reader.GetBoolean(4)||reader.GetBoolean(5)||now.Hour<8))continue;
                jobs.Add((reader.GetGuid(0),provider,reader.GetInt32(2),reader.GetInt32(3)));
            }
        using var ownedTransport=transport is null?new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(60)}:null;
        var http=transport??ownedTransport!;
        var failed=false;
        foreach(var job in jobs)
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(settings.TimeoutSeconds));var ct=timeout.Token;
            var first=date.AddDays(job.First);var last=date.AddDays(job.Last);Guid attempt;
            await using(var begin=ScheduleStore.Command(connection,"SELECT power_data.begin_attempt($1,$2,$3)",job.Id,first,last))attempt=(Guid)(await begin.ExecuteScalarAsync(ct))!;
            try
            {
                var provider=settings.Providers.Single(p=>p.Provider==job.Provider);
                var payload=new List<object>();
                await using var command=ScheduleStore.Command(connection,"SELECT power_data.source_config($1)::text",job.Id);
                using var sources=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct))!);
                async Task Slot()
                {
                    await using var slot=ScheduleStore.Command(connection,"SELECT power_data.reserve_request($1)",job.Provider);
                    var at=(DateTime)(await slot.ExecuteScalarAsync(ct))!;
                    var delay=at-DateTime.UtcNow;if(delay>TimeSpan.Zero)await Task.Delay(delay,ct);
                }
                var client=new PowerPriceClient(http,Slot,provider,job.Provider=="PJM"?settings.PjmPageSize:settings.ErcotPageSize,settings.MaximumRows);
                foreach(var source in sources.RootElement.EnumerateArray())
                {
                    var clock=source.GetProperty("clock");
                    string? Optional(string name)=>clock.GetProperty(name).ValueKind==JsonValueKind.Null?null:clock.GetProperty(name).GetString();
                    var rule=new ClockRule(clock.GetProperty("code").GetString()!,clock.GetProperty("kind").GetString()!,clock.GetProperty("fixed_offset_seconds").ValueKind==JsonValueKind.Null?null:clock.GetProperty("fixed_offset_seconds").GetInt32(),Optional("iana_zone"),Optional("tzdb_version"));
                    var reference=new ReferenceRevision(source.GetProperty("reference_catalog_id").GetGuid(),source.GetProperty("reference_revision_id").GetGuid());
                    var unit=new PriceUnit(new(reference,source.GetProperty("currency_code").GetString()!),new(reference,source.GetProperty("quantity_code").GetString()!),ExactDecimal.Parse("1"));
                    var rows=await client.Fetch(source.GetProperty("dataset").GetString()!,source.GetProperty("market").GetString()!,source.GetProperty("point").GetString()!,source.GetProperty("minutes").GetInt32(),first,last,PowerClock.FromRule(rule,TimeZoneDatabase.Installed()),unit,ct);
                    payload.Add(new {sourceId=source.GetProperty("id").GetGuid(),rows=rows.Select(p=>new {start=p.Start,end=p.End,price=p.Price.Value.ToString(),revision=p.Revision}).ToArray()});
                }
                await using var finish=ScheduleStore.Command(connection,"SELECT power_data.finish_attempt($1,$2::jsonb,NULL)",attempt,JsonSerializer.Serialize(payload));
                await finish.ExecuteNonQueryAsync(ct);
                Console.WriteLine($"COLLECTION_SUCCEEDED {job.Provider} {attempt}");
            }
            catch(Exception e) when(e is MarketDataException or HttpRequestException or JsonException or OperationCanceledException or NpgsqlException or InvalidOperationException or FormatException)
            {
                failed=true;var code=e is MarketDataException market?market.Code:e is OperationCanceledException?"collection_timeout":"collection_failed";
                try{await using var fail=ScheduleStore.Command(connection,"SELECT power_data.finish_attempt($1,NULL,$2)",attempt,code);await fail.ExecuteNonQueryAsync();}
                catch(NpgsqlException){code="collection_fence";}
                Console.WriteLine($"COLLECTION_FAILED {job.Provider} {attempt} {code}");
            }
        }
        return failed?1:0;
    }
}
