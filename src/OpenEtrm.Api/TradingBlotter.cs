using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Report adapter over the same authorized product valuation operations. Numeric
// values stay typed in the backend; unsupported measures remain unavailable.
public sealed class TradingBlotter(PreviewManifest manifest,FuturesStore futures,SwapStore swaps)
{
 public const string Convention="trading_workspace_v1";
 public const string PivotConvention="trading_blotter_totals_v1";
 internal static readonly IReadOnlyDictionary<string,(string Field,PriceStatistic Statistic)> PriceMeasures=
  new[]{"executionPrice","mark","fixedPrice"}.SelectMany(field=>new[]{("Min",PriceStatistic.Minimum),("Max",PriceStatistic.Maximum),("Mean",PriceStatistic.ArithmeticMean)}
   .Select(stat=>new KeyValuePair<string,(string,PriceStatistic)>(field+stat.Item1,(field,stat.Item2)))).ToDictionary();
 private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 private static MarketDataException Invalid()=>new("blotter_view","Select supported dimensions/measures and exact authorized book/portfolio scopes.");
 public async Task<object> Read(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesBlotterView view,CancellationToken ct)
 {
  if(view is null||view.Mode is not("trades" or "pivot")||view.GroupBy.IsDefault||view.Measures.IsDefault||
   view.GroupBy.Distinct().Count()!=view.GroupBy.Length||view.Measures.Distinct().Count()!=view.Measures.Length||
   view.GroupBy.Any(d=>d is not("portfolio" or "book" or "market" or "contract" or "instrumentType"))||
   view.Measures.Any(m=>m is not("tradeCount" or "lots" or "quantity" or "executionPnl" or "presentValue")&&!PriceMeasures.ContainsKey(m))||
   (view.Measures.Any(m=>PriceMeasures.TryGetValue(m,out var price)&&price.Statistic==PriceStatistic.ArithmeticMean)?view.PriceNumericalPolicy!=PriceStatistics.MeanPolicy:view.PriceNumericalPolicy is not null)||
   (view.Mode=="trades"?(view.GroupBy.Length!=0||view.Measures.Length!=0||view.PivotConvention is not null):
    (view.GroupBy.IsEmpty||view.Measures.IsEmpty||view.PivotConvention!=PivotConvention||view.Measures.Contains("lots")&&!view.GroupBy.Contains("contract"))))throw Invalid();
  var tradeView=view with {Mode="trades",GroupBy=[],Measures=[],PivotConvention=null,PriceNumericalPolicy=null};
  var priced=JsonSerializer.SerializeToNode(await futures.Blotter(connection,data,frame,tradeView,ct),Json)!.AsObject();
  var rows=priced["holdings"]!.AsArray().Select(n=>n!.DeepClone().AsObject()).ToList();
  foreach(var row in rows)
  {
   var curve=data.Definitions["curves"].Single(c=>c.Text("code")==row["curve"]!.GetValue<string>());
   var quote=data.Quotes.Single(q=>q.Key.Code==curve.Text("quote_code"));
   row["quantitySpecification"]=JsonSerializer.SerializeToNode(quote.Quantity,Json);row["currencyReference"]=JsonSerializer.SerializeToNode(quote.Currency,Json);
   row["definitionRevisionId"]=manifest.RevisionId.ToString();row["instrumentType"]="future";row["presentValue"]=null;row["fixedPrice"]=null;
  }
  var swapRows=await swaps.Holdings(connection,data,frame,view.BookIds,view.PortfolioIds,ct);
  rows.AddRange(swapRows.Rows.Select(r=>JsonNode.Parse(r.GetRawText())!.AsObject()));
  if(rows.Select(r=>r["tradeId"]!.GetValue<string>()+r["bindingId"]!.GetValue<string>()).Distinct().Count()!=rows.Count)throw Invalid();
  var formats=await DealAmountFormats.All(connection,manifest.CatalogId,ct);
  foreach(var row in rows)
  {
   var selectedFormats=formats.GetValueOrDefault(Guid.Parse(row["tradeId"]!.GetValue<string>()));
   row["amountFormat"]=JsonSerializer.SerializeToNode(DealAmountFormats.Resolve(selectedFormats,"trade"),Json);
   row["fixedAmountFormat"]=JsonSerializer.SerializeToNode(DealAmountFormats.Resolve(selectedFormats,"fixed"),Json);
  }
  string? Text(JsonObject row,string key)=>row[key]?.GetValue<string>();
  ReferenceKey Key(JsonObject row,string key)=>row[key]!.Deserialize<ReferenceKey>(Json);
  var groups=new List<JsonObject>();
  var hasPrices=view.Measures.Any(PriceMeasures.ContainsKey);
  var dimensions=new Dictionary<string,string>{{"portfolio","portfolioId"},{"book","bookId"},{"market","curve"},{"contract","contractId"},{"instrumentType","instrumentType"}};
  var partitions=rows.OrderBy(r=>Text(r,"tradeId"),StringComparer.Ordinal).ThenBy(r=>Text(r,"bindingId"),StringComparer.Ordinal).GroupBy(r=>JsonSerializer.Serialize(new{
   dimensions=view.GroupBy.Select(d=>Text(r,dimensions[d])),type=Text(r,"instrumentType"),quantity=Key(r,"quantitySpecification"),currency=Key(r,"currencyReference"),
   priceMultiplier=hasPrices?Text(r,"quoteMultiplier"):null,definition=view.GroupBy.Any(d=>d is "market" or "contract")?Text(r,"definitionRevisionId"):null}));
  foreach(var partition in partitions)
  {
   var first=partition.First();var group=new JsonObject();
   foreach(var d in view.GroupBy)
   {
    var field=dimensions[d];group[field=="curve"?"market":field]=first[field]?.DeepClone();
    if(d is "portfolio" or "book")group[d]=first[d]?.DeepClone();
    if(d=="contract"){group["deliveryFrom"]=first["deliveryFrom"]?.DeepClone();group["deliveryTo"]=first["deliveryTo"]?.DeepClone();group["definitionRevisionId"]=first["definitionRevisionId"]?.DeepClone();}
   }
   foreach(var field in new[]{"instrumentType","unit","currency","quantitySpecification","currencyReference"})group[field]=first[field]?.DeepClone();
   if(hasPrices){group["quoteMultiplier"]=first["quoteMultiplier"]?.DeepClone();group["priceUnit"]=Text(first,"currency")+"/"+Text(first,"unit")+(Text(first,"quoteMultiplier") is {} multiplier&&multiplier!="1"?" × "+multiplier:"");}
   foreach(var measure in view.Measures)
   {
    if(measure=="tradeCount"){group[measure]=partition.Select(r=>Text(r,"tradeId")).Distinct().Count();continue;}
    if(PriceMeasures.TryGetValue(measure,out var price))
    {
     if(partition.Any(r=>r[price.Field] is null)){group[measure]=null;continue;}
     var priceUnit=new PriceUnit(Key(first,"currencyReference"),Key(first,"quantitySpecification"),ExactDecimal.Parse(Text(first,"quoteMultiplier")??throw Invalid()));
     group[measure]=PriceStatistics.Aggregate(partition.Select(r=>new UnitPrice(ExactDecimal.Parse(Text(r,price.Field)!),priceUnit)).ToImmutableArray(),price.Statistic,
      price.Statistic==PriceStatistic.ArithmeticMean?view.PriceNumericalPolicy:null).Value.ToString();continue;
    }
    if(partition.Any(r=>r[measure] is null)){group[measure]=null;continue;}
    if(measure=="quantity")group[measure]=partition.Select(r=>new Quantity(ExactDecimal.Parse(Text(r,measure)!),Key(r,"quantitySpecification"))).Aggregate((a,b)=>a.Add(b)).Amount.ToString();
    else if(measure is "presentValue" or "executionPnl")group[measure]=partition.Select(r=>new Money(ExactDecimal.Parse(Text(r,measure)!),Key(r,"currencyReference"))).Aggregate((a,b)=>a.Add(b)).Amount.ToString();
    else group[measure]=partition.Select(r=>ExactDecimal.Parse(Text(r,measure)!)).Aggregate((a,b)=>a+b).ToString();
   }
   groups.Add(group);
  }
  return new{view,priceNumericalPolicy=view.PriceNumericalPolicy,pivotNumericalPolicy="typed_partitioned_trade_totals_v1",groups=view.Mode=="pivot"?groups:[],holdings=rows,evaluatedContracts=priced["evaluatedContracts"]!.GetValue<int>(),totals=priced["totals"],
   work=new{swapTrades=swapRows.Rows.Length,swapValuations=swapRows.Valuations,supplementalMarketLoads=swapRows.SupplementalMarketLoads},
   hasSwaps=rows.Any(r=>Text(r,"instrumentType")=="financial_swap"),convention=Convention};
 }
}
