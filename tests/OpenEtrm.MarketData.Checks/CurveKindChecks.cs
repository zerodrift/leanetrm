using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;

internal static class CurveKindChecks
{
    public static void Run()
    {
        RecipeNode Node(string id,string kind,(string,string)[]? parameters=null,(string,string)[]? ports=null)=>new(id,kind,
            (parameters??[]).ToImmutableDictionary(x=>x.Item1,x=>x.Item2),(ports??[]).ToImmutableDictionary(x=>x.Item1,x=>x.Item2));
        RecipeDefinition Graph(params RecipeNode[] nodes)=>new("synthetic","Synthetic",[..nodes],"out");
        RecipeNode Out(string from)=>Node("out","output",ports:[("values",from)]);
        RecipeNode Dates(string mode)=>Node("dates","dates",[("mode",mode)]);
        RecipeNode Sample()=>Node("sample","sample",ports:[("selection","select"),("dates","dates")]);
        RecipeNode Positions()=>Node("select","select",[("mode","positions"),("first","1"),("count","1")],[("source","source")]);
        RecipeNode Source()=>Node("source","source",[("curve","gas")]);
        void Equal(string label,string actual,string expected){if(actual!=expected)throw new Exception($"{label}: expected {expected}, got {actual}");}
        // Window mean, wired by edges so node ids carry no meaning.
        string Mean(string axis,string mode)=>CurveKinds.Of(Graph(Source(),Positions(),Dates(mode),Sample(),Node("avg","mean",[("axis",axis)],[("values","sample")]),Out("avg")));
        Equal("Version",CurveKinds.Version,"curve_kinds_v1");
        Equal("Weighted panels",CurveKinds.Of(Graph(Source(),Node("a","weighted_panels",ports:[("source_1","source")]),Out("a"))),"weighted_blend");
        Equal("Weighted contracts",CurveKinds.Of(Graph(Source(),Node("a","weighted_contracts",ports:[("source_1","source")]),Out("a"))),"weighted_blend");
        Equal("Prior calendar window",Mean("dates","prior_calendar"),"rolling_average");
        Equal("Prior business window",Mean("dates","prior_business"),"rolling_average");
        Equal("Schedule window",Mean("dates","schedule"),"custom");
        Equal("Balance of month",Mean("contracts","balance_month"),"balance_of_month");
        Equal("Contract mean on a prior window",Mean("contracts","prior_calendar"),"custom");
        Equal("Prompt",CurveKinds.Of(Graph(Source(),Positions(),Dates("context"),Sample(),Out("sample"))),"prompt_contract");
        Equal("Daily-delivery selection",CurveKinds.Of(Graph(Source(),Node("select","select",[("mode","daily_delivery")],[("source","source")]),Dates("context"),Sample(),Out("sample"))),"custom");
        Equal("Source passthrough",CurveKinds.Of(Graph(Source(),Out("source"))),"custom");
        Equal("Published passthrough",CurveKinds.Of(Graph(Node("pub","published_source"),Out("pub"))),"custom");
        Equal("Unconnected output",CurveKinds.Of(Graph(Node("out","output"))),"custom");
        Equal("Settlement",CurveKinds.Of(CurveRule.Direct,ObservationRole.DailySettlement),"market_settlement");
        Equal("Trader mark",CurveKinds.Of(CurveRule.Direct,ObservationRole.TraderMark),"trader_mark");
        Equal("Add",CurveKinds.Of(CurveRule.Add,null),"basis_spread");
        Equal("Scale add",CurveKinds.Of(CurveRule.ScaleAdd,null),"scaled");
        foreach(var role in new ObservationRole?[]{null,ObservationRole.FinalContractSettlement,ObservationRole.Parameter,ObservationRole.IndexFixing})
        {
            try{CurveKinds.Of(CurveRule.Direct,role);}
            catch(MarketDataException e)when(e.Code=="curve_kind_undefined"){continue;}
            throw new Exception($"A direct curve with role {role?.ToString()??"none"} was classified.");
        }
        Console.WriteLine("PASS curve kinds");
    }
}
