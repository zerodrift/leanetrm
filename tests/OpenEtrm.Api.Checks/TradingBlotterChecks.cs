using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

// D221: desk money totals add money of one currency across quantity units, and never currencies, instrument types or
// quantities. The preview example holds one currency, so a second currency, a second currency revision and a second
// definition revision are exercised here, on synthetic positions.
static class TradingBlotterChecks
{
    private const string TotalsConvention="trading_blotter_totals_v1",MoneyConvention="trading_blotter_money_totals_v1";
    private static readonly string[] MoneyMeasures=["presentValue","executionPnl","tradeCount"];
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static readonly ReferenceRevision Revision=new(Guid.NewGuid(),Guid.NewGuid());
    private static readonly ReferenceRevision OtherRevision=new(Revision.CatalogId,Guid.NewGuid());

    private static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    private static FuturesBlotterView Pivot(string? convention,string[] groupBy,string[] measures,string? policy=null)=>
        new("pivot",[],[],[..groupBy],[..measures],convention,policy);
    private static bool Accepts(FuturesBlotterView view)
    {
        try{TradingBlotter.ValidateView(view);return true;}
        catch(MarketDataException e)when(e.Code=="blotter_view"){return false;}
    }
    private static JsonNode? Reference(string code,bool otherRevision=false)=>
        JsonSerializer.SerializeToNode(new ReferenceKey(otherRevision?OtherRevision:Revision,code),Json);
    // A position as the Blotter reads state it: valued unless it gives the reason it is not.
    private static JsonObject Position(string trade,string type,string unit,string? amount,string currency="usd",bool otherRevision=false,
        string? unavailable=null,string binding="1",string market="gas",string definition="d1")=>new()
    {
        ["tradeId"]=trade,["bindingId"]=binding,["instrumentType"]=type,["bookId"]="a",["book"]="Book A",["curve"]=market,["definitionRevisionId"]=definition,
        ["quantitySpecification"]=Reference(unit),["unit"]=unit,["currencyReference"]=Reference(currency,otherRevision),["currency"]=currency.ToUpperInvariant(),
        ["quoteMultiplier"]="1",["presentValue"]=type=="future"?null:amount,["executionPnl"]=type=="future"?amount:null,["unavailableReason"]=unavailable
    };
    // Swaps in MWh and MMBtu, one trade with two bindings and one not valued; a second currency; one currency code in another
    // reference revision; the same market in another definition revision. Futures in two markets and units, one not valued.
    private static List<JsonObject> Positions()=>
    [
        Position("swap-1","financial_swap","mwh","0.1"),Position("swap-1","financial_swap","mwh","1/6",binding:"2"),
        Position("swap-2","financial_swap","mmbtu","1/3"),Position("swap-3","financial_swap","mmbtu",null,unavailable:"missing_observation"),
        Position("swap-4","financial_swap","mwh","7",currency:"eur"),Position("swap-5","financial_swap","mwh","3",otherRevision:true),
        Position("swap-6","financial_swap","mwh","11",definition:"d2"),
        Position("future-1","future","mmbtu","2"),Position("future-2","future","mwh","5",market:"power"),
        Position("future-3","future","mmbtu",null,unavailable:"source_expired")
    ];
    private static string? Text(JsonObject group,string key)=>group[key]?.GetValue<string>();
    private static int Count(JsonObject group,string key)=>group[key]!.GetValue<int>();
    private static bool InCurrency(JsonObject group,string currency,bool otherRevision=false)=>
        group["currencyReference"]!.ToJsonString()==Reference(currency,otherRevision)!.ToJsonString();
    private static JsonObject Group(IEnumerable<JsonObject> groups,string type,string currency,bool otherRevision=false)=>
        groups.Single(g=>Text(g,"instrumentType")==type&&InCurrency(g,currency,otherRevision));

    public static void Run()
    {
        Validation();
        Partitions();
        Aggregation();
    }

    private static void Validation()
    {
        string[][] dimensions=[["instrumentType"],["book"],["market","instrumentType"],["portfolio","book","market","contract","instrumentType","commodity"]];
        foreach(var groupBy in dimensions)
            Require(Accepts(Pivot(MoneyConvention,groupBy,MoneyMeasures)),"The money convention refused the dimensions "+string.Join(",",groupBy));
        string[][] subsets=[["presentValue"],["executionPnl"],["tradeCount"],["tradeCount","presentValue"]];
        foreach(var measures in subsets)
            Require(Accepts(Pivot(MoneyConvention,["book"],measures)),"The money convention refused the measures "+string.Join(",",measures));
        Console.WriteLine("PASS the money convention accepts present value, execution P&L and trade count over any supported dimensions");

        // Each refused view is accepted under the totals convention, so it is the money convention that refuses it.
        (string[] GroupBy,string[] Measures)[] refused=
        [
            ..TradingBlotter.PriceMeasures.Keys.Select(measure=>(new[]{"instrumentType"},new[]{measure})),
            (["contract"],["lots"]),(["instrumentType"],["quantity"]),(["instrumentType"],["presentValue","quantity"]),(["book"],["tradeCount","markMax"])
        ];
        foreach(var (groupBy,measures) in refused)
        {
            var policy=measures.Any(measure=>measure.EndsWith("Mean",StringComparison.Ordinal))?PriceStatistics.MeanPolicy:null;
            var view=Pivot(TotalsConvention,groupBy,measures,policy);
            Require(Accepts(view),"The totals convention refused "+string.Join(",",measures));
            Require(!Accepts(view with{PivotConvention=MoneyConvention}),"The money convention accepted "+string.Join(",",measures));
        }
        Console.WriteLine("PASS the money convention refuses quantity, lots and every price statistic that the totals convention accepts");

        Require(!Accepts(Pivot(MoneyConvention,["instrumentType"],MoneyMeasures,PriceStatistics.MeanPolicy)),"The money convention accepted a price policy it has no use for");
        Require(!Accepts(Pivot(MoneyConvention+"_v2",["instrumentType"],MoneyMeasures))&&!Accepts(Pivot(null,["instrumentType"],MoneyMeasures)),
            "An unknown or missing convention was accepted");
        Require(!Accepts(new FuturesBlotterView("trades",[],[],[],[],MoneyConvention))&&Accepts(new FuturesBlotterView("trades",[],[],[],[],null)),
            "Trade rows selected a pivot convention");
        Console.WriteLine("PASS the money convention takes no price policy; trade rows, unknown and missing conventions are refused");
    }

    private static void Partitions()
    {
        var groups=TradingBlotter.Groups(Pivot(MoneyConvention,["instrumentType"],MoneyMeasures),Positions());
        Require(groups.Count==4,"Money groups were not one per instrument type and exact currency: "+groups.Count);
        string[] stated=["currency","currencyReference","executionPnl","instrumentType","presentValue","tradeCount","unvaluedPositions"];
        Require(groups.All(group=>group.Select(member=>member.Key).Order(StringComparer.Ordinal).SequenceEqual(stated)),
            "A money group stated a unit or quantity specification, or lacked its type, currency, measures or unvalued count");
        Require(Text(Group(groups,"financial_swap","usd"),"presentValue")=="11.6",
            "Swap present value was not added across quantity units and definition revisions of one currency");
        Require(Text(Group(groups,"financial_swap","eur"),"presentValue")=="7","A second currency was added to the first, or lost");
        Require(Text(Group(groups,"financial_swap","usd",otherRevision:true),"presentValue")=="3","One currency code in two reference revisions was added together");
        Require(Text(Group(groups,"future","usd"),"executionPnl")=="7","Futures execution P&L was not added across quantity units");
        Console.WriteLine("PASS money adds across quantity units within one exact currency and instrument type, and never across currencies, currency revisions or instrument types");

        var books=TradingBlotter.Groups(Pivot(MoneyConvention,["book"],MoneyMeasures),Positions());
        Require(books.Count==4&&Text(Group(books,"financial_swap","usd"),"presentValue")=="11.6"&&Text(Group(books,"future","usd"),"executionPnl")=="7",
            "A dimension other than instrument type added swaps and futures together");
        Console.WriteLine("PASS a book holding swaps and futures gets a group for each, as instrument types are kept apart whatever the dimensions");

        var markets=TradingBlotter.Groups(Pivot(MoneyConvention,["market","instrumentType"],MoneyMeasures),Positions());
        var gasSwaps=markets.Where(group=>Text(group,"market")=="gas"&&Text(group,"instrumentType")=="financial_swap"&&InCurrency(group,"usd")).ToArray();
        Require(markets.Count==6&&gasSwaps.Select(group=>Text(group,"presentValue")).Order(StringComparer.Ordinal).SequenceEqual(["0.6","11"]),
            "A market dimension merged the definition revisions of its market, or split a definition revision by unit");
        Console.WriteLine("PASS dimensions that name a market keep its definition revisions apart");

        // D221: a group kept apart by definition revision says which revision it is, so two groups of one market are never indistinguishable.
        Require(gasSwaps.Select(group=>Text(group,"definitionRevisionId")).Order(StringComparer.Ordinal).SequenceEqual(["d1","d2"]),
            "Two groups of one market did not state the definition revision that keeps them apart");
        var byMarket=new[]{TradingBlotter.Groups(Pivot(TotalsConvention,["market"],["presentValue","quantity"]),Positions()),markets};
        Require(byMarket.All(groups=>groups.All(group=>group["definitionRevisionId"] is not null)),"A market group did not state its definition revision");
        Require(TradingBlotter.Groups(Pivot(MoneyConvention,["book","instrumentType"],MoneyMeasures),Positions()).All(group=>!group.ContainsKey("definitionRevisionId")),
            "A group that no market or contract dimension keeps apart by definition revision stated one");
        Console.WriteLine("PASS market groups state the definition revision that keeps them apart, under either convention, and other groups state none");

        var perUnit=TradingBlotter.Groups(Pivot(TotalsConvention,["instrumentType"],["presentValue","tradeCount"]),Positions());
        Require(perUnit.Count==6&&perUnit.All(group=>group["unit"] is not null&&group["quantitySpecification"] is not null),
            "The totals convention stopped partitioning by quantity specification");
        Console.WriteLine("PASS the totals convention still partitions by quantity specification");
    }

    private static void Aggregation()
    {
        var groups=TradingBlotter.Groups(Pivot(MoneyConvention,["instrumentType"],MoneyMeasures),Positions());
        var swaps=Group(groups,"financial_swap","usd");
        Require(swaps["executionPnl"] is null,"A measure that does not apply to swaps was not null");
        Require(Count(swaps,"tradeCount")==4&&Count(swaps,"unvaluedPositions")==1,"A trade with two bindings was counted twice, or the unvalued trade was not left out once");
        var futures=Group(groups,"future","usd");
        Require(futures["presentValue"] is null&&Count(futures,"tradeCount")==3&&Count(futures,"unvaluedPositions")==1,"Futures counted wrongly, or stated present value");
        Console.WriteLine("PASS money groups add valued positions only, count every trade once and state what they leave out");

        var none=TradingBlotter.Groups(Pivot(MoneyConvention,["instrumentType"],MoneyMeasures),[Position("future-3","future","mmbtu",null,unavailable:"source_expired")]).Single();
        Require(none["executionPnl"] is null&&Count(none,"tradeCount")==1&&Count(none,"unvaluedPositions")==1,"A group that values no position stated a total instead of null");
        Console.WriteLine("PASS a group that values no position states null, never zero");
    }
}
