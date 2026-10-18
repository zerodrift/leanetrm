using System.Text.Json.Serialization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DealAmountFormat([property:JsonRequired]int Money,[property:JsonRequired]int Price,[property:JsonRequired]int Quantity,[property:JsonRequired]int Factor)
{
 public static DealAmountFormat Default=>new(3,4,3,6);
 public void Validate(){foreach(var places in new[]{Money,Price,Quantity,Factor})_=new AmountFormat(places,CashTieRule.ToEven,true,false);}
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DealFormatRead(FuturesReadRequest Context,Guid TradeId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DealFormatSave(Guid TradeId,string Role,int ExpectedRevision,DealAmountFormat Format);
public static class DealAmountFormats
{
 public sealed record Selection(string Role,int Revision,DealAmountFormat Format);
 public static async Task<Dictionary<Guid,Dictionary<string,Selection>>> All(NpgsqlConnection connection,Guid catalog,CancellationToken ct)
 {
  var rows=await PreviewDatabase.Rows(connection,"SELECT row_to_json(f)::text FROM presentation.trade_amount_formats f WHERE catalog_id=$1",[catalog],ct);
  int Number(JsonElement row,string name)=>int.Parse(row.Text(name),System.Globalization.CultureInfo.InvariantCulture);
  Selection Select(JsonElement row)=>new(row.Text("role"),Number(row,"revision"),new(Number(row,"money"),Number(row,"price"),Number(row,"quantity"),Number(row,"factor")));
  return rows.GroupBy(r=>r.Id("trade_id")).ToDictionary(g=>g.Key,g=>g.ToDictionary(r=>r.Text("role"),Select));
 }
 public static DealAmountFormat Resolve(Dictionary<string,Selection>? rows,string role)=>rows?.GetValueOrDefault(role)?.Format??DealAmountFormat.Default;
 public static async Task<object> Read(NpgsqlConnection connection,Guid catalog,Guid trade,CancellationToken ct)
 {
  var visible=await PreviewDatabase.Rows(connection,"SELECT json_build_object('id',id)::text FROM trading.trades WHERE catalog_id=$1 AND id=$2",[catalog,trade],ct);
  if(visible.Length!=1)throw new MarketDataException("amount_format","Choose a visible deal.");
  var formats=(await All(connection,catalog,ct)).GetValueOrDefault(trade);
  return new{implementation="deal_amount_display_v1",scope="current_deal_presentation",roles=new[]{"trade","fixed","floating"}.Select(role=>formats?.GetValueOrDefault(role)??new Selection(role,0,DealAmountFormat.Default))};
 }
 public static async Task<object> Save(NpgsqlConnection connection,Guid catalog,DealFormatSave body,CancellationToken ct)
 {
  if(body.Format is null||body.TradeId==Guid.Empty)throw new MarketDataException("amount_format","Choose a deal and explicit display precision.");
  body.Format.Validate();
  await using var command=ScheduleStore.Command(connection,"SELECT security.save_trade_amount_format($1,$2,$3,$4,$5,$6,$7,$8)",catalog,body.TradeId,body.Role,body.ExpectedRevision,body.Format.Money,body.Format.Price,body.Format.Quantity,body.Format.Factor);
  return new{revision=(int)(await command.ExecuteScalarAsync(ct))!,body.Role,body.Format};
 }
}
