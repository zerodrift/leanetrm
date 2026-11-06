using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed class PpaScheduleSource(PreviewDatabase database):IBookedScheduleSource
{
 public ImmutableArray<string> InstrumentTypes=>["ppa"];
 public async Task<BookedSchedulePosition> Open(NpgsqlConnection c,TradingView view,TradePositionSelection selected,CancellationToken ct)
 {
  var frame=view.CalculationFrame;var manifest=database.Manifest;
  var store=new PpaStore(database,new TouProductStore(manifest.CatalogId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId));
  async Task<JsonElement> Binding(CancellationToken token)
  {
   var scope=await PpaPortfolio.Scope(c,manifest.CatalogId,frame,view.Context,[],token,trades:[selected.TradeId]);selected.Require(scope.Bindings);
   return scope.Bindings.Single(b=>b.Id("binding_id")==selected.BindingId);
  }
  var binding=await Binding(ct);var instrument=binding.Id("instrument_id");
  var terms=(await store.StoredTerms(c,instrument,ct)).GetProperty("terms").Deserialize<PpaTermsInput>(PpaStore.Json)!;
  var from=PpaCompiler.Day(terms.Delivery.From);var to=PpaCompiler.Day(terms.Delivery.To);
  var windows=ImmutableArray.CreateBuilder<ScheduleReadWindow>();
  for(var month=new DateOnly(from.Year,from.Month,1);month<to;month=month.AddMonths(1))
  {
   if(windows.Count>=360)throw PpaCompiler.Error("ppa_limit","The delivery window directory exceeds the admitted term.");
   windows.Add(new(month.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),month.ToString("MMM yyyy",CultureInfo.InvariantCulture)));
  }
  var schedules=new RetainedScheduleStore(database);
  async Task<ImmutableArray<BookedScheduleSide>> Directory(CancellationToken token)=>BookedScheduleSources.Headers(await schedules.Directory(c,instrument,frame.ContextId,frame.At,token),windows.ToImmutable());
  var headers=await Directory(ct);
  async Task<SideScheduleSnapshot> Evaluate(int slot,string? window,CancellationToken token)
  {
   var side=headers.Single(s=>s.Slot==slot);SideScheduleSnapshot? result=null;
   var known=SchedulePreview.ParseAt(view.Context.KnownAt).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",CultureInfo.InvariantCulture);
   var request=new PpaPreviewRequest(frame.Id,terms.DefinitionRevisionId,terms.ReferenceCatalogId,terms.ReferenceRevisionId,"trades",[selected.TradeId],true,null,null,null,null,known,PpaBlotter.PendingActuals,[window!]);
   await store.Preview(c,frame,request,token,new(request,selected.BindingId,side.SideId,0,1),frame.ContextId,(id,value,quantity)=>
   {
    if(id!=selected.BindingId)throw TradePositionSelection.Unavailable();
    result=BookedScheduleSources.Inspect(value,headers,quantity)[slot];
   });
   return result??throw BookedScheduleSources.Missing();
  }
  async Task Recheck(CancellationToken token)
  {
   if((await Binding(token)).Id("instrument_id")!=instrument)throw TradePositionSelection.Unavailable();
   await store.Recheck(c,frame,token);
   BookedScheduleSources.RequireSame(headers,await Directory(token));
  }
  return new(selected,instrument,headers,Evaluate,Recheck);
 }
}
