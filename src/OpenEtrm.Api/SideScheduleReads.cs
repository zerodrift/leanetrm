using System.Text.Json.Serialization;
using System.Globalization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SideScheduleReadRequest([property:JsonRequired]TradePositionReadRequest Position,[property:JsonRequired]int? SideSlot,
 [property:JsonRequired]string? Contribution,[property:JsonRequired]int Offset,[property:JsonRequired]int Limit,string? Window=null);

public sealed partial class TradeReads
{
 public Task<object> SideSchedule(NpgsqlConnection connection,TradingView view,SideScheduleReadRequest request,CancellationToken ct)
 {
  if(request?.Position is not {} positionRequest)throw new MarketDataException("side_schedule_inspection","Select an exact position and financial context.");
  var selected=TradePositionSelection.From(positionRequest);
  if(request.Offset<0||request.Limit is <1 or >SideScheduleInspection.MaximumPageSize||request.SideSlot<0||request.SideSlot is null&&(request.Contribution is not null||request.Offset!=0))
   throw new MarketDataException("side_schedule_inspection","Select a Side and an explicit bounded page, or its Side directory.");
  return ReadBound(connection,view,async()=>
  {
   await RequireReadTrades(connection,ct);
   return await scheduleReader.Read(connection,view,selected,request,ct);
  },ct);
 }
}
