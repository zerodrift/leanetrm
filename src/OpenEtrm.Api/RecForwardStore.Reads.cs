using System.Collections.Immutable;
using System.Globalization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

public sealed partial class RecForwardStore
{
 public async Task<RecForwardBookedTrade> ReadExact(NpgsqlConnection c,InputFrame frame,Guid trade,int revision,CancellationToken ct)
 {
  if(frame.ContextId is null||trade==Guid.Empty||revision<=0)throw Invalid();
  var positions=await RecPositions.Read(c,database.Manifest,[],[],ct,trade:trade);
  if(positions.Length!=1||int.Parse(positions[0].Head.Text("revision"),CultureInfo.InvariantCulture)!=revision)throw TradePositionSelection.Unavailable();
  var p=positions[0];var programs=await new RetainedScheduleStore(database).LoadPrograms(c,p.Binding,frame.ContextId,frame.At,ct);
  var members=p.Head.GetProperty("members").EnumerateArray().ToArray();
  if(programs.Length!=2||members.Length!=2)throw Invalid();
  var sides=programs.OrderBy(s=>s.Slot).Select(s=>{
   var member=members.Single(m=>int.Parse(m.Text("slot"),CultureInfo.InvariantCulture)==s.Slot);
   if(member.Id("sideId")!=s.SideId)throw Invalid();
   return new RecForwardBookedSide(s.Slot,s.SideId,s.ScheduleId,s.Slot==0?"physical_delivery":"consideration",member.Text("coefficient"),s.Program.Document.ScheduleManifest);
  }).ToImmutableArray();
  return new(p.Trade,revision,p.Binding,p.Book.Id("id"),TradingReference.Trade(p.Head),p.Terms(),sides){SideGroups=[RetainedInstrumentSidePresentation.Rec(p,programs)]};
 }
 public async Task<RecForwardPreviewResult> Value(NpgsqlConnection c,InputFrame frame,Guid trade,int revision,CancellationToken ct)
 {
  var positions=await RecPositions.Read(c,database.Manifest,[],[],ct,trade:trade);
  if(positions.Length!=1||int.Parse(positions[0].Head.Text("revision"),CultureInfo.InvariantCulture)!=revision)throw TradePositionSelection.Unavailable();
  var p=positions[0];var source=await MarketInputSelection.Resolve(database,c,frame,new(p.Definition,p.Reference.CatalogId,p.Reference.RevisionId),[],false,ct);
  var context=(await new KnowledgeResolver(Catalog).Read(c,frame.ContextId!.Value,ct)).Context;var knownAt=SchedulePreview.ParseAt(context.KnownAt);
  var value=await EvaluatePrograms(c,source.Data,source.Frame,p,ct);var t=p.Terms().Terms;
  return new(frame.Id,frame.ContextId,knownAt,Convention,t.ProductCode,t.VintageFrom,t.VintageTo,t.DeliveryFrom,t.DeliveryTo,ExactQuantity.FromQuantity(new(p.TotalQuantity,p.Quantity)),value,
   ["Retained contractual delivery and consideration schedules; certificate fulfillment evidence is a separate inventory operation.","Explicitly undiscounted; started delivery and cash lifecycle are not yet admitted."]);
 }
 internal async Task<StructuredPowerValuationResult> EvaluatePrograms(NpgsqlConnection c,PreviewData data,InputFrame frame,RecPosition p,CancellationToken ct,Action<int,SideViewInput>? inspect=null,int? selectedSlot=null)
 {
  var programs=await new RetainedScheduleStore(database).LoadPrograms(c,p.Binding,frame.ContextId,frame.At,ct);
  if(programs.Length!=2)throw Invalid();
  var context=(await new KnowledgeResolver(Catalog).Read(c,frame.ContextId!.Value,ct)).Context;
  var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(frame),frame.ContextId.Value,SchedulePreview.ParseAt(context.KnownAt));
  var chosen=selectedSlot is {} selection?programs.Where(p=>p.Slot==selection).ToArray():programs.ToArray();
  if(chosen.Length!=(selectedSlot is null?2:1))throw Invalid();
  var dependencies=chosen.SelectMany(s=>s.Program.Document.Instructions).SelectMany(i=>i.Bindings).Where(b=>b.Kind=="contract_price").Select(b=>b.Market!.Value).Distinct().ToImmutableArray();
  var market=data.Model.Build(snapshot,dependencies,ct);var discounting=new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted);
  var sideContext=SideValuationContext.ForRetainedPrograms(market,market,discounting,ct);
  var members=p.Head.GetProperty("members").EnumerateArray().ToArray();var sides=ImmutableArray.CreateBuilder<StructuredPowerSideResult>();var total=ExactMoney.FromMoney(new(default,p.Currency));
  foreach(var program in chosen.OrderBy(s=>s.Slot))
  {
   var member=members.Single(m=>int.Parse(m.Text("slot"),CultureInfo.InvariantCulture)==program.Slot);if(member.Id("sideId")!=program.SideId)throw Invalid();
   var name=program.Slot==0?"physical_delivery":"consideration";var sign=ExactDecimal.Parse(member.Text("coefficient"));
   var concrete=RetainedProgramSide.ForOperating(program.SideId,program.Program,new(ExactDecimal.Parse("1"),p.Quantity),p.Currency);
   var value=concrete.PV(sideContext).Execution;
   inspect?.Invoke(program.Slot,new(name,value,program.Program,p.TotalQuantity,new(sign.Power(1).Numerator,sign.Power(1).Denominator),[]));
   var rows=ImmutableArray.CreateBuilder<StructuredPowerSideRow>();ReferenceKey? quote=null;
   foreach(var guard in program.Program.Document.Instructions.Where(i=>i.Output.Operation=="output_guard"))
   {
    var amount=program.Program.Document.Instructions[guard.Inputs["amount"][0]];var quantity=value.Values[amount.Inputs["quantity"][0]].Scenario;var price=value.Values[amount.Inputs["price"][0]].Scenario;
    var cash=value.Values[amount.Output.Ordinal].Scenario;var pv=value.Values[guard.Output.Ordinal].Scenario;
    var q=ExactQuantity.FromQuantity(new(ExactDecimal.Parse("1"),quantity.Unit!.Value)).Scale(new ExactRatio(quantity.Values[0].Numerator,quantity.Values[0].Denominator)).Scale(p.TotalQuantity);
    var priceUnit=new PriceUnit(price.Currency!.Value,price.Unit!.Value,ExactDecimal.Parse(price.QuoteMultiplier!));
    var quoteCode=program.Slot==0?p.Product.Text("quote_code"):p.Terms().Terms.Consideration.QuoteCode;quote=new(p.Reference,quoteCode);
    rows.Add(new(guard.Output.Interval!,guard.Output.ValueAt!.Value,q,ExactUnitPrice.Parse(price.Values[0].Numerator+"/"+price.Values[0].Denominator,priceUnit),
     ExactMoney.Parse(cash.Values[0].Numerator+"/"+cash.Values[0].Denominator,p.Currency).Scale(p.TotalQuantity),ExactMoney.Parse(pv.Values[0].Numerator+"/"+pv.Values[0].Denominator,p.Currency).Scale(p.TotalQuantity),VolumeState.Contractual,null,null){PriceUnitLabel=data.UnitLabel(priceUnit)});
   }
   var gross=value.PresentValue.Scale(p.TotalQuantity);var contribution=gross.Scale(sign);total=total.Add(contribution);
   sides.Add(new(program.SideId,name,sign.ToString(),quote??throw Invalid(),gross,contribution,rows.ToImmutable()));
  }
  return new(RecPositions.Measure,"trade_holder",p.Currency,new(p.TotalQuantity,p.Quantity),total,sides.ToImmutable(),BookedSideEvaluation.NumericalPolicy);
 }
}
