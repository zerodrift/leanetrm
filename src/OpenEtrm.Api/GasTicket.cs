using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

public sealed record TicketSideDraft(string Kind,string Code,ImmutableDictionary<string,string> Values);
public sealed record TicketDraft(string Product,int Version,ImmutableDictionary<string,string> Trade,ImmutableArray<TicketSideDraft> Sides);

public sealed record TicketChoice(string Value,string Label);
public sealed record TicketSource(string Kind,string Label);
public sealed record TicketLookup(string Value,TicketSource Source);

public static class GasTicket
{
 const string Physical="physical";
 // The composition role each side kind books as.
 static readonly ImmutableDictionary<string,string> Roles=new Dictionary<string,string>{[Physical]="physical",["cash"]="consideration",["fee"]="fee"}.ToImmutableDictionary();
 public static TicketDescription Description{get;}=TicketDescriptions.Validate(new("physical_gas",1,"flow",
  [Trade("book","Book","Book that holds the trade.","choice",choices:"books"),
   Trade("legal_entity","Legal entity","Our legal entity on the trade.","choice",choices:"legal_entities",required:false),
   Trade("counterparty","Counterparty","The other party to the trade.","choice",choices:"counterparties",required:false),
   Trade("gas_setup","Gas setup","Published gas conventions and market assignments that price the trade; empty where the context resolves none and the reference conventions apply.","choice",choices:"gas_setups",required:false),
   new("trade_date","Trade date","Date the trade is booked.","date",null,"info",false,null,null,[],"trade_date",null,null),
   // The master agreement's terms are not stored yet, so the strip shows where they will be read.
   Soon("force_majeure","Force majeure","Force majeure terms of the master agreement the trade is under.","text","Will show the master agreement's force majeure terms, read-only. Agreement terms are not stored yet."),
   Soon("credit_terms","Credit terms","Credit terms of the master agreement the trade is under.","text","Will show the master agreement's credit terms, read-only. Agreement terms are not stored yet.")],
  [new(Physical,"Physical","gas",GasCompositionInstrument.MinPhysicalSides,1,GasCompositionInstrument.MaxPhysicalSides,
    [Field("direction","Direction","Whether gas is delivered or received.","choice","direction",choices:"physical_direction"),
     Field("delivery_from","From","First gas day of delivery.","date","start"),
     Field("delivery_to","To","End of delivery, exclusive.","date","end"),
     Field("product","Product","Service level of the gas.","choice","product",choices:"gas_products"),
     Field("quantity","Quantity","Quantity per the chosen basis, in the unit the side's gas path is quoted in. Taken from the link when the side is linked.","decimal","quantity",unit:"quantity",required:false,rule:"Derived from the link source when a link is set."),
     Info("price","Price","Market price at the delivery point.","price","market_price"),
     Info("payment","Payment","Sides that pay for this gas.","payment","paid_through"),
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
   // An index price is the existing floating side (FloatingSide, D84 and D93), offered greyed until gas booking takes it.
   new("floating","Floating","neutral",0,0,0,[],[],"Index price: pays for the gas at a published index, the same floating side swaps book. Gas booking does not accept it yet.")],
  // Payment and fee sides are the composition's cash sides.
  [new(["cash","fee"],GasCompositionInstrument.MaxCashSides)],
  [new("net_cash","Net cash",null,"money",[]),new("payment_at","Payment at",null,"time",[])],
  ["month","day"],[new("links","Quantity links"),
   new("imbalance","Imbalance","Will compare each gas day's nominated and actual quantities, with the imbalance, its cash-out price and its amount.")],["new","booked"]));

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
  var follows="Follows the quantity source side.";
  TicketField Own(string id,string label,string meaning,string type,string? band=null,string? choices=null,bool required=true,string? lookup=null)=>
   new($"{kind}_{id}",label,meaning,type,null,"editable",required,band,choices,[],lookup,null,null);
  TicketField Follows(string id,string label)=>new($"{kind}_{id}",label,follows,"date",null,"derived",false,id,null,[],null,null,follows);
  return new(kind,pill,colour,0,initial,GasCompositionInstrument.MaxCashSides,
   [Own("direction","Direction","Whether we pay or receive.","choice","direction","cash_direction"),
    Follows("start","From"),Follows("end","To"),
    Own("source","Quantity source","Physical side whose quantity this amount is charged on.","choice","quantity","physical_sides"),
    Own("stage","Quantity stage","Receipt, delivery or fuel quantity of the source side.","choice",choices:"quantity_stages"),
    Own("price","Price","Price per unit of the source quantity.","decimal","price",required:!fee),
    Own("payment_at","Payment at","When the amount is paid.","datetime","payment"),
    ..fee?[Own("tariff","Tariff","Configured usage tariff that sets the price.","choice",choices:"usage_tariffs",required:false,lookup:"eligible_tariff")]:Array.Empty<TicketField>()],
   [new("quantity","Quantity","quantity","quantity",[]),new("price","Price",null,"price",[]),new("amount","Amount",null,"money",[])]);
 }

 public static GasCompositionRequest Request(TicketDraft draft,Guid snapshotId,string knowledgeMode,Guid operationId,Guid definitionRevisionId)
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
  var codes=physical.Select(s=>s.Code).ToHashSet();
  string Source(TicketSideDraft side,string id)=>codes.Contains(side.Values[id])?side.Values[id]:throw On(side.Code,id,$"{side.Kind} side '{side.Code}' names '{side.Values[id]}', which is not a physical side code.");
  var t=draft.Trade;
  var (entity,entityRevision)=Versioned(t,"legal_entity");var (party,partyRevision)=Versioned(t,"counterparty");
  return new(snapshotId,PhysicalGasStore.Convention,knowledgeMode,GasCompositionStore.Profile,operationId,Id(t,"book")!.Value,definitionRevisionId,Id(t,"gas_setup"),false,entity,entityRevision,party,partyRevision,
   [..physical.Select(s=>PhysicalRequest(s,Source))],
   [..draft.Sides.Where(s=>s.Kind!=Physical).Select(s=>new GasCashRequest(s.Code,Roles[s.Kind],s.Values[$"{s.Kind}_direction"],Source(s,$"{s.Kind}_source"),s.Values[$"{s.Kind}_stage"],s.Values.GetValueOrDefault($"{s.Kind}_price"),s.Values.GetValueOrDefault($"{s.Kind}_tariff"),s.Values[$"{s.Kind}_payment_at"]))]);
 }

 static GasPhysicalRequest PhysicalRequest(TicketSideDraft side,Func<TicketSideDraft,string,string> source)
 {
  var v=side.Values;var link=Link(side,source);
  return new(side.Code,v["pipeline"],v["zone"],v["delivery_point"],v["product"],v["direction"],v["delivery_from"],v["delivery_to"],v["quantity_basis"],Quantity(side,link),link);
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
   if(field.State!="editable")throw On(side,id,$"Field '{id}' on the {scope} is {field.State} and takes no value.");
   if(string.IsNullOrWhiteSpace(value))throw On(side,id,$"Field '{id}' on the {scope} is blank.");
   // A decimal field holds one exact decimal, read by the parser the booking reads it with.
   if(field.Type=="decimal")try{ExactDecimal.Parse(value);}catch(MarketDataException error){throw new TicketFieldException(error.Code,error.Message,side,id);}
  }
  foreach(var field in fields.Where(f=>f.Required&&!values.ContainsKey(f.Id)))throw On(side,field.Id,$"The {scope} needs a value for '{field.Id}'.");
 }

 static Guid? Id(ImmutableDictionary<string,string> values,string id)=>values.TryGetValue(id,out var v)?Guid.TryParse(v,out var guid)?guid:throw Bad($"Field '{id}' must be an id."):null;

 static (Guid?,int?) Versioned(ImmutableDictionary<string,string> values,string id)
 {
  if(!values.TryGetValue(id,out var v))return(null,null);
  var parts=v.Split('@');
  return parts.Length==2&&Guid.TryParse(parts[0],out var guid)&&int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var revision)?(guid,revision):throw Bad($"Field '{id}' must be '<id>@<revision>'.");
 }

 // A malformed client draft refuses as ticket input, never as a missing object.
 static void RequireShape(TicketDraft? draft)
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
   ("quantity_basis",p.QuantityBasis),("pipeline",p.Pipeline),("zone",p.Zone),("delivery_point",p.DeliveryPoint),("link_source",p.Link?.Source),("link_formula",p.Link?.Formula),("link_fraction",p.Link?.Fraction))));
  var cash=terms.CashSides.Select(s=>{var kind=SideKind(s.Role).Kind;
   return new TicketSideDraft(kind,s.Code,Values(($"{kind}_direction",s.Direction),($"{kind}_source",s.Source),($"{kind}_stage",s.Stage),($"{kind}_price",s.Price),($"{kind}_payment_at",s.PaymentAt),($"{kind}_tariff",s.TariffCode)));});
  return new(Description.Product,Description.Version,trade.ToImmutableDictionary(),[..physical,..cash]);
 }

 // The draft a booked request reopens as. The draft must map back to exactly the stored terms;
 // terms it cannot express refuse rather than open as a different trade.
 public static TicketDraft Reopen(GasCompositionRequest terms)
 {
  var draft=Draft(terms);
  var mapped=Request(draft,terms.SnapshotId,terms.KnowledgeMode,terms.OperationId,terms.DefinitionRevisionId);
  return JsonSerializer.Serialize(mapped)==JsonSerializer.Serialize(terms)?draft:throw Bad("The booked terms hold values the deal ticket cannot express, so the trade does not open in it.");
 }

 // A single-point trade's retained terms as the composition they are: one physical side, the payment for its gas and an optional fee,
 // coded as a new ticket codes the first side of each kind. The single-point profile charges both on the side's contractual delivery
 // quantity and pays them at its one payment instant. Terms it does not admit refuse here or in Reopen; none is filled in.
 public static GasCompositionRequest SinglePoint(PhysicalGasTerms t)
 {
  static string Code(string kind)=>$"{kind}-1";
  var payment=t.Direction switch{"receive"=>"pay","deliver"=>"receive",_=>throw Bad($"The booked gas direction '{t.Direction}' is neither receive nor deliver.")};
  var fee=t.UsageFee is not null||t.UsageTariffCode is not null;
  if(fee!=(t.UsageFeeDirection is not null)||t.UsageFee is not null&&t.UsageTariffCode is not null)throw Bad("The booked fee needs its direction and exactly one of a price or a tariff.");
  GasCashRequest Charge(string kind,string direction,string? price,string? tariff)=>new(Code(kind),Roles[kind],direction,Code(Physical),"delivery",price,tariff,t.PaymentAt!);
  return new(t.SnapshotId,t.Convention!,t.KnowledgeMode!,GasCompositionStore.Profile,t.OperationId,t.BookId,t.DefinitionRevisionId,t.GasSetupId,t.Discounting,t.LegalEntityId,t.LegalEntityRevision,t.CounterpartyId,t.CounterpartyRevision,
   [new(Code(Physical),t.Pipeline!,t.Zone!,t.DeliveryPoint!,t.Product!,t.Direction,t.DeliveryFrom!,t.DeliveryTo!,t.QuantityBasis!,t.Quantity,null)],
   [Charge("cash",payment,t.FixedPrice,null),..fee?[Charge("fee",t.UsageFeeDirection!,t.UsageFee,t.UsageTariffCode)]:Array.Empty<GasCashRequest>()]);
 }

 // Fixed lists the booking contract admits, and the lists read from the context's gas catalog.
 static readonly ImmutableDictionary<string,ImmutableArray<TicketChoice>> Fixed=new Dictionary<string,ImmutableArray<TicketChoice>>{
  ["physical_direction"]=[new("receive","Receive"),new("deliver","Deliver")],["cash_direction"]=[new("pay","Pay"),new("receive","Receive")],
  ["quantity_basis"]=[new("per_operating_day","Per operating day"),new("period_total_equal_days","Period total over equal days")],
  ["quantity_stages"]=[new("receipt","Receipt"),new("delivery","Delivery"),new("fuel","Fuel")],
  ["link_formulas"]=[new("none","None"),new("gross_less_fraction","Gross less fraction"),new("gross_divided_by_one_plus_fraction","Gross divided by one plus fraction")]}.ToImmutableDictionary();

 // The eligible choices for one field, from the gas catalog read under the request's context and filtered by the draft's parent values.
 public static ImmutableArray<TicketChoice> Choices(TicketDraft draft,string? side,string field,JsonElement catalog)
 {
  var (target,f)=Field(draft,side,field);
  if(Fixed.TryGetValue(f.Choices??throw Bad($"Field '{field}' has no choice list."),out var fixedChoices))return fixedChoices;
  if(f.Choices=="physical_sides")return[..draft.Sides.Where(s=>s.Kind==Physical&&!(f.Id=="link_source"&&s==target)).Select(s=>new TicketChoice(s.Code,s.Code))];
  if(target is not null)RequireSetup(draft,catalog);
  var paths=catalog.GetProperty("locations").EnumerateArray().Where(l=>l.GetProperty("valuation").ValueKind!=JsonValueKind.Null).ToArray();
  IEnumerable<TicketChoice> Distinct(IEnumerable<JsonElement> rows,string code,string name)=>rows.Select(r=>new TicketChoice(r.Text(code),r.Text(name))).Distinct();
  IEnumerable<TicketChoice> Parties(string list)=>catalog.GetProperty(list).EnumerateArray().Select(p=>new TicketChoice($"{p.Text("id")}@{p.Text("revision")}",p.Text("name")));
  return[..f.Choices switch{
   "books"=>catalog.GetProperty("books").EnumerateArray().Select(b=>new TicketChoice(b.Text("id"),b.Text("name"))),
   "legal_entities"=>Parties("legalEntities"),"counterparties"=>Parties("counterparties"),
   "gas_setups"=>catalog.OptionalText("gasSetupId") is {} setup?[new TicketChoice(setup,$"Published setup {catalog.Text("gasSetupPublication")}")]:[],
   "pipelines"=>Distinct(paths,"pipeline","pipelineName"),
   "zones"=>Distinct(paths.Where(p=>p.Text("pipeline")==Term(target!,"pipeline",Bad)),"zone","zoneName"),
   "delivery_points"=>Distinct(paths.Where(p=>p.Text("pipeline")==Term(target!,"pipeline",Bad)&&p.Text("zone")==Term(target!,"zone",Bad)),"deliveryPoint","deliveryPointName"),
   "gas_products"=>Distinct(paths,"product","productName"),
   "usage_tariffs"=>Tariffs(draft,target!,catalog,Bad).Select(t=>new TicketChoice(t.Text("code"),t.Text("name"))),
   _=>throw new MarketDataException("ticket_description",$"Field '{field}' names choice list '{f.Choices}', which {Description.Product} does not serve.")}];
 }

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

 // The request that evaluates one physical side's market price: its own path and delivery term, one unit received per operating day, no cash.
 // Price does not depend on the quantity, direction or basis, so the probe states them only to make a complete side.
 public static GasCompositionRequest PriceProbe(TicketDraft draft,string side,JsonElement catalog,Guid snapshot,Guid definition)
 {
  var (target,_)=Field(draft,side,"price");
  RequireSetup(draft,catalog);
  var (from,to)=(Term(target!,"delivery_from",Unresolved),Term(target!,"delivery_to",Unresolved));var path=Path(target!,catalog,Unresolved);
  return new(snapshot,PhysicalGasStore.Convention,"current",GasCompositionStore.Profile,Guid.Empty,Guid.Empty,definition,Id(draft.Trade,"gas_setup"),false,null,null,null,null,
   [new(target!.Code,path.Text("pipeline"),path.Text("zone"),path.Text("deliveryPoint"),path.Text("product"),"receive",from,to,"per_operating_day","1",null)],[]);
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

 // Usage tariffs apply to a delivery-stage fee and must cover its source side's pipeline, product, quote and whole delivery term.
 static IEnumerable<JsonElement> Tariffs(TicketDraft draft,TicketSideDraft fee,JsonElement catalog,Func<string,MarketDataException> refuse)
 {
  if(fee.Values.GetValueOrDefault($"{fee.Kind}_stage")!="delivery")throw refuse("Configured usage tariffs apply only to a delivery-stage fee.");
  var code=fee.Values.GetValueOrDefault($"{fee.Kind}_source");
  var source=draft.Sides.Where(s=>s.Kind==Physical&&s.Code==code).ToArray() is [var one]?one:throw refuse("Choose the fee's physical quantity source first.");
  var path=Path(source,catalog,refuse);
  if(path.GetProperty("valuation").ValueKind==JsonValueKind.Null)throw refuse($"Side '{source.Code}' has no valued gas path in this context.");
  var (from,to)=(DateWorkspace.Date(Term(source,"delivery_from",refuse)),DateWorkspace.Date(Term(source,"delivery_to",refuse)));
  return catalog.GetProperty("tariffs").EnumerateArray().Where(t=>t.Text("pipeline")==path.Text("pipeline")&&t.Text("product")==path.Text("product")&&t.Text("quote")==path.GetProperty("valuation").Text("quote")&&
   DateWorkspace.Date(t.Text("validFrom"))<=from&&DateWorkspace.Date(t.Text("validTo"))>=to);
 }

 static MarketDataException Unresolved(string message)=>new("ticket_lookup",message);
 static MarketDataException Bad(string message)=>new("ticket_input",message);
 static TicketFieldException On(string? side,string field,string message)=>new("ticket_input",message,side,field);
}
