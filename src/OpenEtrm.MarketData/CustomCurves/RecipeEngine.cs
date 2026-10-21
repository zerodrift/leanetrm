using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.CustomCurves;

public static class RecipeEngine
{
    public const string Version="custom_curve_recipe_v1";
    private static MarketDataException Invalid(string message)=>new("recipe_definition",message);
    private static MarketDataException Missing(string message)=>new("recipe_input",message);
    private static readonly ImmutableDictionary<string,ImmutableDictionary<string,string>> Ports=new Dictionary<string,ImmutableDictionary<string,string>>
    {
        ["source"]=ImmutableDictionary<string,string>.Empty,
        ["select"]=new Dictionary<string,string>{{"source","source"}}.ToImmutableDictionary(),
        ["dates"]=ImmutableDictionary<string,string>.Empty,
        ["sample"]=new Dictionary<string,string>{{"selection","select"},{"dates","dates"}}.ToImmutableDictionary(),
        ["mean"]=new Dictionary<string,string>{{"values","panel"}}.ToImmutableDictionary(),
        ["sql_transform"]=new Dictionary<string,string>{{"values","panel"}}.ToImmutableDictionary(),
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
            if(!Ports.TryGetValue(node.Kind,out var ports)||!ports.Keys.ToHashSet().SetEquals(node.Inputs.Keys))
                throw Invalid($"Node {node.Id} has an unsupported kind or input ports.");
            foreach(var (port,id) in node.Inputs)
            {
                if(!nodes.TryGetValue(id,out var upstream))throw Invalid($"Node {node.Id} refers to a missing input.");
                var type=upstream.Kind is "sample" or "mean" or "sql_transform"?"panel":upstream.Kind;
                if(type!=ports[port])throw Invalid($"Port {port} on {node.Id} requires {ports[port]}.");
            }
            switch(node.Kind)
            {
                case "source": Required(node,"curve");break;
                case "select":
                    if(Required(node,"mode") is not ("positions" or "daily_delivery"))throw Invalid("Select positions or daily delivery explicitly.");
                    if(Required(node,"binding") is not ("each_sample" or "context"))throw Invalid("Select a contract binding policy explicitly.");
                    if(node.Parameters["mode"]=="positions"){Number(node,"first",1,120);Number(node,"count",1,120);}
                    else if(node.Parameters["binding"]!="context")throw Invalid("Daily forwards use one context observation frame.");
                    break;
                case "dates": Required(node,"mode");break;
                case "mean": if(Required(node,"axis") is not ("dates" or "contracts"))throw Invalid("Choose the averaging axis explicitly.");break;
                case "sql_transform":
                    if(Required(node,"sqlTransformCode")!="sql_mean_dates_v1"||nodes[node.Inputs["values"]].Kind!="sample")
                        throw Invalid("Choose a qualified transformation over raw sampled prices.");
                    break;
            }
            string[] parameters=node.Kind switch
            {
                "source"=>["curve"],
                "select"=>node.Parameters["mode"]=="positions"?["mode","first","count","binding"]:["mode","binding"],
                "dates"=>["mode","sampling","corrections","count","includeContext","calendar","schedule","first"],
                "mean"=>["axis"],_=>[]
            };
            if(node.Kind=="sql_transform")parameters=["sqlTransformCode"];
            if(node.Parameters.Keys.Any(key=>!parameters.Contains(key,StringComparer.Ordinal)))throw Invalid($"Node {node.Id} contains an unsupported or conflicting parameter.");
        }
        var visiting=new HashSet<string>();var visited=new HashSet<string>();
        void Visit(string id)
        {
            if(visited.Contains(id))return;
            if(!visiting.Add(id))throw Invalid("A curve recipe must be acyclic.");
            foreach(var upstream in nodes[id].Inputs.Values)Visit(upstream);
            visiting.Remove(id);visited.Add(id);
        }
        Visit(output.Id);
        if(visited.Count!=nodes.Count)throw Invalid("Every node must contribute to the selected output.");
    }
    private static string Required(RecipeNode node,string key)=>node.Parameters.TryGetValue(key,out var value)&&!string.IsNullOrWhiteSpace(value)
        ?value:throw Invalid($"Node {node.Id} requires {key}.");
    private static int Number(RecipeNode node,string key,int min,int max)=>int.TryParse(Required(node,key),NumberStyles.None,CultureInfo.InvariantCulture,out var value)&&value>=min&&value<=max
        ?value:throw Invalid($"Node {node.Id} requires {key} between {min} and {max}.");
    private static bool Identity(string? value)=>value is {Length:>0 and <=32}&&value[0] is >= 'a' and <= 'z'&&value.All(c=>c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private sealed record Selection(RecipeSource Source,string Mode,int First,int Count,string Binding);
    private sealed record Cell(int Position,ImmutableArray<DateOnly> Dates,DateOnly ObservationDate,ExactUnitPrice Price,ImmutableArray<RecipeSample> Inputs);
    private sealed record Panel(ImmutableArray<Cell> Cells);

    public static RecipeResult Evaluate(RecipeDefinition definition,RecipeInputs inputs,CancellationToken cancellation=default)
        =>EvaluateAsync(definition,inputs,null,cancellation).GetAwaiter().GetResult();

    public static async Task<RecipeResult> EvaluateAsync(RecipeDefinition definition,RecipeInputs inputs,RecipeSqlExecutor? sql,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();Validate(definition);
        if(inputs.ValuationAt.Offset!=TimeSpan.Zero||inputs.ContextDate!=DateOnly.FromDateTime(inputs.ValuationAt.UtcDateTime))throw Missing("The context date and UTC valuation instant must agree under this sampling policy.");
        var nodes=definition.Nodes.ToDictionary(n=>n.Id);var cache=new Dictionary<string,object>();
        if(!definition.Nodes.Where(n=>n.Kind=="source").Select(n=>n.Id).ToHashSet().SetEquals(inputs.Sources.Keys)||
            !definition.Nodes.Where(n=>n.Kind=="dates").Select(n=>n.Id).ToHashSet().SetEquals(inputs.Dates.Keys))
            throw Missing("Supply exact source and date bindings for every graph input.");
        var work=0;
        async Task<object> EvaluateNode(string id)
        {
            cancellation.ThrowIfCancellationRequested();
            if(cache.TryGetValue(id,out var prior))return prior;
            var node=nodes[id];Task<object> Input(string port)=>EvaluateNode(node.Inputs[port]);
            object result;
            switch(node.Kind)
            {
                case "source":
                    var source=inputs.Sources[id];ValidateSource(node,source,inputs.ValuationAt);result=source;break;
                case "select":
                    result=new Selection((RecipeSource)await Input("source"),node.Parameters["mode"],
                        node.Parameters["mode"]=="positions"?Number(node,"first",1,120):1,
                        node.Parameters["mode"]=="positions"?Number(node,"count",1,120):1,node.Parameters["binding"]);break;
                case "dates":
                    var dates=inputs.Dates[id];
                    if(dates.IsDefaultOrEmpty||dates.Length>366||dates.Distinct().Count()!=dates.Length||!dates.SequenceEqual(dates.Order()))
                        throw Missing("A date binding requires one to 366 distinct ascending dates.");
                    result=dates;break;
                case "sample":result=Sample((Selection)await Input("selection"),(ImmutableArray<DateOnly>)await Input("dates"));break;
                case "mean":result=Mean((Panel)await Input("values"),node.Parameters["axis"]);break;
                case "sql_transform":result=await SqlMean((Panel)await Input("values"),node.Parameters["sqlTransformCode"]);break;
                case "output":result=await Input("values");break;
                default:throw Invalid("Unsupported node.");
            }
            cache.Add(id,result);return result;
        }
        Panel Sample(Selection selection,ImmutableArray<DateOnly> dates)
        {
            var source=selection.Source;var samples=source.Samples.ToDictionary(s=>(s.Date,s.Contract));
            var instants=source.Samples.GroupBy(s=>s.Date).ToDictionary(g=>g.Key,g=>g.Select(s=>s.At).Distinct().ToArray());
            var cells=ImmutableArray.CreateBuilder<Cell>();
            foreach(var date in dates)
            {
                cancellation.ThrowIfCancellationRequested();
                DateTimeOffset at;RecipeContract[] selected;
                if(selection.Mode=="daily_delivery")
                {
                    at=inputs.ValuationAt;
                    selected=ResolveContracts(source.Contracts,selection.Mode,selection.First,selection.Count,at,date).ToArray();
                }
                else
                {
                    if(!instants.TryGetValue(date,out var times)||times.Length!=1)throw Missing($"A unique sampling instant is required for {date:yyyy-MM-dd}.");
                    at=selection.Binding=="context"?inputs.ValuationAt:times[0];
                    selected=ResolveContracts(source.Contracts,selection.Mode,selection.First,selection.Count,at,date).ToArray();
                }
                for(var i=0;i<selected.Length;i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if(++work>50000)throw Missing("The recipe exceeds the 50,000-sample evaluation limit.");
                    var contract=selected[i];var sampleDate=selection.Mode=="daily_delivery"?inputs.ContextDate:date;
                    if(contract.EligibleFrom>at||contract.Contract.Expiry<=at)throw Missing("The selected contract is not live and eligible at the resolution instant.");
                    if(!samples.TryGetValue((sampleDate,contract.Contract.Key),out var sample)||
                        selection.Mode=="daily_delivery"&&sample.At!=inputs.ValuationAt||sample.At<contract.EligibleFrom||sample.At>=contract.Contract.Expiry)
                        throw Missing($"A required live price is missing for {date:yyyy-MM-dd}, position {selection.First+i}.");
                    cells.Add(new(selection.Mode=="daily_delivery"?cells.Count+1:selection.First+i,[date],sampleDate,ExactUnitPrice.FromPrice(sample.Price),[sample]));
                }
            }
            return new(cells.ToImmutable());
        }
        async Task<Panel> SqlMean(Panel values,string code)
        {
            if(sql is null)throw Missing("The qualified SQL transformation is unavailable in this execution context.");
            var sums=await sql(code,values.Cells.Select(c=>new RecipeSqlValue(c.Position,c.Price.WorkingCoefficient())).ToImmutableArray(),cancellation);
            cancellation.ThrowIfCancellationRequested();
            var groups=values.Cells.GroupBy(c=>c.Position).ToArray();
            if(sums.IsDefault||sums.Length!=groups.Length||sums.Select(s=>s.Position).Distinct().Count()!=sums.Length)
                throw Missing("A registered transformation returned an incomplete or ambiguous output panel.");
            return new(groups.Select(group=>
            {
                var cells=group.ToArray();var sum=sums.SingleOrDefault(s=>s.Position==group.Key);
                if(sum is null||sum.Count!=cells.Length||sum.Coefficient.Length>48||
                    !BigInteger.TryParse(sum.Coefficient,NumberStyles.AllowLeadingSign,CultureInfo.InvariantCulture,out var coefficient))
                    throw Missing("A registered transformation returned invalid sample counts or coefficients.");
                if(cells.Any(c=>c.Price.Unit!=cells[0].Price.Unit))throw Missing("A registered transformation cannot merge different price units.");
                return new Cell(group.Key,cells.SelectMany(c=>c.Dates).Distinct().Order().ToImmutableArray(),cells[0].ObservationDate,
                    ExactUnitPrice.FromFraction(coefficient,ExactDecimal.WorkingScale*cells.Length,cells[0].Price.Unit),cells.SelectMany(c=>c.Inputs).ToImmutableArray());
            }).ToImmutableArray());
        }
        var panel=(Panel)await EvaluateNode(definition.Output);
        var used=panel.Cells.SelectMany(c=>c.Inputs).DistinctBy(s=>(s.Date,s.Contract)).OrderBy(s=>s.Date).ThenBy(s=>s.Contract.Id).ToImmutableArray();
        var factors=ImmutableArray.CreateBuilder<QuoteFactor>();
        foreach(var group in used.Where(s=>!s.Fixed).SelectMany(s=>s.Factors).GroupBy(f=>f.Input).OrderBy(g=>g.Key.Series.Id).ThenBy(g=>g.Key.Use))
        {
            var first=group.First();
            if(group.Any(f=>f.Quote!=first.Quote||f.Unit!=first.Unit||f.Contract!=first.Contract||f.Observation!=first.Observation||
                f.Source!=first.Source||f.Role!=first.Role||f.BaseValue!=first.BaseValue||f.TradingTick!=first.TradingTick))
                throw Missing("A live factor has conflicting exact bindings.");
            factors.Add(first with{Curves=group.SelectMany(f=>f.Curves).Distinct().OrderBy(c=>c.Revision.CatalogId).ThenBy(c=>c.Revision.RevisionId).ThenBy(c=>c.Code,StringComparer.Ordinal).ToImmutableArray()});
        }
        return new(panel.Cells.Select(c=>new RecipePoint(c.Position.ToString(CultureInfo.InvariantCulture),c.Position,c.Price,
            c.Inputs.Select(s=>s.Contract).Distinct().ToImmutableArray(),c.Dates.Distinct().Order().ToImmutableArray(),c.Inputs.Length)).ToImmutableArray(),used,factors.ToImmutable());
    }
    // Used by authorized input retrieval as well as evaluation: select exact
    // contracts before loading prices, without promoting a missing quote.
    public static ImmutableArray<RecipeContract> ResolveContracts(ImmutableArray<RecipeContract> contracts,string mode,int first,int count,DateTimeOffset at,DateOnly date)
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
            selected=prefix.Skip(first-1).ToArray();
            if(selected.Length!=count)throw Missing("The source does not cover every requested contract position.");
        }
        else throw Invalid("Choose a supported contract selection and bounded positions.");
        if(selected.Any(c=>c.EligibleFrom>at||c.Contract.Expiry<=at))throw Missing("The selected contract is not live and eligible at the resolution instant.");
        return selected.ToImmutableArray();
    }
    private static Panel Mean(Panel panel,string axis)
    {
        var groups=axis=="dates"?panel.Cells.GroupBy(c=>c.Position.ToString(CultureInfo.InvariantCulture)):
            panel.Cells.GroupBy(c=>c.ObservationDate.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture));
        return new(groups.Select(group=>
        {
            var cells=group.ToArray();var sum=cells[0].Price;
            foreach(var cell in cells.Skip(1))sum=sum.Add(cell.Price);
            return new Cell(axis=="dates"?cells[0].Position:1,cells.SelectMany(c=>c.Dates).Distinct().Order().ToImmutableArray(),cells[0].ObservationDate,sum.Divide(cells.Length),cells.SelectMany(c=>c.Inputs).ToImmutableArray());
        }).ToImmutableArray());
    }
    private static void ValidateSource(RecipeNode node,RecipeSource source,DateTimeOffset valuationAt)
    {
        if(source.Key.Code!=node.Parameters["curve"]||source.Key.Revision.CatalogId==Guid.Empty||source.Key.Revision.RevisionId==Guid.Empty||source.Contracts.IsDefaultOrEmpty||source.Samples.IsDefaultOrEmpty)
            throw Missing("A source requires an exact retained curve and observations.");
        if(source.Contracts.Select(c=>c.Contract.Key).Distinct().Count()!=source.Contracts.Length||source.Contracts.GroupBy(c=>(c.Contract.DeliveryFrom,c.Contract.DeliveryTo)).Any(g=>g.Count()!=1)||
            source.Contracts.Any(c=>c.Contract.Key.Revision!=source.Key.Revision||c.Contract.DeliveryFrom>=c.Contract.DeliveryTo||c.EligibleFrom.Offset!=TimeSpan.Zero||c.Contract.Expiry.Offset!=TimeSpan.Zero))
            throw Missing("Source contracts require unique exact keys and delivery periods under the source revision.");
        if(source.Contracts.Zip(source.Contracts.Skip(1)).Any(pair=>pair.First.Contract.DeliveryFrom>=pair.Second.Contract.DeliveryFrom||pair.First.Contract.Expiry>pair.Second.Contract.Expiry))
            throw Missing("The retained source sequence must increase by delivery and must not reverse expiry order.");
        var contracts=source.Contracts.ToDictionary(c=>c.Contract.Key);
        if(source.Samples.GroupBy(s=>(s.Date,s.Contract)).Any(g=>g.Count()!=1))throw Missing("Duplicate prices for a date and contract are ambiguous.");
        foreach(var sample in source.Samples)
        {
            if(!contracts.ContainsKey(sample.Contract)||sample.Price.Unit!=source.Unit||sample.At.Offset!=TimeSpan.Zero||sample.At>valuationAt||
                sample.Date!=DateOnly.FromDateTime(sample.At.UtcDateTime)||sample.At.TimeOfDay!=valuationAt.TimeOfDay||sample.At<valuationAt&&!sample.Fixed||sample.Observations.IsDefaultOrEmpty||
                sample.Observations.Any(o=>o.CatalogId!=source.Key.Revision.CatalogId||o.Id==Guid.Empty)||sample.Fixed&&!sample.Factors.IsDefaultOrEmpty)
                throw Missing("Samples require coherent units, exact lineage and a visible UTC instant; frozen observations have no live factors.");
            if(!sample.Fixed&&(sample.Factors.IsDefaultOrEmpty||sample.Factors.Any(f=>f.Input.Use!=InputUse.Variable||
                f.Input.Series.Revision!=source.Key.Revision||f.Contract.Revision!=source.Key.Revision||!sample.Observations.Contains(f.Observation)||
                f.Quote.Revision!=sample.Price.Unit.Currency.Revision||f.Source.Revision!=sample.Price.Unit.Currency.Revision||f.TradingTick.CompareTo(default)<=0)))
                throw Missing("A projected price requires complete live factor declarations with exact observation and reference lineage.");
        }
    }
}
