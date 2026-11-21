using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record ScheduleReadWindow(string Id,string Label);
internal sealed record BookedScheduleSide(int Slot,Guid SideId,string Family,string Manifest,ImmutableArray<ScheduleReadWindow> Windows);
// Product storage resolves an ordinary authorized position. The shared reader
// never interprets an instrument's terms, cashflows or pricing model.
internal interface IBookedScheduleSource
{
 ImmutableArray<string> InstrumentTypes {get;}
 Task<BookedSchedulePosition> Open(NpgsqlConnection connection,TradingView view,TradePositionSelection selected,CancellationToken ct);
}
internal sealed record BookedSchedulePosition(TradePositionSelection Position,Guid InstrumentId,ImmutableArray<BookedScheduleSide> Sides,
 Func<int,string?,CancellationToken,Task<SideScheduleSnapshot>> Evaluate,Func<CancellationToken,Task> Recheck);

internal sealed class BookedScheduleReader
{
 readonly ImmutableDictionary<string,IBookedScheduleSource> sources;
 internal BookedScheduleReader(IEnumerable<IBookedScheduleSource> sources)
 {
  var entries=sources.SelectMany(s=>s.InstrumentTypes.Select(type=>(Type:type,Source:s))).ToArray();
  if(entries.Any(e=>string.IsNullOrWhiteSpace(e.Type))||entries.Select(e=>e.Type).Distinct(StringComparer.Ordinal).Count()!=entries.Length)
   throw Error("Register each native position source once, under its exact instrument type.");
  this.sources=entries.ToImmutableDictionary(e=>e.Type,e=>e.Source,StringComparer.Ordinal);
 }
 // Whether an instrument type has a registered source, and so Side schedules to read.
 internal bool Covers(string instrumentType)=>sources.ContainsKey(instrumentType);
 internal async Task<object> Read(NpgsqlConnection connection,TradingView view,TradePositionSelection selected,SideScheduleReadRequest request,CancellationToken ct)
 {
  SideScheduleInspection.RequirePage(request.Offset,request.Limit);
  if(request.SideSlot is null&&(request.Contribution is not null||request.Offset!=0)||request.SideSlot<0)throw Error("Select an exact Side for a contribution or row page.");
  if(!sources.TryGetValue(selected.InstrumentType,out var source))throw new MarketDataException("side_schedule_capability","This instrument has no registered Side schedule capability.");
  var position=await source.Open(connection,view,selected,ct);ct.ThrowIfCancellationRequested();
  if(position.Position!=selected||position.InstrumentId==Guid.Empty||position.Sides.IsDefault||position.Sides.Select(s=>s.Slot).Distinct().Count()!=position.Sides.Length||
   position.Sides.Any(s=>s.Slot<0||s.SideId==Guid.Empty||string.IsNullOrWhiteSpace(s.Manifest)||string.IsNullOrWhiteSpace(s.Family)||s.Windows.IsDefault||s.Windows.Any(w=>string.IsNullOrWhiteSpace(w.Id)||string.IsNullOrWhiteSpace(w.Label))||s.Windows.Select(w=>w.Id).Distinct().Count()!=s.Windows.Length))
   throw Error("The native position must retain its exact binding and unambiguous Side membership directory.");
  object result;
  if(request.SideSlot is null)
  {
   if(request.Window is not null)throw Error("Select a Side before a delivery window.");
   result=new{selected.TradeId,selected.BindingId,selected.Revision,contextId=view.Context.ContextId,snapshotId=view.CalculationFrame.Id,sides=position.Sides};
  }
  else
  {
   var side=position.Sides.SingleOrDefault(s=>s.Slot==request.SideSlot)??throw Error("The selected native Side is unavailable.");
   if(side.Windows.IsEmpty?request.Window is not null:!side.Windows.Any(w=>w.Id==request.Window))throw Error("Select an explicit supported delivery window for this Side.");
   var values=await position.Evaluate(side.Slot,request.Window,ct);ct.ThrowIfCancellationRequested();
   if(values.SideId!=side.SideId||values.Manifest!=side.Manifest||values.SnapshotId!=view.CalculationFrame.Id||values.ContextId!=view.Context.ContextId||
    values.ValuationAt!=view.CalculationFrame.At||values.KnownAt!=SchedulePreview.ParseAt(view.Context.KnownAt))
    throw Error("The evaluated Side belongs to another exact membership, manifest, snapshot or resolved context.");
   object page=request.Contribution is null?values.Read(request.Offset,request.Limit,ct):values.Calculation(request.Contribution,request.Offset,request.Limit,ct);
   if(page is ScheduleInspectionPage inspected)
   {
    var labels=await ReferenceDisplay.Read(connection,inspected.Rows.Where(r=>r.PriceUnit is not null).Select(r=>r.PriceUnit!),ct,
     inspected.Rows.Where(r=>r.Currency is not null).Select(r=>r.Currency!.Value).Concat(inspected.Rows.Where(r=>r.QuantitySpecification is not null&&r.QuantityReferenceKind==ScheduleReferenceKinds.Currency).Select(r=>r.QuantitySpecification!.Value)),inspected.Rows.Where(r=>r.QuantitySpecification is not null&&r.QuantityReferenceKind==ScheduleReferenceKinds.Quantity).Select(r=>r.QuantitySpecification!.Value));
    page=inspected with{Rows=[..inspected.Rows.Select(r=>r with{PriceUnitLabel=r.PriceUnit is {} unit?labels.Price(unit):null,
     CurrencyLabel=r.Currency is {} currency?labels.Currency(currency):null,QuantityLabel=r.QuantitySpecification is {} quantity?labels.Label(quantity,r.QuantityReferenceKind):null})]};
   }
   if(page is ScheduleCalculationPage calculation)
   {
    PriceUnit? Unit(ScheduleCalculationRow r)=>r.Currency is {} c&&r.Unit is {} q&&r.QuoteMultiplier is {} m?new(c,q,ExactDecimal.Parse(m)):null;
    var labels=await ReferenceDisplay.Read(connection,calculation.Rows.Select(Unit).OfType<PriceUnit>(),ct,
     calculation.Rows.Where(r=>r.Currency is not null).Select(r=>r.Currency!.Value).Concat(calculation.Rows.Where(r=>r.Unit is not null&&r.UnitReferenceKind==ScheduleReferenceKinds.Currency).Select(r=>r.Unit!.Value)),calculation.Rows.Where(r=>r.Unit is not null&&r.UnitReferenceKind==ScheduleReferenceKinds.Quantity).Select(r=>r.Unit!.Value));
    page=calculation with{Rows=[..calculation.Rows.Select(r=>r with{ValueUnitLabel=Unit(r) is {} u?labels.Price(u):r.Currency is {} c?labels.Currency(c):r.Unit is {} q?labels.Label(q,r.UnitReferenceKind):null})]};
   }
   result=new{selected.TradeId,selected.BindingId,selected.Revision,contextId=view.Context.ContextId,snapshotId=view.CalculationFrame.Id,request.SideSlot,request.Window,side.SideId,side.Manifest,page};
  }
  await position.Recheck(ct);ct.ThrowIfCancellationRequested();return result;
 }
 static MarketDataException Error(string message)=>new("side_schedule_inspection",message);
}
