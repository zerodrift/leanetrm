using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapPaymentInput(string? Frequency,string? Rule,int? Days,int? DayOfMonth,string? DayBasis,string? ShortMonth,string? Adjustment,string? Calendar,string? Implementation);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapFixedInput(string? Price,string? Currency,string? DeliveryFrom,string? DeliveryTo,string? PaymentAt=null,SwapPaymentInput? PaymentSchedule=null);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapFloatingInput(string? Curve,string? Index,string? Currency,string? DeliveryFrom,string? DeliveryTo,string? FirstFixingAt=null,string? PaymentAt=null,string? Averaging=null,SwapPaymentInput? PaymentSchedule=null,string? ResetFrequency=null,string? AveragingMethod=null);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapWriteRequest(Guid SnapshotId,string? Convention,string? KnowledgeMode,Guid OperationId,Guid BookId,string? ExecutedAt,
 string? Quantity,string? FixedCoefficient,bool? Discounting,SwapFixedInput? Fixed,SwapFloatingInput? Floating,
 Guid? TradeId=null,int? ExpectedRevision=null,string? EffectiveAt=null,string? Reason=null,Guid DefinitionRevisionId=default,string? Commodity=null,Guid? TraderId=null,int? TraderRevision=null,string? Memo=null,string? TradeDate=null,Guid? LegalEntityId=null,int? LegalEntityRevision=null,Guid? CounterpartyId=null,int? CounterpartyRevision=null);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapEditorRequest(FuturesReadRequest Context,Guid TradeId,int? Revision);

public sealed partial class SwapStore(PreviewDatabase database)
{
 private PreviewManifest manifest=>database.Manifest;
 public const string Convention="independent_side_financial_swap_v2";
 private static MarketDataException Error(string code,string text)=>new(code,text);
 private static string Required(string? value)=>!string.IsNullOrWhiteSpace(value)?value:throw Error("swap_input","Supply every selected swap term explicitly.");
 private static DateOnly Date(string? value)=>DateWorkspace.Date(Required(value));
 private static string Date(DateOnly value)=>value.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);
 private ReferenceKey R(string code)=>new(new(manifest.ReferenceCatalogId,manifest.ReferenceRevisionId),code);
 private static T One<T>(IEnumerable<T> source)
 {var rows=source.Take(2).ToArray();return rows.Length==1?rows[0]:throw Error("swap_definition","An exact side reference is missing or ambiguous.");}
 private static string Exact(ExactMoney value)
 {
  try{return value.ToMoney().Amount.ToString();}
  catch(MarketDataException e)when(e.Code is "precision_loss" or "numeric_range"){return value.Numerator+"/"+value.Denominator;}
 }
 private static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,new JsonSerializerOptions(JsonSerializerDefaults.Web));
 private PaymentRule Payment(PreviewData data,SwapPaymentInput input)
 {
  PaymentCalendar? calendar=null;
  if(input.Calendar is {} code)
  {
   var row=One(data.References["calendars"].Where(c=>c.Text("code")==code));
   var from=Date(row.Text("coverage_from"));var to=Date(row.Text("coverage_to"));
   if(to.DayNumber-from.DayNumber>PaymentRule.MaximumCalendarDays)throw Error("payment_calendar","The selected calendar exceeds supported coverage.");
   var exceptions=data.References["calendar_days"].Where(c=>c.Text("calendar_code")==code).ToDictionary(c=>Date(c.Text("business_date")),c=>c.GetProperty("is_business_day").GetBoolean());
   var mask=int.Parse(row.Text("weekend_mask"),CultureInfo.InvariantCulture);
   calendar=new(R(code),DateSet.Create(from,to,Enumerable.Range(from.DayNumber,to.DayNumber-from.DayNumber).Select(DateOnly.FromDayNumber)
    .Where(d=>exceptions.TryGetValue(d,out var open)?open:(mask&(1<<(((int)d.DayOfWeek+6)%7)))==0)));
  }
  return new(SwapPaymentOptions.Parse(SwapPaymentOptions.Frequencies,input.Frequency),SwapPaymentOptions.Parse(SwapPaymentOptions.Rules,input.Rule),
   input.Days,input.DayOfMonth,input.DayBasis is null?null:SwapPaymentOptions.Parse(SwapPaymentOptions.DayBases,input.DayBasis),
   input.ShortMonth is null?null:SwapPaymentOptions.Parse(SwapPaymentOptions.ShortMonths,input.ShortMonth),
   SwapPaymentOptions.Parse(SwapPaymentOptions.Adjustments,input.Adjustment),calendar,Required(input.Implementation));
 }
 private static string Sampling(SwapFloatingInput input)=>input.Averaging is {} legacy&&input.ResetFrequency is null&&input.AveragingMethod is null?legacy:
  input.Averaging is null?(input.ResetFrequency,input.AveragingMethod) switch{("monthly","none")=>"monthly_index_price",("daily","arithmetic")=>"calendar_day_arithmetic",_=>throw Error("swap_averaging","Choose compatible reset frequency and averaging method.")}:throw Error("swap_averaging","Supply one explicit sampling definition.");
 private void RequireWriteRules(PreviewData data,SwapWriteRequest body)
 {
  if(body.Fixed?.PaymentAt is not null||body.Floating?.PaymentAt is not null||body.Fixed?.PaymentSchedule is null||body.Floating?.PaymentSchedule is null||
   body.Floating.Averaging is not null||string.IsNullOrWhiteSpace(body.Commodity))
   throw Error("swap_input","Select commodity, independent payment rules, reset frequency and averaging method; payment dates are generated by the backend.");
  if(body.Floating.FirstFixingAt is not null)throw Error("swap_fixing_read_only","Fixing dates come from the selected index convention and cannot be supplied as trade terms.");
  _=One(data.References["commodities"].Where(c=>c.Text("code")==body.Commodity));
 }
 private (FinancialSwapInstrument Instrument,Quantity Quantity) Resolve(PreviewData data,SwapWriteRequest body,Guid? instrumentId=null,Guid? fixedId=null,Guid? floatingId=null)
 {
  if(body.Convention!=Convention||body.KnowledgeMode!="current"&&body.KnowledgeMode!="retained"||body.Discounting is null||body.Fixed is null||body.Floating is null)
   throw Error("swap_input","Select explicit side terms, quantity, direction and discounting policy.");
  var a=body.Fixed??throw Error("swap_input","Select a fixed side.");var b=body.Floating??throw Error("swap_input","Select a floating side.");
  if(body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)throw Error("swap_definition","Select the exact supported definition revision.");
  var averaging=Sampling(b) switch {"monthly_index_price"=>FloatingAveraging.MonthlyIndexPrice,"calendar_day_arithmetic"=>FloatingAveraging.CalendarDayArithmetic,_=>throw Error("swap_averaging","Select an available monthly-index or daily-observation averaging rule.")};
  var curve=One(data.Definitions["curves"].Where(c=>c.Text("code")==b.Curve));
  var target=One(data.References["settlement_targets"].Where(t=>t.Text("code")==curve.Text("settlement_target_code")));
  var quote=One(data.Quotes.Where(q=>q.Key.Code==curve.Text("quote_code")));
  if(body.Commodity is not null&&body.Commodity!=curve.Text("commodity_code")||target.Text("kind")!="index"||target.Text("index_a_code")!=b.Index||quote.Kind!=QuoteKind.Outright||a.Currency!=quote.Currency.Code||b.Currency!=quote.Currency.Code)
   throw Error("swap_definition","The selected curve, index, currency and exact unit must agree. Currency conversion is unsupported.");
  var from=Date(b.DeliveryFrom);var to=Date(b.DeliveryTo);
  var ids=data.Definitions["curve_members"].Where(m=>m.Text("curve_code")==b.Curve).Select(m=>m.Id("contract_id")).ToHashSet();
  if(from.Day!=1||from.Year==9999||from.AddMonths(1)!=to)throw Error("swap_projection","This booking model selects one complete delivery month per instrument.");
  var selected=data.Contracts.Where(c=>ids.Contains(c.Key.Id)&&c.Kind==ContractKind.Forward&&
   (averaging==FloatingAveraging.MonthlyIndexPrice?c.DeliveryFrom==from&&c.DeliveryTo==to:from<=c.DeliveryFrom&&c.DeliveryFrom<to&&c.DeliveryTo.DayNumber-c.DeliveryFrom.DayNumber==1)).OrderBy(c=>c.DeliveryFrom).ToArray();
  if(averaging==FloatingAveraging.MonthlyIndexPrice)_=One(selected);
  var unit=new Quantity(ExactDecimal.Parse("1"),quote.Quantity);
  var fixedPrice=new UnitPrice(ExactDecimal.Parse(Required(a.Price)),PriceUnit.FromQuote(quote));
  var fixedSide=a.PaymentSchedule is {} fixedRule?
   new FixedSide(fixedId??Guid.NewGuid(),fixedPrice,unit,Date(a.DeliveryFrom),Date(a.DeliveryTo),Payment(data,fixedRule),FixedSide.Implementation,quote):
   new FixedSide(fixedId??Guid.NewGuid(),fixedPrice,unit,Date(a.DeliveryFrom),Date(a.DeliveryTo),SchedulePreview.ParseAt(a.PaymentAt),FixedSide.Implementation,quote);
  var projections=selected.Select(c=>new SwapProjection(new(new(data.DefinitionRevision,Required(b.Curve)),c.Key),c)).ToImmutableArray();
  // Only retained legacy definitions carry a manual boundary. New entry resolves
  // the exact immutable observation schedule from the selected index bundle.
  var index=R(Required(b.Index));var sideId=floatingId??Guid.NewGuid();
  var floating=b.FirstFixingAt is {} legacyBoundary?
   b.PaymentSchedule is {} legacyRule?
    new FloatingSide(sideId,index,quote,unit,from,to,SchedulePreview.ParseAt(legacyBoundary),Payment(data,legacyRule),averaging,projections,FloatingSide.Implementation):
    new FloatingSide(sideId,index,quote,unit,from,to,SchedulePreview.ParseAt(legacyBoundary),SchedulePreview.ParseAt(b.PaymentAt),averaging,projections,FloatingSide.Implementation):
   b.PaymentSchedule is {} floatingRule?
    new FloatingSide(sideId,index,quote,unit,from,to,Fixings(data,index,quote,projections),Payment(data,floatingRule),averaging,projections,FloatingSide.IndexedImplementation):
    throw Error("payment_rule","New index-based swaps require an explicit payment convention.");
  var coefficient=ExactDecimal.Parse(Required(body.FixedCoefficient));
  var instrument=new FinancialSwapInstrument(instrumentId??Guid.NewGuid(),[new(fixedSide,coefficient),new(floating,ExactDecimal.Parse("0")-coefficient)],Convention,body.Discounting.Value);
  var quantity=new Quantity(ExactDecimal.Parse(Required(body.Quantity)),quote.Quantity);
  if(quantity.Amount.CompareTo(default)<=0)throw Error("swap_quantity","Trade quantity must be positive; each membership specifies pay/receive direction.");
  return(instrument,quantity);
 }
 private async Task<CurrencyDiscountCurve?> Discount(NpgsqlConnection connection,PreviewData data,InputFrame frame,ReferenceKey currency,CancellationToken ct)
 {
  await using var command=ScheduleStore.Command(connection,"SELECT security.swap_discount_default($1,$2,$3,$4,$5,$6)::text",manifest.CatalogId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId,currency.Code,data.DefinitionRevision.RevisionId,frame.At);
  if(await command.ExecuteScalarAsync(ct) is not string text)return null;
  var row=JsonSerializer.Deserialize<JsonElement>(text);
  if(row.Id("definition_revision_id")!=data.DefinitionRevision.RevisionId)throw Error("discount_curve","The configured currency default has a different exact definition revision.");
  return new(row.Id("id"),currency,data.RateDefinition(row.Text("curve_code")),row.Text("interpolation_policy"));
 }
 private async Task<FinancialSwapEvaluation> Evaluate(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,
  Guid? instrumentId,Guid? fixedId,Guid? floatingId,CancellationToken ct,ImmutableArray<(PreviewData Data,InputFrame Frame)>? markets=null)
 {
  if(body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)
  {
   var selected=(markets??await database.ReadSwapMarkets(connection,frame,ct,data)).Where(m=>m.Data.DefinitionRevision.RevisionId==body.DefinitionRevisionId).ToArray();
   if(selected.Length!=1)throw Error("swap_definition","The selected exact swap market is unavailable in this historical context.");
   data=selected[0].Data;frame=selected[0].Frame;
  }
  var terms=Resolve(data,body,instrumentId,fixedId,floatingId);var snapshot=data.Snapshot(frame);
  var market=data.Model.Build(snapshot,terms.Instrument.DependenciesAt(snapshot));
  var mapping=body.Discounting==true?await Discount(connection,data,frame,terms.Instrument.Currency,ct):null;
  return FinancialSwapValuation.Evaluate(terms.Instrument,market,body.Discounting!.Value,mapping);
 }
 private static FinancialSwapValue Scale(FinancialSwapEvaluation value,SwapWriteRequest terms)=>
  value.Scale(new Quantity(ExactDecimal.Parse(Required(terms.Quantity)),value.Instrument.UnitQuantity.Specification));
 private async Task<FinancialSwapValue> Price(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,Guid? instrumentId,Guid? fixedId,Guid? floatingId,CancellationToken ct)=>
  Scale(await Evaluate(connection,data,frame,body,instrumentId,fixedId,floatingId,ct),body);
 private static object Value(FinancialSwapValue value)=>Value(value.Instrument,value.Sides,value.Quantity,value.PresentValue.Amount.ToString(),value.NumericalPolicy);
 private static object DraftValue(FinancialSwapEvaluation value,Quantity quantity)=>Value(value.Instrument,value.Sides,quantity,Exact(value.Instrument.PresentValue.Scale(quantity.Amount)),DealRisk.NumericalPolicy);
 private static object Value(InstrumentValuation instrument,ImmutableArray<DiscountedSideValuation> sides,Quantity quantity,string presentValue,string numericalPolicy)=>new{
  presentValue=presentValue,currency=instrument.Currency,quantity=quantity.Amount.ToString(),
  instrumentPresentValue=Exact(instrument.PresentValue),numericalPolicy=numericalPolicy,
  snapshotId=instrument.Snapshot.Id,definitions=instrument.Snapshot.Definitions,asOf=instrument.Snapshot.ValuationAt,marketCutoff=instrument.Snapshot.MarketCutoff,
  marketInputs=instrument.Snapshot.Selections.Select(s=>new{s.Input,s.Observation.Key,s.Observation.Source,s.Observation.Role,s.Observation.ObservedAt,value=s.Observation.Value.ToString()}),
  sides=sides.Select(s=>new{sideId=s.Contribution.SideId,kind=s.Contribution.Value.Schedule.Implementation==FixedSide.Implementation?"fixed":"floating",
   coefficient=s.Contribution.Coefficient.ToString(),presentValue=Exact(s.Contribution.Value.PresentValue),contribution=Exact(s.Contribution.Contribution),
   implementation=s.Contribution.Value.Schedule.Implementation,payments=s.Payments.Select(p=>new{
    ordinal=p.Cashflow.Plan.Key.Ordinal,deliveryFrom=Date(p.Cashflow.Plan.DeliveryFrom),deliveryTo=Date(p.Cashflow.Plan.DeliveryTo),paymentAt=p.Cashflow.Plan.PaymentAt,paymentDate=Date(p.Cashflow.Plan.PaymentDate),generatedDate=p.Cashflow.Plan.GeneratedDate,
    paymentConvention=p.Cashflow.Plan.PaymentConvention is {} rule?new{implementation=PaymentRule.Implementation,rule.Frequency,rule.Rule,rule.Days,rule.DayOfMonth,rule.DayBasis,rule.ShortMonthPolicy,rule.Adjustment,calendar=rule.Calendar?.Key,calendarFrom=rule.Calendar?.BusinessDays.From,calendarTo=rule.Calendar?.BusinessDays.To}:null,
    cashAmount=Exact(p.Cashflow.CashAmount),discountFactor=p.Discount.Factor.ToString(),presentValue=Exact(p.Cashflow.PresentValue),
    discountMappingId=p.Discount.MappingId,discountCurve=p.Discount.Curve,discountPolicy=p.Discount.InterpolationPolicy,pillars=p.Discount.Pillars,
    resets=p.Cashflow.Resets.Select(reset=>new{cashContribution=Exact(reset.CashContribution),fixingState=reset.Price.State.ToString(),fixingAt=reset.Price.Term.Lookup?.Fixing?.FixingAt,fixing=reset.Price.Term.Lookup?.Fixing,from=reset.Price.Term.Lookup is {} l?Date(l.Contract.DeliveryFrom):Date(p.Cashflow.Plan.DeliveryFrom),
     to=reset.Price.Term.Lookup is {} e?Date(e.Contract.DeliveryTo):Date(p.Cashflow.Plan.DeliveryTo),weight=reset.Price.Term.Weight.ToString(),price=reset.Price.Price.Value.ToString(),
     currency=reset.Price.Price.Unit.Currency,quantity=reset.Price.Price.Unit.Quantity,multiplier=reset.Price.Price.Unit.Multiplier.ToString(),lookup=reset.Price.Term.Lookup,lineage=reset.Price.Lineage})})})};
 public async Task<object> Catalog(NpgsqlConnection connection,PreviewData data,InputFrame frame,CancellationToken ct)
 {
  var indices=new List<object>();
  var markets=new List<(PreviewData Data,InputFrame Frame)>{(data,frame)};markets.AddRange(await database.ReadSwapMarkets(connection,frame,ct,data));
  foreach(var market in markets)
  {
   data=market.Data;frame=market.Frame;
   foreach(var curve in data.Definitions["curves"])
   {
   var target=One(data.References["settlement_targets"].Where(t=>t.Text("code")==curve.Text("settlement_target_code")));
   var quote=One(data.Quotes.Where(q=>q.Key.Code==curve.Text("quote_code")));
   if(target.Text("kind")!="index"||quote.Kind!=QuoteKind.Outright)continue;
   var ids=data.Definitions["curve_members"].Where(m=>m.Text("curve_code")==curve.Text("code")).Select(m=>m.Id("contract_id")).ToHashSet();
   var contracts=data.Contracts.Where(c=>ids.Contains(c.Key.Id)&&c.Kind==ContractKind.Forward&&c.DeliveryFrom.Day==1&&c.DeliveryFrom.Year<9999&&c.DeliveryFrom.AddMonths(1)==c.DeliveryTo).GroupBy(c=>(c.DeliveryFrom,c.DeliveryTo)).Where(g=>g.Count()==1&&(frame.At<g.Single().Expiry||FixingPublished(data,target.Text("index_a_code"),quote.Key,g.Single(),frame.At))).Select(g=>g.Single()).OrderBy(c=>c.DeliveryFrom).ToArray();
   var daily=data.Contracts.Where(c=>ids.Contains(c.Key.Id)&&c.Kind==ContractKind.Forward&&c.DeliveryTo.DayNumber-c.DeliveryFrom.DayNumber==1)
    .GroupBy(c=>(c.DeliveryFrom.Year,c.DeliveryFrom.Month)).Where(g=>g.Count()==DateTime.DaysInMonth(g.Key.Year,g.Key.Month)&&g.All(c=>frame.At<c.Expiry||FixingPublished(data,target.Text("index_a_code"),quote.Key,c,frame.At))&&g.Select(c=>c.DeliveryFrom).Distinct().Count()==g.Count()).ToArray();
   if(contracts.Length==0&&daily.Length==0)continue;
   var quantity=One(data.References["quantity_specifications"].Where(q=>q.Text("code")==quote.Quantity.Code));
   var currency=One(data.References["currencies"].Where(c=>c.Text("code")==quote.Currency.Code));
   var unit=One(data.References["units"].Where(u=>u.Text("code")==quantity.Text("unit_code")));
   var mapping=await Discount(connection,data,frame,quote.Currency,ct);
   indices.Add(new{definitionRevisionId=data.DefinitionRevision.RevisionId,curve=curve.Text("code"),index=target.Text("index_a_code"),commodity=curve.Text("commodity_code"),currencyCode=quote.Currency.Code,currency=currency.Text("display_code"),
    fixingConvention=data.Definitions["index_fixing_observations"].Any(f=>f.Text("index_code")==target.Text("index_a_code"))?IndexFixing.SupportedImplementation:null,
    fixingUnavailableReason=data.Definitions["index_fixing_observations"].Any(f=>f.Text("index_code")==target.Text("index_a_code"))?null:"This index has no qualified fixing schedule for the selected observations.",
    quantityCode=quote.Quantity.Code,quantityChoices=DraftQuantities(data,quote.Quantity),unit=unit.Text("symbol"),quoteMultiplier=quote.Multiplier.ToString(),minimumPrice=quote.Minimum.ToString(),maximumPrice=quote.Maximum.ToString(),
    samplings=(contracts.Length>0?new[]{new{resetFrequency="monthly",averagingMethod="none",label="Monthly index price"}}:[]).Concat(daily.Length>0?[new{resetFrequency="daily",averagingMethod="arithmetic",label="Arithmetic mean"}]:[]),
   defaultDiscountCurve=mapping?.Definition.Key.Code,averaging=(contracts.Length>0?new[]{"monthly_index_price"}:Array.Empty<string>()).Concat(daily.Length>0?new[]{"calendar_day_arithmetic"}:[]),
    contracts=contracts.Select(c=>new{deliveryFrom=Date(c.DeliveryFrom),deliveryTo=Date(c.DeliveryTo)}).Concat(daily.Select(g=>new{deliveryFrom=Date(new DateOnly(g.Key.Year,g.Key.Month,1)),deliveryTo=Date(new DateOnly(g.Key.Year,g.Key.Month,1).AddMonths(1))})).Distinct()});
  }
  }
  return new{books=await new FuturesStore(manifest).Books(connection,ct),bookingActor=await TradeAnnotations.Actor(connection,manifest.CatalogId,ct),legalEntities=await TradeParties.Catalog(connection,manifest.CatalogId,true,ct),counterparties=await TradeParties.Catalog(connection,manifest.CatalogId,false,ct),indices,
   commodities=data.References["commodities"].Select(c=>new{code=c.Text("code"),name=c.Text("name"),reference=R(c.Text("code"))}),
   paymentCalendars=data.References["calendars"].Select(c=>new{code=c.Text("code"),reference=R(c.Text("code")),from=c.Text("coverage_from"),to=c.Text("coverage_to")}),
   paymentOptions=SwapPaymentOptions.Catalog,convention=Convention};
 }
 private static SwapPaymentInput? StoredPayment(JsonElement row,string key)
 {
  var input=row.GetProperty(key);return input.ValueKind==JsonValueKind.Null?null:JsonSerializer.Deserialize<SwapPaymentInput>(input.GetRawText(),new JsonSerializerOptions(JsonSerializerDefaults.Web));
 }
 private async Task<ImmutableArray<JsonElement>> Rows(NpgsqlConnection connection,Guid? trade,CancellationToken ct)
 {
  var rows=await PreviewDatabase.Rows(connection,"""
  SELECT row_to_json(v)::text FROM(
   SELECT r.*,facts.booker_id,facts.booker_version,facts.booker_name,facts.booker_login,facts.executed_at AS booked_at,facts.trade_date AS booking_date,facts.implementation AS booking_implementation,facts.label_captured_at AS booker_label_captured_at,le.code AS legal_entity_code,le.name AS legal_entity_name,cp.code AS counterparty_code,cp.name AS counterparty_name,trdr.code AS trader_code,trdr.name AS trader_name,tr.reference_number AS trade_reference,ir.reference_number AS instrument_reference,k.known_from AS recorded_at,
    i.fixed_side_id,i.floating_side_id,i.fixed_coefficient,i.discounting,i.implementation,
    f.fixed_price,f.delivery_from AS fixed_from,f.delivery_to AS fixed_to,f.payment_at AS fixed_payment,f.reference_catalog_id,f.reference_revision_id,
    (SELECT array_agg(p.contract_id ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE p.catalog_id=s.catalog_id AND p.side_id=s.id) AS projection_contracts,
    s.implementation AS floating_implementation,s.definition_revision_id,s.curve_code,s.index_code,s.quote_code,s.delivery_from AS floating_from,s.delivery_to AS floating_to,s.first_fixing_at,s.payment_at AS floating_payment,s.averaging,
    (SELECT jsonb_build_object('frequency',p.frequency,'rule',p.rule,'days',p.days,'dayOfMonth',p.day_of_month,'dayBasis',p.day_basis,'shortMonth',p.short_month,'adjustment',p.adjustment,'calendar',p.calendar_code,'implementation',p.implementation) FROM trading.swap_payment_rules p WHERE(p.catalog_id,p.id)=(f.catalog_id,f.payment_rule_id)) AS fixed_payment_rule,
    (SELECT jsonb_build_object('frequency',p.frequency,'rule',p.rule,'days',p.days,'dayOfMonth',p.day_of_month,'dayBasis',p.day_basis,'shortMonth',p.short_month,'adjustment',p.adjustment,'calendar',p.calendar_code,'implementation',p.implementation) FROM trading.swap_payment_rules p WHERE(p.catalog_id,p.id)=(s.catalog_id,s.payment_rule_id)) AS floating_payment_rule
   FROM trading.swap_trade_revisions r JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id)
   JOIN trading.swap_fixed_sides f ON(f.catalog_id,f.id)=(i.catalog_id,i.fixed_side_id)
   JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id)
   LEFT JOIN trading.booking_facts facts ON(facts.catalog_id,facts.trade_id)=(r.catalog_id,r.trade_id)
   LEFT JOIN trading.legal_entities le ON(le.catalog_id,le.id,le.revision)=(r.catalog_id,r.legal_entity_id,r.legal_entity_revision)
   LEFT JOIN trading.counterparties cp ON(cp.catalog_id,cp.id,cp.revision)=(r.catalog_id,r.counterparty_id,r.counterparty_revision)
   LEFT JOIN trading.traders trdr ON(trdr.catalog_id,trdr.id,trdr.revision)=(r.catalog_id,r.trader_id,r.trader_revision)
   JOIN trading.trade_references tr ON(tr.catalog_id,tr.trade_id)=(r.catalog_id,r.trade_id)
   JOIN trading.instrument_references ir ON(ir.catalog_id,ir.instrument_id)=(r.catalog_id,r.instrument_id)
   JOIN trading.knowledge_receipts k ON(k.catalog_id,k.publication_order)=(r.catalog_id,r.publication_order)
   WHERE r.catalog_id=$1 AND($2::uuid IS NULL OR r.trade_id=$2) ORDER BY r.trade_id,r.revision
  )v
  """,[manifest.CatalogId,trade is {} id?id:DBNull.Value],ct);
  TradeAnnotations.RequireFacts(rows);return rows;
 }
 private SwapWriteRequest Terms(PreviewData data,JsonElement row,Guid snapshot)
 {
  if(row.Id("definition_revision_id")!=manifest.RevisionId&&!(manifest.SwapMarkets??[]).Any(m=>m.RevisionId==row.Id("definition_revision_id"))||row.Id("reference_catalog_id")!=manifest.ReferenceCatalogId||row.Id("reference_revision_id")!=manifest.ReferenceRevisionId||row.Text("implementation")!=Convention)
   throw Error("swap_definition","The retained swap requires its exact supported reference and model versions.");
  var quote=One(data.Quotes.Where(q=>q.Key.Code==row.Text("quote_code")));
  return new(snapshot,Convention,"retained",Guid.Empty,row.Id("book_id"),row.Text("execution_at"),row.Text("quantity"),row.Text("fixed_coefficient"),row.GetProperty("discounting").GetBoolean(),
   new(row.Text("fixed_price"),quote.Currency.Code,row.Text("fixed_from"),row.Text("fixed_to"),StoredPayment(row,"fixed_payment_rule") is null?row.Text("fixed_payment"):null,StoredPayment(row,"fixed_payment_rule")),
   new(row.Text("curve_code"),row.Text("index_code"),quote.Currency.Code,row.Text("floating_from"),row.Text("floating_to"),row.Text("floating_implementation")==FloatingSide.Implementation?row.Text("first_fixing_at"):null,
    StoredPayment(row,"floating_payment_rule") is null?row.Text("floating_payment"):null,null,StoredPayment(row,"floating_payment_rule"),row.Text("averaging")=="monthly_index_price"?"monthly":"daily",row.Text("averaging")=="monthly_index_price"?"none":"arithmetic"),DefinitionRevisionId:row.Id("definition_revision_id"),
    Commodity:One(data.References["quantity_specifications"].Where(q=>q.Text("code")==quote.Quantity.Code)).Text("commodity_code"),TraderId:row.OptionalId("trader_id"),TraderRevision:row.OptionalText("trader_revision") is {} tv?int.Parse(tv,CultureInfo.InvariantCulture):null,Memo:row.OptionalText("memo"),TradeDate:row.OptionalText("trade_date")??row.Text("booking_date"),LegalEntityId:row.OptionalId("legal_entity_id"),LegalEntityRevision:row.OptionalText("legal_entity_revision") is {} lev?int.Parse(lev,CultureInfo.InvariantCulture):null,CounterpartyId:row.OptionalId("counterparty_id"),CounterpartyRevision:row.OptionalText("counterparty_revision") is {} cpv?int.Parse(cpv,CultureInfo.InvariantCulture):null);
 }
 private static void ValidateProjections(FinancialSwapValue value,JsonElement row)
 {
  var stored=row.GetProperty("projection_contracts");
  var actual=value.Sides[1].Payments.SelectMany(p=>p.Cashflow.Prices).Select(r=>r.Term.Lookup!.Contract.Key.Id);
  if(stored.ValueKind!=JsonValueKind.Array||!actual.SequenceEqual(stored.EnumerateArray().Select(v=>v.GetGuid())))
   throw Error("swap_projection","The retained reset references do not match this side's exact schedule.");
 }
 private object State(PreviewData data,JsonElement row,Guid snapshot)=>new{
  booking=TradeParties.State(manifest.CatalogId,row),bookingFacts=TradeAnnotations.Facts(row),traderId=row.Id("booker_id"),traderRevision=int.Parse(row.Text("booker_version"),CultureInfo.InvariantCulture),trader=row.Text("booker_name"),traderCode=row.Text("booker_login"),executedAt=row.Text("booked_at"),memo=row.OptionalText("memo"),tradeId=row.Id("trade_id"),tradeReference=TradingReference.Trade(row),instrumentId=row.Id("instrument_id"),instrumentReference=TradingReference.Instrument(row),
  revision=int.Parse(row.Text("revision"),CultureInfo.InvariantCulture),eventId=row.Id("event_id"),kind=row.Text("event_kind"),effectiveAt=row.Text("effective_at"),recordedAt=row.Text("recorded_at"),reason=row.Text("reason"),
  quantity=row.Text("quantity"),terms=Terms(data,row,snapshot)};
 public async Task<object> Editor(NpgsqlConnection connection,PreviewData data,InputFrame frame,Guid tradeId,int? revision,CancellationToken ct)
 {
  var rows=await Rows(connection,tradeId,ct);if(rows.IsEmpty)throw Error("swap_trade","This trade is unavailable in the selected context.");
  var selected=revision??rows.Max(r=>int.Parse(r.Text("revision"),CultureInfo.InvariantCulture));
  var row=One(rows.Where(r=>r.Number("revision")==ExactDecimal.Parse(selected.ToString(CultureInfo.InvariantCulture))));
  var terms=Terms(data,row,frame.Id);var value=await Price(connection,data,frame,terms,row.Id("instrument_id"),row.Id("fixed_side_id"),row.Id("floating_side_id"),ct);
  ValidateProjections(value,row);
  return new{state=State(data,row,frame.Id),history=rows.Select(r=>State(data,r,frame.Id)),valuation=Value(value),catalog=await Catalog(connection,data,frame,ct),
   lifecycleEligible=frame.At<row.Instant("first_fixing_at")&&frame.At>=row.Instant("effective_at")};
 }
 private static string CanonicalTerms(SwapWriteRequest body)=>JsonSerializer.Serialize(new{
  body.TradeDate,body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision,body.TraderId,body.TraderRevision,body.Memo,body.BookId,executed=SchedulePreview.ParseAt(body.ExecutedAt).ToUniversalTime(),quantity=ExactDecimal.Parse(Required(body.Quantity)).ToString(),
  coefficient=ExactDecimal.Parse(Required(body.FixedCoefficient)).ToString(),body.Discounting,body.DefinitionRevisionId,
  fixedSide=new{price=ExactDecimal.Parse(Required(body.Fixed?.Price)).ToString(),currency=Required(body.Fixed?.Currency),from=Date(body.Fixed?.DeliveryFrom),to=Date(body.Fixed?.DeliveryTo),payment=body.Fixed?.PaymentAt is {} a?SchedulePreview.ParseAt(a).ToUniversalTime():(DateTimeOffset?)null,body.Fixed?.PaymentSchedule},
  floatingSide=new{curve=Required(body.Floating?.Curve),index=Required(body.Floating?.Index),currency=Required(body.Floating?.Currency),from=Date(body.Floating?.DeliveryFrom),to=Date(body.Floating?.DeliveryTo),fixing=body.Floating?.FirstFixingAt is {} legacyFixing?SchedulePreview.ParseAt(legacyFixing).ToUniversalTime():(DateTimeOffset?)null,payment=body.Floating?.PaymentAt is {} b?SchedulePreview.ParseAt(b).ToUniversalTime():(DateTimeOffset?)null,body.Floating?.PaymentSchedule,averaging=Sampling(body.Floating!)}});
 private async Task<SwapWriteRequest> BookingTerms(NpgsqlConnection connection,InputFrame frame,SwapWriteRequest body,CancellationToken ct)
 {
  TradeAnnotations.RequireServerFacts(body.ExecutedAt,body.TraderId,body.TraderRevision,body.TradeDate);
  if(body.TradeId is not {} trade)return body with{ExecutedAt=frame.At.ToString("O",CultureInfo.InvariantCulture),TradeDate=Date(DateOnly.FromDateTime(DateTime.UtcNow))};
  var rows=await Rows(connection,trade,ct);if(rows.IsEmpty)throw Error("swap_trade","The retained trade is unavailable.");
  var head=rows[^1];return body with{ExecutedAt=head.Text("execution_at"),TradeDate=head.OptionalText("trade_date")??head.Text("booking_date"),TraderId=head.OptionalId("trader_id"),TraderRevision=head.OptionalText("trader_revision") is {} version?int.Parse(version,CultureInfo.InvariantCulture):null};
 }
 public async Task<object> Preview(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,CancellationToken ct)
 {
  body=await BookingTerms(connection,frame,body,ct);
  RequireWriteRules(data,body);
  await TradeAnnotations.Validate(connection,manifest.CatalogId,body.TraderId,body.TraderRevision,body.Memo,SchedulePreview.ParseAt(body.ExecutedAt),ct);
  await TradeParties.Validate(connection,manifest.CatalogId,TradeParties.Payload(body.TradeDate,body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision),SchedulePreview.ParseAt(body.ExecutedAt),ct);
  if(SchedulePreview.ParseAt(body.ExecutedAt)>frame.At||!(await new FuturesStore(manifest).Books(connection,ct)).Any(b=>b.Id("id")==body.BookId))
   throw Error("swap_input","Choose an available book and execution no later than As of.");
  if(body.TradeId is {} trade)
  {
   var rows=await Rows(connection,trade,ct);if(rows.IsEmpty)throw Error("swap_trade","The retained trade is unavailable.");
   var head=rows[^1];var retained=Terms(data,head,frame.Id);
   if(body.ExpectedRevision!=int.Parse(head.Text("revision"),CultureInfo.InvariantCulture)||body.ExpectedRevision>=256||
    SchedulePreview.ParseAt(body.EffectiveAt)!=frame.At||frame.At<head.Instant("effective_at")||
    string.IsNullOrWhiteSpace(body.Reason)||body.Reason.Length>500||
    frame.At>=head.Instant("first_fixing_at")||frame.At>=head.Instant("fixed_payment")||frame.At>=head.Instant("floating_payment")||
    body.Fixed?.Currency!=retained.Fixed!.Currency||body.Floating?.Currency!=retained.Floating!.Currency)
    throw Error("swap_amendment","Amend only the latest eligible revision at As of, with a reason and compatible exact market and currency references.");
   var selectedData=data;
   if(body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)
    selectedData=One((await database.ReadSwapMarkets(connection,frame,ct,data)).Where(m=>m.Data.DefinitionRevision.RevisionId==body.DefinitionRevisionId)).Data;
   if(One(selectedData.Definitions["curves"].Where(c=>c.Text("code")==body.Floating!.Curve)).Text("quote_code")!=head.Text("quote_code"))
    throw Error("swap_amendment","The replacement curve must preserve the retained exact quote and quantity specification.");
   if(CanonicalTerms(body)==CanonicalTerms(retained))
    throw Error("swap_no_change","Change at least one retained trade term.");
  }
  else if(body.ExpectedRevision is not null)throw Error("swap_input","A new trade has no prior revision.");
  var value=await Price(connection,data,frame,body,null,null,null,ct);return new{expectedRevision=body.ExpectedRevision,valuation=Value(value)};
 }
 public async Task<JsonElement> Save(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,CancellationToken ct)
 {
  if(body.KnowledgeMode!="current"||body.OperationId==Guid.Empty||body.Discounting is null||body.Fixed is null||body.Floating is null)
   throw Error("swap_input","A stable operation identity, working context and complete explicit terms are required.");
  TradeAnnotations.RequireServerFacts(body.ExecutedAt,body.TraderId,body.TraderRevision,body.TradeDate);
  TradeAnnotations.ValidateShape(body.TraderId,body.TraderRevision,body.Memo);
  var prior=await Status(connection,body.OperationId,ct);
  if(!prior.TryGetProperty("tradeId",out _)){RequireWriteRules(data,body);await Price(connection,data,frame,body,null,null,null,ct);}
  var a=body.Fixed??throw Error("swap_input","Select a fixed side.");var b=body.Floating??throw Error("swap_input","Select a floating side.");
  var fixedPayment=a.PaymentSchedule is {} ar?new DateTimeOffset(Payment(data,ar).Generate(Date(a.DeliveryFrom),Date(a.DeliveryTo)).Date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)):SchedulePreview.ParseAt(a.PaymentAt);
  var floatingPayment=b.PaymentSchedule is {} br?new DateTimeOffset(Payment(data,br).Generate(Date(b.DeliveryFrom),Date(b.DeliveryTo)).Date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)):SchedulePreview.ParseAt(b.PaymentAt);
  var legacy=b.FirstFixingAt is not null;
  if(legacy&&!prior.TryGetProperty("tradeId",out _))throw Error("swap_fixing_read_only","Fixing dates are resolved from the selected index convention.");
  Guid[] actualPins=[];
  if(!legacy&&!prior.TryGetProperty("tradeId",out _))
  {
   var source=data;var sourceFrame=frame;
   if(body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)
   {
    var selected=One((await database.ReadSwapMarkets(connection,frame,ct,data)).Where(m=>m.Data.DefinitionRevision.RevisionId==body.DefinitionRevisionId));
    source=selected.Data;sourceFrame=selected.Frame;
   }
   var fixings=Resolve(source,body).Instrument.Floating.Fixings.Select(f=>f.Series).ToHashSet();
   actualPins=source.Snapshot(sourceFrame).Selections.Where(p=>p.Input.Use==InputUse.FrozenFact&&p.Observation.Role==ObservationRole.IndexFixing&&fixings.Contains(p.Input.Series))
    .Select(p=>p.Observation.Key.Id).Order().ToArray();
  }
  var arguments=new List<object>{manifest.CatalogId,body.OperationId,body.TradeId is {} trade?trade:DBNull.Value,body.ExpectedRevision is {} expected?expected:DBNull.Value,
   body.BookId,body.DefinitionRevisionId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId,Required(b.Curve),Required(b.Index),Required(a.Currency),Required(b.Currency),
   Date(a.DeliveryFrom),Date(a.DeliveryTo),fixedPayment,Date(b.DeliveryFrom),Date(b.DeliveryTo),floatingPayment,Sampling(b),
   ExactDecimal.Parse(Required(a.Price)).ToString(),ExactDecimal.Parse(Required(body.Quantity)).ToString(),ExactDecimal.Parse(Required(body.FixedCoefficient)).ToString(),body.Discounting.Value,
   frame.At,DBNull.Value,body.TradeId is null?frame.At:SchedulePreview.ParseAt(body.EffectiveAt),body.Reason??"Initial booking",Required(body.Convention),
   a.PaymentSchedule is null?DBNull.Value:JsonSerializer.Serialize(a.PaymentSchedule,new JsonSerializerOptions(JsonSerializerDefaults.Web)),
   b.PaymentSchedule is null?DBNull.Value:JsonSerializer.Serialize(b.PaymentSchedule,new JsonSerializerOptions(JsonSerializerDefaults.Web)),
   body.Commodity is {} commodity?commodity:DBNull.Value,DBNull.Value,DBNull.Value,body.Memo is {} memo?memo:DBNull.Value,
   TradeParties.Submitted(body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision)};
  string sql;
  if(legacy)
  {
   arguments.Insert(17,SchedulePreview.ParseAt(b.FirstFixingAt));
   sql="SELECT security.retry_legacy_swap_with_parties($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20,$21::numeric,$22::numeric,$23::numeric,$24,$25,$26,$27,$28,$29,$30::jsonb,$31::jsonb,$32,$33,$34,$35,$36::jsonb)::text";
  }
  else
  {
   arguments.Add(actualPins);
   sql="SELECT security.save_indexed_swap_with_parties($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20::numeric,$21::numeric,$22::numeric,$23,$24,$25,$26,$27,$28,$29::jsonb,$30::jsonb,$31,$32,$33,$34,$35::jsonb,$36::uuid[])::text";
  }
  await using var command=ScheduleStore.Command(connection,sql,arguments.ToArray());
  return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
 }
 public async Task<JsonElement> Status(NpgsqlConnection connection,Guid operation,CancellationToken ct)
 {
  if(operation==Guid.Empty)throw Error("swap_input","A retained operation identity is required.");
  await using var command=ScheduleStore.Command(connection,"SELECT security.swap_operation_status($1,$2)::text",manifest.CatalogId,operation);
  return JsonSerializer.Deserialize<JsonElement>(await command.ExecuteScalarAsync(ct) is string text?text:"{\"found\":false}");
 }
 internal sealed record HoldingsResult(JsonElement[] Rows,int Valuations,int SupplementalMarketLoads);
 internal async Task<HoldingsResult> Holdings(NpgsqlConnection connection,PreviewData data,InputFrame frame,ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct)
 {
  var books=await new FuturesStore(manifest).Books(connection,ct);FuturesStore.ValidateScope(books,bookIds,portfolioIds);
  var rows=(await Rows(connection,null,ct)).GroupBy(r=>r.Id("trade_id")).Select(g=>g.MaxBy(r=>r.Number("revision"))!).Where(r=>
   (bookIds.IsEmpty||bookIds.Contains(r.Id("book_id")))&&(portfolioIds.IsEmpty||portfolioIds.Contains(One(books.Where(b=>b.Id("id")==r.Id("book_id"))).Id("portfolio_id")))).ToArray();
  var result=new List<JsonElement>();
  // This cache cannot outlive the authorized read operation. Economic identity
  // includes discounting and distinguishes unit work under this context.
  var markets=rows.Any(r=>r.Id("definition_revision_id")!=data.DefinitionRevision.RevisionId)?await database.ReadSwapMarkets(connection,frame,ct,data):[];
  var values=new Dictionary<(DefinitionIdentity Economics,DefinitionIdentity Binding,Guid RetainedId),FinancialSwapEvaluation>();
  var identities=new Dictionary<Guid,(DefinitionIdentity Economics,DefinitionIdentity Binding,Guid RetainedId)>();
  foreach(var row in rows)
  {
   var terms=Terms(data,row,frame.Id);var id=row.Id("instrument_id");
   if(!identities.TryGetValue(id,out var key))
   {
    var source=data.DefinitionRevision.RevisionId==terms.DefinitionRevisionId?data:One(markets.Where(m=>m.Data.DefinitionRevision.RevisionId==terms.DefinitionRevisionId)).Data;
    var instrument=Resolve(source,terms,id,row.Id("fixed_side_id"),row.Id("floating_side_id")).Instrument;
    key=(instrument.EconomicIdentity,instrument.PricingBindingIdentity,id);identities.Add(id,key);
   }
   if(!values.TryGetValue(key,out var unitValue)){unitValue=await Evaluate(connection,data,frame,terms,id,row.Id("fixed_side_id"),row.Id("floating_side_id"),ct,markets);values.Add(key,unitValue);}
   if(unitValue.Instrument.InstrumentId!=id)throw Error("instrument_identity","Equivalent retained terms cannot silently replace an exact published instrument identity.");
   var value=Scale(unitValue,terms);
   ValidateProjections(value,row);
   var quote=value.Instrument.Currency;var currency=One(data.References["currencies"].Where(c=>c.Text("code")==quote.Code));
   var quantity=One(data.References["quantity_specifications"].Where(q=>q.Text("code")==value.Quantity.Specification.Code));
   var unit=One(data.References["units"].Where(u=>u.Text("code")==quantity.Text("unit_code")));var book=One(books.Where(b=>b.Id("id")==row.Id("book_id")));
   result.Add(Json(new{booking=TradeParties.State(manifest.CatalogId,row),bookingFacts=TradeAnnotations.Facts(row),traderId=row.Id("booker_id"),trader=row.Text("booker_name"),traderCode=row.Text("booker_login"),memo=row.OptionalText("memo"),tradeId=row.Id("trade_id"),tradeReference=TradingReference.Trade(row),instrumentId=row.Id("instrument_id"),instrumentReference=TradingReference.Instrument(row),bindingId=row.Id("binding_id"),
    revision=row.Text("revision"),bookId=row.Id("book_id"),book=book.Text("name"),portfolioId=book.Id("portfolio_id"),portfolio=book.Text("portfolio"),instrumentType="financial_swap",definitionRevisionId=row.Id("definition_revision_id"),
    contractId=row.Id("instrument_id"),contractIdentityKind="swap_instrument",curve=row.Text("curve_code"),deliveryFrom=row.Text("floating_from"),deliveryTo=row.Text("floating_to"),
    quoteMultiplier=value.Sides[0].Payments[0].Cashflow.Prices[0].Price.Unit.Multiplier.ToString(),lots=(string?)null,quantity=value.Quantity.Amount.ToString(),unit=unit.Text("symbol"),quantitySpecification=value.Quantity.Specification,currencyReference=quote,
    executedAt=row.Text("booked_at"),executionPrice=(string?)null,fixedPrice=row.Text("fixed_price"),mark=value.Sides[1].Payments[0].Cashflow.Prices.Length==1?value.Sides[1].Payments[0].Cashflow.Prices[0].Price.Value.ToString():null,executionPnl=(string?)null,
    presentValue=value.PresentValue.Amount.ToString(),currency=currency.Text("display_code"),settlement="Projection"}));
  }
  return new(result.ToArray(),values.Count,markets.IsEmpty?0:1);
 }
}
