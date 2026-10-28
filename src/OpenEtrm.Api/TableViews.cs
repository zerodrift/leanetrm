using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using NpgsqlTypes;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveTableView(JsonElement Layout, Guid? ExpectedVersion);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DeleteTableView(string Name, Guid ExpectedVersion);

public static class TableViews
{
    private static readonly string[] Columns=["tradeDate","legalEntity","counterparty","traderCode","trader","memo","instrumentType","fixedPrice","presentValue","portfolio","book","market","contract","commodity","executed","tradeCount","lots","quantity","unit","priceUnit","executionPrice","mark","executionPnl","currency","settlement","tradeReference","instrumentReference","revision",..TradingBlotter.PriceMeasures.Keys];
    private static readonly string[] Dimensions=["instrumentType","portfolio","book","market","contract","commodity"];
    private static readonly string[] Measures=["presentValue","tradeCount","lots","quantity","executionPnl",..TradingBlotter.PriceMeasures.Keys];
    // How the desk groups trade rows; null is a flat list.
    private static readonly string[] RowGroups=["book","market","instrumentType"];
    // The only layout version. Layouts of an earlier version are not migrated and cannot be saved.
    private const int Version=5;
    private static MarketDataException Invalid()=>new("table_view","Supply a valid named table layout. Dates, authority and market inputs cannot be saved in a layout.");
    private static bool Field(string key)
    {
        if(Columns.Contains(key))return true;
        if(!key.StartsWith("pivot:",StringComparison.Ordinal)||key.Length>512)return false;
        try
        {
            var parts=JsonSerializer.Deserialize<JsonElement>(key[6..]);
            if(parts.ValueKind!=JsonValueKind.Array||parts.GetArrayLength()!=2||!Measures.Contains(parts[1].GetString()))return false;
            var ids=JsonSerializer.Deserialize<JsonElement>(parts[0].GetString()!);
            return ids.ValueKind==JsonValueKind.Array&&ids.GetArrayLength() is >0 and <=3&&ids.EnumerateArray().All(id=>id.ValueKind==JsonValueKind.String&&System.Text.RegularExpressions.Regex.IsMatch(id.GetString()!,@"\A[a-zA-Z0-9_-]{1,80}\z"));
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or ArgumentException){return false;}
    }
    public static void Validate(JsonElement layout)
    {
        try
        {
            var fields=new[]{"version","name","sidebar","mode","rowGroup","columns","bookIds","portfolioIds","groupBy","measures","query","columnFilters","splitBy","grid","chart"};
            if(layout.ValueKind!=JsonValueKind.Object)throw Invalid();
            if(layout.GetProperty("version").GetInt32()!=Version||layout.EnumerateObject().Count()!=fields.Length||layout.EnumerateObject().Select(p=>p.Name).Distinct().Count()!=fields.Length||layout.EnumerateObject().Any(p=>!fields.Contains(p.Name)))throw Invalid();
            var name=layout.GetProperty("name").GetString();
            if(string.IsNullOrWhiteSpace(name)||name!=name.Trim()||name.Length>60||name.Any(char.IsControl))throw Invalid();
            string[] List(string key,string[] allowed)
            {
                var list=layout.GetProperty(key).EnumerateArray().Select(v=>v.GetString()??throw Invalid()).ToArray();
                if(list.Length>allowed.Length||list.Distinct().Count()!=list.Length||list.Any(v=>!allowed.Contains(v)))throw Invalid();
                return list;
            }
            foreach(var key in new[]{"bookIds","portfolioIds"})
            {
                var ids=layout.GetProperty(key).EnumerateArray().Select(v=>v.GetString()).ToArray();
                if(ids.Length>256||ids.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=ids.Length||ids.Any(v=>!Guid.TryParseExact(v,"D",out _)))throw Invalid();
            }
            var columns=List("columns",Columns);var dimensions=List("groupBy",Dimensions);var measures=List("measures",Measures);
            var mode=layout.GetProperty("mode").GetString();
            if(layout.GetProperty("sidebar").ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw Invalid();
            var rowGroup=layout.GetProperty("rowGroup");
            if(rowGroup.ValueKind is not JsonValueKind.Null&&(rowGroup.ValueKind!=JsonValueKind.String||!RowGroups.Contains(rowGroup.GetString())))throw Invalid();
            if(columns.Length==0||mode is not("trades" or "pivot")||mode=="trades"&&columns.Any(c=>c=="tradeCount"||TradingBlotter.PriceMeasures.ContainsKey(c)))throw Invalid();
            if(mode=="pivot"&&measures.Any(TradingBlotter.PriceMeasures.ContainsKey)&&!columns.Contains("priceUnit"))throw Invalid();
            if(mode=="pivot"&&(dimensions.Length==0||measures.Length==0||measures.Contains("lots")&&!dimensions.Contains("contract")||!columns.Contains("unit")||!columns.Contains("currency")||columns.Any(c=>!dimensions.Concat(measures).Concat(["instrumentType","unit","currency","priceUnit"]).Contains(c))))throw Invalid();
            var query=layout.GetProperty("query").GetString();if(query is null||query.Length>200)throw Invalid();
            var filters=layout.GetProperty("columnFilters");
            if(filters.ValueKind!=JsonValueKind.Object||filters.EnumerateObject().Count()>128||filters.EnumerateObject().Any(p=>!Field(p.Name)||p.Value.ValueKind!=JsonValueKind.String||p.Value.GetString()!.Length>200))throw Invalid();
            var split=List("splitBy",Dimensions);
            if(split.Any(d=>!dimensions.Contains(d))||split.Length>0&&(mode!="pivot"||!dimensions.Any(d=>!split.Contains(d))))throw Invalid();
            var grid=layout.GetProperty("grid");
            if(grid.ValueKind!=JsonValueKind.Object||grid.EnumerateObject().Count()!=3||grid.EnumerateObject().Any(p=>p.Name is not("order" or "widths" or "sort")))throw Invalid();
            var order=grid.GetProperty("order").EnumerateArray().Select(p=>p.GetString()??throw Invalid()).ToArray();
            if(order.Length>128||order.Distinct().Count()!=order.Length||order.Any(k=>!Field(k)))throw Invalid();
            var widths=grid.GetProperty("widths");
            if(widths.ValueKind!=JsonValueKind.Object||widths.EnumerateObject().Count()>128||widths.EnumerateObject().Any(p=>!Field(p.Name)||p.Value.ValueKind!=JsonValueKind.Number||!p.Value.TryGetInt32(out var width)||width is <40 or >3000))throw Invalid();
            var sort=grid.GetProperty("sort").EnumerateArray().ToArray();
            if(sort.Length>16||sort.Any(p=>p.ValueKind!=JsonValueKind.Object||p.EnumerateObject().Count()!=2||!Field(p.GetProperty("key").GetString()??"")||p.GetProperty("direction").GetString() is not("asc" or "desc"))||sort.Select(p=>p.GetProperty("key").GetString()).Distinct().Count()!=sort.Length)throw Invalid();
            var chart=layout.GetProperty("chart");
            if(chart.ValueKind!=JsonValueKind.Object||chart.EnumerateObject().Count()!=3||chart.EnumerateObject().Select(p=>p.Name).Distinct().Count()!=3||chart.GetProperty("visible").ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw Invalid();
            var chartMeasure=chart.GetProperty("measure").GetString();
            if(!Measures.Contains(chartMeasure!)||chart.GetProperty("kind").GetString() is not("bar" or "line" or "heatmap"))throw Invalid();
            if(chart.GetProperty("visible").GetBoolean()&&chartMeasure=="lots"&&mode=="pivot"&&!dimensions.Contains("contract"))throw Invalid();
            if(System.Text.Encoding.UTF8.GetByteCount(layout.GetRawText())>24000)throw Invalid();
        }
        catch(Exception e) when(e is InvalidOperationException or KeyNotFoundException or FormatException or OverflowException){throw Invalid();}
    }

    public static async Task<JsonElement[]> Read(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=new NpgsqlCommand("SELECT json_build_object('version',version,'layout',layout)::text FROM presentation.table_views WHERE table_key='blotter' ORDER BY name",connection);
        await using var reader=await command.ExecuteReaderAsync(ct);var rows=new List<JsonElement>();
        while(await reader.ReadAsync(ct))rows.Add(JsonSerializer.Deserialize<JsonElement>(reader.GetString(0)));
        return rows.ToArray();
    }

    private static async Task Lock(NpgsqlConnection connection,CancellationToken ct)
    {
        // Serialize this account's layout changes, including the bounded view count.
        await using(var guard=new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(tenant_id::text||workspace_id::text||principal_id::text||'blotter',0)) FROM security.table_view_identity()",connection))
        {
            await using var reader=await guard.ExecuteReaderAsync(ct);
            if(!await reader.ReadAsync(ct))throw new PreviewDeniedException();
        }
    }

    public static async Task<JsonElement> Save(NpgsqlConnection connection,SaveTableView request,CancellationToken ct)
    {
        Validate(request.Layout);
        await Lock(connection,ct);
        var name=request.Layout.GetProperty("name").GetString()!;
        var existing=await Read(connection,ct);
        var prior=existing.SingleOrDefault(v=>v.GetProperty("layout").GetProperty("name").GetString()==name);
        if(request.ExpectedVersion is null ? prior.ValueKind!=JsonValueKind.Undefined : prior.ValueKind==JsonValueKind.Undefined||prior.GetProperty("version").GetGuid()!=request.ExpectedVersion)
            throw new MarketDataException("table_view_conflict","This saved view changed. Refresh before saving or deleting it.");
        if(request.ExpectedVersion is null&&existing.Length>=20)throw new MarketDataException("table_view","Keep at most twenty named views per table.");
        await using var command=new NpgsqlCommand("""
            INSERT INTO presentation.table_views(tenant_id,workspace_id,principal_id,table_key,name,version,layout)
            SELECT tenant_id,workspace_id,principal_id,'blotter',$1,$2,$3 FROM security.table_view_identity()
            ON CONFLICT(tenant_id,workspace_id,principal_id,table_key,name) DO UPDATE SET version=excluded.version,layout=excluded.layout
            RETURNING json_build_object('version',version,'layout',layout)::text
            """,connection);
        command.Parameters.AddWithValue(name);command.Parameters.AddWithValue(Guid.NewGuid());command.Parameters.AddWithValue(NpgsqlDbType.Jsonb,request.Layout.GetRawText());
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct)??throw new PreviewDeniedException()));
    }

    public static async Task Delete(NpgsqlConnection connection,DeleteTableView request,CancellationToken ct)
    {
        await Lock(connection,ct);
        if(string.IsNullOrWhiteSpace(request.Name)||request.Name.Length>60)throw Invalid();
        await using var command=new NpgsqlCommand("DELETE FROM presentation.table_views WHERE table_key='blotter' AND name=$1 AND version=$2",connection);
        command.Parameters.AddWithValue(request.Name);command.Parameters.AddWithValue(request.ExpectedVersion);
        if(await command.ExecuteNonQueryAsync(ct)!=1)throw new MarketDataException("table_view_conflict","This saved view changed. Refresh before saving or deleting it.");
    }
}
