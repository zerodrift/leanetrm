using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveDashboardLayout(Guid RequestId,JsonElement Layout,Guid? ExpectedVersion);

public static class DashboardLayouts
{
    // The only layout version. Widgets hold references to curves and definitions, never prices, dates or figures.
    private const int Version=1;
    private const int MaxWidgets=32,MaxBytes=24000;
    private static readonly string[] Arrangements=["board","morning"];
    private static readonly string[] Sections=["weather_events","inventories","prices_positions","missed"];
    private static readonly Regex CurveCode=new(@"\A[a-z][a-z0-9_.-]{0,63}\z",RegexOptions.Compiled);
    private static MarketDataException Invalid()=>new("dashboard_layout","Supply a valid dashboard layout. Dates, authority and market figures cannot be saved in a layout.");
    private static MarketDataException Conflict()=>new("dashboard_layout_conflict","This dashboard changed. Refresh before saving it.");
    private static void Exactly(JsonElement value,params string[] names)
    {
        if(value.ValueKind!=JsonValueKind.Object||value.EnumerateObject().Count()!=names.Length||value.EnumerateObject().Any(p=>!names.Contains(p.Name)))throw Invalid();
    }
    private static int Integer(JsonElement value,int min,int max)
    {
        if(value.ValueKind!=JsonValueKind.Number||!value.TryGetInt32(out var number)||number<min||number>max)throw Invalid();
        return number;
    }
    private static Guid Id(JsonElement value)
    {
        var text=value.ValueKind==JsonValueKind.String?value.GetString():null;
        return Guid.TryParseExact(text,"D",out var id)?id:throw Invalid();
    }
    private static void Config(string kind,JsonElement config)
    {
        switch(kind)
        {
            case "latest_levels":
                Exactly(config,"curves");
                var curves=config.GetProperty("curves");
                if(curves.ValueKind!=JsonValueKind.Array)throw Invalid();
                var codes=curves.EnumerateArray().Select(c=>c.ValueKind==JsonValueKind.String&&CurveCode.IsMatch(c.GetString()!)?c.GetString()!:throw Invalid()).ToArray();
                if(codes.Length is <1 or >12||codes.Distinct().Count()!=codes.Length)throw Invalid();
                break;
            case "price_chart":
                Exactly(config,"curve","contractId");
                var curve=config.GetProperty("curve");
                if(curve.ValueKind!=JsonValueKind.String||!CurveCode.IsMatch(curve.GetString()!))throw Invalid();
                Id(config.GetProperty("contractId"));
                break;
            case "positions":
                Exactly(config,"definitionId");
                Id(config.GetProperty("definitionId"));
                break;
            default: throw Invalid();
        }
    }
    public static void Validate(JsonElement layout)
    {
        try
        {
            Exactly(layout,"version","arrangement","widgets");
            if(Integer(layout.GetProperty("version"),Version,Version)!=Version||layout.GetProperty("arrangement").GetString() is not string arrangement||!Arrangements.Contains(arrangement))throw Invalid();
            var widgets=layout.GetProperty("widgets");
            if(widgets.ValueKind!=JsonValueKind.Array||widgets.GetArrayLength()>MaxWidgets)throw Invalid();
            var ids=new HashSet<Guid>();
            foreach(var widget in widgets.EnumerateArray())
            {
                Exactly(widget,"id","kind","config","board","morning");
                if(!ids.Add(Id(widget.GetProperty("id"))))throw Invalid();
                Config(widget.GetProperty("kind").GetString()??throw Invalid(),widget.GetProperty("config"));
                var board=widget.GetProperty("board");
                Exactly(board,"x","y","w","h");
                Integer(board.GetProperty("x"),0,11);Integer(board.GetProperty("y"),0,199);Integer(board.GetProperty("w"),1,12);Integer(board.GetProperty("h"),1,12);
                var morning=widget.GetProperty("morning");
                Exactly(morning,"section","order");
                if(morning.GetProperty("section").GetString() is not string section||!Sections.Contains(section))throw Invalid();
                Integer(morning.GetProperty("order"),0,63);
            }
            if(System.Text.Encoding.UTF8.GetByteCount(layout.GetRawText())>MaxBytes)throw Invalid();
        }
        catch(Exception e) when(e is InvalidOperationException or KeyNotFoundException){throw Invalid();}
    }

    private static JsonElement Parse(string json)=>JsonSerializer.Deserialize<JsonElement>(json);

    public static async Task<JsonElement> Read(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("SELECT json_build_object('version',version,'layout',layout)::text FROM presentation.dashboard_layouts",connection);
        return Parse(await command.ExecuteScalarAsync(ct) as string??"""{"version":null,"layout":null}""");
    }

    public static async Task<JsonElement> Save(NpgsqlConnection connection,SaveDashboardLayout request,CancellationToken ct)
    {
        Validate(request.Layout);
        // Serialize this account's saves so a replay, the version check and the write see one state.
        await using(var guard=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(tenant_id::text||workspace_id::text||principal_id::text||'dashboard',0)) FROM security.dashboard_identity()",connection))
        {
            await using var reader=await guard.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct))throw new PreviewDeniedException();
        }
        // The hash covers the layout's canonical form and the version it expects.
        await using var prior=new NpgsqlCommand("""
            WITH body AS(SELECT sha256(convert_to($2::jsonb::text||coalesce($3::text,''),'UTF8')) AS hash)
            SELECT r.body_hash=body.hash,r.response::text FROM body
            JOIN presentation.dashboard_layout_requests r ON r.request_id=$1
            """,connection);
        prior.Parameters.AddWithValue(request.RequestId);prior.Parameters.AddWithValue(NpgsqlDbType.Jsonb,request.Layout.GetRawText());
        prior.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Uuid,Value=(object?)request.ExpectedVersion??DBNull.Value});
        await using(var reader=await prior.ExecuteReaderAsync(ct))
        {
            if(await reader.ReadAsync(ct))
                return reader.GetBoolean(0)?Parse(reader.GetString(1)):throw new MarketDataException("dashboard_request_conflict","This request id already saved a different dashboard. Reload before saving again.");
        }
        var current=await Read(connection,ct);
        var version=current.GetProperty("version");
        if((version.ValueKind==JsonValueKind.Null?(Guid?)null:version.GetGuid())!=request.ExpectedVersion)throw Conflict();
        await using var command=new NpgsqlCommand("""
            WITH saved AS(
             INSERT INTO presentation.dashboard_layouts(tenant_id,workspace_id,principal_id,version,layout)
             SELECT tenant_id,workspace_id,principal_id,$1,$2 FROM security.dashboard_identity()
             ON CONFLICT(tenant_id,workspace_id,principal_id) DO UPDATE SET version=excluded.version,layout=excluded.layout
             RETURNING tenant_id,workspace_id,principal_id,json_build_object('version',version,'layout',layout)::jsonb AS response)
            INSERT INTO presentation.dashboard_layout_requests(tenant_id,workspace_id,principal_id,request_id,body_hash,response)
            SELECT tenant_id,workspace_id,principal_id,$3,sha256(convert_to($2::text||coalesce($4::text,''),'UTF8')),response FROM saved
            RETURNING response::text
            """,connection);
        command.Parameters.AddWithValue(Guid.NewGuid());command.Parameters.AddWithValue(NpgsqlDbType.Jsonb,request.Layout.GetRawText());command.Parameters.AddWithValue(request.RequestId);
        command.Parameters.Add(new NpgsqlParameter{NpgsqlDbType=NpgsqlDbType.Uuid,Value=(object?)request.ExpectedVersion??DBNull.Value});
        return Parse((string)(await command.ExecuteScalarAsync(ct)??throw new PreviewDeniedException()));
    }
}
