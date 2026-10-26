using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;
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
public sealed record CustomCurveProjectionInput(Guid CatalogId,Guid CurveId,int Version,string OutputNode,
 [property:System.Text.Json.Serialization.JsonRequired]int? Position,string Selection,string Implementation);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapFloatingInput(string? Curve,string? Index,string? Currency,string? DeliveryFrom,string? DeliveryTo,string? FirstFixingAt=null,string? PaymentAt=null,string? Averaging=null,SwapPaymentInput? PaymentSchedule=null,string? ResetFrequency=null,string? AveragingMethod=null,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]CustomCurveProjectionInput? CustomProjection=null,string? Spread=null,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]SwapObservationScheduleInput? ObservationSchedule=null);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapWriteRequest(Guid SnapshotId,string? Convention,string? KnowledgeMode,Guid OperationId,Guid BookId,string? ExecutedAt,
 string? Quantity,string? FixedCoefficient,bool? Discounting,SwapFixedInput? Fixed,SwapFloatingInput? Floating,
 Guid? TradeId=null,int? ExpectedRevision=null,string? EffectiveAt=null,string? Reason=null,Guid DefinitionRevisionId=default,string? Commodity=null,Guid? TraderId=null,int? TraderRevision=null,string? Memo=null,string? TradeDate=null,Guid? LegalEntityId=null,int? LegalEntityRevision=null,Guid? CounterpartyId=null,int? CounterpartyRevision=null,SwapFloatingInput? FirstFloating=null,string? SwapType=null,string? FirstCoefficient=null,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]SwapScheduledInput? Scheduled=null);
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
 private static T One<T>(IEnumerable<T> source)
 {var rows=source.Take(2).ToArray();return rows.Length==1?rows[0]:throw Error("swap_definition","An exact side reference is missing or ambiguous.");}
 private static string Exact(ExactMoney value)=>value.ToExactString();
 private static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,new JsonSerializerOptions(JsonSerializerDefaults.Web));
 private PaymentRule Payment(PreviewData data,SwapPaymentInput input)
 {
  PaymentCalendar? calendar=null;
  if(input.Calendar is {} code)
   calendar=ReferenceCalendar(data,new(data.ReferenceRevision,code));
  return new(SwapPaymentOptions.Parse(SwapPaymentOptions.Frequencies,input.Frequency),SwapPaymentOptions.Parse(SwapPaymentOptions.Rules,input.Rule),
   input.Days,input.DayOfMonth,input.DayBasis is null?null:SwapPaymentOptions.Parse(SwapPaymentOptions.DayBases,input.DayBasis),
   input.ShortMonth is null?null:SwapPaymentOptions.Parse(SwapPaymentOptions.ShortMonths,input.ShortMonth),
   SwapPaymentOptions.Parse(SwapPaymentOptions.Adjustments,input.Adjustment),calendar,Required(input.Implementation));
 }
 private static string Sampling(SwapFloatingInput input)
 {
  if(input.Averaging is {} legacy&&input.ResetFrequency is null&&input.AveragingMethod is null)return legacy;
  if(input.Averaging is not null)throw Error("swap_averaging","Supply one explicit sampling definition.");
  return SwapObservationCompatibility.Parse(input.ResetFrequency,input.AveragingMethod) switch
  {FloatingAveraging.MonthlyIndexPrice=>"monthly_index_price",FloatingAveraging.CalendarDayArithmetic=>"calendar_day_arithmetic",_=>input.AveragingMethod!};
 }
 private static string Coefficient(SwapWriteRequest body)=>Required(body.FirstFloating is null?body.FixedCoefficient:body.FirstCoefficient);
 private static void RequireSideKinds(SwapWriteRequest body)
 {
  if(body.FirstFloating is null?body.Fixed is null||body.FirstCoefficient is not null||body.SwapType is not(null or "financial_fixed_floating"):
   body.Fixed is not null||body.FixedCoefficient is not null||body.SwapType!="financial_floating_floating")
   throw Error("swap_input","Choose a supported swap type with its explicit independent sides and first-side direction.");
 }
 private void RequireWriteRules(PreviewData data,SwapWriteRequest body)
 {
  RequireSideKinds(body);
  if(body.FirstFloating is not null&&(body.FirstFloating.Spread is null||body.Floating?.Spread is null))throw Error("swap_input","Choose an explicit fixed spread on each floating side; use zero for no spread.");
  if(body.Floating is null||body.Fixed is {} a&&(a.PaymentAt is not null||a.PaymentSchedule is null)||
   new[]{body.FirstFloating,body.Floating}.OfType<SwapFloatingInput>().Any(b=>b.PaymentAt is not null||b.PaymentSchedule is null||b.Averaging is not null)||string.IsNullOrWhiteSpace(body.Commodity))
   throw Error("swap_input","Select commodity, independent payment rules, reset frequency and averaging method; payment dates are generated by the backend.");
  if(new[]{body.FirstFloating,body.Floating}.OfType<SwapFloatingInput>().Any(side=>side.FirstFixingAt is not null))throw Error("swap_fixing_read_only","Fixing dates come from the selected index convention and cannot be supplied as trade terms.");
  _=One(data.References["commodities"].Where(c=>c.Text("code")==body.Commodity));
 }
 private FloatingSide ResolveFloating(PreviewData data,SwapFloatingInput b,string? commodity,Guid? id)
 {
  var averaging=StoredAveraging(Sampling(b));
  var curve=One(data.Definitions["curves"].Where(c=>c.Text("code")==b.Curve));
  var target=One(data.References["settlement_targets"].Where(t=>t.Text("code")==curve.Text("settlement_target_code")));
  var quote=One(data.Quotes.Where(q=>q.Key.Code==curve.Text("quote_code")));
  if(commodity is not null&&commodity!=curve.Text("commodity_code")||target.Text("kind")!="index"||target.Text("index_a_code")!=b.Index||quote.Kind!=QuoteKind.Outright||b.Currency!=quote.Currency.Code)
   throw Error("swap_definition","The selected curve, index, currency and exact unit must agree. Currency conversion is unsupported.");
  var from=Date(b.DeliveryFrom);var to=Date(b.DeliveryTo);
  var ids=data.Definitions["curve_members"].Where(m=>m.Text("curve_code")==b.Curve).Select(m=>m.Id("contract_id")).ToHashSet();
  if(from.Day!=1||from.Year==9999||from.AddMonths(1)!=to)throw Error("swap_projection","This booking model selects one complete delivery month per side.");
  var selected=data.Contracts.Where(c=>ids.Contains(c.Key.Id)&&c.Kind==ContractKind.Forward&&
   (averaging==FloatingAveraging.MonthlyIndexPrice?c.DeliveryFrom==from&&c.DeliveryTo==to:from<=c.DeliveryFrom&&c.DeliveryFrom<to&&c.DeliveryTo.DayNumber-c.DeliveryFrom.DayNumber==1)).OrderBy(c=>c.DeliveryFrom).ToArray();
  if(averaging==FloatingAveraging.MonthlyIndexPrice)_=One(selected);
  var unit=new Quantity(ExactDecimal.Parse("1"),quote.Quantity);
  var spread=b.Spread is null?null:new UnitPrice(ExactDecimal.Parse(Required(b.Spread)),PriceUnit.FromQuote(quote));
  var projections=selected.Select(c=>new SwapProjection(new(new(data.DefinitionRevision,Required(b.Curve)),c.Key),c)).ToImmutableArray();
  // Only retained legacy definitions carry a manual boundary. New entry resolves
  // the exact immutable observation schedule from the selected index bundle.
  var index=new ReferenceKey(data.ReferenceRevision,Required(b.Index));var sideId=id??Guid.NewGuid();
  var observations=ObservationTerms(data,b,averaging);
  var customBinding=b.CustomProjection is {} input?new CustomCurveProjectionBinding(input.CatalogId,input.CurveId,input.Version,input.OutputNode,input.Position,input.Selection,input.Implementation):null;
  CompatibleSideObservations? planned=null;
  if(b.FirstFixingAt is null)
  {
   planned=SwapObservationCompatibility.Validate(data,curve,index,quote,from,to,averaging,observations.Calendar,observations.Quantities,customBinding);
   projections=planned.Projections;
  }
  var floating=b.FirstFixingAt is {} legacyBoundary?
   b.PaymentSchedule is {} legacyRule?
    new FloatingSide(sideId,index,quote,unit,from,to,SchedulePreview.ParseAt(legacyBoundary),Payment(data,legacyRule),averaging,projections,FloatingSide.Implementation,observations.Calendar,spread):
    new FloatingSide(sideId,index,quote,unit,from,to,SchedulePreview.ParseAt(legacyBoundary),SchedulePreview.ParseAt(b.PaymentAt),averaging,projections,FloatingSide.Implementation,observations.Calendar,spread):
   b.PaymentSchedule is {} floatingRule?
    new FloatingSide(sideId,index,quote,unit,from,to,planned!.Fixings,Payment(data,floatingRule),averaging,projections,FloatingSide.IndexedImplementation,observations.Calendar,spread):
    throw Error("payment_rule","New index-based swaps require an explicit payment convention.");
  if(b.CustomProjection is {} custom)
   floating=floating.WithCustomProjection(new(custom.CatalogId,custom.CurveId,custom.Version,custom.OutputNode,custom.Position,custom.Selection,custom.Implementation));
  return floating;
 }
 private FixedSide ResolveFixed(PreviewData data,SwapFixedInput input,QuoteSpecification quote,Guid? id)
 {
  if(input.Currency!=quote.Currency.Code)throw Error("swap_definition","The fixed currency must match the exact floating quote.");
  var price=new UnitPrice(ExactDecimal.Parse(Required(input.Price)),PriceUnit.FromQuote(quote));
  var quantity=new Quantity(ExactDecimal.Parse("1"),quote.Quantity);
  return input.PaymentSchedule is {} rule?
   new(id??Guid.NewGuid(),price,quantity,Date(input.DeliveryFrom),Date(input.DeliveryTo),Payment(data,rule),FixedSide.Implementation,quote):
   new(id??Guid.NewGuid(),price,quantity,Date(input.DeliveryFrom),Date(input.DeliveryTo),SchedulePreview.ParseAt(input.PaymentAt),FixedSide.Implementation,quote);
 }
 private (FinancialSwapInstrument Instrument,Quantity Quantity) Resolve(PreviewData data,SwapWriteRequest body,Guid? instrumentId=null,Guid? fixedId=null,Guid? floatingId=null)
 {
  RequireSideKinds(body);
  if(body.Convention!=Convention||body.KnowledgeMode is not("current" or "retained")||body.Discounting is null||body.Floating is null)
   throw Error("swap_input","Select explicit side terms, quantity, direction and discounting policy.");
  if(body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)throw Error("swap_definition","Select the exact supported definition revision.");
  var floating=ResolveFloating(data,body.Floating,body.Commodity,floatingId);
  Side first;
  if(body.FirstFloating is {} firstFloating)first=ResolveFloating(data,firstFloating,body.Commodity,fixedId);
  else
  {
   first=ResolveFixed(data,body.Fixed!,floating.Quote,fixedId);
  }
  var coefficient=ExactDecimal.Parse(Coefficient(body));
  var instrument=new FinancialSwapInstrument(instrumentId??Guid.NewGuid(),[new(first,coefficient),new(floating,ExactDecimal.Parse("0")-coefficient)],Convention,body.Discounting.Value);
  var quantity=new Quantity(ExactDecimal.Parse(Required(body.Quantity)),floating.Quote.Quantity);
  if(quantity.Amount.CompareTo(default)<=0)throw Error("swap_quantity","Trade quantity must be positive; each membership specifies pay/receive direction.");
  return(instrument,quantity);
 }
 private async Task<CurrencyDiscountCurve?> Discount(NpgsqlConnection connection,PreviewData data,InputFrame frame,ReferenceKey currency,CancellationToken ct)
 {
  await using var command=ScheduleStore.Command(connection,"SELECT security.swap_discount_default($1,$2,$3,$4,$5,$6)::text",manifest.CatalogId,currency.Revision.CatalogId,currency.Revision.RevisionId,currency.Code,data.DefinitionRevision.RevisionId,frame.At);
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
  var mapping=body.Discounting==true?await Discount(connection,data,frame,terms.Instrument.Currency,ct):null;
  var dependencies=terms.Instrument.DependenciesAt(snapshot);
  if(frame.ContextId is not {} contextId)throw Error("swap_context","Pricing requires the retained financial context.");
  var context=(await new KnowledgeStore(manifest.CatalogId).Read(connection,contextId,ct)).Context;
  var connected=await CustomCurves.PrepareMarket(connection,database,data,frame,snapshot,context,[terms.Instrument],dependencies,ct);
  return FinancialSwapValuation.Evaluate(terms.Instrument,connected.Valuation,connected.Bindings,connected.Authority,mapping,ct);
 }
 private static FinancialSwapValue Scale(FinancialSwapEvaluation value,SwapWriteRequest terms)=>
  value.Scale(new Quantity(ExactDecimal.Parse(Required(terms.Quantity)),value.Instrument.UnitQuantity.Specification));
 private async Task<FinancialSwapValue> Price(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,Guid? instrumentId,Guid? fixedId,Guid? floatingId,CancellationToken ct)=>
  body.Scheduled is not null?await ScheduledPrice(connection,data,frame,body,instrumentId,ct):Scale(await Evaluate(connection,data,frame,body,instrumentId,fixedId,floatingId,ct),body);
 private static string QuantityText(ExactQuantity value)
 {
  try{return value.ToQuantity().Amount.ToString();}
  catch(MarketDataException error)when(error.Code is "precision_loss" or "numeric_range"){return value.Numerator+"/"+value.Denominator;}
 }
 private static object Value(FinancialSwapValue value)=>Value(value.Instrument,value.Sides,value.Quantity,Exact(value.ExactPresentValue),value.NumericalPolicy);
 private static object DraftValue(FinancialSwapEvaluation value,Quantity quantity)=>Value(value.Instrument,value.Sides,quantity,Exact(value.Instrument.PresentValue.Scale(quantity.Amount)),DealRisk.NumericalPolicy);
 private static object Value(InstrumentValuation instrument,ImmutableArray<DiscountedSideValuation> sides,Quantity quantity,string presentValue,string numericalPolicy)=>new{
  presentValue=presentValue,currency=instrument.Currency,quantity=quantity.Amount.ToString(),quantityApplication=instrument.Sides.Any(s=>s.Value.Schedule.Implementation==ScheduledSwapSide.Implementation)?ScheduledPriceApplication:"total_per_side_delivery_period_v1",
  instrumentPresentValue=Exact(instrument.PresentValue),numericalPolicy=numericalPolicy,instrument.CurveNumericalPolicy,instrument.ScenarioId,
  snapshotId=instrument.Snapshot.Id,definitions=instrument.Snapshot.Definitions,asOf=instrument.Snapshot.ValuationAt,marketCutoff=instrument.Snapshot.MarketCutoff,
  marketInputs=instrument.Snapshot.Selections.Select(s=>new{s.Input,s.Observation.Key,s.Observation.Source,s.Observation.Role,s.Observation.ObservedAt,value=s.Observation.Value.ToString()}),
  sides=sides.Select(s=>new{sideId=s.Contribution.SideId,kind=s.Contribution.Value is PaymentSideValuation pv&&pv.Payments.All(p=>p.Prices.All(r=>r.Term.FixedPrice is not null))?"fixed":"floating",
   coefficient=s.Contribution.Coefficient.ToString(),quantityRatio=new{numerator=s.Contribution.QuantityRatio.Numerator.ToString(CultureInfo.InvariantCulture),denominator=s.Contribution.QuantityRatio.Denominator.ToString(CultureInfo.InvariantCulture)},presentValue=Exact(s.Contribution.Value.PresentValue),contribution=Exact(s.Contribution.Contribution),
   slot=instrument.Sides.IndexOf(s.Contribution),implementation=s.Contribution.Value.Schedule.Implementation,payments=s.Payments.Select(p=>new{
    ordinal=p.Cashflow.Plan.Key.Ordinal,deliveryFrom=Date(p.Cashflow.Plan.DeliveryFrom),deliveryTo=Date(p.Cashflow.Plan.DeliveryTo),paymentAt=p.Cashflow.Plan.PaymentAt,paymentDate=Date(p.Cashflow.Plan.PaymentDate),generatedDate=p.Cashflow.Plan.GeneratedDate,
    paymentConvention=p.Cashflow.Plan.PaymentConvention is {} rule?new{implementation=PaymentRule.Implementation,rule.Frequency,rule.Rule,rule.Days,rule.DayOfMonth,rule.DayBasis,rule.ShortMonthPolicy,rule.Adjustment,calendar=rule.Calendar?.Key,calendarFrom=rule.Calendar?.BusinessDays.From,calendarTo=rule.Calendar?.BusinessDays.To}:null,
    resolvedQuantity=QuantityText(p.Cashflow.Plan.AllocatedQuantity.Scale(s.Contribution.QuantityRatio).Scale(quantity.Amount)),
    tradeCashAmount=Exact(p.Cashflow.CashAmount.Scale(s.Contribution.QuantityRatio).Scale(s.Contribution.Coefficient).Scale(quantity.Amount)),
    tradePresentValue=Exact(p.Cashflow.PresentValue.Scale(s.Contribution.QuantityRatio).Scale(s.Contribution.Coefficient).Scale(quantity.Amount)),
    cashAmount=Exact(p.Cashflow.CashAmount),discountFactor=p.Discount.Factor.ToString(),presentValue=Exact(p.Cashflow.PresentValue),
    discountMappingId=p.Discount.MappingId,discountCurve=p.Discount.Curve,discountPolicy=p.Discount.InterpolationPolicy,pillars=p.Discount.Pillars,
    observationSchedule=ObservationInspection(p.Cashflow),
    resets=p.Cashflow.Resets.Select(reset=>new{cashContribution=Exact(reset.CashContribution),fixingState=reset.Price.State.ToString(),fixingAt=reset.Price.Term.Lookup?.Fixing?.FixingAt,fixing=reset.Price.Term.Lookup?.Fixing,from=reset.Price.Term.Lookup is {} l?Date(l.Contract.DeliveryFrom):Date(p.Cashflow.Plan.DeliveryFrom),
     to=reset.Price.Term.Lookup is {} e?Date(e.Contract.DeliveryTo):Date(p.Cashflow.Plan.DeliveryTo),weight=reset.Price.Term.Weight.ToString(),price=ExactPrice(reset.Price.ExactPrice),
     exactPrice=new{reset.Price.ExactPrice.Numerator,reset.Price.ExactPrice.Denominator},spread=reset.Price.Term.Spread?.Value.ToString(),
     currency=reset.Price.ExactPrice.Unit.Currency,quantity=reset.Price.ExactPrice.Unit.Quantity,multiplier=reset.Price.ExactPrice.Unit.Multiplier.ToString(),lookup=reset.Price.Term.Lookup,lineage=reset.Price.Lineage})})})};
 private static string ExactPrice(ExactUnitPrice price)=>price.ToExactString();
 // Pay or receive fixed, from the stored membership coefficient of the fixed side: the first side of a fixed/floating swap,
 // whose periods are all fixed when it is scheduled. A swap of two floating sides has no fixed side to pay or receive.
 private static string Direction(SidedInstrument instrument)
 {
  var first=instrument.Sides[0];
  if(first.Side is not(FixedSide or ScheduledSwapSide{IsFixed:true}))return "multi_side";
  return first.Coefficient==ExactDecimal.Parse("-1")?"pay_fixed":first.Coefficient==ExactDecimal.Parse("1")?"receive_fixed":
   throw Error("swap_instrument","The fixed side's coefficient is neither pay (-1) nor receive (1).");
 }
 // Each membership's side kind and direction, which a read with no pure valuation to state them in states this way (D207).
 private static object[] Memberships(SidedInstrument instrument)=>instrument.Sides.Select((membership,slot)=>(object)new{slot,
  kind=membership.Side is FixedSide or ScheduledSwapSide{IsFixed:true}?"fixed":"floating",coefficient=membership.Coefficient.ToString()}).ToArray();
 // The mark less the fixed price, exactly. A swap without one fixed price or without one mark states none.
 private static string? MarkMinusPrice(SidedInstrument instrument,ExactUnitPrice? mark)=>
  mark is not null&&instrument.Sides[0].Side is FixedSide fixedSide?ExactPrice(mark.Subtract(ExactUnitPrice.FromPrice(fixedSide.Price))):null;
 public Task<object> Catalog(NpgsqlConnection connection,PreviewData data,InputFrame frame,CancellationToken ct)=>Catalog(connection,data,frame,ct,null);
 private async Task<object> Catalog(NpgsqlConnection connection,PreviewData data,InputFrame frame,CancellationToken ct,SwapWriteRequest? retained)
 {
  var indices=new List<object>();
  var markets=new List<(PreviewData Data,InputFrame Frame)>{(data,frame)};markets.AddRange((await database.ReadSwapMarkets(connection,frame,ct,data)).Where(m=>m.Data.DefinitionRevision!=data.DefinitionRevision));
  foreach(var market in markets)
  {
   data=market.Data;frame=market.Frame;
   var customProjections=await CustomCurves.ProjectionChoices(connection,database,data,frame,ct);
   foreach(var curve in data.Definitions["curves"])
   {
   var target=One(data.References["settlement_targets"].Where(t=>t.Text("code")==curve.Text("settlement_target_code")));
   var quote=One(data.Quotes.Where(q=>q.Key.Code==curve.Text("quote_code")));
   if(target.Text("kind")!="index"||quote.Kind!=QuoteKind.Outright)continue;
   var samplings=SwapObservationCompatibility.SupportedSamplings(data,curve);
   // Retained terms remain inspectable without acquiring new-pricing eligibility.
   var retainedIndex=retained?.DefinitionRevisionId==data.DefinitionRevision.RevisionId&&
    new[]{retained.Floating,retained.FirstFloating}.Concat(retained.Scheduled is {} scheduled?new[]{scheduled.First,scheduled.Second}.SelectMany(s=>s.Periods).Select(p=>p.Floating):[]).Any(side=>side?.Curve==curve.Text("code")&&side.Index==target.Text("index_a_code"));
   if(samplings.IsEmpty&&!retainedIndex)continue;
   var ids=data.Definitions["curve_members"].Where(m=>m.Text("curve_code")==curve.Text("code")).Select(m=>m.Id("contract_id")).ToHashSet();
   var contracts=data.Contracts.Where(c=>ids.Contains(c.Key.Id)&&c.Kind==ContractKind.Forward&&c.DeliveryFrom.Day==1&&c.DeliveryFrom.Year<9999&&c.DeliveryFrom.AddMonths(1)==c.DeliveryTo).GroupBy(c=>(c.DeliveryFrom,c.DeliveryTo)).Where(g=>g.Count()==1&&(frame.At<g.Single().Expiry||FixingPublished(data,target.Text("index_a_code"),quote.Key,g.Single(),frame.At))).Select(g=>g.Single()).OrderBy(c=>c.DeliveryFrom).ToArray();
   var daily=data.Contracts.Where(c=>ids.Contains(c.Key.Id)&&c.Kind==ContractKind.Forward&&c.DeliveryTo.DayNumber-c.DeliveryFrom.DayNumber==1)
    .GroupBy(c=>(c.DeliveryFrom.Year,c.DeliveryFrom.Month)).Where(g=>g.Count()==DateTime.DaysInMonth(g.Key.Year,g.Key.Month)&&g.All(c=>frame.At<c.Expiry||FixingPublished(data,target.Text("index_a_code"),quote.Key,c,frame.At))&&g.Select(c=>c.DeliveryFrom).Distinct().Count()==g.Count()).ToArray();
   var dailyCandidates=data.Contracts.Where(c=>ids.Contains(c.Key.Id)&&c.Kind==ContractKind.Forward&&c.DeliveryTo.DayNumber-c.DeliveryFrom.DayNumber==1&&c.DeliveryFrom.Year<9999)
    .Select(c=>new DateOnly(c.DeliveryFrom.Year,c.DeliveryFrom.Month,1)).Distinct().Order().ToArray();
   var quantity=One(data.References["quantity_specifications"].Where(q=>q.Text("code")==quote.Quantity.Code));
   var currency=One(data.References["currencies"].Where(c=>c.Text("code")==quote.Currency.Code));
   var unit=One(data.References["units"].Where(u=>u.Text("code")==quantity.Text("unit_code")));
   var mapping=await Discount(connection,data,frame,quote.Currency,ct);
   indices.Add(new{customProjections=customProjections.Where(p=>p.Unit==PriceUnit.FromQuote(quote)&&
    (p.Reference.Selection==CustomCurveProjectionBinding.SupportedSelection?contracts.Length>0:
     daily.Any(g=>g.All(c=>p.Deliveries.Contains(new(c.DeliveryFrom,c.DeliveryTo))))))
    .Select(p=>new{p.Label,p.Reference,samplings=p.Reference.Selection==CustomCurveProjectionBinding.SupportedSelection?
     new[]{new{resetFrequency="monthly",averagingMethod="none"}}:new[]{new{resetFrequency="daily",averagingMethod="arithmetic"}}}).ToArray(),definitionRevisionId=data.DefinitionRevision.RevisionId,primitiveEntrySupported=!samplings.IsEmpty,curve=curve.Text("code"),index=target.Text("index_a_code"),commodity=curve.Text("commodity_code"),currencyCode=quote.Currency.Code,currency=currency.Text("display_code"),
    fixingConvention=data.Definitions["index_fixing_observations"].Any(f=>f.Text("index_code")==target.Text("index_a_code"))?IndexFixing.SupportedImplementation:null,
    fixingUnavailableReason=data.Definitions["index_fixing_observations"].Any(f=>f.Text("index_code")==target.Text("index_a_code"))?null:"This index has no qualified fixing schedule for the selected observations.",
    quantityCode=quote.Quantity.Code,powerQuantitySupported=quantity.Text("unit_code")=="megawatt_hour"&&quantity.Text("dimension_code")=="energy",quantityChoices=DraftQuantities(data,quote.Quantity),unit=unit.Text("symbol"),quoteMultiplier=quote.Multiplier.ToString(),minimumPrice=quote.Minimum.ToString(),maximumPrice=quote.Maximum.ToString(),
    samplings,
   defaultDiscountCurve=mapping?.Definition.Key.Code,averaging=samplings.Select(s=>s.Averaging switch{FloatingAveraging.MonthlyIndexPrice=>"monthly_index_price",FloatingAveraging.CalendarDayArithmetic=>"calendar_day_arithmetic",_=>s.AveragingMethod}),
    contracts=contracts.Select(c=>new{deliveryFrom=Date(c.DeliveryFrom),deliveryTo=Date(c.DeliveryTo)}).Concat(dailyCandidates.Select(from=>new{deliveryFrom=Date(from),deliveryTo=Date(from.AddMonths(1))})).Distinct()});
  }
  }
  var referenceChoices=markets.Select(m=>new{definitionRevisionId=m.Data.DefinitionRevision.RevisionId,
   commodities=m.Data.References["commodities"].Select(c=>new{code=c.Text("code"),name=c.Text("name"),reference=new ReferenceKey(m.Data.ReferenceRevision,c.Text("code"))}).ToArray(),
   paymentCalendars=m.Data.References["calendars"].Select(c=>new{code=c.Text("code"),reference=new ReferenceKey(m.Data.ReferenceRevision,c.Text("code")),from=c.Text("coverage_from"),to=c.Text("coverage_to")}).ToArray(),
   powerQuantities=m.Data.References["quantity_specifications"].Where(q=>q.Text("unit_code")=="megawatt"&&q.Text("dimension_code")=="power").Select(q=>new{specification=new ReferenceKey(m.Data.ReferenceRevision,q.Text("code")),unit="MW",commodity=q.Text("commodity_code")}).ToArray(),
   clocks=m.Data.References["clock_rules"].Select(c=>new{reference=new ReferenceKey(m.Data.ReferenceRevision,c.Text("code")),label=c.OptionalText("iana_zone")??c.Text("code")}).ToArray()}).ToArray();
  return new{books=await new FuturesStore(manifest).Books(connection,ct),bookingActor=await TradeAnnotations.Actor(connection,manifest.CatalogId,ct),legalEntities=await TradeParties.Catalog(connection,manifest.CatalogId,true,ct),counterparties=await TradeParties.Catalog(connection,manifest.CatalogId,false,ct),indices,referenceChoices,
   commodities=markets.SelectMany(m=>m.Data.References["commodities"]).DistinctBy(c=>c.Text("code")).Select(c=>new{code=c.Text("code"),name=c.Text("name")}),
   paymentCalendars=markets.SelectMany(m=>m.Data.References["calendars"].Select(c=>new{definitionRevisionId=m.Data.DefinitionRevision.RevisionId,code=c.Text("code"),reference=new ReferenceKey(m.Data.ReferenceRevision,c.Text("code")),from=c.Text("coverage_from"),to=c.Text("coverage_to")})),
   factorOptions=new{swapImplementation=CrossCommodityScheduledSwap.Implementation,factorImplementation=QuantityFactor.Implementation,unitConversionImplementation=UnitConverter.Implementation,quantityBasis="factor_of_first_side_v1"},
   scheduledOptions=new{implementation=ScheduledFinancialSwap.Implementation,priceApplication=ScheduledPriceApplication,maximumPeriods=120,
    powerQuantities=markets.SelectMany(m=>m.Data.References["quantity_specifications"].Where(q=>q.Text("unit_code")=="megawatt"&&q.Text("dimension_code")=="power").Select(q=>new{definitionRevisionId=m.Data.DefinitionRevision.RevisionId,specification=new ReferenceKey(m.Data.ReferenceRevision,q.Text("code")),unit="MW",commodity=q.Text("commodity_code")})),
    clocks=markets.SelectMany(m=>m.Data.References["clock_rules"].Select(c=>new{definitionRevisionId=m.Data.DefinitionRevision.RevisionId,reference=new ReferenceKey(m.Data.ReferenceRevision,c.Text("code")),label=c.OptionalText("iana_zone")??c.Text("code")}))},
   parRateOptions=new{implementation=FinancialSwapValuation.ParRatePolicy,initialDecimalPlaces=6,tieRule="to_even"},entryTemplate=SwapPaymentOptions.EntryTemplate,paymentOptions=SwapPaymentOptions.Catalog,convention=Convention};
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
    coalesce(i.fixed_side_id,i.first_floating_side_id) AS fixed_side_id,i.first_floating_side_id,i.floating_side_id,i.fixed_coefficient,i.discounting,i.implementation,
    i.scheduled_first_id,i.scheduled_second_id,i.second_quantity_numerator,i.second_quantity_denominator,
    CASE WHEN i.scheduled_first_id IS NULL THEN NULL ELSE trading.scheduled_swap_terms(r.catalog_id,r.trade_id,r.revision,i.instrument_id) END AS scheduled_data,
    f.fixed_price,f.delivery_from AS fixed_from,f.delivery_to AS fixed_to,f.payment_at AS fixed_payment,s.reference_catalog_id,s.reference_revision_id,s.spread AS floating_spread,
    (SELECT array_agg(p.contract_id ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE p.catalog_id=s.catalog_id AND p.side_id=s.id) AS projection_contracts,
    (SELECT array_agg(p.weight ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE p.catalog_id=s.catalog_id AND p.side_id=s.id) AS projection_weights,
    q.currency_code AS quote_currency_code,q.commodity_code AS quote_commodity_code,
    s.implementation AS floating_implementation,s.definition_revision_id,s.curve_code,s.index_code,s.quote_code,s.delivery_from AS floating_from,s.delivery_to AS floating_to,s.first_fixing_at,s.payment_at AS floating_payment,s.averaging,s.custom_projection_id,s.observation_schedule_id,
    NULL::jsonb AS observation_schedule,
    (SELECT jsonb_build_object('curve',a.curve_code,'index',a.index_code,'currency',q.currency_code,'deliveryFrom',a.delivery_from,'deliveryTo',a.delivery_to,
     'firstFixingAt',CASE WHEN a.implementation='prefixing_index_projection_v1' THEN to_jsonb(a.first_fixing_at) ELSE NULL END,'paymentAt',CASE WHEN a.payment_rule_id IS NULL THEN to_jsonb(a.payment_at) ELSE NULL END,
     'paymentSchedule',(SELECT jsonb_build_object('frequency',p.frequency,'rule',p.rule,'days',p.days,'dayOfMonth',p.day_of_month,'dayBasis',p.day_basis,'shortMonth',p.short_month,'adjustment',p.adjustment,'calendar',p.calendar_code,'implementation',p.implementation) FROM trading.swap_payment_rules p WHERE(p.catalog_id,p.id)=(a.catalog_id,a.payment_rule_id)),
     'resetFrequency',CASE WHEN a.averaging='monthly_index_price' THEN 'monthly' ELSE 'daily' END,'averagingMethod',CASE a.averaging WHEN 'monthly_index_price' THEN 'none' WHEN 'calendar_day_arithmetic' THEN 'arithmetic' ELSE a.averaging END,'spread',a.spread::text,'observationSchedule',NULL,
     'customProjection',(SELECT jsonb_build_object('catalogId',p.catalog_id,'curveId',p.curve_id,'version',p.version,'outputNode',p.output_node,'position',p.position,'selection',p.selection,'implementation',p.implementation) FROM trading.custom_curve_projection_bindings p WHERE(p.catalog_id,p.id)=(a.catalog_id,a.custom_projection_id)))
     FROM trading.swap_floating_sides a JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(a.reference_catalog_id,a.reference_revision_id,a.quote_code) WHERE(a.catalog_id,a.id)=(i.catalog_id,i.first_floating_side_id)) AS first_floating,
    a.first_fixing_at AS first_floating_fixing,a.custom_projection_id AS first_custom_projection_id,a.observation_schedule_id AS first_observation_schedule_id,
    (SELECT array_agg(p.contract_id ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE(p.catalog_id,p.side_id)=(i.catalog_id,i.first_floating_side_id)) AS first_projection_contracts,
    (SELECT array_agg(p.weight ORDER BY p.ordinal) FROM trading.swap_reset_projections p WHERE(p.catalog_id,p.side_id)=(i.catalog_id,i.first_floating_side_id)) AS first_projection_weights,
    (SELECT jsonb_build_object('catalogId',p.catalog_id,'curveId',p.curve_id,'version',p.version,'outputNode',p.output_node,'position',p.position,'selection',p.selection,'implementation',p.implementation) FROM trading.custom_curve_projection_bindings p WHERE(p.catalog_id,p.id)=(s.catalog_id,s.custom_projection_id)) AS custom_projection,
    (SELECT jsonb_build_object('frequency',p.frequency,'rule',p.rule,'days',p.days,'dayOfMonth',p.day_of_month,'dayBasis',p.day_basis,'shortMonth',p.short_month,'adjustment',p.adjustment,'calendar',p.calendar_code,'implementation',p.implementation) FROM trading.swap_payment_rules p WHERE(p.catalog_id,p.id)=(f.catalog_id,f.payment_rule_id)) AS fixed_payment_rule,
    (SELECT jsonb_build_object('frequency',p.frequency,'rule',p.rule,'days',p.days,'dayOfMonth',p.day_of_month,'dayBasis',p.day_basis,'shortMonth',p.short_month,'adjustment',p.adjustment,'calendar',p.calendar_code,'implementation',p.implementation) FROM trading.swap_payment_rules p WHERE(p.catalog_id,p.id)=(s.catalog_id,s.payment_rule_id)) AS floating_payment_rule
   FROM trading.swap_trade_revisions r JOIN trading.swap_instruments i ON(i.catalog_id,i.instrument_id)=(r.catalog_id,r.instrument_id)
   LEFT JOIN trading.swap_fixed_sides f ON(f.catalog_id,f.id)=(i.catalog_id,i.fixed_side_id)
   JOIN trading.swap_floating_sides s ON(s.catalog_id,s.id)=(i.catalog_id,i.floating_side_id)
   JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(s.reference_catalog_id,s.reference_revision_id,s.quote_code)
   LEFT JOIN trading.swap_floating_sides a ON(a.catalog_id,a.id)=(i.catalog_id,i.first_floating_side_id)
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
  TradeAnnotations.RequireFacts(rows);
  return await ObservationSchedules(connection,rows,ct);
 }
 // Headers and children are immutable, published atomically no later than a
 // referencing side. The selected knowledge context therefore qualifies the
 // same exact references across both bounded reads; current RLS remains active
 // on this connection, and absent required rows remain absent.
 private async Task<ImmutableArray<JsonElement>> ObservationSchedules(NpgsqlConnection connection,ImmutableArray<JsonElement> rows,CancellationToken ct)
 {
  var ids=rows.SelectMany(row=>new[]{row.OptionalId("observation_schedule_id"),row.OptionalId("first_observation_schedule_id")})
   .OfType<Guid>().Distinct().ToArray();
  if(ids.Length==0)return rows;
  var schedules=await PreviewDatabase.Rows(connection,"""
   SELECT jsonb_build_object('id',o.id,'terms',jsonb_build_object('implementation',o.implementation,
    'calendar',CASE WHEN o.calendar_code IS NULL THEN NULL ELSE jsonb_build_object('revision',jsonb_build_object('catalogId',o.reference_catalog_id,'revisionId',o.reference_revision_id),'code',o.calendar_code) END,
    'quantities',CASE WHEN o.averaging='quantity_weighted' THEN(SELECT jsonb_agg(jsonb_build_object('from',k.delivery_from,'to',k.delivery_to,'quantity',jsonb_build_object('value',trim_scale(q.amount)::text,'specification',jsonb_build_object('revision',jsonb_build_object('catalogId',q.reference_catalog_id,'revisionId',q.reference_revision_id),'code',q.quantity_code))) ORDER BY q.ordinal)
     FROM trading.swap_observation_quantities q JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(q.catalog_id,q.definition_revision_id,q.contract_id) WHERE(q.catalog_id,q.schedule_id)=(o.catalog_id,o.id)) ELSE NULL END))::text
   FROM trading.swap_observation_schedules o WHERE o.catalog_id=$1 AND o.id=ANY($2::uuid[])
   """,[manifest.CatalogId,ids],ct);
  var byId=schedules.ToDictionary(row=>row.Id("id"),row=>row.GetProperty("terms"));
  JsonNode? Terms(Guid? id)=>id is {} key&&byId.TryGetValue(key,out var value)?JsonNode.Parse(value.GetRawText()):null;
  return rows.Select(row=>
  {
   var retained=JsonNode.Parse(row.GetRawText())!.AsObject();
   retained["observation_schedule"]=Terms(row.OptionalId("observation_schedule_id"));
   if(retained["first_floating"] is JsonObject first)first["observationSchedule"]=Terms(row.OptionalId("first_observation_schedule_id"));
   return JsonSerializer.SerializeToElement(retained);
  }).ToImmutableArray();
 }
 private SwapWriteRequest Terms(PreviewData data,JsonElement row,Guid snapshot)
 {
  if(row.OptionalId("scheduled_first_id") is not null)return ScheduledTerms(row,snapshot);
  var custom=row.GetProperty("custom_projection");
  var observation=row.GetProperty("observation_schedule");
  if(row.GetProperty("observation_schedule_id").ValueKind!=JsonValueKind.Null&&observation.ValueKind==JsonValueKind.Null)
   throw Error("swap_averaging","The exact retained observation schedule is unavailable under the selected context and current authority.");
  if(row.GetProperty("custom_projection_id").ValueKind!=JsonValueKind.Null&&custom.ValueKind==JsonValueKind.Null)
   throw Error("custom_projection","The retained custom pricing definition is unavailable under the current context and authority.");
  if(row.Id("definition_revision_id")!=manifest.RevisionId&&!(manifest.SwapMarkets??[]).Any(m=>m.RevisionId==row.Id("definition_revision_id"))||row.Id("reference_catalog_id")!=manifest.ReferenceCatalogId||row.Text("implementation")!=Convention)
   throw Error("swap_definition","The retained swap requires its exact supported reference and model versions.");
  var sampling=SwapObservationCompatibility.Mode(StoredAveraging(row.Text("averaging")));
  var first=row.GetProperty("first_floating");
  if(row.OptionalId("first_floating_side_id") is not null&&first.ValueKind==JsonValueKind.Null)
   throw Error("swap_definition","The exact first floating side is unavailable under the selected context and current authority.");
  if(row.GetProperty("first_custom_projection_id").ValueKind!=JsonValueKind.Null&&first.GetProperty("customProjection").ValueKind==JsonValueKind.Null)
   throw Error("custom_projection","The retained first floating projection is unavailable under the selected context and current authority.");
  if(row.GetProperty("first_observation_schedule_id").ValueKind!=JsonValueKind.Null&&first.GetProperty("observationSchedule").ValueKind==JsonValueKind.Null)
   throw Error("swap_averaging","The exact first floating observation schedule is unavailable under the selected context and current authority.");
  return new(snapshot,Convention,"retained",Guid.Empty,row.Id("book_id"),row.Text("execution_at"),row.Text("quantity"),row.OptionalId("first_floating_side_id") is null?row.Text("fixed_coefficient"):null,row.GetProperty("discounting").GetBoolean(),
   row.OptionalId("first_floating_side_id") is null?new(row.Text("fixed_price"),row.Text("quote_currency_code"),row.Text("fixed_from"),row.Text("fixed_to"),StoredPayment(row,"fixed_payment_rule") is null?row.Text("fixed_payment"):null,StoredPayment(row,"fixed_payment_rule")):null,
   new(row.Text("curve_code"),row.Text("index_code"),row.Text("quote_currency_code"),row.Text("floating_from"),row.Text("floating_to"),row.Text("floating_implementation")==FloatingSide.Implementation?row.Text("first_fixing_at"):null,
    StoredPayment(row,"floating_payment_rule") is null?row.Text("floating_payment"):null,null,StoredPayment(row,"floating_payment_rule"),sampling.ResetFrequency,sampling.AveragingMethod,
    custom.ValueKind==JsonValueKind.Null?null:JsonSerializer.Deserialize<CustomCurveProjectionInput>(custom.GetRawText(),new JsonSerializerOptions(JsonSerializerDefaults.Web)),row.Text("floating_spread"),
    observation.ValueKind==JsonValueKind.Null?null:JsonSerializer.Deserialize<SwapObservationScheduleInput>(observation.GetRawText(),new JsonSerializerOptions(JsonSerializerDefaults.Web))),DefinitionRevisionId:row.Id("definition_revision_id"),
    Commodity:row.Text("quote_commodity_code"),TraderId:row.OptionalId("trader_id"),TraderRevision:row.OptionalText("trader_revision") is {} tv?int.Parse(tv,CultureInfo.InvariantCulture):null,Memo:row.OptionalText("memo"),TradeDate:row.OptionalText("trade_date")??row.Text("booking_date"),LegalEntityId:row.OptionalId("legal_entity_id"),LegalEntityRevision:row.OptionalText("legal_entity_revision") is {} lev?int.Parse(lev,CultureInfo.InvariantCulture):null,CounterpartyId:row.OptionalId("counterparty_id"),CounterpartyRevision:row.OptionalText("counterparty_revision") is {} cpv?int.Parse(cpv,CultureInfo.InvariantCulture):null,
    FirstFloating:first.ValueKind==JsonValueKind.Null?null:JsonSerializer.Deserialize<SwapFloatingInput>(first.GetRawText(),new JsonSerializerOptions(JsonSerializerDefaults.Web)),SwapType:row.OptionalId("first_floating_side_id") is null?"financial_fixed_floating":"financial_floating_floating",FirstCoefficient:row.OptionalId("first_floating_side_id") is null?null:row.Text("fixed_coefficient"));
 }
 private static void ValidateProjections(FinancialSwapValue value,JsonElement row)
 {
  ValidateProjections(value.Sides[1].Payments.SelectMany(p=>p.Cashflow.Prices).Select(r=>r.Term),row);
  if(row.OptionalId("first_floating_side_id") is not null)ValidateProjections(value.Sides[0].Payments.SelectMany(p=>p.Cashflow.Prices).Select(r=>r.Term),row,"first_projection_contracts","first_projection_weights");
 }
 private static void ValidateProjections(IEnumerable<SchedulePriceTerm> actual,JsonElement row,string contractField="projection_contracts",string weightField="projection_weights")
 {
  var terms=actual.ToArray();
  var stored=row.GetProperty(contractField);
  var weights=row.GetProperty(weightField);
  if(stored.ValueKind!=JsonValueKind.Array||weights.ValueKind!=JsonValueKind.Array||
   !terms.Select(t=>t.Lookup!.Contract.Key.Id).SequenceEqual(stored.EnumerateArray().Select(v=>v.GetGuid()))||
   !terms.Select(t=>t.Weight).SequenceEqual(weights.EnumerateArray().Select(v=>ExactDecimal.Parse(v.ValueKind==JsonValueKind.String?v.GetString()!:v.GetRawText()))))
   throw Error("swap_projection","The retained reset references and weights do not match this side's exact schedule.");
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
  var settlement=await SettlementState(connection,tradeId,ct);
  if(settlement.GetProperty("occurrences").GetArrayLength()!=0)
  {
   if(selected!=int.Parse(rows[^1].Text("revision"),CultureInfo.InvariantCulture))throw Error("settlement_revision","Select the effective trade head or its original certified historical context.");
   var lifecycle=await EvaluateLifecycle(connection,data,frame,row,Terms(data,row,frame.Id),settlement,ct);
   return new{state=State(data,row,frame.Id),history=rows.Select(r=>State(data,r,frame.Id)),valuation=(object?)null,memberships=Memberships(lifecycle.Instrument),
    settlementValuation=LifecycleValue(lifecycle.Value),settlement,catalog=await Catalog(connection,data,frame,ct,Terms(data,row,frame.Id)),lifecycleEligible=false};
  }
  var terms=Terms(data,row,frame.Id);var value=await Price(connection,data,frame,terms,row.Id("instrument_id"),row.Id("fixed_side_id"),row.Id("floating_side_id"),ct);
  if(terms.Scheduled is null)ValidateProjections(value,row);
  return new{state=State(data,row,frame.Id),history=rows.Select(r=>State(data,r,frame.Id)),valuation=Value(value),quantityInputs=terms.Scheduled is null?null:await RetainedQuantityInputs(connection,row,ct),catalog=await Catalog(connection,data,frame,ct,terms),
   lifecycleEligible=terms.Scheduled is not null?ScheduledAmendmentEligible(ResolveScheduled((await ScheduledMarket(connection,data,frame,terms.DefinitionRevisionId,ct)).Data,terms,row.Id("instrument_id")).Instrument,frame.At):frame.At<row.Instant("first_fixing_at")&&(row.OptionalText("first_floating_fixing") is not {} firstFixing||frame.At<SchedulePreview.ParseAt(firstFixing))&&frame.At>=row.Instant("effective_at")};
 }
 private static object? CanonicalFloating(SwapFloatingInput? side)=>side is null?null:new{
  curve=Required(side.Curve),index=Required(side.Index),currency=Required(side.Currency),from=Date(side.DeliveryFrom),to=Date(side.DeliveryTo),
  fixing=side.FirstFixingAt is {} fixing?SchedulePreview.ParseAt(fixing).ToUniversalTime():(DateTimeOffset?)null,payment=side.PaymentAt is {} payment?SchedulePreview.ParseAt(payment).ToUniversalTime():(DateTimeOffset?)null,
  side.PaymentSchedule,averaging=Sampling(side),spread=ExactDecimal.Parse(side.Spread??"0").ToString(),side.CustomProjection,
  observationSchedule=side.ObservationSchedule is {} observation?observation with{
   Quantities=observation.Quantities is {} quantities?quantities.Select(row=>row with{Quantity=row.Quantity with{Value=ExactDecimal.Parse(row.Quantity.Value).ToString()}}).ToImmutableArray():null}:null};
 private static string CanonicalTerms(SwapWriteRequest body)=>JsonSerializer.Serialize(new{
  body.TradeDate,body.LegalEntityId,body.LegalEntityRevision,body.CounterpartyId,body.CounterpartyRevision,body.TraderId,body.TraderRevision,body.Memo,body.BookId,executed=SchedulePreview.ParseAt(body.ExecutedAt).ToUniversalTime(),quantity=ExactDecimal.Parse(Required(body.Quantity)).ToString(),
  coefficient=ExactDecimal.Parse(Coefficient(body)).ToString(),body.Discounting,body.DefinitionRevisionId,
  fixedSide=body.Fixed is {} a?new{price=ExactDecimal.Parse(Required(a.Price)).ToString(),currency=Required(a.Currency),from=Date(a.DeliveryFrom),to=Date(a.DeliveryTo),payment=a.PaymentAt is {} payment?SchedulePreview.ParseAt(payment).ToUniversalTime():(DateTimeOffset?)null,a.PaymentSchedule}:null,
  firstFloating=CanonicalFloating(body.FirstFloating),floatingSide=CanonicalFloating(body.Floating)});
 private async Task<SwapWriteRequest> BookingTerms(NpgsqlConnection connection,InputFrame frame,SwapWriteRequest body,CancellationToken ct)
 {
  TradeAnnotations.RequireServerFacts(body.ExecutedAt,body.TraderId,body.TraderRevision,body.TradeDate);
  if(body.TradeId is not {} trade)return body with{ExecutedAt=frame.At.ToString("O",CultureInfo.InvariantCulture),TradeDate=Date(DateOnly.FromDateTime(DateTime.UtcNow))};
  var rows=await Rows(connection,trade,ct);if(rows.IsEmpty)throw Error("swap_trade","The retained trade is unavailable.");
  var head=rows[^1];return body with{ExecutedAt=head.Text("execution_at"),TradeDate=head.OptionalText("trade_date")??head.Text("booking_date"),TraderId=head.OptionalId("trader_id"),TraderRevision=head.OptionalText("trader_revision") is {} version?int.Parse(version,CultureInfo.InvariantCulture):null};
 }
 public async Task<object> Preview(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,CancellationToken ct)
 {
  if(body.Scheduled is not null)return await PreviewScheduled(connection,data,frame,body,ct);
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
    frame.At>=head.Instant("first_fixing_at")||(head.OptionalText("fixed_payment") is {} fp&&frame.At>=SchedulePreview.ParseAt(fp))||frame.At>=head.Instant("floating_payment")||
    body.Fixed?.Currency!=retained.Fixed?.Currency||body.FirstFloating?.Currency!=retained.FirstFloating?.Currency||body.Floating?.Currency!=retained.Floating!.Currency)
    throw Error("swap_amendment","Amend only the latest eligible revision at As of, with a reason and compatible exact market and currency references.");
   if(head.OptionalText("first_floating_fixing") is {} firstFixing&&frame.At>=SchedulePreview.ParseAt(firstFixing))throw Error("swap_amendment","The retained first floating side requires actual fixing state.");
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
  if(body.DefinitionRevisionId!=data.DefinitionRevision.RevisionId)
  {
   var selected=One((await database.ReadSwapMarkets(connection,frame,ct,data)).Where(m=>m.Data.DefinitionRevision.RevisionId==body.DefinitionRevisionId));
   data=selected.Data;frame=selected.Frame;
  }
  if(body.Scheduled is not null)return await SaveScheduled(connection,data,frame,body,ct);
  RequireSideKinds(body);
  if(body.FirstFloating is not null||body.Floating?.Spread is {} spread&&ExactDecimal.Parse(Required(spread))!=default)return await SaveComposed(connection,data,frame,body,ct);
  if(body.KnowledgeMode!="current"||body.OperationId==Guid.Empty||body.Discounting is null||body.Fixed is null||body.Floating is null)
   throw Error("swap_input","A stable operation identity, working context and complete explicit terms are required.");
  TradeAnnotations.RequireServerFacts(body.ExecutedAt,body.TraderId,body.TraderRevision,body.TradeDate);
  TradeAnnotations.ValidateShape(body.TraderId,body.TraderRevision,body.Memo);
  var prior=await Status(connection,body.OperationId,ct);
  if(!prior.TryGetProperty("tradeId",out _)){RequireWriteRules(data,body);await ValidateBookingPrice(connection,data,frame,body,ct);}
  var a=body.Fixed??throw Error("swap_input","Select a fixed side.");var b=body.Floating??throw Error("swap_input","Select a floating side.");
  var fixedPayment=a.PaymentSchedule is {} ar?new DateTimeOffset(Payment(data,ar).Generate(Date(a.DeliveryFrom),Date(a.DeliveryTo)).Date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)):SchedulePreview.ParseAt(a.PaymentAt);
  var floatingPayment=b.PaymentSchedule is {} br?new DateTimeOffset(Payment(data,br).Generate(Date(b.DeliveryFrom),Date(b.DeliveryTo)).Date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)):SchedulePreview.ParseAt(b.PaymentAt);
  var legacy=b.FirstFixingAt is not null;
  if(legacy&&b.CustomProjection is not null)throw Error("custom_projection","Custom projections require explicit indexed actual fixing rules.");
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
   actualPins=SideComponents.ActualFixingPins(source.Snapshot(sourceFrame),[Resolve(source,body).Instrument.Floating]).ToArray();
  }
  var arguments=new List<object>{manifest.CatalogId,body.OperationId,body.TradeId is {} trade?trade:DBNull.Value,body.ExpectedRevision is {} expected?expected:DBNull.Value,
   body.BookId,body.DefinitionRevisionId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId,Required(b.Curve),Required(b.Index),Required(a.Currency),Required(b.Currency),
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
   arguments.Add(b.CustomProjection is null?DBNull.Value:JsonSerializer.Serialize(b.CustomProjection,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
   sql="SELECT security.save_projected_swap_with_parties($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20::numeric,$21::numeric,$22::numeric,$23,$24,$25,$26,$27,$28,$29::jsonb,$30::jsonb,$31,$32,$33,$34,$35::jsonb,$36::uuid[],$37::jsonb)::text";
   if(b.ObservationSchedule is {} observation)
   {
    arguments.Add(JsonSerializer.Serialize(observation,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    sql="SELECT security.save_observation_swap_with_parties($1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13,$14,$15,$16,$17,$18,$19,$20::numeric,$21::numeric,$22::numeric,$23,$24,$25,$26,$27,$28,$29::jsonb,$30::jsonb,$31,$32,$33,$34,$35::jsonb,$36::uuid[],$37::jsonb,$38::jsonb)::text";
   }
  }
  await using var command=ScheduleStore.Command(connection,sql,arguments.ToArray());
  return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
 }
 private async Task ValidateBookingPrice(NpgsqlConnection connection,PreviewData data,InputFrame frame,SwapWriteRequest body,CancellationToken ct) =>
  _ = await WithBookingRead(connection,frame,(selected,pinned)=>Price(connection,selected,pinned,body,null,null,null,ct),ct);
 private async Task<T> WithBookingRead<T>(NpgsqlConnection connection,InputFrame frame,Func<PreviewData,InputFrame,Task<T>> read,CancellationToken ct)
 {
  // Current-knowledge booking explicitly resolves a certified pricing view.
  // Its temporary read binding must not turn the ensuing write into a historical write.
  var context=(await new KnowledgeStore(manifest.CatalogId).Capture(connection,
   new(Date(DateOnly.FromDateTime(frame.At.UtcDateTime)),null,"current"),ct)).Context;
  await using(var savepoint=ScheduleStore.Command(connection,"SAVEPOINT connected_booking_price"))await savepoint.ExecuteNonQueryAsync(ct);
  try
  {
   await PinnedContext.BindExact(connection,manifest.CatalogId,context,ct);
   await using(var trading=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",manifest.CatalogId,frame.At))
    await trading.ExecuteNonQueryAsync(ct);
   var pinned=frame with{ContextId=context.ContextId};
   var selected=await database.Read(connection,ct,observationIds:frame.ObservationIds,observationScope:ObservationReadScope.ExactPins);
   return await read(selected,pinned);
  }
  finally
  {
   await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT connected_booking_price; RELEASE SAVEPOINT connected_booking_price");
   await restore.ExecuteNonQueryAsync(CancellationToken.None);
  }
 }
 public async Task<JsonElement> Status(NpgsqlConnection connection,Guid operation,CancellationToken ct)
 {
  if(operation==Guid.Empty)throw Error("swap_input","A retained operation identity is required.");
  await using var command=ScheduleStore.Command(connection,"SELECT security.swap_operation_status($1,$2)::text",manifest.CatalogId,operation);
  return JsonSerializer.Deserialize<JsonElement>(await command.ExecuteScalarAsync(ct) is string text?text:"{\"found\":false}");
 }
 internal sealed record HoldingsResult(JsonElement[] Rows,int Valuations,int SupplementalMarketLoads,ImmutableArray<(JsonElement Row,string Reason)> Unvalued);
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
  var unvalued=new Dictionary<(DefinitionIdentity Economics,DefinitionIdentity Binding,Guid RetainedId),string>();
  var identities=new Dictionary<Guid,((DefinitionIdentity Economics,DefinitionIdentity Binding,Guid RetainedId) Key,FinancialSwapInstrument Instrument)>();
  var unvaluedRows=ImmutableArray.CreateBuilder<(JsonElement Row,string Reason)>();
  // A scheduled swap, or one whose settlement lifecycle is under way, is valued for its own binding.
  var bindingValuations=0;
  foreach(var row in rows)
  {
   var terms=Terms(data,row,frame.Id);var id=row.Id("instrument_id");
   var source=data.DefinitionRevision.RevisionId==terms.DefinitionRevisionId?data:One(markets.Where(m=>m.Data.DefinitionRevision.RevisionId==terms.DefinitionRevisionId)).Data;
   var book=One(books.Where(b=>b.Id("id")==row.Id("book_id")));
   SidedInstrument instrument;HeldValue held;
   // D220: a position-specific failure leaves the position unvalued at the close, with that code; any other failure fails the read.
   var settlement=await SettlementState(connection,row.Id("trade_id"),ct);
   if(settlement.GetProperty("occurrences").GetArrayLength()!=0)
   {
    bindingValuations++;LifecycleEvaluation? lifecycle=null;string? reason=null;
    try{lifecycle=await EvaluateLifecycle(connection,data,frame,row,terms,settlement,ct);}
    catch(MarketDataException error) when(ValuationStatus.PositionSpecific(error)){reason=error.Code;}
    instrument=lifecycle?.Instrument??ResolveAny(source,terms,id,row.Id("fixed_side_id"),row.Id("floating_side_id")).Instrument;
    held=lifecycle is null?new(null,null,reason,"Active",null):new(Exact(lifecycle.Value.RemainingValue),null,null,"Active",LifecycleValue(lifecycle.Value));
   }
   else if(terms.Scheduled is not null)
   {
    bindingValuations++;FinancialSwapValue? value=null;string? reason=null;
    try{value=await ScheduledPrice(connection,data,frame,terms,id,ct);}
    catch(MarketDataException error) when(ValuationStatus.PositionSpecific(error)){reason=error.Code;}
    instrument=ResolveScheduled(source,terms,id).Instrument;
    held=value is null?new(null,null,reason,"Projection",null):new(Exact(value.ExactPresentValue),null,null,"Projection",null);
   }
   else
   {
    if(!identities.TryGetValue(id,out var identity))
    {
     var resolved=Resolve(source,terms,id,row.Id("fixed_side_id"),row.Id("floating_side_id")).Instrument;
     identity=((resolved.EconomicIdentity,resolved.PricingBindingIdentity,id),resolved);identities.Add(id,identity);
    }
    var key=identity.Key;instrument=identity.Instrument;
    // A position-specific failure leaves the unit, and each position holding it, unvalued.
    if(!values.ContainsKey(key)&&!unvalued.ContainsKey(key))
     try{values.Add(key,await Evaluate(connection,data,frame,terms,id,row.Id("fixed_side_id"),row.Id("floating_side_id"),ct,markets));}
     catch(MarketDataException error) when(ValuationStatus.PositionSpecific(error)){unvalued.Add(key,error.Code);}
    if(values.TryGetValue(key,out var unitValue))
    {
     if(unitValue.Instrument.InstrumentId!=id)throw Error("instrument_identity","Equivalent retained terms cannot silently replace an exact published instrument identity.");
     var value=Scale(unitValue,terms);ValidateProjections(value,row);
     // A mark is the floating side's one reset price; an averaged side has several and so no mark.
     held=new(Exact(value.ExactPresentValue),value.Sides[1].Payments[0].Cashflow.Prices is {Length:1} resets?resets[0].ExactPrice:null,null,"Projection",null);
    }
    else held=new(null,null,unvalued[key],"Projection",null);
   }
   var holding=HoldingRow(source,row,book,terms,instrument,held);
   if(held.Reason is {} unvaluedReason)unvaluedRows.Add((row,unvaluedReason));
   result.Add(holding);
  }
  // Request-local work: each valuation attempted, whether or not the close could value it.
  return new(result.ToArray(),values.Count+unvalued.Count+bindingValuations,markets.IsEmpty?0:1,unvaluedRows.ToImmutable());
 }
 // What a held swap's valuation at the close states: its value, its mark when it has one curve price, or the position-specific
 // reason the close cannot value it; whether it is valued as a projection or by its settlement lifecycle, and that lifecycle's measures.
 private sealed record HeldValue(string? PresentValue,ExactUnitPrice? Mark,string? Reason,string Settlement,object? Lifecycle);
 // A held swap's Blotter row: its booked facts from its retained terms and resolved instrument, and what its valuation states.
 // A scheduled swap states no single quantity or mark, a fixed price only when every fixed period shares it, its delivery as the
 // envelope of its periods and, when its periods project from several curves, those markets together. A swap valued by its
 // settlement lifecycle states no curve mark.
 private JsonElement HoldingRow(PreviewData source,JsonElement row,JsonElement book,SwapWriteRequest terms,SidedInstrument instrument,HeldValue held)
 {
  var scheduled=terms.Scheduled;
  var plans=instrument.Sides.SelectMany(s=>((SidePaymentSchedule)s.Side.GenerateSchedule()).Payments).ToArray();
  var fixedPrices=((SidePaymentSchedule)instrument.Sides[0].Side.GenerateSchedule()).Payments.SelectMany(p=>p.Prices).Select(p=>p.FixedPrice).Distinct().ToArray();
  var commonFixedPrice=fixedPrices.Length==1?fixedPrices[0]?.Value.ToString():null;
  var projectionCurves=scheduled is null?null:new[]{scheduled.First,scheduled.Second}.SelectMany(s=>s.Periods).Select(p=>p.Floating?.Curve).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray();
  var marketKey=projectionCurves is {Length:>1}?JsonSerializer.Serialize(projectionCurves):null;
  var marketLabel=marketKey is null?null:string.Join(" / ",projectionCurves!.Select(code=>One(source.Definitions["curves"].Where(c=>c.Text("code")==code)).Text("name")));
  var quote=SideComponents.Floating(instrument.Sides[1].Side).First().Quote;
  // A scheduled swap's binding holds its first side's canonical total, which is no single quantity of the trade.
  var quantity=scheduled is null?new Quantity(ExactDecimal.Parse(Required(terms.Quantity)),instrument.UnitQuantity.Specification).Amount.ToString():null;
  var currency=One(source.References["currencies"].Where(c=>c.Text("code")==instrument.Currency.Code));
  var specification=One(source.References["quantity_specifications"].Where(q=>q.Text("code")==instrument.UnitQuantity.Specification.Code));
  var unit=One(source.References["units"].Where(u=>u.Text("code")==specification.Text("unit_code")));
  return Json(new{booking=TradeParties.State(manifest.CatalogId,row),bookingFacts=TradeAnnotations.Facts(row),traderId=row.Id("booker_id"),trader=row.Text("booker_name"),traderCode=row.Text("booker_login"),memo=row.OptionalText("memo"),tradeId=row.Id("trade_id"),tradeReference=TradingReference.Trade(row),instrumentId=row.Id("instrument_id"),instrumentReference=TradingReference.Instrument(row),bindingId=row.Id("binding_id"),
   revision=row.Text("revision"),bookId=row.Id("book_id"),book=book.Text("name"),portfolioId=book.Id("portfolio_id"),portfolio=book.Text("portfolio"),instrumentType="financial_swap",definitionRevisionId=row.Id("definition_revision_id"),
   contractId=row.Id("instrument_id"),contractIdentityKind=scheduled is null?"swap_instrument":"scheduled_swap_instrument",curve=row.Text("curve_code"),marketKey,marketLabel,projectionCurves,commodity=source.Commodity(row.Text("curve_code")),direction=Direction(instrument),
   deliveryFrom=scheduled is null?row.Text("floating_from"):Date(plans.Min(p=>p.DeliveryFrom)),deliveryTo=scheduled is null?row.Text("floating_to"):Date(plans.Max(p=>p.DeliveryTo)),
   quoteMultiplier=quote.Multiplier.ToString(),lots=(string?)null,quantity,unit=unit.Text("symbol"),quantitySpecification=instrument.UnitQuantity.Specification,currencyReference=instrument.Currency,
   executedAt=row.Text("booked_at"),executionPrice=(string?)null,fixedPrice=scheduled is null?row.OptionalText("fixed_price"):commonFixedPrice,
   mark=held.Mark is null?null:ExactPrice(held.Mark),markMinusPrice=MarkMinusPrice(instrument,held.Mark),executionPnl=(string?)null,
   presentValue=held.PresentValue,currency=currency.Text("display_code"),settlement=held.Settlement,lifecycle=held.Lifecycle,unavailableReason=held.Reason});
 }
}
