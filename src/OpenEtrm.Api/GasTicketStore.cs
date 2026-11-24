using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketDescriptionRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]string Product,
 [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]TicketDraft? Draft=null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketFieldRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]TicketDraft Draft,[property:JsonRequired]string? Side,[property:JsonRequired]string Field);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketPreviewRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]TicketDraft Draft);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketBookRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]TicketDraft Draft,[property:JsonRequired]Guid DefinitionRevisionId,[property:JsonRequired]Guid OperationId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketTradeRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]Guid TradeId,[property:JsonRequired]Guid BindingId,[property:JsonRequired]int Revision)
{public string? Product{get;init;}};

// Deal ticket reads for physical gas: choices and lookups from the gas catalog under the read's
// context, and a booked trade as the ticket draft of its terms.
public sealed class GasTicketStore(PreviewDatabase database)
{
 const string CloseOutReason="Gas close-out is not supported yet.";
 const string RecordReason="A day's quantity is recorded as an operational revision; amounts stay the contract's.";
 const string FollowedRecordReason="Scheduled and allocated quantities drive value on sides set to Delivery schedule; each day shows its selected source.";
 const string SinglePointRecordReason="Daily quantities are recorded on composed gas trades; this single-point trade shows its contract quantities.";
 static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 readonly PhysicalGasStore gas=new(database);
 readonly GasCompositionStore composition=new(database);
 readonly GasDailyQuantities daily=new(database);
 // A composed trade's schedule shows each day's quantity under the operations projection: its latest allocated, scheduled, confirmed or
 // nominated record, else the contract's. Amounts stay the contract's: recorded quantities do not revalue the trade.
 const string QuantityPolicy=GasDailyQuantitySelection.ProjectionPolicy;

 // An owned retry may express old terms with the new visible fields. Compare the
 // complete mapped command before restoring its original representation; changed
 // terms continue to the native command hash and conflict there.
 internal async Task<GasCompositionRequest> RetriedRequest(string profile,TicketDraft draft,Guid operation,bool amendment,
  Func<TicketDraft,bool,GasCompositionRequest> map,CancellationToken ct)
 {
  GasTicket.RequireShape(draft);
  var permission=amendment?"AmendTrade":"BookTrade";
  var command=await database.WithIdentity(profile,permission,async(connection,token)=>{
   await using var q=ScheduleStore.Command(connection,"SELECT security.gas_ticket_retained_command($1,$2,$3)::text",database.Manifest.CatalogId,operation,permission);
   return await q.ExecuteScalarAsync(token) as string;
  },ct);
  if(command is null)return map(draft,false);
  using var document=JsonDocument.Parse(command);
  var retained=document.RootElement.GetProperty("request");
  var original=retained.ValueKind==JsonValueKind.Null?null:retained.Deserialize<GasCompositionRequest>(Json)!;
  var candidate=map(GasTicket.RetryDraft(draft,original),original is not null);
  return original is not null&&GasTicket.SameTerms(candidate,original)?original:candidate;
 }

 public static TicketDescription Description(string product,TicketDraft? draft=null)
 {
  if(draft is not null&&draft.Product!=product)throw new MarketDataException("ticket_input","The description draft must belong to the explicitly requested product.");
  return product==GasTransportFinancialTicket.Product?GasTransportFinancialTicket.Describe(draft):product==GasParkLoanTicket.Product?GasParkLoanTicket.Describe(draft):product==GasTicket.Description.Product?GasTicket.Description:
   throw new MarketDataException("ticket_input",$"No deal ticket describes product '{product}'.");
 }

 // The catalog's books and parties are trading records under row security: the bound read context
 // shows only those known and effective in it, so historical choices list that context's revisions.
 async Task<JsonElement> Catalog(NpgsqlConnection c,PreviewData data,InputFrame frame,CancellationToken ct)=>JsonSerializer.SerializeToElement(await gas.CatalogRead(c,data,frame,ct),Json);

 public async Task<object> Choices(NpgsqlConnection c,PreviewData data,InputFrame frame,TicketFieldRequest body,CancellationToken ct)
 {
  // The fixed assigned-curve fallback still requires this exact source admission.
  if(body.Field is "forecast_curve" or "floating_forecast_curve")
  {
   var probe=GasTicket.PriceProbe(body.Draft,body.Side!,await Catalog(c,data,frame,ct),frame.Id,data.DefinitionRevision.RevisionId);
   var physical=probe.PhysicalSides.Single();var metadata=await gas.Metadata(c,data,GasCompositionStore.Selection(probe,physical),ct);
   var from=DateWorkspace.Date(physical.DeliveryFrom);var to=DateWorkspace.Date(physical.DeliveryTo);
   var days=Enumerable.Range(0,to.DayNumber-from.DayNumber).Select(n=>new ProjectionDelivery(from.AddDays(n),from.AddDays(n+1)));
   var choices=await CustomCurves.ProjectionChoices(c,database,data,frame,ct);
   var eligible=choices.Where(choice=>choice.Reference.Selection==CustomCurveProjectionBinding.ContainingDeliverySelection&&
    choice.Unit==PriceUnit.FromQuote(metadata.Assignments[0].Quote)&&choice.Covers(days)).ToArray();
   if((body.Field=="forecast_curve"||body.Draft.Sides.Single(s=>s.Code==body.Side).Values.GetValueOrDefault("floating_pricing")=="point_publications"&&metadata.Mapping.GetProperty("settlement_periods").GetArrayLength()>0)&&GasPointPricing.Configured(database.Manifest.CatalogId,metadata.Mapping) is {} configured)
   {
    var selected=eligible.SingleOrDefault(choice=>choice.Reference==configured)??throw new MarketDataException("gas_location_curve_unavailable","The point's exact pricing curve must be visible and cover this delivery term in the required price unit.");
    return GasTicket.ChoiceSet(body.Draft,body.Side,body.Field,[new TicketChoice(GasTicket.ForecastValue(selected.Reference),selected.Label)]);
   }
   return GasTicket.ChoiceSet(body.Draft,body.Side,body.Field,[GasTicket.AssignedForecastChoice,..eligible.Select(choice=>new TicketChoice(GasTicket.ForecastValue(choice.Reference),choice.Label))]);
  }
  if(body.Field is "floating_pricing" or "floating_index")
  {
   var catalog=await Catalog(c,data,frame,ct);
   return GasTicket.ChoiceSet(body.Draft,body.Side,body.Field,GasTicket.Choices(body.Draft,body.Side,body.Field,catalog),catalog);
  }
  ImmutableArray<TicketChoice>? independent=GasTicket.LocalChoices(body.Draft,body.Side,body.Field);
  if(independent is null&&body.Side is null)
  {
   if(body.Field=="gas_setup")
   {
    var setup=await gas.CurrentSetup(c,data,ct);independent=GasTicket.SetupChoices(setup.Id,setup.Publication);
   }
   else independent=body.Field switch{
    "book"=>GasTicket.BookChoices(await gas.Books(c,ct)),
    "legal_entity"=>GasTicket.PartyChoices(await TradeParties.Catalog(c,database.Manifest.CatalogId,true,ct)),
    "counterparty"=>GasTicket.PartyChoices(await TradeParties.Catalog(c,database.Manifest.CatalogId,false,ct)),
    _=>null};
  }
  if(independent is {} localChoices)return GasTicket.ChoiceSet(body.Draft,body.Side,body.Field,localChoices);
  return GasTicket.ChoiceSet(body.Draft,body.Side,body.Field,GasTicket.Choices(body.Draft,body.Side,body.Field,
   GasTicket.PhysicalReferenceField(body.Draft,body.Side,body.Field)?JsonSerializer.SerializeToElement(await gas.PhysicalReferences(c,data,ct),Json):await Catalog(c,data,frame,ct)));
 }

 public async Task<object> Lookup(NpgsqlConnection c,PreviewData data,InputFrame frame,TicketFieldRequest body,CancellationToken ct)
 {
  if(GasTicketStartingValues.Retained(body) is {} retained)return retained;
  var catalog=await Catalog(c,data,frame,ct);
  var lookup=GasTicket.LookupOf(body.Draft,body.Side,body.Field);
  if(lookup is not null&&lookup.StartsWith("entry_",StringComparison.Ordinal))
  {
   if(lookup!="entry_price")return GasTicketStartingValues.Value(data,frame,body,catalog);
   var side=body.Draft.Sides.Single(s=>s.Code==body.Side);
   if(side.Values.TryGetValue(body.Field,out var current))return new TicketLookup(current,new("suggestion","Retained price"));
   var source=GasTicketStartingValues.Source(body.Draft,side);
   var probe=GasTicket.PriceProbe(body.Draft,source.Code,catalog,frame.Id,data.DefinitionRevision.RevisionId);
   var metadata=await gas.Metadata(c,data,GasCompositionStore.Selection(probe,probe.PhysicalSides[0]),ct);
   var market=await composition.MarketPrice(c,data,frame,probe,ct);
   return new TicketLookup(GasTicketStartingValues.Price(market,metadata.Assignments[0].Quote.TradingTick),new("suggestion","Suggested market price at quote precision"));
  }
  if(GasTicket.LookupOf(body.Draft,body.Side,body.Field)!="market_price")return GasTicket.Lookup(body.Draft,body.Side,body.Field,catalog);
  var price=await composition.MarketPrice(c,data,frame,GasTicket.PriceProbe(body.Draft,body.Side!,catalog,frame.Id,data.DefinitionRevision.RevisionId),ct);
  return GasTicket.MarketPrice(body.Draft,body.Side!,catalog,price,TradingView.WorkingDate(frame));
 }

 // The booked gas trade, selected by its exact binding and revision, as its ticket draft and schedule, with its quantity links. A composed
 // trade's schedule shows its recorded day quantities; a single-point trade opens as the one-physical-side composition it is, and records none.
 public async Task<object> Trade(NpgsqlConnection c,PreviewData data,InputFrame frame,TicketTradeRequest body,bool working,CancellationToken ct)
 {
  var selected=TradePositionSelection.From(new(body.Context,body.TradeId,body.BindingId,body.Revision,GasPositions.Type));
  var position=(await GasPositions.Read(c,database.Manifest,[],[],ct,selected)).Single();
  var composed=position.Head.GetProperty("composition").GetBoolean();
  GasCompositionRead read;
  if(composed)
  {
   var selection=await daily.Select(c,frame,body.TradeId,QuantityPolicy,false,ct);var days=selection.Days.Where(d=>d.Selected.RevisionId is not null);
   read=await composition.Read(c,data,frame,body.TradeId,ct,days.GroupBy(d=>d.SideCode).ToDictionary(g=>g.Key,g=>g.ToImmutableDictionary(d=>d.Contract.Day,d=>(d.Selected.Quantity,d.Selected.Kind))),selection.Programs);
  }
  else read=await composition.ReadSinglePoint(c,data,frame,body.TradeId,ct);
  var refused=composed?await daily.RecordRefusal(c,working,ct):null;
  var facilityAllocations=composed?await new FacilityQuantityStore(database).TradeAllocations(c,body.TradeId,ct):JsonSerializer.SerializeToElement(Array.Empty<object>(),Json);
  return new{snapshotId=frame.Id,draft=GasTicket.Reopen(read.Terms),definitionRevisionId=read.Terms.DefinitionRevisionId,schedule=read.Valuation.Schedule,summaries=read.Valuation.Summaries,scheduleRefusal=read.Valuation.ScheduleRefusal,quantityUnit=read.Valuation.QuantityUnit,
   measures=read.Valuation.Measures,currency=read.Valuation.Currency,links=read.Valuation.Links,sideSlots=read.Valuation.SideSlots,quantityPolicy=composed?(read.Terms.PhysicalSides.Any(p=>p.QuantityPolicy is not null)?GasDailyQuantitySelection.ValuationPolicy:QuantityPolicy):null,facilityAllocations,
   actions=new{amend=GasTicket.Retired(read.Terms)?new GasAmendmentAction(false,"This retained strict daily index form is read-only. Book a new trade using named index pricing."):await GasAmendments.Action(c,database.Manifest.CatalogId,body.TradeId,body.BindingId,body.Revision,frame.At,working,ct),closeOut=new{allowed=false,reason=CloseOutReason},record=new{allowed=composed&&refused is null,reason=!composed?SinglePointRecordReason:refused?.Message??(read.Terms.PhysicalSides.Any(p=>p.QuantityPolicy is not null)?FollowedRecordReason:RecordReason)}}};
 }
}
