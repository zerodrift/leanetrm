using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal static class TradeAnnotations
{
    public static async Task<JsonElement> Actor(NpgsqlConnection connection,Guid catalog,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.booking_actor($1)::text",catalog);
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    }
    public static void RequireServerFacts(string? executed,Guid? trader,int? revision,string? date)
    {
        if(executed is not null||trader is not null||revision is not null||date is not null)
            throw new MarketDataException("trade_booking_read_only","Trader, trade date and execution time are assigned by the server and cannot be changed.");
    }
    public static void RequireFacts(ImmutableArray<JsonElement> rows)
    {
        if(rows.Any(row=>row.GetProperty("booker_id").ValueKind==JsonValueKind.Null))
            throw new MarketDataException("trade_booking","The original booking facts are unavailable in this context.");
    }
    public static object Facts(JsonElement row)=>new{implementation=row.Text("booking_implementation"),labelCapturedAt=row.Text("booker_label_captured_at")};
    public static void ValidateShape(Guid? trader,int? revision,string? memo)
    {
        if((trader is null)!=(revision is null)||memo?.Length>4000||memo?.Contains('\0')==true)
            throw new MarketDataException("trade_annotation","Select an exact trader version or leave attribution unassigned; memo is limited to 4000 characters.");
    }
    public static async Task Validate(NpgsqlConnection connection,Guid catalog,Guid? trader,int? revision,string? memo,DateTimeOffset executed,CancellationToken ct)
    {
        ValidateShape(trader,revision,memo);
        if(trader is null)return;
        var rows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(t)::text FROM trading.traders t WHERE catalog_id=$1 AND id=$2 AND revision=$3 AND effective_at<=$4",[catalog,trader.Value,revision!.Value,executed],ct);
        if(rows.Length!=1)throw new MarketDataException("trade_annotation","The selected trader version is unavailable at the trade's execution date and knowledge view.");
    }
    public static bool Same(JsonElement row,Guid? trader,int? revision,string? memo)=>
        row.OptionalId("trader_id")==trader&&row.OptionalText("trader_revision")==revision?.ToString(System.Globalization.CultureInfo.InvariantCulture)&&row.OptionalText("memo")==memo;
}
