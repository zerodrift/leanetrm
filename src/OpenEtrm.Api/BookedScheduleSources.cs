using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class BookedScheduleSources
{
 internal static IEnumerable<IBookedScheduleSource> Create(PreviewDatabase database)=>[
  new ProgramPositionSource(database,[GasPositions.Type],async(c,selected,ct)=>{
   var p=(await GasPositions.Read(c,database.Manifest,[],[],ct,selected)).Single();return p.Binding;
  },async(c,view,selected,slot,side,ct)=>{
   var source=await PhysicalMarketSelection.Gas.Read(database,c,view.CalculationFrame,ct,selected.TradeId);var store=new GasCompositionStore(database);
   var p=(await GasPositions.Read(c,database.Manifest,[],[],ct,selected)).Single();
   var execution=p.Head.GetProperty("composition").GetBoolean()?await store.Load(c,source.Data,source.Frame,selected.TradeId,ct):await store.LoadSinglePoint(c,source.Data,source.Frame,selected.TradeId,ct);
   if(execution.InstrumentId!=selected.BindingId)throw TradePositionSelection.Unavailable();
   return await store.InspectSchedule(c,source.Data,source.Frame,execution,slot,ct);
  }),
  new ProgramPositionSource(database,[CrudePositions.Type],async(c,selected,ct)=>{
   _=(await CrudePositions.Read(c,database.Manifest,[],[],ct,selected)).Single();return selected.BindingId;
  },async(c,view,selected,slot,side,ct)=>{
   var source=await PhysicalMarketSelection.Crude.Read(database,c,view.CalculationFrame,ct,selected.TradeId);SideScheduleSnapshot? result=null;
   await new PhysicalCrudeStore(database).Editor(c,source.Data,source.Frame,selected.TradeId,ct,(at,input)=>{
    if(at==slot)result=SideScheduleSnapshot.FromProgram(side.SideId,source.Frame.Id,input,Utc);
   },slot);return result??throw Missing();
  }),
  new ProgramPositionSource(database,[LngPositions.Type],async(c,selected,ct)=>{
   _=(await LngPositions.Read(c,database.Manifest,[],[],ct,selected)).Single();return selected.BindingId;
  },async(c,view,selected,slot,side,ct)=>{
   var source=await PhysicalMarketSelection.Lng.Read(database,c,view.CalculationFrame,ct,selected.TradeId);SideScheduleSnapshot? result=null;
   await new PhysicalLngStore(database).Editor(c,source.Data,source.Frame,selected.TradeId,ct,(at,input)=>{
    if(at==slot)result=SideScheduleSnapshot.FromProgram(side.SideId,source.Frame.Id,input,Utc);
   },slot);return result??throw Missing();
  }),
  new SwapScheduleSource(database),new PpaScheduleSource(database)
 ];
 internal static DateTimeOffset Utc(DateOnly day)=>new(day.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
 internal static MarketDataException Missing()=>new("side_schedule_inspection","The selected native Side produced no schedule values.");
 internal static ImmutableArray<SideScheduleSnapshot> Inspect(InstrumentValuation value,ImmutableArray<BookedScheduleSide> headers,ExactDecimal quantity)
 {
  var ordered=headers.OrderBy(s=>s.Slot).ToArray();
  if(!ordered.Select(s=>s.Slot).SequenceEqual(Enumerable.Range(0,value.Sides.Length)))throw Missing();
  return value.InspectSchedules([..ordered.Select(s=>new SideScheduleIdentity(s.SideId,s.Manifest))],quantity,Utc);
 }
 internal static void RequireSame(ImmutableArray<BookedScheduleSide> expected,ImmutableArray<BookedScheduleSide> actual)
 {
  if(expected.Length!=actual.Length||expected.Zip(actual).Any(p=>p.First.Slot!=p.Second.Slot||p.First.SideId!=p.Second.SideId||p.First.Manifest!=p.Second.Manifest||p.First.Family!=p.Second.Family||!p.First.Windows.SequenceEqual(p.Second.Windows)))throw TradePositionSelection.Unavailable();
 }
 internal static ImmutableArray<BookedScheduleSide> Headers(ImmutableArray<JsonElement> headers,ImmutableArray<ScheduleReadWindow> windows)=>
  [..headers.Select(h=>new BookedScheduleSide(h.GetProperty("slot").GetInt32(),h.Id("side_id"),h.Text("family"),h.Text("manifest"),windows))];

 sealed class ProgramPositionSource(PreviewDatabase database,ImmutableArray<string> types,
  Func<NpgsqlConnection,TradePositionSelection,CancellationToken,Task<Guid>> authorize,
  Func<NpgsqlConnection,TradingView,TradePositionSelection,int,BookedScheduleSide,CancellationToken,Task<SideScheduleSnapshot>> evaluate):IBookedScheduleSource
 {
  public ImmutableArray<string> InstrumentTypes=>types;
  public async Task<BookedSchedulePosition> Open(NpgsqlConnection c,TradingView view,TradePositionSelection selected,CancellationToken ct)
  {
   var instrument=await authorize(c,selected,ct);var store=new RetainedScheduleStore(database);
   async Task<ImmutableArray<BookedScheduleSide>> Directory(CancellationToken token)=>Headers(await store.Directory(c,instrument,view.CalculationFrame.ContextId,view.CalculationFrame.At,token),[]);
   var headers=await Directory(ct);
   async Task<SideScheduleSnapshot> Evaluate(int slot,string? window,CancellationToken token)
   {
    var side=headers.Single(s=>s.Slot==slot);
    try{return await evaluate(c,view,selected,slot,side,token);}
    catch(MarketDataException error)when(ValuationStatus.PositionSpecific(error)||error.Code is "crude_lifecycle" or "lng_delivery_lifecycle" or "side_payment_due")
    {
     var program=(await store.LoadPrograms(c,instrument,view.CalculationFrame.ContextId,view.CalculationFrame.At,token)).Single(p=>p.Slot==slot);
     return SideScheduleSnapshot.Plan(program.SideId,view.CalculationFrame.Id,view.CalculationFrame.ContextId!.Value,$"side_{slot}",program.Program,Utc,view.CalculationFrame.At,SchedulePreview.ParseAt(view.Context.KnownAt),error.Code);
    }
   }
   async Task Recheck(CancellationToken token)
   {
    if(await authorize(c,selected,token)!=instrument)throw TradePositionSelection.Unavailable();
    RequireSame(headers,await Directory(token));
   }
   return new(selected,instrument,headers,Evaluate,Recheck);
  }
 }
}
