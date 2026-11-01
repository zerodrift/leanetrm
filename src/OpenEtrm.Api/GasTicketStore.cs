using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketDescriptionRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]string Product);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketFieldRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]TicketDraft Draft,[property:JsonRequired]string? Side,[property:JsonRequired]string Field);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketPreviewRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]TicketDraft Draft);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketBookRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]TicketDraft Draft,[property:JsonRequired]Guid OperationId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketTradeRequest([property:JsonRequired]FuturesReadRequest Context,[property:JsonRequired]Guid TradeId,[property:JsonRequired]Guid BindingId,[property:JsonRequired]int Revision);

// Deal ticket reads for physical gas: choices and lookups from the gas catalog under the read's
// context, and a booked composition trade as the draft that booked it.
public sealed class GasTicketStore(PreviewDatabase database)
{
 const string AmendReason="Gas amendments and physical settlement are not supported yet. Daily quantities are separate operational revisions.";
 const string CloseOutReason="Gas close-out is not supported yet.";
 static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 readonly PhysicalGasStore gas=new(database);
 readonly GasCompositionStore composition=new(database);
 readonly GasDailyQuantities daily=new(database);
 // A booked trade's schedule shows each day's quantity under the operations projection: its latest allocated, scheduled, confirmed or
 // nominated record, else the contract's. Amounts stay the contract's: recorded quantities do not revalue the trade.
 const string QuantityPolicy=GasDailyQuantitySelection.ProjectionPolicy;

 public static TicketDescription Description(string product)=>product==GasTicket.Description.Product?GasTicket.Description:
  throw new MarketDataException("ticket_input",$"No deal ticket describes product '{product}'.");

 // The catalog's books and parties are trading records under row security: the bound read context
 // shows only those known and effective in it, so historical choices list that context's revisions.
 async Task<JsonElement> Catalog(NpgsqlConnection c,PreviewData data,InputFrame frame,CancellationToken ct)=>JsonSerializer.SerializeToElement(await gas.CatalogRead(c,data,frame,ct),Json);

 public async Task<object> Choices(NpgsqlConnection c,PreviewData data,InputFrame frame,TicketFieldRequest body,CancellationToken ct)=>
  new{choices=GasTicket.Choices(body.Draft,body.Side,body.Field,await Catalog(c,data,frame,ct))};

 public async Task<object> Lookup(NpgsqlConnection c,PreviewData data,InputFrame frame,TicketFieldRequest body,CancellationToken ct)
 {
  var catalog=await Catalog(c,data,frame,ct);
  if(GasTicket.LookupOf(body.Draft,body.Side,body.Field)!="market_price")return GasTicket.Lookup(body.Draft,body.Side,body.Field,catalog);
  var price=await composition.MarketPrice(c,data,frame,GasTicket.PriceProbe(body.Draft,body.Side!,catalog,frame.Id,data.DefinitionRevision.RevisionId),ct);
  return GasTicket.MarketPrice(body.Draft,body.Side!,catalog,price,TradingView.WorkingDate(frame));
 }

 // The booked composition trade, selected by its exact binding and revision, as its ticket draft and schedule, with its quantity links.
 public async Task<object> Trade(NpgsqlConnection c,PreviewData data,InputFrame frame,TicketTradeRequest body,CancellationToken ct)
 {
  var selected=TradePositionSelection.From(new(body.Context,body.TradeId,body.BindingId,body.Revision,GasPositions.Type));
  var position=(await GasPositions.Read(c,database.Manifest,[],[],ct,selected)).Single();
  if(!position.Head.GetProperty("composition").GetBoolean())throw new MarketDataException("ticket_input","This single-point gas trade does not open in the deal ticket yet.");
  var days=(await daily.Select(c,frame,body.TradeId,QuantityPolicy,false,ct)).Days.Where(d=>d.Selected.RevisionId is not null);
  var read=await composition.Read(c,data,frame,body.TradeId,ct,days.GroupBy(d=>d.SideCode).ToDictionary(g=>g.Key,g=>g.ToImmutableDictionary(d=>d.Contract.Day,d=>(d.Selected.Quantity,d.Selected.Kind))));
  return new{draft=GasTicket.Reopen(read.Terms),schedule=read.Valuation.Schedule,summaries=read.Valuation.Summaries,scheduleRefusal=read.Valuation.ScheduleRefusal,quantityUnit=read.Valuation.QuantityUnit,
   links=read.Valuation.Links,quantityPolicy=QuantityPolicy,
   actions=new{amend=new{allowed=false,reason=AmendReason},closeOut=new{allowed=false,reason=CloseOutReason}}};
 }
}
