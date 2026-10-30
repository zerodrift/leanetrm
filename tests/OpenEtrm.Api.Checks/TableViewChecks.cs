using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

static class TableViewChecks
{
    private static JsonElement Element(IDictionary<string,object?> layout)=>JsonSerializer.SerializeToElement(layout);
    private static Dictionary<string,object?> With(IDictionary<string,object?> layout,params (string Key,object? Value)[] changes)
    {
        var changed=new Dictionary<string,object?>(layout);
        foreach(var (key,value) in changes)changed[key]=value;
        return changed;
    }
    private static Dictionary<string,object?> Without(IDictionary<string,object?> layout,params string[] keys)
    {
        var changed=new Dictionary<string,object?>(layout);
        foreach(var key in keys)changed.Remove(key);
        return changed;
    }
    private static void Refuses(IDictionary<string,object?> layout,string what)
    {
        try{TableViews.Validate(Element(layout));throw new Exception("Accepted "+what);}
        catch(MarketDataException e) when(e.Code=="table_view"){}
    }

    private static Dictionary<string,object?> Group(string tab,params string[] more)=>new(){["kind"]="group",["tabs"]=more.Prepend(tab).ToArray(),["active"]=tab};
    private static Dictionary<string,object?> Split(double ratio,object first,object second)=>new(){["kind"]="split",["direction"]="column",["ratio"]=ratio,["children"]=new[]{first,second}};
    private static Dictionary<string,object?> Panes(object? layout=null,params string[] collapsed)=>new()
    {
        ["layout"]=layout??Split(0.06,Group("summary"),Split(0.6,Group("grid"),Split(0.5,Group("details"),Group("charts")))),
        ["collapsed"]=collapsed.Length==0?new[]{"details"}:collapsed
    };

    public static void Run()
    {
        // Version 6 is the only version: it records whether the sidebar lists the view beside the desk grouping, grid, column split, pane arrangement and chart.
        var layout=new Dictionary<string,object?>
        {
            ["version"]=6,["sidebar"]=true,["name"]="My trades",["mode"]="trades",["rowGroup"]="book",
            ["columns"]=new[]{"book","quantity","unit","priceUnit"},["bookIds"]=Array.Empty<string>(),["portfolioIds"]=Array.Empty<string>(),
            ["groupBy"]=Array.Empty<string>(),["measures"]=Array.Empty<string>(),["query"]="Jan",
            ["columnFilters"]=new Dictionary<string,string>{{"book","Book A"},{"quantity",">= 100.000000000000000001"}},
            ["splitBy"]=Array.Empty<string>(),
            ["grid"]=new {order=new[]{"unit","book","quantity"},widths=new Dictionary<string,int>{{"quantity",140}},sort=new[]{new {key="quantity",direction="desc"}}},
            ["panes"]=Panes(),
            ["chart"]=new {visible=true,measure="quantity",kind="bar"}
        };
        TableViews.Validate(Element(layout));
        Console.WriteLine("PASS account layout accepts explicit presentation choices");
        foreach(var rowGroup in new object?[]{"book","market","instrumentType",null})TableViews.Validate(Element(With(layout,("rowGroup",rowGroup))));
        Console.WriteLine("PASS account layout records the desk grouping, or none for a flat list");
        foreach(var rowGroup in new object?[]{"portfolio","contract","commodity","","Book",5,true,new[]{"book"}})
        {
            Refuses(With(layout,("rowGroup",rowGroup)),"a desk grouping the Blotter does not offer");
            Console.WriteLine("PASS rejects desk grouping "+JsonSerializer.Serialize(rowGroup));
        }
        Refuses(Without(layout,"rowGroup"),"a layout that leaves its desk grouping out");
        Console.WriteLine("PASS rejects a layout that leaves its desk grouping out");
        TableViews.Validate(Element(With(layout,("sidebar",false))));
        Console.WriteLine("PASS account layout records whether the sidebar lists it");
        foreach(var sidebar in new object?[]{null,"true",1})Refuses(With(layout,("sidebar",sidebar)),"a sidebar choice that is not true or false");
        Refuses(Without(layout,"sidebar"),"a layout that leaves its sidebar choice out");
        Console.WriteLine("PASS rejects a sidebar choice that is not stated as true or false");
        // Layouts of versions 1 to 5 are refused: each as that version stored it, and as the current layout under that version.
        var version5=Without(layout,"panes");version5["version"]=5;
        var version4=Without(version5,"sidebar");version4["version"]=4;
        var version3=Without(version4,"rowGroup");version3["version"]=3;
        var version2=new Dictionary<string,object?>(version3){["version"]=2,["chart"]=new {visible=true,measure="quantity"}};
        var version1=Without(version2,"splitBy","grid","chart");version1["version"]=1;
        foreach(var (version,stored) in new[]{(1,version1),(2,version2),(3,version3),(4,version4),(5,version5)})
        {
            Refuses(stored,"a version "+version+" layout");
            Refuses(With(layout,("version",version)),"the current layout stamped as version "+version);
            Console.WriteLine("PASS rejects layouts saved by version "+version);
        }
        foreach(var version in new object?[]{0,7,"6",null})Refuses(With(layout,("version",version)),"version "+JsonSerializer.Serialize(version));
        Console.WriteLine("PASS rejects layouts of a version this release does not know");
        // A pane arrangement is the Blotter's four panes once each, at most three splits deep, ratios 0.05 to 0.95, the grid never collapsed.
        TableViews.Validate(Element(With(layout,("panes",Panes(Split(0.3,Group("grid"),Group("summary","details","charts")),"charts","details")))));
        var deepest=Split(0.5,Split(0.5,Split(0.5,Split(0.5,Group("summary"),Group("grid")),Group("details")),Group("charts")),Group("x"));
        var badPanes=new Dictionary<string,object?>
        {
            ["an unknown pane"]=Panes(Split(0.5,Group("summary"),Split(0.5,Group("grid"),Split(0.5,Group("details"),Group("reports"))))),
            ["a missing pane"]=Panes(Split(0.5,Group("summary"),Split(0.5,Group("grid"),Group("details")))),
            ["a repeated pane"]=Panes(Split(0.5,Group("summary"),Split(0.5,Group("grid"),Split(0.5,Group("details"),Group("summary","charts"))))),
            ["a collapsed grid"]=Panes(null,"grid"),
            ["a collapsed pane named twice"]=Panes(null,"details","details"),
            ["a collapsed pane that does not exist"]=Panes(null,"reports"),
            ["splits four deep"]=Panes(deepest),
            ["a ratio of 0.97"]=Panes(Split(0.97,Group("summary"),Split(0.6,Group("grid"),Split(0.5,Group("details"),Group("charts"))))),
            ["a ratio of 0.04"]=Panes(Split(0.04,Group("summary"),Split(0.6,Group("grid"),Split(0.5,Group("details"),Group("charts"))))),
            ["an active tab its group does not hold"]=Panes(Split(0.5,Group("summary"),Split(0.5,Group("grid"),new Dictionary<string,object?>{["kind"]="group",["tabs"]=new[]{"details","charts"},["active"]="grid"}))),
            ["no collapsed list"]=Without(Panes(),"collapsed"),
            ["an unknown property"]=With(Panes(),("note","x")),
            ["a null arrangement"]=null
        };
        foreach(var (what,panes) in badPanes)Refuses(With(layout,("panes",panes)),what);
        Refuses(Without(layout,"panes"),"a layout that leaves its panes out");
        Console.WriteLine("PASS pane arrangements are validated, not repaired");
        var visual=With(layout,("chart",new {visible=true,measure="presentValue",kind="heatmap"}));
        TableViews.Validate(Element(visual));
        foreach(var chart in new[]{new {visible=true,measure="presentValue",kind="pie"},new {visible=true,measure="inferred",kind="bar"}})
        {
            Refuses(With(visual,("chart",chart)),"unsupported chart choice");
            Console.WriteLine("PASS chart layouts reject unsupported plot styles and measures");
        }
        Console.WriteLine("PASS layouts retain explicit pivot chart measures and styles");
        var commodityPivot=With(visual,("mode","pivot"),("groupBy",new[]{"commodity"}),("measures",new[]{"tradeCount"}),
            ("columns",new[]{"commodity","tradeCount","unit","currency"}),("chart",new {visible=true,measure="tradeCount",kind="bar"}));
        TableViews.Validate(Element(commodityPivot));
        Console.WriteLine("PASS a pivot layout may group by commodity");
        TableViews.Validate(Element(With(commodityPivot,("groupBy",new[]{"instrumentType"}),("columns",new[]{"instrumentType","tradeCount","unit","currency"}))));
        Console.WriteLine("PASS a pivot layout may group by instrument type, which it lists once");
        Refuses(With(layout,("splitBy",new[]{"contract"})),"an unbound split dimension");
        Console.WriteLine("PASS cross-tab columns must belong to explicit pivot grouping");
        foreach(var patch in new Dictionary<string,object?>[] {
            new(){{"snapshotId",Guid.NewGuid()}}, new(){{"actorId",Guid.NewGuid()}},
            new(){{"mode","pivot"}}, new(){{"columns",new[]{"unknown"}}},
            new(){{"bookIds",new[]{"invented"}}}, new(){{"query",new string('x',201)}},
            new(){{"columnFilters",new Dictionary<string,string>{{"permission","BookTrade"}}}}
        })
        {
            var changed=new Dictionary<string,object?>(layout);
            foreach(var pair in patch)changed[pair.Key]=pair.Value;
            Refuses(changed,"an invalid layout");
            Console.WriteLine("PASS rejects invalid account layout "+patch.Keys.Single());
        }
    }
}
