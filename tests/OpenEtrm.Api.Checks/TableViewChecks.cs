using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

static class TableViewChecks
{
    public static void Run()
    {
        var layout = JsonSerializer.SerializeToElement(new {version=1,name="My trades",mode="trades",columns=new[]{"book","quantity","unit","priceUnit"},bookIds=Array.Empty<string>(),portfolioIds=Array.Empty<string>(),groupBy=Array.Empty<string>(),measures=Array.Empty<string>(),query="Jan",columnFilters=new Dictionary<string,string>{{"book","Book A"},{"quantity",">= 100.000000000000000001"}}});
        TableViews.Validate(layout);
        Console.WriteLine("PASS account layout accepts explicit presentation choices");
        var modern=JsonSerializer.Deserialize<Dictionary<string,object>>(layout.GetRawText())!;
        modern["version"]=2;modern["splitBy"]=Array.Empty<string>();
        modern["grid"]=new {order=new[]{"unit","book","quantity"},widths=new Dictionary<string,int>{{"quantity",140}},sort=new[]{new {key="quantity",direction="desc"}}};
        modern["chart"]=new {visible=true,measure="quantity"};
        TableViews.Validate(JsonSerializer.SerializeToElement(modern));
        Console.WriteLine("PASS account layout retains grid and chart settings without financial coordinates");
        var visual=new Dictionary<string,object>(modern){["version"]=3,["chart"]=new {visible=true,measure="presentValue",kind="heatmap"}};
        TableViews.Validate(JsonSerializer.SerializeToElement(visual));
        foreach(var chart in new[]{new {visible=true,measure="presentValue",kind="pie"},new {visible=true,measure="inferred",kind="bar"}})
        {
            visual["chart"]=chart;
            try{TableViews.Validate(JsonSerializer.SerializeToElement(visual));throw new Exception("Accepted unsupported chart choice");}
            catch(MarketDataException e) when(e.Code=="table_view"){Console.WriteLine("PASS chart layouts reject unsupported plot styles and measures");}
        }
        Console.WriteLine("PASS versioned layouts retain explicit pivot chart measures and styles");
        var commodityPivot=new Dictionary<string,object>(visual){["mode"]="pivot",["groupBy"]=new[]{"commodity"},["measures"]=new[]{"tradeCount"},
            ["columns"]=new[]{"commodity","tradeCount","unit","currency"},["chart"]=new {visible=true,measure="tradeCount",kind="bar"}};
        TableViews.Validate(JsonSerializer.SerializeToElement(commodityPivot));
        Console.WriteLine("PASS a pivot layout may group by commodity");
        modern["splitBy"]=new[]{"contract"};
        try{TableViews.Validate(JsonSerializer.SerializeToElement(modern));throw new Exception("Accepted unbound split dimension");}
        catch(MarketDataException e) when(e.Code=="table_view"){Console.WriteLine("PASS cross-tab columns must belong to explicit pivot grouping");}
        foreach(var patch in new Dictionary<string,object>[] {
            new(){{"snapshotId",Guid.NewGuid()}}, new(){{"actorId",Guid.NewGuid()}},
            new(){{"mode","pivot"}}, new(){{"columns",new[]{"unknown"}}},
            new(){{"bookIds",new[]{"invented"}}}, new(){{"query",new string('x',201)}},
            new(){{"columnFilters",new Dictionary<string,string>{{"permission","BookTrade"}}}}
        })
        {
            var changed=JsonSerializer.Deserialize<Dictionary<string,object>>(layout.GetRawText())!;
            foreach(var pair in patch)changed[pair.Key]=pair.Value;
            try {TableViews.Validate(JsonSerializer.SerializeToElement(changed));throw new Exception("Accepted invalid layout");}
            catch(MarketDataException e) when(e.Code=="table_view") {Console.WriteLine("PASS rejects invalid account layout "+patch.Keys.Single());}
        }
    }
}
