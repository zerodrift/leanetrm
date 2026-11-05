using System.Globalization;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class DeliverySetupDefaultsChecks
{
 internal static void Run()
 {
  var config=JsonSerializer.SerializeToElement(new{
   paths=new[]{new{clock="utc",dayStartMinute=540}},groups=Array.Empty<object>(),
   meters=new[]{new{validFrom="2021-01-01",validTo="2021-02-01"}},tariffs=Array.Empty<object>()});
  var before=config.GetRawText();
  var result=JsonSerializer.SerializeToElement(DeliverySetupDefaults.Gas(config),new JsonSerializerOptions(JsonSerializerDefaults.Web));
  var records=result.GetProperty("records");
  Require(records.GetProperty("paths").GetProperty("adjustment").GetString()=="0","A new point starts with an explicit zero adder.");
  Require(records.GetProperty("paths").GetProperty("clock").GetString()=="utc"&&records.GetProperty("paths").GetProperty("dayStartMinute").GetString()=="540","A single contextual gas-day convention is reusable.");
  Require(!records.GetProperty("tariffs").TryGetProperty("rate",out _),"A fee rate is never invented.");
  Require(result.GetProperty("checkPeriod").GetProperty("to").GetString()=="2021-02-01","A single supported meter period supplies coverage starting dates.");
  Require(config.GetRawText()==before,"Starting values must not alter existing setup terms.");
  var longMeter=JsonSerializer.SerializeToElement(new{paths=Array.Empty<object>(),meters=new[]{new{validFrom="2021-01-01",validTo="2022-01-01"}},tariffs=Array.Empty<object>()});
  Require(DeliverySetupDefaults.Gas(longMeter).CheckPeriod is null,"A long assignment does not produce a coverage suggestion beyond the 31-day limit.");
  var mixed=JsonSerializer.SerializeToElement(new{
   paths=new[]{new{clock="utc",dayStartMinute=540},new{clock="utc",dayStartMinute=60}},
   meters=new[]{new{validFrom="2021-01-01",validTo="2021-02-01"},new{validFrom="2021-02-01",validTo="2021-03-01"}},tariffs=Array.Empty<object>()});
  result=JsonSerializer.SerializeToElement(DeliverySetupDefaults.Gas(mixed),new JsonSerializerOptions(JsonSerializerDefaults.Web));
  Require(!result.GetProperty("records").GetProperty("paths").TryGetProperty("dayStartMinute",out _)&&result.GetProperty("checkPeriod").ValueKind==JsonValueKind.Null,"Conflicting conventions or meter periods do not choose a winner.");
  var at=DateTimeOffset.Parse("2020-12-18T20:00:00Z",CultureInfo.InvariantCulture);
  DeliverySetupContract jan=new(ContractKind.Forward,new(2021,1,1),new(2021,2,1)),feb=new(ContractKind.Forward,new(2021,2,1),new(2021,3,1));
  Require(DeliverySetupDefaults.FuturePeriod([jan],at)==new DeliverySetupPeriod("2021-01-01","2021-02-01"),"One future source month is a useful explicit date suggestion.");
  Require(DeliverySetupDefaults.FuturePeriod([jan,feb],at) is null,"Several future source months need a user choice.");
  Require(DeliverySetupDefaults.FuturePeriod([jan,jan],at) is null,"Overlapping source contracts do not imply a unique source period.");
  Require(DeliverySetupDefaults.FuturePeriod([jan],DateTimeOffset.Parse("2021-01-01T00:00:00Z")) is null,"A source month already starting at valuation As of is not a pre-delivery default.");
  Require(DeliverySetupDefaults.FuturePeriod([jan with{From=new(2021,1,2)}],at) is null,"An irregular source contract is not advertised as a monthly starting period.");
  Require(DeliverySetupDefaults.FuturePeriod([jan with{Kind=ContractKind.Future}],at) is null,"A monthly future is not an admitted crude forward.");
  Require(DeliverySetupDefaults.FuturePeriod([jan with{HasInterval=true}],at) is null,"An interval contract is not an admitted crude monthly source.");
  Require(DeliverySetupDefaults.FuturePeriod([jan with{From=new(2100,1,1),To=new(2100,2,1)}],at) is null,"A source outside the native crude horizon is not an authoring default.");
  Require(DeliverySetupDefaults.FuturePeriod([jan with{From=new(2099,12,1),To=new(2100,1,1)}],at)==new DeliverySetupPeriod("2099-12-01","2100-01-01"),"The admitted upper boundary remains eligible.");
  Console.WriteLine("PASS delivery starting values: explicit zero adders, consistent contextual conventions, unique coverage, future source dates and ambiguity refusal");
 }
 private static void Require(bool valid,string message){if(!valid)throw new Exception(message);}
}
