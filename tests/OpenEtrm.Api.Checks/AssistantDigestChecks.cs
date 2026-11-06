using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using System.Text;
using OpenEtrm.Api;

static class AssistantDigestChecks
{
    public static void Run()
    {
        var call=AgentTools.Parse("read_page_digest",JsonSerializer.SerializeToElement(new{mode="overview"}));
        AgentTools.Parse("read_page_digest",JsonSerializer.SerializeToElement(new{mode="controls"}));
        foreach(var mode in new[]{"{}","{\"mode\":\"guess\"}","{\"mode\":\"controls\",\"page\":\"blotter\"}"})
        {
            try{AgentTools.Parse("read_page_digest",JsonDocument.Parse(mode).RootElement);throw new Exception("Digest mode was defaulted or the admitted page replaced.");}
            catch(ToolRefusal e) when(e.Code=="tool_arguments_invalid"){}
        }
        if(call.Operation.Id!="workspace.digest_v1"||
            !AgentTools.Tools.Single(t=>t.Name=="read_page_digest").Permissions.SequenceEqual(["UseAssistantWorkspace"]))
            throw new Exception("A page digest is an explicitly enabled workspace read.");
        try{AgentTools.Parse("read_page_digest",JsonSerializer.SerializeToElement(new{page="blotter"}));throw new Exception("A model replaced the admitted page report.");}
        catch(ToolRefusal e) when(e.Code=="tool_arguments_invalid"){}
        var inconsistent=JsonSerializer.SerializeToElement(new{page="blotter",selection=new{view="blotter",mode="trades",bookIds=Array.Empty<Guid>(),portfolioIds=Array.Empty<Guid>(),groupBy=Array.Empty<string>(),measures=Array.Empty<string>(),tradeId=(Guid?)null,instrumentType=(string?)null,revision=(int?)null},
            view=new{mode="pivot",bookIds=Array.Empty<Guid>(),portfolioIds=Array.Empty<Guid>(),groupBy=new[]{"book"},measures=new[]{"executionPnl"},primaryMeasure="executionPnl"}});
        try{PageReports.Require(inconsistent);throw new Exception("Digest and current-page reads accepted conflicting views.");}
        catch(OpenEtrm.MarketData.MarketDataException e) when(e.Code=="page_report_invalid"){}
        try{BlotterDigestView.Parse(JsonSerializer.SerializeToElement(new{mode="pivot",bookIds=Array.Empty<Guid>(),portfolioIds=Array.Empty<Guid>(),groupBy=new[]{"book"},measures=new[]{"lots"},primaryMeasure="lots"}));throw new Exception("Digest accepted a lots pivot the page refuses.");}
        catch(OpenEtrm.MarketData.MarketDataException e) when(e.Code=="blotter_view"){}
        var prices=new JsonObject{["executionPriceMean"]="3.123456789"};
        ToolAmountDisplay.Annotate(DisplayWalk.Viewer(prices));
        if(prices["executionPriceMean"] is not JsonObject amount||amount["display"]?.GetValue<string>()!="3.1235")
            throw new Exception("Digest price statistics must use the page's server display precision.");
        SeededBlotter();
        Console.WriteLine("PASS assistant digest: explicit workspace consent and immutable asked page");
    }

    static void SeededBlotter()
    {
        var catalog=Guid.Parse("c1000000-0000-0000-0000-000000000001");
        var manifest=new PreviewManifest("local-synthetic-preview",catalog,Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),[],null);
        var frame=new InputFrame(Guid.Parse("f1000000-0000-0000-0000-000000000001"),"Synthetic close",DateTimeOffset.Parse("2020-12-18T16:00:00Z"),[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty);
        var pin=TurnPin.From(JsonSerializer.SerializeToElement(new{mode="retained",contextId=Guid.Parse("c2000000-0000-0000-0000-000000000001"),cutoff="2026-10-21T12:00:00Z",frameId=frame.Id}),JsonDocument.Parse(TurnPin.Record(frame)).RootElement,catalog,manifest);
        JsonObject Row(int id,string pnl)=>JsonSerializer.SerializeToNode(new{tradeId=$"71000000-0000-0000-0000-{id:000000000000}",bindingId=$"72000000-0000-0000-0000-{id:000000000000}",revision="1",tradeReference=$"TR-{id:000000}",instrumentReference="Synthetic future",instrumentType="future",bookId="b1000000-0000-0000-0000-000000000001",book="Synthetic book",unit="lots",currency="USD",deliveryFrom="2021-01-01",deliveryTo="2021-02-01",quantity="1",executionPnl=pnl,
            quantitySpecification=new{revision=new{catalogId=catalog,revisionId=catalog},code="synthetic_lots"},currencyReference=new{revision=new{catalogId=catalog,revisionId=catalog},code="usd"}})!.AsObject();
        var workspace=new JsonObject{["blotter"]=new JsonObject{["holdings"]=new JsonArray(Row(1,"10"),Row(2,"-20"),Row(3,"30"))}};
        var view=new BlotterDigestView("trades",[],[],[],["executionPnl"],"executionPnl");
        var digest=PageDigest.Blotter(workspace,view,pin,1);
        var expected="""
            Page: blotter | As of: Synthetic close (2020-12-18T16:00:00.0000000+00:00) | cutoff: 2026-10-21T12:00:00.0000000+00:00
            View: trades | books: all authorized | portfolios: all authorized | groupBy:  | measures: executionPnl
            Positions: future=3
            Status: valued=3 | unavailable=0 | drafts/close-out drafts: unavailable (unsaved browser state)
            Book: book=Synthetic book | executionPnl=20.000 | future USD lots
            Largest: n2 TR-000003 | Synthetic book | future Synthetic future | 2021-01-01..2021-02-01 | quantity=1 lots | executionPnl=30.000 USD
            Largest: n3 TR-000001 | Synthetic book | future Synthetic future | 2021-01-01..2021-02-01 | quantity=1 lots | executionPnl=10.000 USD
            Largest: n4 TR-000002 | Synthetic book | future Synthetic future | 2021-01-01..2021-02-01 | quantity=1 lots | executionPnl=-20.000 USD
            Smallest: n4 TR-000002 | Synthetic book | future Synthetic future | 2021-01-01..2021-02-01 | quantity=1 lots | executionPnl=-20.000 USD
            Smallest: n3 TR-000001 | Synthetic book | future Synthetic future | 2021-01-01..2021-02-01 | quantity=1 lots | executionPnl=10.000 USD
            Smallest: n2 TR-000003 | Synthetic book | future Synthetic future | 2021-01-01..2021-02-01 | quantity=1 lots | executionPnl=30.000 USD
            Freshness: values read at the turn pin; browser equality/pending-request state unavailable.
            """;
        if(digest.Text!=expected)throw new Exception("Pinned synthetic digest lines differ:\n"+digest.Text);
        if(Encoding.UTF8.GetByteCount(digest.Text)>4096||digest.Targets.Length!=4)throw new Exception("Digest budget or target identity differs.");
        var quantity=PageDigest.Blotter(workspace,view with{Measures=["quantity"],PrimaryMeasure="quantity"},pin,1);
        if(quantity.Text.Split('\n').Any(line=>(line.StartsWith("Largest:")||line.StartsWith("Smallest:"))&&!line.EndsWith("quantity=1 lots")))
            throw new Exception("Quantity extremes must use their native unit, never a cash currency.");
        var trade=Guid.Parse("71000000-0000-0000-0000-000000000001");
        var selected=PageDigest.Blotter(workspace,view,pin,1,focus:new([trade,Guid.Parse("71000000-0000-0000-0000-000000000002")],null));
        if(selected.Text.Split('\n').Count(line=>line.StartsWith("Selected:"))!=2||
            !selected.Text.Contains("Selected: n3 TR-000001")||!selected.Text.Contains("executionPnl=-20.000 USD"))throw new Exception("Selected position values did not come from the server projection.");
        try{PageDigest.Blotter(workspace,view,pin,1,focus:new([Guid.Empty],null));throw new Exception("An unavailable selected identity became a partial digest.");}
        catch(ToolRefusal e) when(e.Code=="page_selection_unavailable"){}
        var group=TradingBlotter.Groups(new("pivot",[],[],["book"],["executionPnl"],TradingBlotter.PivotConvention),workspace["blotter"]!["holdings"]!.AsArray().Select(n=>n!.DeepClone().AsObject()).ToList()).Single();
        var keys=new JsonObject();foreach(var field in new[]{"groupId","bookId","instrumentType","quantitySpecification","currencyReference"})keys[field]=group[field]!.DeepClone();
        var cell=PageDigest.Blotter(workspace,view with{Mode="pivot",GroupBy=["book"]},pin,1,focus:new([],new(keys,"executionPnl")));
        if(!cell.Text.Contains("Selected pivot:")||!cell.Text.Contains("executionPnl=20.000"))throw new Exception("Pivot identity did not resolve the server total.");
        var priceRows=new[]{Row(1,"10"),Row(2,"20")};
        priceRows[0]["quoteMultiplier"]="1";priceRows[1]["quoteMultiplier"]="10";
        foreach(var row in priceRows)row["executionPrice"]="3.123456789";
        var priceGroups=TradingBlotter.Groups(new("pivot",[],[],["book"],["executionPriceMean"],TradingBlotter.PivotConvention,OpenEtrm.MarketData.PriceStatistics.MeanPolicy),[..priceRows]);
        if(priceGroups.Count!=2||priceGroups.Any(g=>g["groupId"] is null)||JsonNode.DeepEquals(priceGroups[0]["groupId"],priceGroups[1]["groupId"]))
            throw new Exception("Native pivot identities must distinguish quote-multiplier partitions without sending an amount.");
        var gas=JsonSerializer.SerializeToNode(new{head=new{trade_id=trade,revision="1",trade_reference="1"},physical=new{quantity="10",fixed_price="3.125"}})!.AsObject();
        var gasDigest=PageDigest.Blotter(workspace,view,pin,1,new(trade,"physical_gas",1),gas);
        if(!gasDigest.Text.Contains("quantity=10")||!gasDigest.Text.Contains("fixedPrice=3.125"))throw new Exception("Docked gas key terms were omitted.");
        var swap=JsonSerializer.SerializeToNode(new{state=new{tradeId=trade,revision=1,tradeReference="TR-000001",terms=new{quantity="14",@fixed=new{fixedPrice="1.25"}}}})!.AsObject();
        var swapDigest=PageDigest.Blotter(workspace,view,pin,1,new(trade,"financial_swap",1),swap);
        if(!swapDigest.Text.Contains("quantity=14")||!swapDigest.Text.Contains("fixedPrice=1.25"))throw new Exception("Docked nested swap terms were omitted.");
        try{PageDigest.Bounded([new string('é',2049)]);throw new Exception("Oversized UTF-8 digest was published.");}
        catch(ToolRefusal e) when(e.Code=="result_too_large"){}
        Console.WriteLine("PASS assistant digest: exact seeded lines, ordered extremes, targets and whole-result UTF-8 refusal");
    }
}
