using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed class SwapScheduleSource(PreviewDatabase database):IBookedScheduleSource
{
 public ImmutableArray<string> InstrumentTypes=>["financial_swap","heat_rate"];
 public async Task<BookedSchedulePosition> Open(NpgsqlConnection c,TradingView view,TradePositionSelection selected,CancellationToken ct)
 {
  var store=new SwapStore(database);var schedules=new RetainedScheduleStore(database);
  async Task<System.Text.Json.JsonElement> Head(CancellationToken token)
  {
   var rows=await store.Rows(c,selected.TradeId,token,latestBooks:[]);if(rows.IsEmpty)throw TradePositionSelection.Unavailable();
   var row=rows[^1];selected.Require([row]);
   var type=row.Text("implementation")==CrossCommodityScheduledSwap.Implementation?"heat_rate":"financial_swap";
   if(type!=selected.InstrumentType)throw TradePositionSelection.Unavailable();return row;
  }
  var head=await Head(ct);var instrument=head.Id("instrument_id");
  async Task<ImmutableArray<BookedScheduleSide>> Directory(CancellationToken token)=>BookedScheduleSources.Headers(await schedules.Directory(c,instrument,view.CalculationFrame.ContextId,view.CalculationFrame.At,token),[]);
  var headers=await Directory(ct);
  async Task<SideScheduleSnapshot> Evaluate(int slot,string? window,CancellationToken token)
  {
   var data=await database.Read(c,token,observationIds:view.CalculationFrame.ObservationIds,referenceScope:ReferenceReadScope.TradeValuation,observationScope:ObservationReadScope.ExactPins);
   return await store.InspectSchedule(c,data,view.CalculationFrame,head,headers,slot,token);
  }
  async Task Recheck(CancellationToken token)
  {
   if((await Head(token)).Id("instrument_id")!=instrument)throw TradePositionSelection.Unavailable();
   BookedScheduleSources.RequireSame(headers,await Directory(token));
  }
  return new(selected,instrument,headers,Evaluate,Recheck);
 }
}
