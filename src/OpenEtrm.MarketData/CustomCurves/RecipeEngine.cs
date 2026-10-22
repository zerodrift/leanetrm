using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.MarketData.CustomCurves;

// Recipe preparation selects topology only. All price arithmetic is performed
// by the connected calculation plan, including preview and saved consumers.
public static partial class RecipeEngine
{
    public const string Version="custom_curve_recipe_v2";
    public const string SignedSum="signed_sum_v1";
    public const string WeightedMean="normalized_weighted_mean_v1";
    private static MarketDataException Invalid(string message)=>new("recipe_definition",message);
    private static MarketDataException Missing(string message)=>new("recipe_input",message);
    private static readonly ImmutableDictionary<string,ImmutableDictionary<string,string>> Ports=new Dictionary<string,ImmutableDictionary<string,string>>
    {
        ["source"]=ImmutableDictionary<string,string>.Empty,
        ["published_source"]=ImmutableDictionary<string,string>.Empty,
        ["select"]=new Dictionary<string,string>{{"source","source"}}.ToImmutableDictionary(),
        ["dates"]=ImmutableDictionary<string,string>.Empty,
        ["sample"]=new Dictionary<string,string>{{"selection","selection"},{"dates","dates"}}.ToImmutableDictionary(),
        ["mean"]=new Dictionary<string,string>{{"values","panel"}}.ToImmutableDictionary(),
        ["output"]=new Dictionary<string,string>{{"values","panel"}}.ToImmutableDictionary()
    }.ToImmutableDictionary();
    public static void Validate(RecipeDefinition definition)
    {
        if(definition is null||!Identity(definition.Code)||string.IsNullOrWhiteSpace(definition.Name)||definition.Name.Length>80||
            definition.Name.Trim()!=definition.Name||definition.Nodes.IsDefaultOrEmpty||definition.Nodes.Length>64)
            throw Invalid("A named recipe requires one to 64 nodes.");
        if(definition.Nodes.Any(n=>n is null||!Identity(n.Id)||string.IsNullOrEmpty(n.Kind)||n.Parameters is null||n.Inputs is null||n.Parameters.Any(p=>p.Value is null)||n.Inputs.Any(p=>string.IsNullOrEmpty(p.Value)))||
            definition.Nodes.Select(n=>n.Id).Distinct(StringComparer.Ordinal).Count()!=definition.Nodes.Length)
            throw Invalid("Node identities must be nonempty and unique.");
        var nodes=definition.Nodes.ToDictionary(n=>n.Id,StringComparer.Ordinal);
        if(!nodes.TryGetValue(definition.Output??"",out var output)||output.Kind!="output")throw Invalid("Select an output node.");
        foreach(var node in definition.Nodes)
        {
            var weighted=node.Kind=="weighted_contracts";
            var ports=weighted?node.Inputs.Keys.ToImmutableDictionary(k=>k,_=>"source"):
                Ports.GetValueOrDefault(node.Kind)??throw Invalid($"Node {node.Id} has an unsupported kind.");
            if(!ports.Keys.ToHashSet().SetEquals(node.Inputs.Keys)||weighted&&(node.Inputs.Count is <1 or >32||node.Inputs.Keys.Any(p=>!Identity(p))))
                throw Invalid($"Node {node.Id} has unsupported input ports.");
            foreach(var (port,id) in node.Inputs)
            {
                if(!nodes.TryGetValue(id,out var upstream))throw Invalid($"Node {node.Id} refers to a missing input.");
                var type=upstream.Kind switch{"sample" or "mean" or "published_source"=>"panel","select" or "weighted_contracts"=>"selection",_=>upstream.Kind};
                if(type!=ports[port])throw Invalid($"Port {port} on {node.Id} requires {ports[port]}.");
            }
            switch(node.Kind)
            {
                case "source":Required(node,"curve");break;
                case "published_source":
                    if(node.PublishedSource is not {} published||published.CatalogId==Guid.Empty||published.CurveId==Guid.Empty||
                        published.Version is <1 or >64||!Identity(published.OutputNode)||Required(node,"selection")!="all_output_points_v1")
                        throw Invalid("Choose an exact published curve version/output and explicit whole-panel selection.");
                    break;
                case "select":
                    if(Required(node,"mode") is not ("positions" or "daily_delivery"))throw Invalid("Select positions or daily delivery explicitly.");
                    if(Required(node,"binding") is not ("each_sample" or "context"))throw Invalid("Select a contract binding policy explicitly.");
                    if(node.Parameters["mode"]=="positions"){Number(node,"first",1,120);Number(node,"count",1,120);}
                    else if(node.Parameters["binding"]!="context")throw Invalid("Daily forwards use one context observation frame.");break;
                case "dates":Required(node,"mode");break;
                case "mean":if(Required(node,"axis") is not ("dates" or "contracts"))throw Invalid("Choose the averaging axis explicitly.");break;
                case "weighted_contracts":
                    if(Required(node,"aggregation") is not (SignedSum or WeightedMean)||node.ContractWeights.IsDefaultOrEmpty||node.ContractWeights.Length>1024||
                        node.ContractWeights.Any(w=>w is null||w.OutputPosition is <1 or >120||w.ContractId==Guid.Empty||!node.Inputs.ContainsKey(w.SourcePort))||
                        node.ContractWeights.Select(w=>(w.OutputPosition,w.SourcePort,w.ContractId)).Distinct().Count()!=node.ContractWeights.Length||
                        !node.Inputs.Keys.ToHashSet().SetEquals(node.ContractWeights.Select(w=>w.SourcePort)))
                        throw Invalid("Supply explicit output positions, source ports, exact contracts and weights for every mapping input.");
                    foreach(var group in node.ContractWeights.GroupBy(w=>w.OutputPosition))
                    {
                        var weights=group.Select(w=>Weight(w.Weight)).ToArray();
                        if(node.Parameters["aggregation"]==WeightedMean&&(weights.Any(w=>w.CompareTo(default)<0)||weights.Aggregate(default(ExactDecimal),(a,b)=>a+b).CompareTo(default)<=0))
                            throw Invalid("A normalized weighted mean requires nonnegative weights with a positive sum.");
                    }
                    break;
            }
            if(!weighted&&!node.ContractWeights.IsDefaultOrEmpty)throw Invalid("Contract weights belong only to the explicit weighted mapping operator.");
            if(node.Kind!="published_source"&&node.PublishedSource is not null)throw Invalid("A published source binding belongs only to its published-source operator.");
            string[] parameters=node.Kind switch
            {
                "source"=>["curve"],"select"=>node.Parameters["mode"]=="positions"?["mode","first","count","binding"]:["mode","binding"],
                "dates"=>["mode","sampling","corrections","count","includeContext","calendar","schedule","first"],
                "mean"=>["axis"],"weighted_contracts"=>["aggregation"],"published_source"=>["selection"],_=>[]
            };
            if(node.Parameters.Keys.Any(key=>!parameters.Contains(key,StringComparer.Ordinal)))throw Invalid($"Node {node.Id} contains an unsupported or conflicting parameter.");
        }
        var visiting=new HashSet<string>();var visited=new HashSet<string>();
        void Visit(string id)
        {
            if(visited.Contains(id))return;if(!visiting.Add(id))throw Invalid("A curve recipe must be acyclic.");
            foreach(var upstream in nodes[id].Inputs.Values)Visit(upstream);visiting.Remove(id);visited.Add(id);
        }
        Visit(output.Id);if(visited.Count!=nodes.Count)throw Invalid("Every node must contribute to the selected output.");
    }
    private static ExactDecimal Weight(string text)
    {
        if(string.IsNullOrWhiteSpace(text)||text.Trim()!=text)throw Invalid("Every contract weight must be explicit exact decimal text.");
        try{return ExactDecimal.Parse(text);}catch(Exception error)when(error is FormatException or OverflowException or ArgumentException)
        {throw Invalid("Every contract weight must be explicit exact decimal text.");}
    }
    private static string Required(RecipeNode node,string key)=>node.Parameters.TryGetValue(key,out var value)&&!string.IsNullOrWhiteSpace(value)?value:throw Invalid($"Node {node.Id} requires {key}.");
    private static int Number(RecipeNode node,string key,int min,int max)=>int.TryParse(Required(node,key),NumberStyles.None,CultureInfo.InvariantCulture,out var value)&&value>=min&&value<=max?value:throw Invalid($"Node {node.Id} requires {key} between {min} and {max}.");
    private static bool Identity(string? value)=>value is {Length:>0 and <=32}&&value[0] is >= 'a' and <= 'z'&&value.All(c=>c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
    internal sealed record SampleKey(CurveKey Source,DateOnly Date,ContractKey Contract);
    internal sealed record CellFormula(string Node,int Index,SampleKey? Sample,ImmutableArray<CellFormula> Inputs,ImmutableArray<ExactDecimal> Weights,string Aggregation,CurvePointKey? PublishedPoint=null);
    internal sealed record Cell(int Position,ImmutableArray<DateOnly> Dates,DateOnly ObservationDate,PriceUnit Unit,ImmutableArray<SampleKey> Inputs,CellFormula Formula,
        DateOnly DeliveryFrom,DateOnly DeliveryTo);
    internal sealed record Panel(ImmutableArray<Cell> Cells);
    private static Panel Prepare(RecipeDefinition definition,RecipeInputs inputs,ImmutableArray<ConnectedRecipeSample> samples,CancellationToken cancellation,
        IReadOnlyDictionary<string,ConnectedRecipeGraph> publishedSources,RecipeOutputDemand? demand)
    {
        cancellation.ThrowIfCancellationRequested();Validate(definition);
        if(inputs.ValuationAt.Offset!=TimeSpan.Zero||inputs.ContextDate!=DateOnly.FromDateTime(inputs.ValuationAt.UtcDateTime)||
            !definition.Nodes.Where(n=>n.Kind=="source").Select(n=>n.Id).ToHashSet().SetEquals(inputs.Sources.Keys)||
            !definition.Nodes.Where(n=>n.Kind=="dates").Select(n=>n.Id).ToHashSet().SetEquals(inputs.Dates.Keys))
            throw Missing("Supply exact source/date bindings and a coherent UTC valuation context.");
        var nodes=definition.Nodes.ToDictionary(n=>n.Id);var cache=new Dictionary<string,object>();
        var available=samples.ToDictionary(s=>new SampleKey(s.Source,s.Date,s.Contract));var work=0;
        foreach(var node in definition.Nodes.Where(n=>n.Kind=="source"))ValidateSource(node,inputs.Sources[node.Id]);
        Cell Leaf(string node,int index,string sourceNode,DateOnly date,RecipeContract contract,int position,DateOnly displayDate,DateTimeOffset at)
        {
            cancellation.ThrowIfCancellationRequested();if(++work>50000)throw Missing("The recipe exceeds its 50,000-sample preparation limit.");
            var source=inputs.Sources[sourceNode];var key=new SampleKey(source.Key,date,contract.Contract.Key);
            if(demand is null&&(!available.TryGetValue(key,out var sample)||sample.Snapshot.ValuationAt!=at||contract.EligibleFrom>at||contract.Contract.Expiry<=at))
                throw Missing("A required exact live contract sample is absent or ineligible at its sampling instant.");
            return new(position,[displayDate],date,source.Unit,[key],new(node,index,key,[],[],SignedSum),contract.Contract.DeliveryFrom,contract.Contract.DeliveryTo);
        }
        DateTimeOffset At(CurveKey source,DateOnly date)
        {
            if(demand is not null)return demand.SamplingInstants.TryGetValue(date,out var instant)?instant:
                throw Missing("Supply every explicit sampling instant for an output-demand preparation.");
            var times=samples.Where(s=>s.Source==source&&s.Date==date).Select(s=>s.Snapshot.ValuationAt).Distinct().ToArray();
            if(times.Length!=1)throw Missing($"A unique sampling instant is required for {date:yyyy-MM-dd}.");return times[0];
        }
        object Node(string id)
        {
            cancellation.ThrowIfCancellationRequested();if(cache.TryGetValue(id,out var prior))return prior;var node=nodes[id];object result;
            switch(node.Kind)
            {
                case "source":result=inputs.Sources[id];break;
                case "published_source":
                    var published=publishedSources[id];
                    result=new Panel(published.PreparedPanel.Cells.Select((cell,i)=>cell with{
                        Formula=new(id,i,null,[],[],SignedSum,published.OutputPoint(i))}).ToImmutableArray());break;
                case "select":case "weighted_contracts":result=node;break;
                case "dates":
                    var dates=inputs.Dates[id];if(dates.IsDefaultOrEmpty||dates.Length>366||dates.Distinct().Count()!=dates.Length||!dates.SequenceEqual(dates.Order()))throw Missing("A date binding requires one to 366 distinct ascending dates.");result=dates;break;
                case "sample":
                    var selector=(RecipeNode)Node(node.Inputs["selection"]);var cells=ImmutableArray.CreateBuilder<Cell>();
                    foreach(var date in (ImmutableArray<DateOnly>)Node(node.Inputs["dates"]))
                    {
                        if(selector.Kind=="weighted_contracts")
                        {
                            foreach(var group in selector.ContractWeights.GroupBy(w=>w.OutputPosition).OrderBy(g=>g.Key))
                            {
                                var parts=group.OrderBy(w=>w.SourcePort,StringComparer.Ordinal).ThenBy(w=>w.ContractId).Select(w=>
                                {
                                    var sourceNode=selector.Inputs[w.SourcePort];var source=inputs.Sources[sourceNode];
                                    var contract=source.Contracts.SingleOrDefault(c=>c.Contract.Key.Id==w.ContractId)??throw Missing("A mapped contract is absent from its exact source curve.");
                                    return(Cell:Leaf(id,cells.Count,sourceNode,date,contract,group.Key,date,At(source.Key,date)),Weight:Weight(w.Weight));
                                }).ToArray();
                                if(parts.Select(p=>p.Cell.Unit).Distinct().Count()!=1)throw Missing("Weighted sources must retain identical exact price units.");
                                cells.Add(new(group.Key,[date],date,parts[0].Cell.Unit,parts.SelectMany(p=>p.Cell.Inputs).ToImmutableArray(),
                                    new(id,cells.Count,null,parts.Select(p=>p.Cell.Formula).ToImmutableArray(),parts.Select(p=>p.Weight).ToImmutableArray(),selector.Parameters["aggregation"]),
                                    parts.Min(p=>p.Cell.DeliveryFrom),parts.Max(p=>p.Cell.DeliveryTo)));
                            }
                        }
                        else
                        {
                            var sourceNode=selector.Inputs["source"];var source=inputs.Sources[sourceNode];var mode=selector.Parameters["mode"];
                            var sampleDate=mode=="daily_delivery"?inputs.ContextDate:date;var sampleAt=At(source.Key,sampleDate);
                            if(mode=="daily_delivery"&&sampleAt!=inputs.ValuationAt)
                                throw Missing("Daily delivery prices require the exact valuation snapshot instant.");
                            var at=mode=="daily_delivery"||selector.Parameters["binding"]=="context"?inputs.ValuationAt:sampleAt;
                            var first=mode=="positions"?Number(selector,"first",1,120):1;var count=mode=="positions"?Number(selector,"count",1,120):1;
                            var selected=ResolveContracts(source.Contracts,mode,first,count,at,date,requireLive:demand is null);
                            for(var i=0;i<selected.Length;i++)cells.Add(Leaf(id,cells.Count,sourceNode,sampleDate,selected[i],mode=="daily_delivery"?cells.Count+1:first+i,date,sampleAt));
                        }
                    }
                    result=new Panel(cells.ToImmutable());break;
                case "mean":
                    var panel=(Panel)Node(node.Inputs["values"]);var axis=node.Parameters["axis"];
                    var groups=axis=="dates"?panel.Cells.GroupBy(c=>c.Position.ToString(CultureInfo.InvariantCulture)):panel.Cells.GroupBy(c=>c.ObservationDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
                    result=new Panel(groups.Select((group,index)=>
                    {
                        var values=group.ToArray();if(values.Select(c=>c.Unit).Distinct().Count()!=1)throw Missing("An average requires identical exact price units.");
                        return new Cell(axis=="dates"?values[0].Position:1,values.SelectMany(c=>c.Dates).Distinct().Order().ToImmutableArray(),values[0].ObservationDate,
                            values[0].Unit,values.SelectMany(c=>c.Inputs).ToImmutableArray(),new(id,index,null,values.Select(c=>c.Formula).ToImmutableArray(),Enumerable.Repeat(ExactDecimal.Parse("1"),values.Length).ToImmutableArray(),WeightedMean),
                            values.Min(c=>c.DeliveryFrom),values.Max(c=>c.DeliveryTo));
                    }).ToImmutableArray());break;
                case "output":result=Node(node.Inputs["values"]);break;
                default:throw Invalid("Unsupported node.");
            }
            cache.Add(id,result);return result;
        }
        return(Panel)Node(definition.Output);
    }
    public static ImmutableArray<RecipeContract> ResolveContracts(ImmutableArray<RecipeContract> contracts,string mode,int first,int count,DateTimeOffset at,DateOnly date,bool requireLive=true)
    {
        if(at.Offset!=TimeSpan.Zero||contracts.IsDefaultOrEmpty)throw Missing("Contract resolution requires a retained universe and UTC instant.");
        RecipeContract[] selected;
        if(mode=="daily_delivery")
        {
            selected=contracts.Where(c=>c.Contract.Kind==ContractKind.Forward&&c.Contract.DeliveryFrom==date&&c.Contract.DeliveryTo==date.AddDays(1)).ToArray();
            if(selected.Length!=1)throw Missing($"Daily forward coverage is missing or ambiguous for {date:yyyy-MM-dd}.");
        }
        else if(mode=="positions"&&first is >=1 and <=120&&count is >=1 and <=120)
        {
            var prefix=contracts.Where(c=>c.Contract.Expiry>at).Take(first-1+count).ToArray();
            if(prefix.Any(c=>c.EligibleFrom>at))throw Missing("The selected rank prefix is not completely eligible at the resolution instant.");
            selected=prefix.Skip(first-1).ToArray();if(selected.Length!=count)throw Missing("The source does not cover every requested contract position.");
        }
        else throw Invalid("Choose a supported contract selection and bounded positions.");
        if(requireLive&&selected.Any(c=>c.EligibleFrom>at||c.Contract.Expiry<=at))throw Missing("The selected contract is not live and eligible at the resolution instant.");return selected.ToImmutableArray();
    }
    private static void ValidateSource(RecipeNode node,RecipeSource source)
    {
        if(source.Key.Code!=node.Parameters["curve"]||source.Key.Revision.CatalogId==Guid.Empty||source.Key.Revision.RevisionId==Guid.Empty||source.Contracts.IsDefaultOrEmpty)
            throw Missing("A source requires an exact retained curve and contract universe.");
        if(source.Contracts.Select(c=>c.Contract.Key).Distinct().Count()!=source.Contracts.Length||source.Contracts.GroupBy(c=>(c.Contract.DeliveryFrom,c.Contract.DeliveryTo)).Any(g=>g.Count()!=1)||
            source.Contracts.Any(c=>c.Contract.Key.Revision!=source.Key.Revision||c.Contract.DeliveryFrom>=c.Contract.DeliveryTo||c.EligibleFrom.Offset!=TimeSpan.Zero||c.Contract.Expiry.Offset!=TimeSpan.Zero)||
            source.Contracts.Zip(source.Contracts.Skip(1)).Any(pair=>pair.First.Contract.DeliveryFrom>=pair.Second.Contract.DeliveryFrom||pair.First.Contract.Expiry>pair.Second.Contract.Expiry))
            throw Missing("The exact source universe must have unique keys/periods, ascending delivery and nondecreasing expiry.");
    }
}
