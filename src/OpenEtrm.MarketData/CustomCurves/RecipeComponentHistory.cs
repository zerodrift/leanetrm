using System.Collections.Immutable;

namespace OpenEtrm.MarketData.CustomCurves;

// A read-only projection of a native selection. It reuses the ordinary sampling
// engine; neither contract resolution nor price arithmetic has a second implementation.
public static class RecipeComponentHistory
{
    public const string Policy="raw_component_history_v1";
    public static RecipeDefinition Build(RecipeDefinition definition,string nodeId,RecipeNode dates,string policy)
    {
        RecipeEngine.Validate(definition);
        MarketDataException Invalid(string message)=>new("component_history",message);
        if(policy!=Policy)throw Invalid("Select raw component history; prices are not adjusted at contract rolls.");
        var nodes=definition.Nodes.ToDictionary(n=>n.Id);
        if(nodeId is null||!nodes.TryGetValue(nodeId,out var target))throw Invalid("Choose a component in this graph.");
        if(target.Kind=="sample")target=nodes[target.Inputs["selection"]];
        if(target.Kind!="weighted_contracts"&&(target.Kind!="select"||target.Parameters["mode"]!="positions"))
            throw Invalid("History requires a relative-contract selection, weighted contract mapping, or its sample step. Delivery panels and aggregated outputs are not resampled by this view.");
        if(dates is null||dates.Kind!="dates"||dates.Inputs is null||dates.Parameters is null||dates.Inputs.Count!=0||dates.Parameters.GetValueOrDefault("mode") is not("prior_calendar" or "prior_business"))
            throw Invalid("Choose an explicit prior-calendar or prior-business date window.");
        var kept=new HashSet<string>();
        void Visit(string id){if(kept.Add(id))foreach(var upstream in nodes[id].Inputs.Values)Visit(upstream);}
        Visit(target.Id);
        var result=definition.Nodes.Where(n=>kept.Contains(n.Id)).ToImmutableArray();
        string Id(string stem){var id=stem;for(var i=1;kept.Contains(id);i++)id=stem+"_"+i;kept.Add(id);return id;}
        var dateId=Id("history_dates");var sampleId=Id("history_sample");var outputId=Id("history_output");
        result=result.Add(dates with{Id=dateId}).Add(new(sampleId,"sample",ImmutableDictionary<string,string>.Empty,
            new Dictionary<string,string>{{"selection",target.Id},{"dates",dateId}}.ToImmutableDictionary()));
        result=result.Add(new(outputId,"output",ImmutableDictionary<string,string>.Empty,
            ImmutableDictionary<string,string>.Empty.Add("values",sampleId)));
        var view=definition with{Nodes=result,Output=outputId};RecipeEngine.Validate(view);return view;
    }
}
