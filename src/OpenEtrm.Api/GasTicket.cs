using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TicketSideDraft(string Kind,string Code,ImmutableDictionary<string,string> Values)
{
 [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]public SideCalendarPolicyInput? CalendarPolicy { get; init; }
}
public sealed record TicketDraft(string Product,int Version,ImmutableDictionary<string,string> Trade,ImmutableArray<TicketSideDraft> Sides)
{
 internal static TicketDraft Require(TicketDraft? draft)=>draft??throw new MarketDataException("ticket_input","Supply the complete ticket draft.");
}

public sealed record TicketChoice(string Value,string Label);
public sealed record TicketSource(string Kind,string Label);
public sealed record TicketLookup(string Value,TicketSource Source);
public sealed record TicketChoices(ImmutableArray<TicketChoice> Choices,TicketLookup? StartingValue);

public static class GasTicket
{
 const string Physical="physical";
 public static TicketChoice AssignedForecastChoice{get;}=new("assigned","Assigned location curve");
 // The composition role each side kind books as.
 static readonly ImmutableDictionary<string,string> Roles=new Dictionary<string,string>{[Physical]="physical",["cash"]="consideration",["fee"]="fee",["floating"]="indexed_consideration"}.ToImmutableDictionary();
 public static TicketDescription Description{get;}=WithCalendars(TicketDescriptions.Validate(new("physical_gas",1,"flow",
  [Trade("book","Book","Book that holds the trade.","choice",choices:"books"),
   Trade("legal_entity","Legal entity","Our legal entity on the trade.","choice",choices:"legal_entities",required:false),
   Trade("counterparty","Counterparty","The other party to the trade.","choice",choices:"counterparties",required:false),
   Trade("gas_setup","Gas setup","Published gas conventions and market assignments that price the trade; empty where the context resolves none and the reference conventions apply.","choice",choices:"gas_setups",required:false),
   new("trade_date","Trade date","Date the trade is booked.","date",null,"info",false,null,null,[],null,null,"Assigned when booked: the UTC date of execution."),
   Soon("force_majeure","Force majeure","Force majeure terms of the master agreement the trade is under.","text","The executed document and special provisions are available under Legal entities & counterparties."),
   Soon("credit_terms","Credit terms","Credit terms of the master agreement the trade is under.","text","The credit desk records clearance for this entity, counterparty and product under Legal entities & counterparties.")],
  [new(Physical,"Physical","gas",GasCompositionInstrument.MinPhysicalSides,1,GasCompositionInstrument.MaxPhysicalSides,
    [Field("direction","Direction","Whether gas is delivered or received.","choice","direction",choices:"physical_direction"),
     Field("delivery_from","From","First gas day of delivery.","date","start") with{Lookup="entry_from"},
     Field("delivery_to","To · exclusive","End of delivery, exclusive.","date","end") with{Lookup="entry_to"},
     Field("product","Product","Service level of the gas.","choice","product",choices:"gas_products"),
     Field("quantity","Quantity","Quantity per the chosen basis, in the unit the side's gas path is quoted in. Taken from the link when the side is linked.","decimal","quantity",unit:"quantity",required:false,rule:"Derived from the link source when a link is set.") with{Lookup="entry_quantity"},
     Field("forecast_curve","Forecast curve","Published curve used for future prices; the assigned index and fixing source remain contractual.","choice",choices:"forecast_curves",required:false) with{DependsOn=["delivery_from","delivery_to","product","delivery_point"]},
     Info("price","Price","Market price at the delivery point.","price","market_price"),
     Info("payment","Payment","Sides that pay for this gas.","payment","paid_through"),
     Field("quantity_policy","Valuation quantity","Choose whether value and accrued cash use the contract quantity or selected scheduled and allocated delivery quantities.","choice",choices:"quantity_policies"),
     Field("quantity_basis","Quantity basis","Whether the quantity applies to each operating day or to the whole period.","choice",choices:"quantity_basis"),
     Field("pipeline","Pipeline","Pipeline the gas moves on.","choice",choices:"pipelines"),
     Field("zone","Zone","Pipeline zone.","choice",choices:"zones",dependsOn:"pipeline"),
     Field("delivery_point","Delivery point","Point in the zone where gas changes hands.","choice",choices:"delivery_points",dependsOn:"zone"),
     Info("gas_day_start","Gas day start","Hour the gas day starts at this point.",null,"gas_day_start"),
     Field("link_source","Link source","Physical side whose quantity this side follows.","choice",choices:"physical_sides",required:false),
     Field("link_formula","Link formula","How fuel loss relates this side to its source.","choice",choices:"link_formulas",required:false,dependsOn:"link_source"),
     Field("link_fraction","Link fraction","Fuel fraction used by the link formula.","decimal",required:false,dependsOn:"link_source"),
     Soon("swing_daily_min","Daily minimum","Least quantity that can be called for on a gas day.","decimal","Will set the least quantity that can be called for on a gas day."),
     Soon("swing_daily_max","Daily maximum","Most quantity that can be called for on a gas day.","decimal","Will set the most quantity that can be called for on a gas day."),
     Soon("swing_range","Swing range","How far a day's quantity may move from the base quantity, as a percentage of it.","decimal","Will set how far a day's quantity may move from the base quantity, as a percentage of it."),
     Soon("swing_notice_deadline","Swing notice deadline","Latest time a change to a day's quantity can be called.","text","Will set the latest time a change to a day's quantity can be called."),
     Soon("nomination_cycle","Nomination cycle","Pipeline cycle in which the side's quantities are nominated.","choice","Will name the pipeline cycle in which this side's quantities are nominated."),
     Soon("nominating_party","Nominating party","Party that nominates the gas to the pipeline.","choice","Will name the party that nominates this gas to the pipeline."),
     Soon("heat_content","Heat content · Btu per cubic foot","Energy in each cubic foot of the gas.","decimal","Will hold the gas's heat content in Btu per cubic foot, which converts its volume to energy."),
     Soon("quality_spec","Quality specification","Quality the gas must meet at the point.","text","Will name the quality the gas must meet at this point.")],
    // A booked side's day quantities are recorded as daily quantity revisions (GasDailyQuantities); a new trade's come from its terms.
    [new("quantity","Quantity","quantity","quantity",["booked"]),
     new("volume","Volume",null,"quantity",[],"Will show the day's gas volume beside its energy quantity, converted with the side's heat content."),
     new("market_price","Market price",null,"price",[]),new("amount","Amount",null,"money",[])]),
   // A new trade opens with the payment for its gas; a fee is added when one applies.
   CashKind("cash","Cash","cash",false,1),
   CashKind("fee","Fee","fee",true,0),
   IndexedCashKind()],
  // Payment and fee sides are the composition's cash sides.
  [new(["cash","fee","floating"],GasCompositionInstrument.MaxCashSides),new([Physical,"cash","fee","floating"],GasCompositionInstrument.MaxSides)],
  [new("net_cash","Net cash",null,"money",[]),new("payment_at","Payment date",null,"payment_date",[])],
  ["term","month","day"],[new("links","Quantity links"),
   new("imbalance","Imbalance","Will compare each gas day's nominated and actual quantities, with the imbalance, its cash-out price and its amount.")],["new","booked","amend"])));

 static TicketDescription WithCalendars(TicketDescription description)=>description with{Sides=[..description.Sides.Select(kind=>kind with
  {CalendarPurposes=new(true,kind.Kind=="floating",kind.Kind!=Physical)})]};
 // Preserve an absent field through completed-command lookup. New generation
 // requires the policy in the same managed resolver as the direct API.
 static SideCalendarPolicyInput Calendar(TicketSideDraft side)=>side.CalendarPolicy!;

 public static TicketSideKind SideKind(string role)=>Description.Sides.SingleOrDefault(kind=>Roles.GetValueOrDefault(kind.Kind)==role)??
  throw new MarketDataException("ticket_description",$"No {Description.Product} side kind books as role '{role}'.");

 static TicketField Trade(string id,string label,string meaning,string type,string? choices=null,bool required=true)=>
  new(id,label,meaning,type,null,"editable",required,null,choices,[],null,null,null);
 static TicketField Field(string id,string label,string meaning,string type,string? band=null,string? choices=null,string? unit=null,bool required=true,string? rule=null,string? dependsOn=null)=>
  new(id,label,meaning,type,unit,"editable",required,band,choices,dependsOn is null?[]:[dependsOn],null,null,rule);
 static TicketField Info(string id,string label,string meaning,string? band,string lookup)=>
  new(id,label,meaning,"text",null,"info",false,band,null,[],lookup,null,null);
 static TicketField Soon(string id,string label,string meaning,string type,string soon)=>new(id,label,meaning,type,null,"soon",false,null,null,[],null,soon,null);

 static TicketSideKind CashKind(string kind,string pill,string colour,bool fee,int initial)
 {
  TicketField Own(string id,string label,string meaning,string type,string? band=null,string? choices=null,bool required=true,string? lookup=null)=>
   new($"{kind}_{id}",label,meaning,type,null,"editable",required,band,choices,[],lookup,null,null);
  TicketField Level(string id,string label,params string[] choices)=>Own(id,label,"Daily quantity level in the trade unit.","decimal") with{VisibleWhen=new($"{kind}_part",[..choices])};
  return new(kind,pill,colour,0,initial,GasCompositionInstrument.MaxCashSides,
   [Own("direction","Direction","Whether we pay or receive.","choice","direction","cash_direction") with{RefreshOn=[$"{kind}_source","direction"]},
    Own("start","From","First gas day priced by this Side.","date","start",lookup:"entry_source_from") with{DependsOn=[$"{kind}_source"],RefreshOn=["delivery_from"]},
    Own("end","To · exclusive","End of the priced delivery period, exclusive.","date","end",lookup:"entry_source_to") with{DependsOn=[$"{kind}_source"],RefreshOn=["delivery_to"]},
    Own("source","Quantity source","Physical side whose quantity this amount is charged on.","choice","quantity","physical_sides"),
    Own("stage","Quantity stage","Receipt, delivery or fuel quantity of the source side.","choice",choices:"quantity_stages"),
    Own("share","Share of quantity","Positive share of the source quantity priced by this Side.","decimal",lookup:"entry_share"),
    Own("part","Quantity part","The portion of each source day quantity priced by this Side.","choice",choices:"quantity_parts"),
    Level("part_lower","Lower daily level","above","between","shortfall"),Level("part_upper","Upper daily level","up_to","between"),
    Own("price","Price","Price per unit of the source quantity.","decimal","price",required:!fee,lookup:fee?null:"entry_price"),
    Own("payment_at","Payment date","Contractual UTC date on which the amount is due.","payment_date","payment",lookup:"entry_payment"),
    ..fee?[Own("tariff","Tariff","Configured usage tariff that sets the price.","choice",choices:"usage_tariffs",required:false,lookup:"eligible_tariff")]:Array.Empty<TicketField>()],
   [new("quantity","Quantity","quantity","quantity",[]),new("price","Price",null,"price",[]),new("amount","Amount",null,"money",[])]);
 }

 static TicketSideKind IndexedCashKind()
 {
  var cash=CashKind("floating","Index payment","floating",false,0);
  return cash with{Fields=[..cash.Fields.Where(f=>f.Id is not("floating_price" or "floating_payment_at")),
   Field("floating_pricing","Pricing","How the payment selects its index publications.","choice",choices:"index_pricing",dependsOn:"floating_source"),
   Field("floating_index","Pricing index","Named index that determines the payment price.","choice",choices:"pricing_indices",dependsOn:"floating_source") with{VisibleWhen=new("floating_pricing",["named_daily","named_monthly","retained_daily"])},
   Field("floating_forecast_curve","Forecast curve","Published curve used for unfixed daily index prices.","choice",choices:"forecast_curves") with{DependsOn=["floating_source","floating_start","floating_end","delivery_from","delivery_to","delivery_point","product"],RefreshOn=["forecast_curve","floating_pricing"]},
   Field("floating_spread","Index spread","Contractual adder to the index in the source quote units.","decimal","price") with{Lookup="entry_spread"},
   Field("floating_payment_day","Payment day","Contractual calendar date in the following month, without a holiday adjustment.","choice","payment",choices:"payment_days")]};
 }

 public static GasCompositionRequest Request(TicketDraft draft,Guid snapshotId,string knowledgeMode,Guid operationId,Guid definitionRevisionId)=>Request(draft,snapshotId,knowledgeMode,operationId,definitionRevisionId,false);
 internal static GasCompositionRequest Request(TicketDraft draft,Guid snapshotId,string knowledgeMode,Guid operationId,Guid definitionRevisionId,bool retained)
 {
  RequireShape(draft);
  Check(Description.TradeFields,draft.Trade,"trade",null);
  foreach(var side in draft.Sides)
  {
   var kind=Description.Sides.SingleOrDefault(k=>k.Kind==side.Kind)??throw Bad($"Unknown side kind '{side.Kind}'.");
   if(kind.Soon is not null)throw Bad($"A {kind.Pill.ToLowerInvariant()} side is coming soon; gas booking does not take one yet.");
   Check(kind.Fields,side.Values,$"{side.Kind} side '{side.Code}'",side.Code);
   if(string.IsNullOrWhiteSpace(side.Code))throw Bad($"Each {side.Kind} side needs a code.");
  }
  if(draft.Sides.GroupBy(s=>s.Code).Any(g=>g.Count()>1))throw Bad("Side codes must be unique.");
  foreach(var kind in Description.Sides)
  {
   var count=draft.Sides.Count(s=>s.Kind==kind.Kind);
   if(count<kind.Min||count>kind.Max)throw Bad($"Use {kind.Min} to {kind.Max} {kind.Kind} sides, not {count}.");
  }
  foreach(var limit in Description.SideLimits)
  {
   var count=draft.Sides.Count(s=>limit.Kinds.Contains(s.Kind));
   if(count>limit.Max)throw Bad($"Use at most {limit.Max} {string.Join(" and ",limit.Kinds)} sides together, not {count}.");
  }
  var physical=draft.Sides.Where(s=>s.Kind==Physical).ToArray();
  if(physical.Any(s=>s.Values.ContainsKey("link_source")))
   foreach(var side in physical.Where(s=>s.Values["quantity_policy"]==GasDeliveryQuantityStore.Policy))
    throw On(side.Code,"quantity_policy","Delivery schedule valuation is unavailable on a composition with a quantity link. Choose Contract quantity.");
  var codes=physical.Select(s=>s.Code).ToHashSet();
  string Source(TicketSideDraft side,string id)=>codes.Contains(side.Values[id])?side.Values[id]:throw On(side.Code,id,$"{side.Kind} side '{side.Code}' names '{side.Values[id]}', which is not a physical side code.");
  var t=draft.Trade;
  var (entity,entityRevision)=Versioned(t,"legal_entity");var (party,partyRevision)=Versioned(t,"counterparty");
  return new(snapshotId,PhysicalGasStore.Convention,knowledgeMode,draft.Sides.Any(s=>s.Kind=="floating")?GasCompositionStore.IndexedProfile:GasCompositionStore.Profile,operationId,Id(t,"book")!.Value,definitionRevisionId,Id(t,"gas_setup"),false,entity,entityRevision,party,partyRevision,
   [..physical.Select(s=>PhysicalRequest(s,Source))],
   [..draft.Sides.Where(s=>s.Kind!=Physical).Select(s=>CashRequest(s,physical,Source,retained))]);
 }

 static GasCashRequest CashRequest(TicketSideDraft side,TicketSideDraft[] physical,Func<TicketSideDraft,string,string> source,bool retained)
 {
  var v=side.Values;var kind=side.Kind;string Field(string name)=>$"{kind}_{name}";
  var owner=physical.Single(s=>s.Code==source(side,Field("source")));
  DateOnly Date(string name)
  {
   if(!DateOnly.TryParseExact(v[Field(name)],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day))throw On(side.Code,Field(name),"Enter a gas day as YYYY-MM-DD.");
   return day;
  }
  var from=Date("start");var to=Date("end");
  if(from<DateWorkspace.Date(owner.Values["delivery_from"])||from>=DateWorkspace.Date(owner.Values["delivery_to"]))throw On(side.Code,Field("start"),"The first payment day must lie within its source's term.");
  if(to>DateWorkspace.Date(owner.Values["delivery_to"])||to<=from)throw On(side.Code,Field("end"),"The exclusive end must follow the start and lie within its source's term.");
  try{GasCompositionStore.PaymentShare(v[Field("share")]);}catch(MarketDataException error){throw On(side.Code,Field("share"),error.Message);}
  var part=Part(side);
  if(part is not null&&physical.Any(p=>p.Values.ContainsKey("link_source")))throw On(side.Code,Field("part"),"A quantity part requires independent physical sources and a composition without fuel links.");
  GasIndexedCashRequest? indexed=null;
  if(kind=="floating")
  {
   var convention=v["floating_pricing"] switch{"point_publications"=>GasIndexedCash.Point,"named_daily"=>GasIndexedCash.Observation,"named_monthly"=>GasIndexedCash.Monthly,"retained_daily" when retained=>"daily_published_index_v1",_=>throw On(side.Code,"floating_pricing","Choose an offered pricing convention; retained strict daily pricing is read-only.")};
   indexed=new(convention==GasIndexedCash.Point?null:v["floating_index"],convention,GasIndexedCash.Payment,PaymentDay(side),v["floating_spread"],Forecast(v["floating_forecast_curve"]));
  }
  return new(side.Code,Roles[kind],v[Field("direction")],owner.Code,v[Field("stage")],v.GetValueOrDefault(Field("price")),v.GetValueOrDefault(Field("tariff")),v.GetValueOrDefault(Field("payment_at")),indexed)
  {CalendarPolicy=Calendar(side),Share=v[Field("share")],Part=part,DeliveryFrom=v[Field("start")]==owner.Values["delivery_from"]?null:v[Field("start")],DeliveryTo=v[Field("end")]==owner.Values["delivery_to"]?null:v[Field("end")]};
 }
 static GasQuantityPartRequest? Part(TicketSideDraft side)
 {
  var prefix=side.Kind+"_part";var v=side.Values;
  string Level(string suffix)
  {
   var value=v[prefix+suffix];if(ExactDecimal.Parse(value).CompareTo(default)<0)throw On(side.Code,prefix+suffix,"A daily level must be nonnegative.");return value;
  }
  var part=v[prefix] switch
  {
   "whole"=>null,"up_to"=>new GasQuantityPartRequest("band","0",Level("_upper")),"above"=>new("band",Level("_lower"),null),
   "between"=>new("band",Level("_lower"),Level("_upper")),"shortfall"=>new("shortfall",Level("_lower"),null),
   _=>throw On(side.Code,prefix,"Choose an offered quantity part.")
  };
  if(part?.Upper is {} upper&&ExactDecimal.Parse(upper).CompareTo(ExactDecimal.Parse(part.Lower))<=0)throw On(side.Code,prefix+"_upper","The upper daily level must exceed the lower level.");
  return part;
 }

 static int PaymentDay(TicketSideDraft side)=>int.TryParse(side.Values["floating_payment_day"],NumberStyles.None,CultureInfo.InvariantCulture,out var day)&&day is >=1 and <=28?day:
  throw On(side.Code,"floating_payment_day","Choose a following-month UTC payment day from 1 through 28.");

 static GasPhysicalRequest PhysicalRequest(TicketSideDraft side,Func<TicketSideDraft,string,string> source)
 {
  var v=side.Values;var link=Link(side,source);
  return new(side.Code,v["pipeline"],v["zone"],v["delivery_point"],v["product"],v["direction"],v["delivery_from"],v["delivery_to"],v["quantity_basis"],Quantity(side,link),link,Forecast(v.GetValueOrDefault("forecast_curve"))){CalendarPolicy=Calendar(side),QuantityPolicy=v["quantity_policy"] switch{"contract"=>null,GasDeliveryQuantityStore.Policy=>GasDeliveryQuantityStore.Policy,_=>throw On(side.Code,"quantity_policy","Choose an offered valuation quantity policy.")}};
 }

 static readonly JsonSerializerOptions CurveJson=new(JsonSerializerDefaults.Web);
 public static string ForecastValue(CustomCurveProjectionInput source)=>JsonSerializer.Serialize(source,CurveJson);
 static CustomCurveProjectionInput? Forecast(string? value)
 {
  if(value is null||value=="assigned")return null;
  try{return JsonSerializer.Deserialize<CustomCurveProjectionInput>(value,CurveJson)??throw Bad("Select a published forecast curve.");}
  catch(JsonException){throw Bad("Select an exact published forecast curve version and query policy.");}
 }
 static GasQuantityLinkRequest? Link(TicketSideDraft side,Func<TicketSideDraft,string,string> source)
 {
  var v=side.Values;
  if(!v.ContainsKey("link_source"))
   return v.ContainsKey("link_formula")||v.ContainsKey("link_fraction")?throw Bad($"Physical side '{side.Code}' sets a link formula or fraction without a link source."):null;
  if(!v.ContainsKey("link_formula")||!v.ContainsKey("link_fraction"))throw Bad($"Physical side '{side.Code}' needs a link formula and fraction with its link source.");
  return new(source(side,"link_source"),v["link_formula"],v["link_fraction"]);
 }

 static string? Quantity(TicketSideDraft side,GasQuantityLinkRequest? link)=>
  link is null?side.Values.GetValueOrDefault("quantity")??throw On(side.Code,"quantity",$"Physical side '{side.Code}' needs a quantity or a link source."):
  side.Values.ContainsKey("quantity")?throw On(side.Code,"quantity",$"Physical side '{side.Code}' takes its quantity from its link source; leave quantity empty."):null;

 static void Check(ImmutableArray<TicketField> fields,ImmutableDictionary<string,string> values,string scope,string? side)
 {
  foreach(var (id,value) in values)
  {
   var field=fields.SingleOrDefault(f=>f.Id==id)??throw On(side,id,$"The {scope} has no field '{id}'.");
   if(field.VisibleWhen is {} condition&&!condition.Matches(values))throw On(side,id,$"Field '{id}' is not used by the selected choice.");
   if(field.State!="editable")throw On(side,id,$"Field '{id}' on the {scope} is {field.State} and takes no value.");
   if(string.IsNullOrWhiteSpace(value))throw On(side,id,$"Field '{id}' on the {scope} is blank.");
   // A decimal field holds one exact decimal, read by the parser the booking reads it with.
   if(field.Type=="decimal")try{ExactDecimal.Parse(value);}catch(MarketDataException error){throw new TicketFieldException(error.Code,error.Message,side,id);}
  }
  foreach(var field in fields.Where(f=>f.Required&&(f.VisibleWhen is null||f.VisibleWhen.Matches(values))&&!values.ContainsKey(f.Id)))throw On(side,field.Id,$"The {scope} needs a value for '{field.Id}'.");
 }

 static Guid? Id(ImmutableDictionary<string,string> values,string id)=>values.TryGetValue(id,out var v)?Guid.TryParse(v,out var guid)?guid:throw Bad($"Field '{id}' must be an id."):null;

 static (Guid?,int?) Versioned(ImmutableDictionary<string,string> values,string id)
 {
  if(!values.TryGetValue(id,out var v))return(null,null);
  var parts=v.Split('@');
  return parts.Length==2&&Guid.TryParse(parts[0],out var guid)&&int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var revision)?(guid,revision):throw Bad($"Field '{id}' must be '<id>@<revision>'.");
 }

 // A malformed client draft refuses as ticket input, never as a missing object.
 internal static void RequireShape(TicketDraft? draft)
 {
  if(draft?.Trade is null||draft.Sides.IsDefault||draft.Sides.Any(side=>side?.Values is null))throw Bad("Send the draft's trade values and each side with its values.");
  if(draft.Product!=Description.Product||draft.Version!=Description.Version)throw Bad($"Draft is for {draft.Product} v{draft.Version}, not {Description.Product} v{Description.Version}.");
 }

 // The draft a booked request was made from: the inverse of Request.
 public static TicketDraft Draft(GasCompositionRequest terms)
 {
  var trade=new Dictionary<string,string>{["book"]=terms.BookId.ToString()};
  if(terms.GasSetupId is {} setup)trade["gas_setup"]=setup.ToString();
  void Party(string id,Guid? party,int? revision)
  {
   if(party is null!=revision is null)throw Bad($"The booked '{id}' needs both its id and revision.");
   if(party is not null)trade[id]=$"{party}@{revision!.Value.ToString(CultureInfo.InvariantCulture)}";
  }
  Party("legal_entity",terms.LegalEntityId,terms.LegalEntityRevision);Party("counterparty",terms.CounterpartyId,terms.CounterpartyRevision);
  static ImmutableDictionary<string,string> Values(params (string Id,string? Value)[] values)=>values.Where(v=>v.Value is not null).ToImmutableDictionary(v=>v.Id,v=>v.Value!);
  var physical=terms.PhysicalSides.Select(p=>new TicketSideDraft(Physical,p.Code,Values(("direction",p.Direction),("delivery_from",p.DeliveryFrom),("delivery_to",p.DeliveryTo),("product",p.Product),("quantity",p.Quantity),
   ("forecast_curve",p.ForecastCurve is {} curve?ForecastValue(curve):null),("quantity_policy",p.QuantityPolicy??"contract"),("quantity_basis",p.QuantityBasis),("pipeline",p.Pipeline),("zone",p.Zone),("delivery_point",p.DeliveryPoint),("link_source",p.Link?.Source),("link_formula",p.Link?.Formula),("link_fraction",p.Link?.Fraction))){CalendarPolicy=p.CalendarPolicy});
  var cash=terms.CashSides.Select(s=>{var kind=SideKind(s.Role).Kind;var owner=terms.PhysicalSides.Single(p=>p.Code==s.Source);
   var part=s.Part is null?"whole":s.Part.Kind=="shortfall"?"shortfall":s.Part.Upper is null?"above":ExactDecimal.Parse(s.Part.Lower).CompareTo(default)==0?"up_to":"between";
   return new TicketSideDraft(kind,s.Code,Values(($"{kind}_direction",s.Direction),($"{kind}_source",s.Source),($"{kind}_stage",s.Stage),($"{kind}_price",s.Price),($"{kind}_payment_at",s.PaymentAt),($"{kind}_tariff",s.TariffCode),
    ($"{kind}_share",s.Share??"1"),($"{kind}_part",part),($"{kind}_part_lower",part is "above" or "between" or "shortfall"?s.Part!.Lower:null),($"{kind}_part_upper",part is "up_to" or "between"?s.Part!.Upper:null),
    ($"{kind}_start",s.DeliveryFrom??owner.DeliveryFrom),($"{kind}_end",s.DeliveryTo??owner.DeliveryTo),
    ("floating_pricing",s.Indexed?.ObservationConvention switch{GasIndexedCash.Point=>"point_publications",GasIndexedCash.Observation=>"named_daily",GasIndexedCash.Monthly=>"named_monthly","daily_published_index_v1"=>"retained_daily",null=>null,_=>throw Bad("The retained index convention is unsupported.")}),
    ("floating_index",s.Indexed?.Index),("floating_forecast_curve",s.Indexed is {} index?index.ForecastCurve is {} forecast?ForecastValue(forecast):"assigned":null),("floating_payment_day",s.Indexed?.PaymentDay.ToString(CultureInfo.InvariantCulture)),("floating_spread",s.Indexed?.Spread))){CalendarPolicy=s.CalendarPolicy};});
  return new(Description.Product,Description.Version,trade.ToImmutableDictionary(),[..physical,..cash]);
 }

 // The draft a booked request reopens as. The draft must map back to exactly the stored terms;
 // terms it cannot express refuse rather than open as a different trade.
 public static TicketDraft Reopen(GasCompositionRequest terms)
 {
  var draft=Draft(terms);
  var mapped=Request(draft,terms.SnapshotId,terms.KnowledgeMode,terms.OperationId,terms.DefinitionRevisionId,true) with
  {TradeId=terms.TradeId,BindingId=terms.BindingId,ExpectedRevision=terms.ExpectedRevision,Reason=terms.Reason,AmendmentPolicy=terms.AmendmentPolicy};
  return SameTerms(mapped,terms)?draft:throw Bad("The booked terms hold values the deal ticket cannot express, so the trade does not open in it.");
 }

 // Normalize only the presentation of predecessor whole-quantity/full-term terms.
 // A completed retry returns its original command bytes, never this normalized view.
 internal static bool SameTerms(GasCompositionRequest a,GasCompositionRequest b)
 {
  GasCompositionRequest Normalize(GasCompositionRequest request)=>request with{CashSides=[..request.CashSides.Select(c=>{
   var source=request.PhysicalSides.Single(p=>p.Code==c.Source);
   return c with{Share=c.Share??"1",DeliveryFrom=c.DeliveryFrom??source.DeliveryFrom,DeliveryTo=c.DeliveryTo??source.DeliveryTo,
    Part=c.Part is {Kind:"band",Upper:not null} part&&ExactDecimal.Parse(part.Lower).CompareTo(default)==0?part with{Lower="0"}:c.Part};})]};
  return JsonSerializer.Serialize(Normalize(a))==JsonSerializer.Serialize(Normalize(b));
 }
 internal static TicketDraft RetryDraft(TicketDraft draft,GasCompositionRequest? original)
 {
  var prior=original is null?null:Draft(original);
  return draft with{Sides=[..draft.Sides.Select(side=>{
   var values=side.Values;var old=prior?.Sides.SingleOrDefault(s=>s.Code==side.Code&&s.Kind==side.Kind)?.Values;
   void Restore(string field,string? fallback=null)
   {if(!values.ContainsKey(field)&&(old?.GetValueOrDefault(field)??fallback) is {} value)values=values.Add(field,value);}
   if(side.Kind==Physical)Restore("quantity_policy","contract");
   else if(Roles.ContainsKey(side.Kind))
   {
    var owner=draft.Sides.SingleOrDefault(s=>s.Kind==Physical&&s.Code==values.GetValueOrDefault($"{side.Kind}_source"));
    Restore($"{side.Kind}_share","1");Restore($"{side.Kind}_part","whole");
    Restore($"{side.Kind}_start",owner?.Values.GetValueOrDefault("delivery_from"));Restore($"{side.Kind}_end",owner?.Values.GetValueOrDefault("delivery_to"));
    Restore($"{side.Kind}_part_lower");Restore($"{side.Kind}_part_upper");
    if(side.Kind=="floating")
    {
     Restore("floating_pricing");
     if(values.GetValueOrDefault("floating_pricing")=="point_publications"&&values.GetValueOrDefault("floating_index")=="@location_publications_v1")values=values.Remove("floating_index");
    }
   }
   return side with{Values=values};})]};
 }
 internal static bool Retired(GasCompositionRequest terms)=>terms.CashSides.Any(c=>c.Indexed?.ObservationConvention=="daily_published_index_v1");

 // A single-point trade's retained terms as the composition they are: one physical side, the payment for its gas and an optional fee,
 // coded as a new ticket codes the first side of each kind. The single-point profile charges both on the side's contractual delivery
 // quantity and pays them at its one payment instant. Terms it does not admit refuse here or in Reopen; none is filled in.
 public static GasCompositionRequest SinglePoint(PhysicalGasTerms t)
 {
  static string Code(string kind)=>$"{kind}-1";
  var payment=t.Direction switch{"receive"=>"pay","deliver"=>"receive",_=>throw Bad($"The booked gas direction '{t.Direction}' is neither receive nor deliver.")};
  var fee=t.UsageFee is not null||t.UsageTariffCode is not null;
  if(fee!=(t.UsageFeeDirection is not null)||t.UsageFee is not null&&t.UsageTariffCode is not null)throw Bad("The booked fee needs its direction and exactly one of a price or a tariff.");
  GasCashRequest Charge(string kind,string direction,string? price,string? tariff)=>new(Code(kind),Roles[kind],direction,Code(Physical),"delivery",price,tariff,t.PaymentAt!){Share="1",CalendarPolicy=(kind=="fee"?t.FeeCalendarPolicy:t.ConsiderationCalendarPolicy)??throw new MarketDataException("side_calendar_policy","The stored cash Side requires explicit calendar terms.")};
  return new(t.SnapshotId,t.Convention!,t.KnowledgeMode!,GasCompositionStore.Profile,t.OperationId,t.BookId,t.DefinitionRevisionId,t.GasSetupId,t.Discounting,t.LegalEntityId,t.LegalEntityRevision,t.CounterpartyId,t.CounterpartyRevision,
   [new(Code(Physical),t.Pipeline!,t.Zone!,t.DeliveryPoint!,t.Product!,t.Direction,t.DeliveryFrom!,t.DeliveryTo!,t.QuantityBasis!,t.Quantity,null){CalendarPolicy=t.DeliveryCalendarPolicy??throw new MarketDataException("side_calendar_policy","The stored delivery Side requires explicit calendar terms.")}],
   [Charge("cash",payment,t.FixedPrice,null),..fee?[Charge("fee",t.UsageFeeDirection!,t.UsageFee,t.UsageTariffCode)]:Array.Empty<GasCashRequest>()]);
 }

 // Fixed lists the booking contract admits, and the lists read from the context's gas catalog.
 static readonly ImmutableDictionary<string,ImmutableArray<TicketChoice>> Fixed=new Dictionary<string,ImmutableArray<TicketChoice>>{
  ["quantity_parts"]=[new("whole","Whole quantity"),new("up_to","Up to a daily level"),new("above","Above a daily level"),new("between","Between two daily levels"),new("shortfall","Shortfall below a daily level")],
  ["index_pricing"]=[new("point_publications","Point publications"),new("named_daily","Named index, daily"),new("named_monthly","Named index, monthly")],
  ["forecast_curves"]=[AssignedForecastChoice],
  ["payment_days"]=[..Enumerable.Range(1,28).Select(n=>new TicketChoice(n.ToString(CultureInfo.InvariantCulture),n.ToString(CultureInfo.InvariantCulture)))],
  ["physical_direction"]=[new("receive","Receive"),new("deliver","Deliver")],["cash_direction"]=[new("pay","Pay"),new("receive","Receive")],
  ["quantity_policies"]=[new("contract","Contract quantity"),new(GasDeliveryQuantityStore.Policy,"Delivery schedule")],
  ["quantity_basis"]=[new("per_operating_day","Per operating day"),new("period_total_equal_days","Period total over equal days")],
  ["quantity_stages"]=[new("receipt","Receipt"),new("delivery","Delivery"),new("fuel","Fuel")],
  ["link_formulas"]=[new("none","None"),new("gross_less_fraction","Gross less fraction"),new("gross_divided_by_one_plus_fraction","Gross divided by one plus fraction")]}.ToImmutableDictionary();

 public static bool PhysicalReferenceField(TicketDraft draft,string? side,string field)=>
  Field(draft,side,field).Field.Choices is "pipelines" or "zones" or "delivery_points" or "gas_products";

 // Visible editable suggestions under the gas ticket entry contract. Book/import
 // validation still requires the resulting explicit terms and never applies defaults.
 public static TicketChoices ChoiceSet(TicketDraft draft,string? side,string field,ImmutableArray<TicketChoice> choices,JsonElement? catalog=null)
 {
  var (target,f)=Field(draft,side,field);
  string? value=f.Choices!="pricing_indices"&&choices.Length==1&&(f.Required||side is null||f.Id=="forecast_curve")?choices[0].Value:null;
  value??=f.Choices switch{
   "quantity_parts"=>"whole",
   "index_pricing" when catalog is {} data=>PointPublications(draft,target!,data)?"point_publications":"named_daily",
   "pricing_indices" when catalog is {} data=>Path(QuantitySource(draft,target!),data,Unresolved).GetProperty("valuation").Text("index"),
   "quantity_policies"=>GasDeliveryQuantityStore.Policy,"physical_direction"=>"receive","quantity_basis"=>"per_operating_day","quantity_stages"=>"delivery","payment_days"=>"10",
   "forecast_curves" when target?.Kind=="floating"&&target.Values.TryGetValue("floating_source",out var linked)=>draft.Sides.SingleOrDefault(s=>s.Code==linked)?.Values.GetValueOrDefault("forecast_curve")??"assigned",
   "cash_direction" when target is not null&&target.Values.TryGetValue($"{target.Kind}_source",out var source)=>
    draft.Sides.SingleOrDefault(s=>s.Code==source)?.Values.GetValueOrDefault("direction") switch{"receive"=>"pay","deliver"=>"receive",_=>null},
   _=>null};
  return new(choices,value is not null&&choices.Any(c=>c.Value==value)?new(value,new("suggestion",choices.Length==1?"Only eligible choice":"Suggested starting value")):null);
 }

 // Physical choices use reference relationships, independently of valuation coverage.
 internal static ImmutableArray<TicketChoice>? LocalChoices(TicketDraft draft,string? side,string field)
 {
  var(target,f)=Field(draft,side,field);
  if(f.Choices=="index_pricing"&&target?.Values.GetValueOrDefault("floating_pricing")=="retained_daily")return [..Fixed["index_pricing"],new("retained_daily","Retained daily index · read-only")];
  if(Fixed.TryGetValue(f.Choices??throw Bad($"Field '{field}' has no choice list."),out var fixedChoices))return fixedChoices;
  return f.Choices=="physical_sides"?[..draft.Sides.Where(s=>s.Kind==Physical&&!(f.Id=="link_source"&&s==target)).Select(s=>new TicketChoice(s.Code,s.Code))]:null;
 }
 internal static ImmutableArray<TicketChoice> BookChoices(IEnumerable<JsonElement> rows)=>
  [..rows.Select(b=>new TicketChoice(b.Text("id"),b.Text("name")))];
 internal static ImmutableArray<TicketChoice> PartyChoices(IEnumerable<JsonElement> rows)=>
  [..rows.Select(p=>PartyChoice(p.Id("id"),int.Parse(p.Text("revision"),CultureInfo.InvariantCulture),p.Text("name")))];
 internal static ImmutableArray<TicketChoice> PartyChoices(IEnumerable<TradeParties.Choice> rows)=>
  [..rows.Select(p=>PartyChoice(p.Id,p.Revision,p.Name))];
 static TicketChoice PartyChoice(Guid id,int revision,string name)=>new($"{id}@{revision.ToString(CultureInfo.InvariantCulture)}",name);
 internal static ImmutableArray<TicketChoice> SetupChoices(Guid? setup,int? publication)
 {
  if(setup is null&&publication is null)return [];
  if(setup is not {} id||id==Guid.Empty||publication is not >0)throw On(null,"gas_setup","The published gas setup needs its exact identity and publication order.");
  return [new(id.ToString(),$"Published setup {publication.Value.ToString(CultureInfo.InvariantCulture)}")];
 }

 public static ImmutableArray<TicketChoice> Choices(TicketDraft draft,string? side,string field,JsonElement catalog)
 {
  if(LocalChoices(draft,side,field) is {} local)return local;
  var (target,f)=Field(draft,side,field);
  var physical=PhysicalReferenceField(draft,side,field);
  if(target is not null&&!physical)RequireSetup(draft,catalog);
  var paths=physical?catalog.GetProperty("physicalReferences").EnumerateArray().ToArray():[];
  IEnumerable<TicketChoice> Distinct(IEnumerable<JsonElement> rows,string code,string name)=>rows.Select(r=>new TicketChoice(r.Text(code),r.Text(name))).Distinct();
  IEnumerable<TicketChoice> Parties(string list)=>PartyChoices(catalog.GetProperty(list).EnumerateArray());
  return[..f.Choices switch{
   "books"=>BookChoices(catalog.GetProperty("books").EnumerateArray()),
   "legal_entities"=>Parties("legalEntities"),"counterparties"=>Parties("counterparties"),
   "gas_setups"=>SetupChoices(catalog.OptionalText("gasSetupId") is {} setup?Guid.Parse(setup):null,
    catalog.OptionalText("gasSetupPublication") is {} publication?int.Parse(publication,CultureInfo.InvariantCulture):null),
   "pricing_indices"=>IndexChoices(draft,target!,catalog),
   "pipelines"=>Distinct(paths,"pipeline","pipelineName"),
   "zones"=>Distinct(paths.Where(p=>p.Text("pipeline")==Term(target!,"pipeline",Bad)),"zone","zoneName"),
   "delivery_points"=>Distinct(paths.Where(p=>p.Text("pipeline")==Term(target!,"pipeline",Bad)&&p.Text("zone")==Term(target!,"zone",Bad)),"deliveryPoint","deliveryPointName"),
   "gas_products"=>Distinct(paths,"product","productName"),
   "usage_tariffs"=>Tariffs(draft,target!,catalog,Bad).Select(t=>new TicketChoice(t.Text("code"),t.Text("name"))),
   _=>throw new MarketDataException("ticket_description",$"Field '{field}' names choice list '{f.Choices}', which {Description.Product} does not serve.")}];
 }

 static ImmutableArray<TicketChoice> IndexChoices(TicketDraft draft,TicketSideDraft target,JsonElement catalog)
 {
  var source=QuantitySource(draft,target);
  var path=Path(source,catalog,Unresolved);
  var valuation=path.GetProperty("valuation");
  if(valuation.ValueKind!=JsonValueKind.Object)throw Unresolved("This path has no pricing index assignment.");
  return [..catalog.GetProperty("pricingIndices").EnumerateArray().Where(index=>index.Text("product")==source.Values["product"]&&index.Text("quote")==valuation.Text("quote")).Select(index=>new TicketChoice(index.Text("code"),index.Text("name")))];
 }
 static bool PointPublications(TicketDraft draft,TicketSideDraft target,JsonElement catalog)
 {
  var valuation=Path(QuantitySource(draft,target),catalog,Unresolved).GetProperty("valuation");
  if(valuation.ValueKind!=JsonValueKind.Object)throw Unresolved("This path has no pricing assignment.");
  return valuation.TryGetProperty("cashPricingSource",out var source)&&source.ValueKind==JsonValueKind.String&&source.GetString()==GasIndexedCash.Point;
 }

 static TicketSideDraft QuantitySource(TicketDraft draft,TicketSideDraft target)=>target.Kind==Physical?target:
  draft.Sides.SingleOrDefault(s=>s.Kind==Physical&&s.Code==target.Values.GetValueOrDefault($"{target.Kind}_source"))??throw Unresolved("Choose the physical quantity source first.");

 // Values a lookup proposes, in the shape the booking contract admits. Market prices are evaluated by the store from PriceProbe.
 public static TicketLookup Lookup(TicketDraft draft,string? side,string field,JsonElement catalog)
 {
  var (target,f)=Field(draft,side,field);
  switch(f.Lookup)
  {
   case "paid_through":
    var paying=draft.Sides.Where(s=>s.Kind!=Physical&&s.Values.GetValueOrDefault($"{s.Kind}_source")==target!.Code).Select(s=>s.Code).ToArray();
    return paying.Length>0?new(string.Join(",",paying),new("ticket","Ticket sides")):throw Unresolved($"No payment or fee side is charged on '{target!.Code}' yet.");
   case "gas_day_start":
    RequireSetup(draft,catalog);
    var path=Path(target!,catalog,Unresolved);var minute=path.GetProperty("dayStartMinute").GetInt32();
    return new($"{minute/60:00}:{minute%60:00}",new("reference",$"{path.Text("deliveryPointName")} · {path.Text("clock")}"));
   // The exact tariff reference, which the booking resolves and pins itself; a rate is never copied into the fee price.
   case "eligible_tariff":
    RequireSetup(draft,catalog);
    if(target!.Values.ContainsKey($"{target.Kind}_price"))throw Unresolved("A manual fee price is set; the booking takes a tariff only without one.");
    var tariffs=Tariffs(draft,target,catalog,Unresolved).ToArray();
    return tariffs.Length==1?new(tariffs[0].Text("code"),new("tariff",$"Tariff {tariffs[0].Text("name")}")):
     throw Unresolved(tariffs.Length==0?"No configured usage tariff covers the source side's pipeline, product and delivery term.":"More than one configured usage tariff covers the source side; choose one.");
   case "market_price":throw new InvalidOperationException("Market prices are evaluated from PriceProbe.");
   case null:throw Bad($"Field '{field}' has no lookup.");
   default:throw Unresolved($"Lookup '{f.Lookup}' is not available for {Description.Product} yet.");
  }
 }

 // A physical market-price probe uses its own path and term. An indexed forecast-choice probe
 // uses that path over the payment's own dates, one unit received per operating day, no cash.
 // Price does not depend on the quantity, direction or basis, so the probe states them only to make a complete side.
 public static GasCompositionRequest PriceProbe(TicketDraft draft,string side,JsonElement catalog,Guid snapshot,Guid definition)
 {
  var entry=draft.Sides.SingleOrDefault(s=>s.Code==side)??throw Bad("Choose an existing Side.");
  var target=QuantitySource(draft,entry);
  _=Field(draft,target.Code,"price");
  RequireSetup(draft,catalog);
  var (from,to)=entry.Kind=="floating"?(Term(entry,"floating_start",Unresolved),Term(entry,"floating_end",Unresolved)):
   (Term(target,"delivery_from",Unresolved),Term(target,"delivery_to",Unresolved));var path=Path(target,catalog,Unresolved);
  return new(snapshot,PhysicalGasStore.Convention,"current",GasCompositionStore.Profile,Guid.Empty,Guid.Empty,definition,Id(draft.Trade,"gas_setup"),false,null,null,null,null,
   [new(target!.Code,path.Text("pipeline"),path.Text("zone"),path.Text("deliveryPoint"),path.Text("product"),"receive",from,to,"per_operating_day","1",null,Forecast(target.Values.GetValueOrDefault("forecast_curve"))){CalendarPolicy=Calendar(target)}],[]);
 }

 public static string? LookupOf(TicketDraft draft,string? side,string field)=>Field(draft,side,field).Field.Lookup;

 // A physical side's evaluated market price, labelled with its point and the close it was priced at.
 public static TicketLookup MarketPrice(TicketDraft draft,string side,JsonElement catalog,string price,DateOnly close)=>
  new(price,new("market",$"{Path(Field(draft,side,"price").Side!,catalog,Unresolved).Text("deliveryPointName")} · close {close.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)}"));

 // The side (null for a trade field) and the description field a choices or lookup request names.
 static (TicketSideDraft? Side,TicketField Field) Field(TicketDraft draft,string? side,string field)
 {
  RequireShape(draft);
  if(side is null)return(null,Description.TradeFields.SingleOrDefault(f=>f.Id==field)??throw Bad($"The trade has no field '{field}'."));
  var target=draft.Sides.Where(s=>s.Code==side).ToArray() is [var one]?one:throw Bad($"The draft has no single side '{side}'.");
  var kind=Description.Sides.SingleOrDefault(k=>k.Kind==target.Kind)??throw Bad($"Unknown side kind '{target.Kind}'.");
  return(target,kind.Fields.SingleOrDefault(f=>f.Id==field)??throw Bad($"The {target.Kind} side has no field '{field}'."));
 }

 // Side answers, previews and bookings come from the gas setup the context resolves, or its reference conventions when it resolves none;
 // a draft naming another setup, or none where the context resolves one, refuses on the trade's setup field.
 public static void RequireSetup(Guid? chosen,Guid? resolved)
 {
  if(chosen!=resolved)
   throw On(null,"gas_setup",resolved is {} setup?$"Choose gas setup {setup}, the one this context resolves.":"This context resolves no gas setup; leave the gas setup empty.");
 }
 static void RequireSetup(TicketDraft draft,JsonElement catalog)=>RequireSetup(Id(draft.Trade,"gas_setup"),catalog.OptionalText("gasSetupId") is {} setup?Guid.Parse(setup):null);

 // A value the side must already hold for this answer; refuse names the code of the refusal.
 static string Term(TicketSideDraft side,string id,Func<string,MarketDataException> refuse)=>
  side.Values.GetValueOrDefault(id) is {Length:>0} value?value:throw refuse($"Choose the {id.Replace('_',' ')} on side '{side.Code}' first.");

 static JsonElement Path(TicketSideDraft side,JsonElement catalog,Func<string,MarketDataException> refuse)
 {
  var key=(Term(side,"pipeline",refuse),Term(side,"zone",refuse),Term(side,"delivery_point",refuse),Term(side,"product",refuse));
  return catalog.GetProperty("locations").EnumerateArray().Where(l=>(l.Text("pipeline"),l.Text("zone"),l.Text("deliveryPoint"),l.Text("product"))==key).ToArray() is [var one]?one:
   throw refuse($"Side '{side.Code}' names no single gas path in this context.");
 }

 // Usage tariffs apply to a delivery-stage fee and must cover its source path and the payment's own delivery term.
 static IEnumerable<JsonElement> Tariffs(TicketDraft draft,TicketSideDraft fee,JsonElement catalog,Func<string,MarketDataException> refuse)
 {
  if(fee.Values.GetValueOrDefault($"{fee.Kind}_stage")!="delivery")throw refuse("Configured usage tariffs apply only to a delivery-stage fee.");
  var code=fee.Values.GetValueOrDefault($"{fee.Kind}_source");
  var source=draft.Sides.Where(s=>s.Kind==Physical&&s.Code==code).ToArray() is [var one]?one:throw refuse("Choose the fee's physical quantity source first.");
  var path=Path(source,catalog,refuse);
  if(path.GetProperty("valuation").ValueKind==JsonValueKind.Null)throw refuse($"Side '{source.Code}' has no valued gas path in this context.");
  var (from,to)=(DateWorkspace.Date(Term(fee,$"{fee.Kind}_start",refuse)),DateWorkspace.Date(Term(fee,$"{fee.Kind}_end",refuse)));
  return catalog.GetProperty("tariffs").EnumerateArray().Where(t=>t.Text("pipeline")==path.Text("pipeline")&&t.Text("product")==path.Text("product")&&t.Text("quote")==path.GetProperty("valuation").Text("quote")&&
   DateWorkspace.Date(t.Text("validFrom"))<=from&&DateWorkspace.Date(t.Text("validTo"))>=to);
 }

 static MarketDataException Unresolved(string message)=>new("ticket_lookup",message);
 static MarketDataException Bad(string message)=>new("ticket_input",message);
 static TicketFieldException On(string? side,string field,string message)=>new("ticket_input",message,side,field);
}
