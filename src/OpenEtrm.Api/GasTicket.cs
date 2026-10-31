using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record TicketSideDraft(string Kind,string Code,ImmutableDictionary<string,string> Values);
public sealed record TicketDraft(string Product,int Version,ImmutableDictionary<string,string> Trade,ImmutableArray<TicketSideDraft> Sides);

public static class GasTicket
{
 const string Physical="physical";
 public static TicketDescription Description{get;}=TicketDescriptions.Validate(new("physical_gas",1,"flow",
  [Trade("book","Book","Book that holds the trade.","choice",choices:"books"),
   Trade("legal_entity","Legal entity","Our legal entity on the trade.","choice",choices:"legal_entities",required:false),
   Trade("counterparty","Counterparty","The other party to the trade.","choice",choices:"counterparties",required:false),
   Trade("gas_setup","Gas setup","Gas conventions and market assignments that price the trade.","choice",choices:"gas_setups"),
   new("trade_date","Trade date","Date the trade is booked.","date",null,"info",false,null,null,[],"trade_date",null,null)],
  [new(Physical,"Physical","gas",1,4,
    [Field("direction","Direction","Whether gas is delivered or received.","choice","direction",choices:"physical_direction"),
     Field("delivery_from","From","First gas day of delivery.","date","start"),
     Field("delivery_to","To","End of delivery, exclusive.","date","end"),
     Field("product","Product","Service level of the gas.","choice","product",choices:"gas_products"),
     Field("quantity","Quantity","Quantity per the chosen basis, in MMBtu. Taken from the link when the side is linked.","decimal","quantity",unit:"MMBtu",required:false,rule:"Derived from the link source when a link is set."),
     Info("price","Price","Market price at the delivery point.","price","market_price"),
     Info("payment","Payment","Sides that pay for this gas.","payment","paid_through"),
     Field("quantity_basis","Quantity basis","Whether the quantity applies to each operating day or to the whole period.","choice",choices:"quantity_basis"),
     Field("pipeline","Pipeline","Pipeline the gas moves on.","choice",choices:"pipelines"),
     Field("zone","Zone","Pipeline zone.","choice",choices:"zones",dependsOn:"pipeline"),
     Field("delivery_point","Delivery point","Point in the zone where gas changes hands.","choice",choices:"delivery_points",dependsOn:"zone"),
     Info("gas_day_start","Gas day start","Hour the gas day starts at this point.",null,"gas_day_start"),
     Field("link_source","Link source","Physical side whose quantity this side follows.","choice",choices:"physical_sides",required:false),
     Field("link_formula","Link formula","How fuel loss relates this side to its source.","choice",choices:"link_formulas",required:false,dependsOn:"link_source"),
     Field("link_fraction","Link fraction","Fuel fraction used by the link formula.","decimal",required:false,dependsOn:"link_source")],
    [new("quantity","Quantity","MMBtu","quantity",false),new("market_price","Market price",null,"price",false)]),
   CashKind("cash","Payment","cash",false),
   CashKind("fee","Fee","fee",true)],
  // The composition contract admits at most four cash sides, fees included.
  [new(["cash","fee"],4)],
  [new("net_cash","Net cash",null,"money",false),new("payment_at","Payment at",null,"time",false)],
  ["month","day"],[new("links","Quantity links")],["new","booked"]));

 static TicketField Trade(string id,string label,string meaning,string type,string? choices=null,bool required=true)=>
  new(id,label,meaning,type,null,"editable",required,null,choices,[],null,null,null);
 static TicketField Field(string id,string label,string meaning,string type,string? band=null,string? choices=null,string? unit=null,bool required=true,string? rule=null,string? dependsOn=null)=>
  new(id,label,meaning,type,unit,"editable",required,band,choices,dependsOn is null?[]:[dependsOn],null,null,rule);
 static TicketField Info(string id,string label,string meaning,string? band,string lookup)=>
  new(id,label,meaning,"text",null,"info",false,band,null,[],lookup,null,null);

 static TicketSideKind CashKind(string kind,string pill,string colour,bool fee)
 {
  var follows="Follows the quantity source side.";
  TicketField Own(string id,string label,string meaning,string type,string? band=null,string? choices=null,bool required=true,string? lookup=null)=>
   new($"{kind}_{id}",label,meaning,type,null,"editable",required,band,choices,[],lookup,null,null);
  TicketField Follows(string id,string label)=>new($"{kind}_{id}",label,follows,"date",null,"derived",false,id,null,[],null,null,follows);
  return new(kind,pill,colour,0,4,
   [Own("direction","Direction","Whether we pay or receive.","choice","direction","cash_direction"),
    Follows("start","From"),Follows("end","To"),
    Own("source","Quantity source","Physical side whose quantity this amount is charged on.","choice","quantity","physical_sides"),
    Own("stage","Quantity stage","Receipt, delivery or fuel quantity of the source side.","choice",choices:"quantity_stages"),
    Own("price","Price","Price per unit of the source quantity.","decimal","price",required:!fee,lookup:fee?"tariff_price":null),
    Own("payment_at","Payment at","When the amount is paid.","datetime","payment"),
    ..fee?[Own("tariff","Tariff","Configured usage tariff that sets the price.","choice",choices:"usage_tariffs",required:false)]:Array.Empty<TicketField>()],
   [new("price","Price",null,"price",false),new("amount","Amount",null,"money",false)]);
 }

 public static GasCompositionRequest Request(TicketDraft draft,Guid snapshotId,string knowledgeMode,Guid operationId,Guid definitionRevisionId)
 {
  if(draft.Product!=Description.Product||draft.Version!=Description.Version)throw Bad($"Draft is for {draft.Product} v{draft.Version}, not {Description.Product} v{Description.Version}.");
  Check(Description.TradeFields,draft.Trade,"trade");
  foreach(var side in draft.Sides)
  {
   var kind=Description.Sides.SingleOrDefault(k=>k.Kind==side.Kind)??throw Bad($"Unknown side kind '{side.Kind}'.");
   Check(kind.Fields,side.Values,$"{side.Kind} side '{side.Code}'");
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
  string Source(TicketSideDraft side,string id)=>codes.Contains(side.Values[id])?side.Values[id]:throw Bad($"{side.Kind} side '{side.Code}' names '{side.Values[id]}', which is not a physical side code.");
  var t=draft.Trade;
  var (entity,entityRevision)=Versioned(t,"legal_entity");var (party,partyRevision)=Versioned(t,"counterparty");
  return new(snapshotId,PhysicalGasStore.Convention,knowledgeMode,GasCompositionStore.Profile,operationId,Id(t,"book")!.Value,definitionRevisionId,Id(t,"gas_setup"),false,entity,entityRevision,party,partyRevision,
   [..physical.Select(s=>PhysicalRequest(s,Source))],
   [..draft.Sides.Where(s=>s.Kind!=Physical).Select(s=>new GasCashRequest(s.Code,s.Kind,s.Values[$"{s.Kind}_direction"],Source(s,$"{s.Kind}_source"),s.Values[$"{s.Kind}_stage"],s.Values.GetValueOrDefault($"{s.Kind}_price"),s.Values.GetValueOrDefault($"{s.Kind}_tariff"),s.Values[$"{s.Kind}_payment_at"]))]);
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
  link is null?side.Values.GetValueOrDefault("quantity")??throw Bad($"Physical side '{side.Code}' needs a quantity or a link source."):
  side.Values.ContainsKey("quantity")?throw Bad($"Physical side '{side.Code}' takes its quantity from its link source; leave quantity empty."):null;

 static void Check(ImmutableArray<TicketField> fields,ImmutableDictionary<string,string> values,string scope)
 {
  foreach(var (id,value) in values)
  {
   var field=fields.SingleOrDefault(f=>f.Id==id)??throw Bad($"The {scope} has no field '{id}'.");
   if(field.State!="editable")throw Bad($"Field '{id}' on the {scope} is {field.State} and takes no value.");
   if(string.IsNullOrWhiteSpace(value))throw Bad($"Field '{id}' on the {scope} is blank.");
  }
  foreach(var field in fields.Where(f=>f.Required&&!values.ContainsKey(f.Id)))throw Bad($"The {scope} needs a value for '{field.Id}'.");
 }

 static Guid? Id(ImmutableDictionary<string,string> values,string id)=>values.TryGetValue(id,out var v)?Guid.TryParse(v,out var guid)?guid:throw Bad($"Field '{id}' must be an id."):null;

 static (Guid?,int?) Versioned(ImmutableDictionary<string,string> values,string id)
 {
  if(!values.TryGetValue(id,out var v))return(null,null);
  var parts=v.Split('@');
  return parts.Length==2&&Guid.TryParse(parts[0],out var guid)&&int.TryParse(parts[1],NumberStyles.None,CultureInfo.InvariantCulture,out var revision)?(guid,revision):throw Bad($"Field '{id}' must be '<id>@<revision>'.");
 }

 static MarketDataException Bad(string message)=>new("ticket_input",message);
}
