using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.Api;

// Visible editable entry suggestions. Calculations/imports still require every
// resulting term explicitly. Existing valid terms are never replaced by these.
internal static class GasTicketStartingValues
{
 internal static TicketSideDraft Source(TicketDraft draft,TicketSideDraft side)=>side.Kind=="physical"?side:
  draft.Sides.SingleOrDefault(s=>s.Kind=="physical"&&s.Code==side.Values.GetValueOrDefault($"{side.Kind}_source"))??throw Missing("Choose the physical quantity source first.");
 internal static JsonElement Path(JsonElement catalog,TicketSideDraft side)=>catalog.GetProperty("locations").EnumerateArray().Where(p=>
  new[]{("pipeline","pipeline"),("zone","zone"),("deliveryPoint","delivery_point"),("product","product")}.All(pair=>p.Text(pair.Item1)==side.Values.GetValueOrDefault(pair.Item2))).ToArray() is [var path]?path:
  throw Missing("Choose the complete delivery path to get a starting value.");
 internal static TicketLookup Value(PreviewData data,InputFrame frame,TicketFieldRequest body,JsonElement catalog)
 {
  var side=body.Draft.Sides.Single(s=>s.Code==body.Side);var current=side.Values.GetValueOrDefault(body.Field);
  var kind=GasTicket.LookupOf(body.Draft,body.Side,body.Field);
  TicketLookup Suggest(string value,string label)=>new(value,new("suggestion",label));
  if(kind=="entry_quantity"&&side.Values.ContainsKey("link_source"))throw Missing("Quantity follows the selected physical source.");
  if(kind=="entry_quantity")return Suggest(current??"100","Suggested native quantity");
  if(kind=="entry_spread")return Suggest(current??"0","Suggested zero additional spread");
  if(kind is "entry_from" or "entry_to")
  {
   if(current is not null)return Suggest(current,"Retained entry");
   var path=Path(catalog,side);var valuation=path.GetProperty("valuation");
   if(valuation.ValueKind!=JsonValueKind.Object)throw Missing("This path has no pricing assignment.");
   var curve=valuation.Text("curve");var members=data.Definitions["curve_members"].Where(m=>m.Text("curve_code")==curve).Select(m=>m.Id("contract_id")).ToHashSet();
   var clock=new GasDayClock(new(data.ReferenceRevision,path.Text("clock")),PpaQuantitiesStore.Clock(data,path.Text("clock")).Rules,path.GetProperty("dayStartMinute").GetInt32());
   var earliest=DateOnly.FromDateTime(frame.At.UtcDateTime);while(clock.Boundary(earliest)<=frame.At)earliest=earliest.AddDays(1);
   if(kind=="entry_to"&&DateOnly.TryParseExact(side.Values.GetValueOrDefault("delivery_from"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var chosen)&&chosen>earliest)earliest=chosen;
   var periods=data.Contracts.Where(c=>members.Contains(c.Key.Id)&&c.Kind==ContractKind.Forward&&c.DeliveryFrom.Day==1&&c.DeliveryTo==c.DeliveryFrom.AddMonths(1)&&c.DeliveryTo>earliest&&c.Expiry>frame.At).OrderBy(c=>c.DeliveryFrom).ToArray();
   if(periods.Length==0||periods.Length>1&&periods[0].DeliveryFrom==periods[1].DeliveryFrom)throw Missing("No unambiguous future source period is available.");
   var first=periods[0];var from=first.DeliveryFrom>earliest?first.DeliveryFrom:earliest;
   return Suggest((kind=="entry_from"?from:first.DeliveryTo).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),"Suggested first available delivery period");
  }
  if(kind=="entry_payment")
  {
   var source=Source(body.Draft,side);if(!DateOnly.TryParseExact(source.Values.GetValueOrDefault("delivery_to"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var to))throw Missing("Choose the source delivery end first.");
   var path=Path(catalog,source);var clock=new GasDayClock(new(data.ReferenceRevision,path.Text("clock")),PpaQuantitiesStore.Clock(data,path.Text("clock")).Rules,path.GetProperty("dayStartMinute").GetInt32());
   if(current is not null&&SchedulePreview.ParseAt(current)>=clock.Boundary(to))return Suggest(current,"Retained payment date");
   var last=to.AddDays(-1);var date=new DateOnly(last.Year,last.Month,1).AddMonths(1).AddDays(9);
   return Suggest(new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)).ToString("O",CultureInfo.InvariantCulture),"Suggested following-month payment on day 10 UTC");
  }
  throw Missing("This starting value requires its market-price lookup.");
 }
 internal static string Price(string exact,ExactDecimal quoteTick)
 {
  var parts=exact.Split('/');var ratio=parts.Length==1?
   SignedRatio.TryParse(exact,out var parsed)?parsed:throw Missing("The suggested price is not an exact number."):
   new SignedRatio(BigInteger.Parse(parts[0],CultureInfo.InvariantCulture),BigInteger.Parse(parts[1],CultureInfo.InvariantCulture));
  return ratio.Format(AmountFormat.ForTick(quoteTick).DecimalPlaces,MidpointRounding.ToEven);
 }
 static MarketDataException Missing(string message)=>new("ticket_lookup",message);
}
