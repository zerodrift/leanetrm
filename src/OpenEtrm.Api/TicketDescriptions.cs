using System.Collections.Immutable;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public static class TicketBand { public static readonly ImmutableArray<string> Rows=["direction","start","end","product","quantity","price","payment"]; }
public sealed record TicketField(string Id,string Label,string Meaning,string Type,string? Unit,string State,bool Required,string? Band,string? Choices,ImmutableArray<string> DependsOn,string? Lookup,string? Soon,string? Rule);
public sealed record TicketColumn(string Id,string Label,string? Unit,string Measure,bool Input);
public sealed record TicketSideKind(string Kind,string Pill,string Colour,int Min,int Max,ImmutableArray<TicketField> Fields,ImmutableArray<TicketColumn> Columns);
// At most Max sides of the named kinds together; each kind keeps its own Min and Max.
public sealed record TicketSideLimit(ImmutableArray<string> Kinds,int Max);
public sealed record TicketTab(string Id,string Label);
public sealed record TicketDescription(string Product,int Version,string RowShape,ImmutableArray<TicketField> TradeFields,ImmutableArray<TicketSideKind> Sides,ImmutableArray<TicketSideLimit> SideLimits,ImmutableArray<TicketColumn> TradeColumns,ImmutableArray<string> Levels,ImmutableArray<TicketTab> Tabs,ImmutableArray<string> Modes);

public static class TicketDescriptions
{
 static readonly ImmutableArray<string> States=["editable","derived","info","soon"];
 static readonly ImmutableArray<string> LevelOrder=["month","day","hour"];
 static readonly ImmutableArray<string> ModeNames=["new","booked","amend","close_out"];

 public static TicketDescription Validate(TicketDescription d)
 {
  if(d.RowShape!="flow")Fail(d,$"row shape '{d.RowShape}' is not supported; only 'flow' is");
  var levels=d.Levels.Select(LevelOrder.IndexOf).ToArray();
  if(levels.Length==0||levels.Contains(-1)||levels.Zip(levels.Skip(1),(a,b)=>a>=b).Any(x=>x))Fail(d,"levels must be a non-empty, ordered subset of month, day, hour");
  if(d.Modes.Any(mode=>!ModeNames.Contains(mode)))Fail(d,"modes must be among new, booked, amend and close_out");
  var all=d.TradeFields.Concat(d.Sides.SelectMany(s=>s.Fields)).ToArray();
  var ids=new HashSet<string>();
  foreach(var field in all)
  {
   if(!ids.Add(field.Id))Fail(d,$"field '{field.Id}' is declared more than once");
   CheckField(d,field);
  }
  foreach(var field in all)
   foreach(var dependency in field.DependsOn)
    if(!ids.Contains(dependency))Fail(d,$"field '{field.Id}' depends on unknown field '{dependency}'");
  foreach(var side in d.Sides)
  {
   if(side.Min<0||side.Min>side.Max||side.Max<1)Fail(d,$"side kind '{side.Kind}' needs 0 <= min <= max and 1 <= max");
   var rows=side.Fields.Where(f=>f.Band is not null).GroupBy(f=>f.Band).FirstOrDefault(g=>g.Count()>1);
   if(rows is not null)Fail(d,$"side kind '{side.Kind}' has more than one field on band row '{rows.Key}'");
  }
  foreach(var limit in d.SideLimits)
  {
   var kinds=limit.Kinds.Select(kind=>d.Sides.SingleOrDefault(side=>side.Kind==kind)).ToArray();
   if(kinds.Length==0||kinds.Contains(null)||limit.Kinds.Distinct().Count()!=kinds.Length)Fail(d,"a side limit must name distinct declared side kinds");
   if(limit.Max<Math.Max(1,kinds.Sum(kind=>kind!.Min)))Fail(d,$"the side limit on {string.Join(", ",limit.Kinds)} is below one or their minimum sides");
  }
  return d;
 }

 static void CheckField(TicketDescription d,TicketField field)
 {
  if(!States.Contains(field.State))Fail(d,$"field '{field.Id}' has unknown state '{field.State}'");
  if(field.Required&&field.State!="editable")Fail(d,$"{field.State} field '{field.Id}' takes no value, so it cannot be required");
  if(field.State=="soon"&&string.IsNullOrWhiteSpace(field.Soon))Fail(d,$"soon field '{field.Id}' needs hover text");
  if(field.State=="soon"&&(field.Choices is not null||field.Lookup is not null))Fail(d,$"soon field '{field.Id}' cannot name choices or a lookup");
  if(field.State=="derived"&&string.IsNullOrWhiteSpace(field.Rule))Fail(d,$"derived field '{field.Id}' needs its rule text");
  if(field.Band is not null&&!TicketBand.Rows.Contains(field.Band))Fail(d,$"field '{field.Id}' names unknown band row '{field.Band}'");
 }

 static void Fail(TicketDescription d,string reason)=>throw new MarketDataException("ticket_description",$"Ticket description '{d.Product}' v{d.Version}: {reason}.");
}
