using System.Text.Json;
using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.Api.Presentation;
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
  var catalog=new UnitCatalogData(revision,new Dictionary<string,ImmutableArray<JsonElement>>{
   ["units"]=[JsonSerializer.SerializeToElement(new{code="energy",dimension_code="energy_dimension",symbol="old symbol"})],
   ["unit_conversions"]=[],
   ["quantity_specifications"]=[JsonSerializer.SerializeToElement(new{code="energy_quantity",commodity_code="power",basis_code="delivery",unit_code="energy",dimension_code="energy_dimension"})]
  }.ToImmutableDictionary());
  var labels=new ReferenceDisplayData(revision,new(revision.CatalogId,Guid.NewGuid()),1,new Dictionary<string,ImmutableArray<JsonElement>>{
   ["units"]=[JsonSerializer.SerializeToElement(new{code="energy",symbol="MWh"})]
  }.ToImmutableDictionary());
  var converted=JsonSerializer.SerializeToElement(UnitOperations.Convert(catalog,new("2","energy_quantity","energy_quantity",null,null),labels),options);
  if(converted.GetProperty("formatted").GetString()!="2 MWh"||converted.GetProperty("value").GetString()!="2"||
   converted.GetProperty("target").GetProperty("revision").GetProperty("revisionId").GetGuid()!=revision.RevisionId)
   throw new Exception("Applied unit labels must change formatting while preserving the exact quantity and original reference identity.");
  try{UnitOperations.Convert(catalog,new("2","energy_quantity","energy_quantity",null,null),labels with{SourceRevision=new(revision.CatalogId,Guid.NewGuid())});throw new Exception("Accepted mismatched unit display revision.");}
  catch(MarketDataException e)when(e.Code=="reference_display"){}
  Console.WriteLine("PASS applied unit metadata changes formatting only, retaining exact quantity identity and value");
  var referenceRow=ReferenceJson.PreserveNumericLexemes("""
   {"code":"point","latitude":41.123456789012345678,"longitude":-87.987654321098765432,"ordinal":2,"is_business_day":true,"missing":null}
   """);
  if(referenceRow.GetProperty("latitude").GetString()!="41.123456789012345678"||referenceRow.GetProperty("longitude").GetString()!="-87.987654321098765432"||
     referenceRow.GetProperty("ordinal").GetInt32()!=2||!referenceRow.GetProperty("is_business_day").GetBoolean()||referenceRow.GetProperty("missing").ValueKind!=JsonValueKind.Null)
   throw new Exception("Reference presentation must preserve exact numeric lexemes, ordinal ordering, booleans and nulls.");
  var financialRevision=Guid.NewGuid();var unitRevision=Guid.NewGuid();
  var manifest=new PreviewManifest("local-synthetic-preview",Guid.NewGuid(),Guid.NewGuid(),revision.CatalogId,financialRevision,Guid.NewGuid(),Guid.NewGuid(),[],null,
    UnitCatalog:new(unitRevision,Guid.NewGuid()),FuturesConvention:"contract_lot_execution_pnl_v2");
  foreach(var table in new[]{"dimensions","units","unit_conversions","commodities","measurement_bases","quantity_specifications"})
   if(ReferenceEditors.Source(manifest,table)!=new ReferenceRevision(revision.CatalogId,unitRevision))
    throw new Exception("The reference editor must use the exact supplemental unit revision for each consumed unit table.");
  if(ReferenceEditors.Source(manifest,"currencies")!=new ReferenceRevision(revision.CatalogId,financialRevision)||
     ReferenceEditors.Source(manifest with{UnitCatalog=null},"units")!=new ReferenceRevision(revision.CatalogId,financialRevision))
   throw new Exception("Reference presentation must retain the explicitly selected financial revision when no supplemental unit revision is selected.");
  Console.WriteLine("PASS common API reference transport preserves numeric lexemes and exact source-revision routing");
 }
}
