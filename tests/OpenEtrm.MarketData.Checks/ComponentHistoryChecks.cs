using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.CustomCurves;

internal static class ComponentHistoryChecks
{
    public static void Run()
    {
        RecipeNode Node(string id,string kind,(string,string)[]? parameters=null,(string,string)[]? inputs=null)=>new(id,kind,
            (parameters??[]).ToImmutableDictionary(x=>x.Item1,x=>x.Item2),(inputs??[]).ToImmutableDictionary(x=>x.Item1,x=>x.Item2));
        var source=Node("source","source",[("curve","synthetic_monthly")]);
        var selection=Node("rolling","select",[("mode","positions"),("first","1"),("count","2"),("binding","each_sample")],[("source","source")]);
        var dates=Node("dates","dates",[("mode","context"),("sampling","context_utc_time"),("corrections","exact_context_frame_history_latest_visible")]);
        var sample=Node("sample","sample",inputs:[("selection","rolling"),("dates","dates")]);
        var mean=Node("mean","mean",[("axis","dates")],[("values","sample")]);
        var output=Node("output","output",inputs:[("values","mean")]);
        var original=new RecipeDefinition("synthetic","Synthetic rolling curve",[source,selection,dates,sample,mean,output],"output");
        var window=dates with{Parameters=dates.Parameters.SetItem("mode","prior_calendar").Add("count","3").Add("includeContext","true")};
        var view=RecipeComponentHistory.Build(original,"rolling",window,RecipeComponentHistory.Policy);
        RecipeEngine.Validate(view);
        var selected=view.Nodes.Single(n=>n.Kind=="select");
        if(selected!=selection||view.Nodes.Any(n=>n.Kind=="mean")||view.Nodes.Single(n=>n.Kind=="dates").Parameters!=window.Parameters)
            throw new Exception("Component history changed selection terms or retained downstream averaging.");
        if(original.Nodes.Length!=6||original.Nodes.Single(n=>n.Id=="dates").Parameters["mode"]!="context")throw new Exception("Inspection edited the original graph.");
        var fromSample=RecipeComponentHistory.Build(original,"sample",window,RecipeComponentHistory.Policy);
        if(!view.SameGraph(fromSample))throw new Exception("Inspecting the sample must preserve its selection's raw history.");
        var pinned=original with{Nodes=original.Nodes.SetItem(1,selection with{Parameters=selection.Parameters.SetItem("binding","context")})};
        if(RecipeComponentHistory.Build(pinned,"rolling",window,RecipeComponentHistory.Policy).Nodes.Single(n=>n.Kind=="select").Parameters["binding"]!="context")
            throw new Exception("Inspection silently changed fixed-contract binding into rolling binding.");
        void Reject(Action action){try{action();}catch(MarketDataException e)when(e.Code=="component_history"){return;}throw new Exception("Expected component_history refusal.");}
        Reject(()=>RecipeComponentHistory.Build(original,"rolling",window,"adjusted"));
        Reject(()=>RecipeComponentHistory.Build(original,"unknown",window,RecipeComponentHistory.Policy));
        Reject(()=>RecipeComponentHistory.Build(original,"mean",window,RecipeComponentHistory.Policy));
        Reject(()=>RecipeComponentHistory.Build(original,"rolling",dates,RecipeComponentHistory.Policy));
        Console.WriteLine("PASS component history preserves selection and raw-price policy without editing or averaging the graph");
    }
}
