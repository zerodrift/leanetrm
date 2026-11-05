using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Crude;

namespace OpenEtrm.Api;

internal sealed record DeliverySetupPeriod(string From,string To);
internal sealed record DeliverySetupContract(ContractKind Kind,DateOnly From,DateOnly To,bool HasInterval=false);
internal sealed record GasSetupStartingValues(Dictionary<string,Dictionary<string,string>> Records,DeliverySetupPeriod? CheckPeriod);

// Suggestions for visible, editable new forms, never fallback calculation terms.
// Callers supply the configuration/contracts read under the selected context.
internal static class DeliverySetupDefaults
{
 internal static GasSetupStartingValues Gas(JsonElement configuration)
 {
  var records=new Dictionary<string,Dictionary<string,string>>{
   ["paths"]=new(){["adjustment"]="0"},["groups"]=new(){["adjustment"]="0"},["meters"]=new(),["tariffs"]=new()};
  var conventions=configuration.GetProperty("paths").EnumerateArray()
   .Select(p=>(Clock:p.Text("clock"),Start:p.Text("dayStartMinute"))).Distinct().ToArray();
  if(conventions is [{Clock:var clock,Start:var start}]&&clock.Length>0)
  {
   records["paths"]["clock"]=clock;records["paths"]["dayStartMinute"]=start;
  }
  DeliverySetupPeriod? CommonPeriod(string category)
  {
   var periods=configuration.GetProperty(category).EnumerateArray()
    .Select(p=>new DeliverySetupPeriod(p.Text("validFrom"),p.Text("validTo"))).Distinct().ToArray();
   return periods.Length==1?periods[0]:null;
  }
  var meterPeriod=CommonPeriod("meters");
  foreach(var category in new[]{"meters","tariffs"})if(CommonPeriod(category) is {} period)
  {
   records[category]["validFrom"]=period.From;records[category]["validTo"]=period.To;
  }
  var checkPeriod=meterPeriod is {} meter&&DateWorkspace.Date(meter.To).DayNumber-DateWorkspace.Date(meter.From).DayNumber is >0 and <=31?meterPeriod:null;
  return new(records,checkPeriod);
 }

 internal static DeliverySetupPeriod? FuturePeriod(IEnumerable<DeliverySetupContract> periods,DateTimeOffset asOf)
 {
  bool Eligible(DeliverySetupContract period)
  {
   if(!CrudeDeliveryWindow.IsMonthlyForward(period.Kind,period.From,period.To,period.HasInterval))return false;
   var from=new DateTimeOffset(period.From.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
   var to=new DateTimeOffset(period.To.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc));
   if(from<=asOf)return false;
   try{_=new CrudeDeliveryWindow(from,to);return true;}
   catch(MarketDataException e)when(e.Code=="crude_delivery_window"){return false;}
  }
  var candidates=periods.Where(Eligible).Take(2).ToArray();
  return candidates.Length==1?new(candidates[0].From.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),candidates[0].To.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture)):null;
 }
}
