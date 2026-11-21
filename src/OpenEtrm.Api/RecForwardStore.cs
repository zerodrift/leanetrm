using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

public sealed record RecForwardPreviewResult(Guid SnapshotId,Guid? ContextId,DateTimeOffset KnownAt,string Convention,string ProductCode,
 string VintageFrom,string VintageTo,string DeliveryFrom,string DeliveryTo,ExactQuantity Quantity,StructuredPowerValuationResult Valuation,ImmutableArray<string> Limitations);
public sealed record RecForwardBookingReceipt(Guid OperationId,Guid TradeId,int Revision,Guid InstrumentId,string TradeReference,string InstrumentReference,bool Changed);
public sealed record RecForwardBookedSide(int Slot,Guid SideId,Guid ScheduleId,string Name,string Coefficient,string Manifest);
public sealed record RecForwardBookedTrade(Guid TradeId,int Revision,Guid InstrumentId,Guid BookId,string TradeReference,RecForwardBookRequest Request,ImmutableArray<RecForwardBookedSide> Sides)
{public ImmutableArray<InstrumentSideGroup> SideGroups{get;init;}=[];public RetainedSideSource? RetainedSource{get;init;}}
public sealed record RecForwardPrepared(RecForwardBookRequest Request,RecForwardInstrument Instrument,RecForwardPreviewResult Preview,DefinitionRevision Definitions,ReferenceRevision References)
{
 internal RetainedScheduleTerms Retained=>new(Definitions,References,Instrument);
}

public sealed partial class RecForwardStore(PreviewDatabase database)
{
 public const string Convention="rec_forward_v1";
 private Guid Catalog=>database.Manifest.CatalogId;
 private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 private static MarketDataException Invalid()=>new("rec_forward","Select one exact certificate product/vintage assignment, future UTC delivery, positive certificate count and explicit independent consideration.");
 private static ExactDecimal Number(string text)
 {try{return ExactDecimal.Parse(text);}catch(Exception e)when(e is FormatException or OverflowException or ArgumentException){throw Invalid();}}
 private static Guid Identity(object value)=>new(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value,Json))).AsSpan(0,16));
 private static PaymentRule Payment(PpaPaymentInput p)
 {
  if(p is null||p.Frequency!="monthly"||p.Rule!="days_after_period_end"||p.Days is not >=0 or >366||p.DayOfMonth is not null||p.DayBasis!="calendar_days"||p.ShortMonth is not null||p.Adjustment!="unadjusted"||p.Calendar is not null||p.Implementation!=PaymentRule.Implementation)throw Invalid();
  return new(PaymentFrequency.Monthly,PaymentDateRule.DaysAfterPeriodEnd,p.Days,null,PaymentDayBasis.CalendarDays,null,DateRoll.Unadjusted,null,p.Implementation);
 }
 private static void Context(FuturesReadRequest context)
 {if(context is null||context.SnapshotId==Guid.Empty||context.Convention!=Convention||context.KnowledgeMode is not("current" or "retained")||context.KnowledgeMode=="current"&&context.ContextId is not null)throw Invalid();}
 private async Task<(RecForwardInstrument Instrument,RecForwardPreviewResult Preview)> Prepare(NpgsqlConnection connection,PreviewData data,InputFrame frame,DateTimeOffset knownAt,RecForwardTermsInput t,Guid identity,CancellationToken ct)
 {
  if(t is null||t.DefinitionRevisionId!=data.DefinitionRevision.RevisionId||frame.ContextId is null||t.Discounting||t.Consideration is null)throw Invalid();
  var from=TradingInstant.Parse(t.DeliveryFrom,"rec_forward");var to=TradingInstant.Parse(t.DeliveryTo,"rec_forward");
  if(from.Hour!=0||from.Minute!=0||from.Second!=0||from.Ticks%TimeSpan.TicksPerDay!=0||to!=from.AddDays(1)||from<=frame.At)throw new MarketDataException("rec_delivery_lifecycle","Select one complete unstarted UTC day of certificate delivery.");
  var vintageFrom=DateWorkspace.Date(t.VintageFrom);var vintageTo=DateWorkspace.Date(t.VintageTo);var count=Number(t.Quantity);
  if(count.CompareTo(default)<=0||count.Power(1).Denominator!=1||count.Power(1).Numerator>long.MaxValue)throw Invalid();
  var physical=t.PhysicalDirection switch{"receive"=>1,"deliver"=>-1,_=>throw Invalid()};
  var cash=t.ConsiderationDirection switch{"receive"=>1,"pay"=>-1,_=>throw Invalid()};if(cash!=-physical)throw Invalid();
  var rows=await PreviewDatabase.Rows(connection,"""
   SELECT row_to_json(r)::text FROM(SELECT a.*,c.quote_code,p.quantity_code,q.unit_code FROM market_data.rec_valuation_assignments a
    JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(a.catalog_id,a.revision_id,a.curve_code)
    JOIN ref_data.rec_products p ON(p.catalog_id,p.revision_id,p.code)=(a.reference_catalog_id,a.reference_revision_id,a.product_code)
    JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(p.catalog_id,p.revision_id,p.quantity_code)
    WHERE(a.catalog_id,a.revision_id,a.code)=($1,$2,$3))r
   """,[data.DefinitionRevision.CatalogId,t.DefinitionRevisionId,t.AssignmentCode],ct);
  if(rows.Length!=1)throw new MarketDataException("rec_mark","Select one authorized exact product/vintage valuation assignment.");
  var assignment=rows[0];
  if(assignment.Text("product_code")!=t.ProductCode||assignment.Text("vintage_from")!=t.VintageFrom||assignment.Text("vintage_to")!=t.VintageTo||
   assignment.Id("reference_catalog_id")!=data.ReferenceRevision.CatalogId||assignment.Id("reference_revision_id")!=data.ReferenceRevision.RevisionId||assignment.Text("unit_code")!="rec")throw Invalid();
  var quantityKey=new ReferenceKey(data.ReferenceRevision,assignment.Text("quantity_code"));var unit=new Quantity(ExactDecimal.Parse("1"),quantityKey);
  var marks=data.QuoteRows(assignment.Text("quote_code")).ToArray();if(marks.Length!=1)throw Invalid();var mark=marks[0];if(mark.Quantity!=quantityKey||mark.Kind!=QuoteKind.Outright)throw Invalid();
  CurveRequest Projection(string curve,Guid contract,string quote)
  {
   var requests=data.Requests(frame,curve).Where(r=>r.Contract.Id==contract).ToArray();var definitions=data.DefinitionRows("curves","code",curve).ToArray();var contracts=data.ContractRows(contract).ToArray();
   if(requests.Length!=1||definitions.Length!=1||definitions[0].Text("quote_code")!=quote||contracts.Length!=1||contracts[0].DeliveryFrom>DateOnly.FromDateTime(from.UtcDateTime)||contracts[0].DeliveryTo<DateOnly.FromDateTime(to.UtcDateTime))throw new MarketDataException("rec_mark","The explicitly selected dated price contract must cover the independent physical delivery window.");
   return requests[0];
  }
  var projection=Projection(assignment.Text("curve_code"),assignment.Id("contract_id"),mark.Key.Code);
  var consideration=data.QuoteRows(t.Consideration.QuoteCode).ToArray();if(consideration.Length!=1||consideration[0].Kind!=QuoteKind.Outright||consideration[0].Quantity!=quantityKey||consideration[0].Currency!=mark.Currency)throw Invalid();
  var quote=consideration[0];var calendar=SideCalendarPolicies.Resolve(data,t.CalendarPolicy);calendar.RequirePurposes(false,false,false);
  var payment=Payment(t.PaymentRule);var day=DateOnly.FromDateTime(from.UtcDateTime);var month=new DateOnly(day.Year,day.Month,1);
  var interval=new DeliveryInterval(from,to,day,0,0,1);
  OperatingSideTerms Terms(QuoteSpecification selected,ComponentKind kind,CurveRequest? request)=>new(unit,selected,kind==ComponentKind.Payment?payment:null,kind,
   [new(interval,month,month.AddMonths(1),new(null,unit),request)],OperatingSideTerms.SupportedImplementation,calendar);
  var delivery=new FloatingSide(Identity(new{identity,slot=0}),Terms(mark,ComponentKind.Delivery,projection));Side money;
  if(t.Consideration.Mode=="fixed"&&t.Consideration.FixedPrice is {} price&&t.Consideration.CurveCode is null&&t.Consideration.ContractId is null)
   money=new FixedSide(Identity(new{identity,slot=1}),Terms(quote,ComponentKind.Payment,null),new FlatPrice(new(Number(price),quote)));
  else if(t.Consideration.Mode=="selected_contract_flat_v1"&&t.Consideration.FixedPrice is null&&t.Consideration.CurveCode is {} curve&&t.Consideration.ContractId is {} contract)
   money=new FloatingSide(Identity(new{identity,slot=1}),Terms(quote,ComponentKind.Payment,Projection(curve,contract,quote.Key.Code)));
  else throw Invalid();
  var instrument=new RecForwardInstrument(identity,new(data.ReferenceRevision,t.ProductCode),vintageFrom,vintageTo,[new(delivery,ExactDecimal.Parse(physical.ToString(CultureInfo.InvariantCulture))),new(money,ExactDecimal.Parse(cash.ToString(CultureInfo.InvariantCulture)))]);
  var snapshot=data.Snapshot(frame).WithKnowledge(frame.ContextId.Value,knownAt);var context=new SideValuationContext(data.Model.Build(snapshot,instrument.Dependencies),new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
  var valuation=instrument.PV(context);var sides=valuation.Sides.Select((s,index)=>{
   var v=(OperatingSideValuation)s.Value;var resultRows=v.Rows.Select(row=>new StructuredPowerSideRow(row.Interval!,row.ValueAt,row.Quantity.Scale(count),row.PriceComponents[0].Price,row.Amount.Scale(count),row.RequirePresentValue().Scale(count),VolumeState.Contractual,null,null){PriceUnitLabel=data.UnitLabel(row.PriceComponents[0].Price.Unit)}).ToImmutableArray();
   return new StructuredPowerSideResult(s.SideId,index==0?"physical_delivery":"consideration",s.Coefficient.ToString(),index==0?mark.Key:quote.Key,v.PresentValue.Scale(count),s.Contribution.Scale(count),resultRows);
  }).ToImmutableArray();
  var value=new StructuredPowerValuationResult("rec_contractual_present_value","trade_holder",mark.Currency,new(count,quantityKey),valuation.PresentValue.Scale(count),sides,valuation.NumericalPolicy);
  return(instrument,new(frame.Id,frame.ContextId,knownAt,Convention,t.ProductCode,t.VintageFrom,t.VintageTo,from.ToString("O"),to.ToString("O"),ExactQuantity.FromQuantity(new(count,quantityKey)),value,
   ["Future contractual certificate delivery and consideration only; inventory allocation, manual transfer and retirement evidence are separate.","Explicitly undiscounted; started delivery and cash lifecycle are not yet admitted."]));
 }
 public async Task<RecForwardPreviewResult> Preview(NpgsqlConnection c,PreviewData data,InputFrame frame,DateTimeOffset knownAt,RecForwardPreviewRequest request,CancellationToken ct)
 {Context(request.Context);return(await Prepare(c,data,frame,knownAt,request.Terms,Identity(new{data.DefinitionRevision,request.Terms}),ct)).Preview;}
 public async Task<RecForwardPrepared> PrepareBook(NpgsqlConnection c,PreviewData data,InputFrame frame,DateTimeOffset knownAt,RecForwardBookRequest request,CancellationToken ct)
 {
  Context(request.Context);if(request.Context.KnowledgeMode!="current"||request.OperationId==Guid.Empty||request.TradeId==Guid.Empty||request.BookId==Guid.Empty)throw Invalid();
  await RequireOwner(c,request.OwnerId,ct);_=DateWorkspace.Date(request.TradeDate);
  await TradeParties.Validate(c,Catalog,TradeParties.Payload(request.TradeDate,request.LegalEntityId,request.LegalEntityRevision,request.CounterpartyId,request.CounterpartyRevision),frame.At,ct);
  var prepared=await Prepare(c,data,frame,knownAt,request.Terms,Identity(new{Catalog,request.TradeId}),ct);
  return new(request,prepared.Instrument,prepared.Preview,data.DefinitionRevision,data.ReferenceRevision);
 }
 public async Task RequireOwner(NpgsqlConnection c,string owner,CancellationToken ct)
 {
  await using var query=ScheduleStore.Command(c,"SELECT security.rec_trade_owner($1)",Catalog);
  if(string.IsNullOrEmpty(owner)||owner!=(string)(await query.ExecuteScalarAsync(ct))!)throw new MarketDataException("rec_owner_changed","Return to the original trading account before recovering this booking.");
 }
 public async Task<RecForwardBookingReceipt?> Status(NpgsqlConnection c,RecForwardStatusRequest request,CancellationToken ct)
 {await RequireOwner(c,request.OwnerId,ct);return await Receipt(c,"SELECT security.rec_forward_booking_status($1,$2)::text",ct,Catalog,request.OperationId);}
 public Task<RecForwardBookingReceipt?> RequestStatus(NpgsqlConnection c,RecForwardBookRequest request,CancellationToken ct)=>Receipt(c,"SELECT security.rec_forward_booking_replay($1,$2::jsonb)::text",ct,Catalog,JsonSerializer.Serialize(request,Json));
 public async Task<RecForwardBookingReceipt> Save(NpgsqlConnection c,InputFrame frame,RecForwardBookRequest request,RecForwardPrepared prepared,CancellationToken ct)
 {
  if(JsonSerializer.Serialize(request,Json)!=JsonSerializer.Serialize(prepared.Request,Json))throw Invalid();
  var metadata=new{instrumentId=prepared.Instrument.Id,members=new[]{new{slot=0,name="physical_delivery"},new{slot=1,name="consideration"}}};
  return await Receipt(c,"SELECT security.book_rec_forward($1,$2::jsonb,$3::jsonb,$4)::text",ct,Catalog,JsonSerializer.Serialize(request,Json),JsonSerializer.Serialize(metadata,Json),frame.At)??throw Invalid();
 }
 private static async Task<RecForwardBookingReceipt?> Receipt(NpgsqlConnection c,string sql,CancellationToken ct,params object[] values)
 {await using var query=ScheduleStore.Command(c,sql,values);return await query.ExecuteScalarAsync(ct) is string text?JsonSerializer.Deserialize<RecForwardBookingReceipt>(text,Json):null;}
}
