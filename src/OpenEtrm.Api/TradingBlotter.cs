using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.Api;

// Report adapter over the same authorized product valuation operations. Numeric
// values stay typed in the backend; unsupported measures remain unavailable.
public sealed class TradingBlotter(PreviewManifest manifest,FuturesStore futures,SwapStore swaps)
{
 public const string Convention="trading_workspace_v1";
 public const string PivotConvention="trading_blotter_totals_v1";
 // D216: money of one currency adds across quantity units, so this convention measures money and trade counts only.
 public const string MoneyTotalsConvention="trading_blotter_money_totals_v1";
 // How many extra groupings of its one valuation a request may ask for.
 public const int MaximumSummaries=4;
 internal static readonly IReadOnlyDictionary<string,(string Field,PriceStatistic Statistic)> PriceMeasures=
  new[]{"executionPrice","mark","fixedPrice"}.SelectMany(field=>new[]{("Min",PriceStatistic.Minimum),("Max",PriceStatistic.Maximum),("Mean",PriceStatistic.ArithmeticMean)}
   .Select(stat=>new KeyValuePair<string,(string,PriceStatistic)>(field+stat.Item1,(field,stat.Item2)))).ToDictionary();
 private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 private static MarketDataException Invalid()=>new("blotter_view","Select supported dimensions/measures and exact authorized book/portfolio scopes.");
 internal static void ValidateView(FuturesBlotterView view)
 {
  if(view is null||view.Mode is not("trades" or "pivot")||view.GroupBy.IsDefault||view.Measures.IsDefault||
   view.GroupBy.Distinct().Count()!=view.GroupBy.Length||view.Measures.Distinct().Count()!=view.Measures.Length||
   view.GroupBy.Any(d=>d is not("portfolio" or "book" or "market" or "contract" or "instrumentType" or "commodity"))||
   view.Measures.Any(m=>m is not("tradeCount" or "lots" or "quantity" or "executionPnl" or "presentValue")&&!PriceMeasures.ContainsKey(m))||
   (view.Measures.Any(m=>PriceMeasures.TryGetValue(m,out var price)&&price.Statistic==PriceStatistic.ArithmeticMean)?view.PriceNumericalPolicy!=PriceStatistics.MeanPolicy:view.PriceNumericalPolicy is not null)||
   (view.Mode=="trades"?(view.GroupBy.Length!=0||view.Measures.Length!=0||view.PivotConvention is not null):
    (view.GroupBy.IsEmpty||view.Measures.IsEmpty||view.PivotConvention is not(PivotConvention or MoneyTotalsConvention)||
     view.PivotConvention==MoneyTotalsConvention&&view.Measures.Any(m=>m is not("presentValue" or "executionPnl" or "tradeCount"))||
     view.Measures.Contains("lots")&&!view.GroupBy.Contains("contract"))))throw Invalid();
 }
 // A summary groups the rows its request's own view reads, so it is a pivot of exactly that view's books and portfolios.
 private static void ValidateSummaries(FuturesBlotterView view,ImmutableArray<FuturesBlotterView> summaries)
 {
  if(summaries.IsDefault||summaries.Length>MaximumSummaries)throw Invalid();
  foreach(var summary in summaries)
  {
   ValidateView(summary);
   if(summary.Mode!="pivot"||!SameScope(summary,view))throw Invalid();
  }
 }
 // Each view names its books and its portfolios once, as the Blotter requires, and they are the same ones.
 private static bool SameScope(FuturesBlotterView first,FuturesBlotterView second)
 {
  static bool SameOnce(ImmutableArray<Guid> a,ImmutableArray<Guid> b)=>
   !a.IsDefault&&!b.IsDefault&&a.Distinct().Count()==a.Length&&b.Distinct().Count()==b.Length&&a.ToHashSet().SetEquals(b);
  return SameOnce(first.BookIds,second.BookIds)&&SameOnce(first.PortfolioIds,second.PortfolioIds);
 }
 // The caller has already bound the exact certified market and trading cut.
 internal async Task<BlotterRead?> ReadEmptyDirectory(NpgsqlConnection connection,InputFrame frame,FuturesBlotterView view,ImmutableArray<FuturesBlotterView> summaries,CancellationToken ct)
 {
  await MarketBundle.Require(connection,manifest,ct);
  // Base revision tables are checked before joins or report filters. A visible
  // revision of either product requires the unchanged complete valuation path.
  await using var command=ScheduleStore.Command(connection,"""
   SELECT EXISTS(SELECT 1 FROM trading.trade_revisions WHERE catalog_id=$1)
       OR EXISTS(SELECT 1 FROM trading.swap_trade_revisions WHERE catalog_id=$1)
   """,manifest.CatalogId);
  var hasRevisions=(bool)(await command.ExecuteScalarAsync(ct)??throw new PreviewDeniedException());
  if(hasRevisions)return null;
  ValidateView(view);ValidateSummaries(view,summaries);
  var books=await futures.Books(connection,ct);
  FuturesStore.ValidateScope(books,view.BookIds,view.PortfolioIds);
  await MarketBundle.RequireObservationPins(connection,manifest,frame.ObservationIds,ct);
  return new(Project(view,[],0,new JsonArray(),new(0,0,0)),Summarize(summaries,[]));
 }
 public async Task<BlotterRead> Read(NpgsqlConnection connection,PreviewData data,InputFrame frame,FuturesBlotterView view,ImmutableArray<FuturesBlotterView> summaries,CancellationToken ct)
 {
  ValidateView(view);ValidateSummaries(view,summaries);
  var tradeView=view with {Mode="trades",GroupBy=[],Measures=[],PivotConvention=null,PriceNumericalPolicy=null};
  var priced=JsonSerializer.SerializeToNode(await futures.Blotter(connection,data,frame,tradeView,ct),Json)!.AsObject();
  var rows=priced["holdings"]!.AsArray().Select(n=>n!.DeepClone().AsObject()).ToList();
  foreach(var row in rows)
  {
   var curve=data.Definitions["curves"].Single(c=>c.Text("code")==row["curve"]!.GetValue<string>());
   var quote=data.Quotes.Single(q=>q.Key.Code==curve.Text("quote_code"));
   row["quantitySpecification"]=JsonSerializer.SerializeToNode(quote.Quantity,Json);row["currencyReference"]=JsonSerializer.SerializeToNode(quote.Currency,Json);
   row["definitionRevisionId"]=manifest.RevisionId.ToString();row["instrumentType"]="future";row["presentValue"]=null;row["fixedPrice"]=null;row["direction"]=null;
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
  return new(Project(view,rows,priced["evaluatedContracts"]!.GetValue<int>(),priced["totals"],
   new(swapRows.Rows.Length,swapRows.Valuations,swapRows.SupplementalMarketLoads)),Summarize(summaries,rows));
 }
 // The report of a view and, grouped from the same valuation, each requested summary in request order.
 public sealed record BlotterRead(object Blotter,ImmutableArray<Summary> Summaries);
 public sealed record Summary(FuturesBlotterView View,List<JsonObject> Groups);
 private static ImmutableArray<Summary> Summarize(ImmutableArray<FuturesBlotterView> summaries,List<JsonObject> rows)=>
  [..summaries.Select(summary=>new Summary(summary,Groups(summary,rows)))];
 private sealed record Work(int SwapTrades,int SwapValuations,int SupplementalMarketLoads);
 private sealed record Report(FuturesBlotterView View,string PivotNumericalPolicy,List<JsonObject> Groups,
  List<JsonObject> Holdings,int EvaluatedContracts,JsonNode? Totals,Work Work,bool HasSwaps,string Convention,int UnvaluedPositions);
 // D213: valuation measures add only positions the selected close values; booked facts count every position.
 private static bool Valuation(string measure)=>measure is "presentValue" or "executionPnl"||PriceMeasures.TryGetValue(measure,out var price)&&price.Field=="mark";
 private static string? Text(JsonObject row,string key)=>row[key]?.GetValue<string>();
 private static ReferenceKey Key(JsonObject row,string key)=>row[key]!.Deserialize<ReferenceKey>(Json);
 private static bool Valued(JsonObject row)=>row["unavailableReason"] is null;
 private static int Unvalued(IEnumerable<JsonObject> selected)=>selected.Where(r=>!Valued(r)).Select(r=>Text(r,"tradeId")).Distinct().Count();
 private static Report Project(FuturesBlotterView view,List<JsonObject> rows,int evaluatedContracts,JsonNode? totals,Work work)=>
  new(view,"typed_partitioned_trade_totals_v1",view.Mode=="pivot"?Groups(view,rows):[],rows,evaluatedContracts,totals,work,
   rows.Any(r=>Text(r,"instrumentType")=="financial_swap"),Convention,Unvalued(rows));
 internal static List<JsonObject> Groups(FuturesBlotterView view,List<JsonObject> rows)
 {
  var groups=new List<JsonObject>();
  var hasPrices=view.Measures.Any(PriceMeasures.ContainsKey);
  // D98 keeps totals apart by quantity specification; D216's money totals add across quantity units, keeping apart only the dimensions, instrument type and currency.
  var byQuantity=view.PivotConvention!=MoneyTotalsConvention;
  // A market or contract is defined in one definition revision, so groups naming one are kept apart by revision and say which (R29).
  var byDefinition=view.GroupBy.Any(d=>d is "market" or "contract");
  var dimensions=new Dictionary<string,string>{{"portfolio","portfolioId"},{"book","bookId"},{"market","curve"},{"contract","contractId"},{"instrumentType","instrumentType"},{"commodity","commodity"}};
  // What identifies a row's group in a dimension: the field's text, or the code of the reference the field names.
  string? Identity(JsonObject row,string field)=>row[field] is JsonObject reference?Text(reference,"code"):Text(row,field);
  var partitions=rows.OrderBy(r=>Text(r,"tradeId"),StringComparer.Ordinal).ThenBy(r=>Text(r,"bindingId"),StringComparer.Ordinal).GroupBy(r=>JsonSerializer.Serialize(new{
   dimensions=view.GroupBy.Select(d=>Identity(r,dimensions[d])),type=Text(r,"instrumentType"),quantity=byQuantity?Key(r,"quantitySpecification"):(ReferenceKey?)null,currency=Key(r,"currencyReference"),
   priceMultiplier=hasPrices?Text(r,"quoteMultiplier"):null,definition=byDefinition?Text(r,"definitionRevisionId"):null}));
  foreach(var partition in partitions)
  {
   var first=partition.First();var group=new JsonObject();
   foreach(var d in view.GroupBy)
   {
    var field=dimensions[d];group[field=="curve"?"market":field]=first[field]?.DeepClone();
    if(d is "portfolio" or "book")group[d]=first[d]?.DeepClone();
    if(d=="contract"){group["deliveryFrom"]=first["deliveryFrom"]?.DeepClone();group["deliveryTo"]=first["deliveryTo"]?.DeepClone();}
   }
   if(byDefinition)group["definitionRevisionId"]=first["definitionRevisionId"]?.DeepClone();
   foreach(var field in byQuantity?new[]{"instrumentType","unit","currency","quantitySpecification","currencyReference"}:new[]{"instrumentType","currency","currencyReference"})
    group[field]=first[field]?.DeepClone();
   if(hasPrices){group["quoteMultiplier"]=first["quoteMultiplier"]?.DeepClone();group["priceUnit"]=PreviewData.PriceUnitLabel(Text(first,"currency")!,Text(first,"unit")!,Text(first,"quoteMultiplier"));}
   foreach(var measure in view.Measures)
   {
    if(measure=="tradeCount"){group[measure]=partition.Select(r=>Text(r,"tradeId")).Distinct().Count();continue;}
    var counted=Valuation(measure)?partition.Where(Valued).ToArray():partition.ToArray();
    if(PriceMeasures.TryGetValue(measure,out var price))
    {
     if(counted.Length==0||counted.Any(r=>r[price.Field] is null)){group[measure]=null;continue;}
     var priceUnit=new PriceUnit(Key(first,"currencyReference"),Key(first,"quantitySpecification"),ExactDecimal.Parse(Text(first,"quoteMultiplier")??throw Invalid()));
     group[measure]=PriceStatistics.AggregateExact(counted.Select(r=>ExactUnitPrice.Parse(Text(r,price.Field)!,priceUnit)).ToImmutableArray(),price.Statistic,
      price.Statistic==PriceStatistic.ArithmeticMean?view.PriceNumericalPolicy:null).ToExactString();continue;
    }
    if(counted.Length==0||counted.Any(r=>r[measure] is null)){group[measure]=null;continue;}
    if(measure=="quantity")group[measure]=counted.Select(r=>new Quantity(ExactDecimal.Parse(Text(r,measure)!),Key(r,"quantitySpecification"))).Aggregate((a,b)=>a.Add(b)).Amount.ToString();
    else if(measure is "presentValue" or "executionPnl")group[measure]=counted.Select(r=>ExactMoney.Parse(Text(r,measure)!,Key(r,"currencyReference"))).Aggregate((a,b)=>a.Add(b)).ToExactString();
    else group[measure]=counted.Select(r=>ExactDecimal.Parse(Text(r,measure)!)).Aggregate((a,b)=>a+b).ToString();
   }
   group["unvaluedPositions"]=Unvalued(partition);
   groups.Add(group);
  }
  return groups;
 }
}
