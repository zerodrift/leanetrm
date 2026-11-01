using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public static class TicketBand { public static readonly ImmutableArray<string> Rows=["direction","start","end","product","quantity","price","payment"]; }
// Unit names the unit a field's or column's values are in, resolved per trade, never a unit symbol: "quantity" is the trade's quantity unit,
// which the product's preview and booked read return.
public sealed record TicketField(string Id,string Label,string Meaning,string Type,string? Unit,string State,bool Required,string? Band,string? Choices,ImmutableArray<string> DependsOn,string? Lookup,string? Soon,string? Rule);
// Input names the modes in which a side column's day cells are entered in the side's tab, each recorded by the product's own operation.
// Soon, on a field, side kind, column or tab, is the hover text of an item drawn as coming soon: it holds no value, is never sent and reads nothing.
public sealed record TicketColumn(string Id,string Label,string? Unit,string Measure,ImmutableArray<string> Input,string? Soon=null);
// A new ticket opens with Initial sides of the kind, between Min and Max; a coming-soon kind is offered greyed and none can be added.
public sealed record TicketSideKind(string Kind,string Pill,string Colour,int Min,int Initial,int Max,ImmutableArray<TicketField> Fields,ImmutableArray<TicketColumn> Columns,string? Soon=null);
// At most Max sides of the named kinds together; each kind keeps its own Min and Max.
public sealed record TicketSideLimit(ImmutableArray<string> Kinds,int Max);
public sealed record TicketTab(string Id,string Label,string? Soon=null);
public sealed record TicketDescription(string Product,int Version,string RowShape,ImmutableArray<TicketField> TradeFields,ImmutableArray<TicketSideKind> Sides,ImmutableArray<TicketSideLimit> SideLimits,ImmutableArray<TicketColumn> TradeColumns,ImmutableArray<string> Levels,ImmutableArray<TicketTab> Tabs,ImmutableArray<string> Modes);
// A refusal of one draft value, naming its side (null for a trade field) and field, so the ticket shows the reason on that field.
public sealed class TicketFieldException(string code,string message,string? side,string field):MarketDataException(code,message)
{
 public string? Side{get;}=side;
 public string Field{get;}=field;
}

public static class TicketDescriptions
{
 static readonly ImmutableArray<string> States=["editable","derived","info","soon"];
 static readonly ImmutableArray<string> LevelOrder=["month","day","hour"];
 static readonly ImmutableArray<string> ModeNames=["new","booked","amend","close_out"];
 static readonly ImmutableArray<string> ResolvedUnits=["quantity"];
 // The one map from a side column's measure to what the schedule view reports.
 static readonly ImmutableDictionary<string,ScheduleViewMeasure> ViewMeasures=new Dictionary<string,ScheduleViewMeasure>{["quantity"]=ScheduleViewMeasure.Quantity,["price"]=ScheduleViewMeasure.Price,["money"]=ScheduleViewMeasure.Amount}.ToImmutableDictionary();

 public static ImmutableArray<(string,ScheduleViewMeasure)> ViewColumns(TicketSideKind kind)=>[..kind.Columns.Where(column=>column.Soon is null).Select(column=>(column.Id,ViewMeasures.TryGetValue(column.Measure,out var measure)?measure:
  throw new MarketDataException("ticket_description",$"Side kind '{kind.Kind}' column '{column.Id}' has measure '{column.Measure}', which the schedule view cannot show.")))];

 public static TicketDescription Validate(TicketDescription d)
 {
  if(d.RowShape!="flow")Fail(d,$"row shape '{d.RowShape}' is not supported; only 'flow' is");
  var levels=d.Levels.Select(LevelOrder.IndexOf).ToArray();
  if(levels.Length==0||levels.Contains(-1)||levels.Zip(levels.Skip(1),(a,b)=>a>=b).Any(x=>x))Fail(d,"levels must be a non-empty, ordered subset of month, day, hour");
  if(d.Modes.Any(mode=>!ModeNames.Contains(mode)))Fail(d,"modes must be among new, booked, amend and close_out");
  if(d.Sides.GroupBy(s=>s.Kind).FirstOrDefault(g=>g.Count()>1) is {} twice)Fail(d,$"side kind '{twice.Key}' is declared more than once");
  foreach(var (owner,columns) in d.Sides.Select(s=>($"side kind '{s.Kind}'",s.Columns)).Append(("the trade",d.TradeColumns)))
   if(columns.GroupBy(c=>c.Id).FirstOrDefault(g=>g.Count()>1) is {} column)Fail(d,$"{owner} declares column '{column.Key}' more than once");
  // Every ticket draws its instrument tab, so a declared tab is another one, once.
  if(d.Tabs.GroupBy(t=>t.Id).FirstOrDefault(g=>g.Count()>1||g.Key=="instrument") is {} declared)Fail(d,$"tab '{declared.Key}' is declared more than once or is the instrument tab every ticket draws");
  var all=d.TradeFields.Concat(d.Sides.SelectMany(s=>s.Fields)).ToArray();
  var ids=new HashSet<string>();
  foreach(var field in all)
  {
   if(!ids.Add(field.Id))Fail(d,$"field '{field.Id}' is declared more than once");
   CheckField(d,field);
  }
  var units=all.Select(f=>(f.Id,f.Unit)).Concat(d.TradeColumns.Concat(d.Sides.SelectMany(s=>s.Columns)).Select(c=>(c.Id,c.Unit)));
  if(d.TradeColumns.FirstOrDefault(c=>c.Soon is not null) is {} later)Fail(d,$"trade column '{later.Id}' cannot be coming soon; only a side column can");
  if(d.Sides.SelectMany(s=>s.Columns).FirstOrDefault(c=>c.Soon is not null&&(string.IsNullOrWhiteSpace(c.Soon)||!c.Input.IsDefaultOrEmpty||c.Unit is not null)) is {} soon)
   Fail(d,$"soon column '{soon.Id}' needs hover text and takes no entries or unit");
  if(d.Tabs.FirstOrDefault(t=>t.Soon is not null&&string.IsNullOrWhiteSpace(t.Soon)) is {} tab)Fail(d,$"soon tab '{tab.Id}' needs hover text");
  if(d.TradeColumns.FirstOrDefault(c=>!c.Input.IsDefaultOrEmpty) is {} entered)Fail(d,$"trade column '{entered.Id}' cannot be an input; inputs are a side's");
  if(d.Sides.SelectMany(s=>s.Columns).FirstOrDefault(c=>c.Input.IsDefault||c.Input.Distinct().Count()!=c.Input.Length||c.Input.Any(mode=>!d.Modes.Contains(mode))) is {} input)
   Fail(d,$"column '{input.Id}' must be an input in distinct declared modes, or none");
  if(units.FirstOrDefault(u=>u.Unit is not null&&!ResolvedUnits.Contains(u.Unit)) is {Id:not null} literal)Fail(d,$"'{literal.Id}' names unit '{literal.Unit}'; a unit is resolved per trade, and only 'quantity' is");
  foreach(var field in all)
   foreach(var dependency in field.DependsOn)
    if(!ids.Contains(dependency))Fail(d,$"field '{field.Id}' depends on unknown field '{dependency}'");
  foreach(var side in d.Sides.Where(s=>s.Soon is not null))
   if(string.IsNullOrWhiteSpace(side.Soon)||side.Min!=0||side.Initial!=0||side.Max!=0||!side.Fields.IsEmpty||!side.Columns.IsEmpty||d.SideLimits.Any(l=>l.Kinds.Contains(side.Kind)))
    Fail(d,$"soon side kind '{side.Kind}' needs hover text, no sides, fields, columns or side limit");
  foreach(var side in d.Sides.Where(s=>s.Soon is null))
  {
   if(side.Min<0||side.Min>side.Max||side.Max<1)Fail(d,$"side kind '{side.Kind}' needs 0 <= min <= max and 1 <= max");
   if(side.Initial<side.Min||side.Initial>side.Max)Fail(d,$"side kind '{side.Kind}' needs min <= initial <= max");
   if(side.Columns.FirstOrDefault(column=>!ViewMeasures.ContainsKey(column.Measure)) is {} unshown)Fail(d,$"side kind '{side.Kind}' column '{unshown.Id}' has measure '{unshown.Measure}', which the schedule view cannot show");
   var rows=side.Fields.Where(f=>f.Band is not null).GroupBy(f=>f.Band).FirstOrDefault(g=>g.Count()>1);
   if(rows is not null)Fail(d,$"side kind '{side.Kind}' has more than one field on band row '{rows.Key}'");
  }
  foreach(var limit in d.SideLimits)
  {
   var kinds=limit.Kinds.Select(kind=>d.Sides.SingleOrDefault(side=>side.Kind==kind)).ToArray();
   if(kinds.Length==0||kinds.Contains(null)||limit.Kinds.Distinct().Count()!=kinds.Length)Fail(d,"a side limit must name distinct declared side kinds");
   if(limit.Max<Math.Max(1,kinds.Sum(kind=>kind!.Initial)))Fail(d,$"the side limit on {string.Join(", ",limit.Kinds)} is below one or their initial sides");
  }
  return d;
 }

 static void CheckField(TicketDescription d,TicketField field)
 {
  if(!States.Contains(field.State))Fail(d,$"field '{field.Id}' has unknown state '{field.State}'");
  if(field.Required&&field.State!="editable")Fail(d,$"{field.State} field '{field.Id}' takes no value, so it cannot be required");
  if(field.State=="soon"&&string.IsNullOrWhiteSpace(field.Soon))Fail(d,$"soon field '{field.Id}' needs hover text");
  if(field.State!="soon"&&field.Soon is not null)Fail(d,$"{field.State} field '{field.Id}' is not coming soon, so it takes no coming-soon hover text");
  if(field.State=="soon"&&(field.Choices is not null||field.Lookup is not null))Fail(d,$"soon field '{field.Id}' cannot name choices or a lookup");
  if(field.State=="derived"&&string.IsNullOrWhiteSpace(field.Rule))Fail(d,$"derived field '{field.Id}' needs its rule text");
  if(field.Band is not null&&!TicketBand.Rows.Contains(field.Band))Fail(d,$"field '{field.Id}' names unknown band row '{field.Band}'");
 }

 static void Fail(TicketDescription d,string reason)=>throw new MarketDataException("ticket_description",$"Ticket description '{d.Product}' v{d.Version}: {reason}.");
}
