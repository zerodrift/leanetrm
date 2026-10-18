using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class AmountJsonChecks
{
 public static void Run()
 {
  var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);options.Converters.Add(new ExactDecimalJsonConverter());
  var revision=new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid());var key=new ReferenceKey(revision,"energy_quantity");
  var amount=new Quantity(ExactDecimal.Parse("12345678901234567890.123456789012345678"),key);
  var json=JsonSerializer.SerializeToElement(amount,options);
  if(json.GetProperty("amount").GetString()!=amount.Amount.ToString() || json.GetProperty("specification").GetProperty("code").GetString()!=key.Code)
   throw new Exception("The amount wire contract lost exact digits or its quantity specification.");
  if(JsonSerializer.Deserialize<ExactDecimal>("\"1.25\"",options)!=ExactDecimal.Parse("1.25"))throw new Exception("Lost decimal input.");
  try{JsonSerializer.Deserialize<ExactDecimal>("1.25",options);throw new Exception("Accepted an untyped JSON number.");}catch(JsonException){}
  if(DealAmountFormat.Default.Money!=3)throw new Exception("Final money display must default to three places.");
  var displays=JsonSerializer.Deserialize<System.Collections.Immutable.ImmutableArray<DisplayNumberRequest>>("""
   [{"value":"1000","decimalPlaces":3,"tick":null,"trimTrailingZeros":true},
    {"value":"0.95","decimalPlaces":6,"tick":null,"trimTrailingZeros":true},
    {"value":"3.1","decimalPlaces":4,"tick":"0.025","trimTrailingZeros":true},
    {"value":"12345678901234567890.1255","decimalPlaces":3,"tick":null,"trimTrailingZeros":false}]
   """,options);
  if(!UnitOperations.Display(displays).SequenceEqual(new[]{"1,000","0.95","3.100","12,345,678,901,234,567,890.126"}))
   throw new Exception("Compact noncash display must preserve tick places and exact three-place money rounding.");
  Console.WriteLine("PASS money defaults to three places, noncash display is compact and futures retain tick places");
  Console.WriteLine("PASS typed amounts retain exact canonical decimals and exact unit references in JSON");
 }
}
