using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class BookedScheduleReaderChecks
{
 sealed class Values(Guid side,TradingView view):SideScheduleSnapshot(side,view.Frame.Id,view.Context.ContextId!.Value,"synthetic-manifest",view.Frame.At,SchedulePreview.ParseAt(view.Context.KnownAt))
 {
  public override ScheduleInspectionPage Read(int offset,int limit,CancellationToken ct=default)=>new(SideId.ToString(),Manifest,ValuationAt,KnownAt,offset,2,[]);
  public override ScheduleCalculationPage Calculation(string contribution,int offset,int limit,CancellationToken ct=default)=>new(SideId.ToString(),Manifest,contribution,"unit_side_v1","1","1","1",offset,0,[]);
 }
 sealed class Source:IBookedScheduleSource
 {
  public ImmutableArray<string> InstrumentTypes=>["synthetic_unlisted_product"];
  internal readonly Guid Side=Guid.NewGuid();internal int Evaluations,Rechecks;internal bool Revoked,WrongContext;
  public Task<BookedSchedulePosition> Open(NpgsqlConnection c,TradingView view,TradePositionSelection selected,CancellationToken ct)
  {
   Task<SideScheduleSnapshot> Evaluate(int slot,string? window,CancellationToken token)
   {
    Evaluations++;
    var bound=WrongContext?new TradingView(view.Frame,view.Context with{ContextId=Guid.NewGuid()},false):view;
    return Task.FromResult<SideScheduleSnapshot>(new Values(Side,bound));
   }
   Task Recheck(CancellationToken token){Rechecks++;if(Revoked)throw new PreviewDeniedException();return Task.CompletedTask;}
   return Task.FromResult(new BookedSchedulePosition(selected,Guid.NewGuid(),[new(7,Side,"synthetic","synthetic-manifest",[new("opaque-period","Synthetic period")])],Evaluate,Recheck));
  }
 }
 internal static async Task Run()
 {
  var frame=new InputFrame(Guid.NewGuid(),"Synthetic schedule reader",new(2040,12,18,0,0,0,TimeSpan.Zero),[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty);
  var view=new TradingView(frame,new("2040-12-18",1,"2040-12-19T00:00:00Z",Guid.NewGuid()),false);
  var position=new TradePositionReadRequest(new(frame.Id,TradingBlotter.Convention,"retained",view.Context.ContextId),Guid.NewGuid(),Guid.NewGuid(),3,"synthetic_unlisted_product");
  var selected=TradePositionSelection.From(position);var source=new Source();var reader=new BookedScheduleReader([source]);using var connection=new NpgsqlConnection();
  var request=new SideScheduleReadRequest(position,null,null,0,8);
  JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,new JsonSerializerOptions(JsonSerializerDefaults.Web));
  var directory=Json(await reader.Read(connection,view,selected,request,default));
  if(source.Evaluations!=0||source.Rechecks!=1||directory.GetProperty("sides")[0].GetProperty("slot").GetInt32()!=7)throw new Exception("The shared directory must discover a new product lazily through its native registration.");
  var page=Json(await reader.Read(connection,view,selected,request with{SideSlot=7,Window="opaque-period",Offset=1},default));
  if(source.Evaluations!=1||page.GetProperty("page").GetProperty("offset").GetInt32()!=1)throw new Exception("The shared reader must page an unlisted instrument without a product branch.");
  await reader.Read(connection,view,selected,request with{SideSlot=7,Window="opaque-period",Contribution="native-key"},default);
  async Task Refused(SideScheduleReadRequest input,string code)
  {
   try{await reader.Read(connection,view,selected,input,default);}catch(MarketDataException error)when(error.Code==code){return;}
   throw new Exception("The generic reader must refuse an ambiguous or mismatched exact schedule selection.");
  }
  await Refused(request with{SideSlot=7},"side_schedule_inspection");
  await Refused(request with{SideSlot=7,Window="not-retained"},"side_schedule_inspection");
  await Refused(request with{SideSlot=8},"side_schedule_inspection");
  source.WrongContext=true;await Refused(request with{SideSlot=7,Window="opaque-period"},"side_schedule_inspection");source.WrongContext=false;
  source.Revoked=true;try{await reader.Read(connection,view,selected,request with{SideSlot=7,Window="opaque-period"},default);throw new Exception("Publication must recheck current authority.");}catch(PreviewDeniedException){}
  Console.WriteLine("PASS shared schedule reader: new registered product, lazy directory, native slot and window, paging, detail, exact context and revoked authority");
 }
}
