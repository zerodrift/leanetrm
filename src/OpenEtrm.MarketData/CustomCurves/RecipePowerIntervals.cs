using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.CustomCurves;

public sealed record PowerIntervalSettlement(Guid PublicationId,int PublicationVersion,string Index,Guid SourceId,string Eligibility,string Projection)
{
    public const string Captured = "captured_required_for_ended_v1";
    public const string PjmDayAheadReplay = IntervalActualRule.PjmDayAheadReplay;
}

public sealed record PowerIntervalTerms(Guid ProductId,int ProductVersion,string Profile,string Market,DateOnly From,DateOnly To,DateOnly? Cutover);

public static partial class RecipeEngine
{
    public const string PowerIntervals="power_intervals";
    public const string DailyAdditiveShape="daily_native_block_additive_v1";
    public static readonly ImmutableArray<string> PowerShapeParameters=["shapeSeries","shapePolicy"];
    public static (Guid Series,string Policy)? PowerShapeSettings(RecipeNode node)
    {
        if(!PowerShapeParameters.Any(node.Parameters.ContainsKey))return null;
        if(!Guid.TryParse(Required(node,"shapeSeries"),out var series)||series==Guid.Empty||Required(node,"shapePolicy")!=DailyAdditiveShape)
            throw Invalid("Select the exact additive price-shape series and explicit daily-native block policy.");
        return(series,DailyAdditiveShape);
    }
    public static readonly ImmutableArray<string> PowerSettlementParameters=
        ["settlementPublication","settlementVersion","settlementIndex","settlementSource","settlementPolicy","projectionPolicy"];
    public static readonly ImmutableArray<string> PowerIntervalParameters=
        ["implementation","product","productVersion","profile","profileMapping","market","from","to","cutover",..PowerSettlementParameters,..PowerShapeParameters];
    public const string DailyThenMonthlyForward="daily_then_monthly_forward_v1";
    public const string SavedProductForwards="saved_product_forwards_v1";
    public static bool SavedProducts(RecipeNode node)=>node.Parameters.GetValueOrDefault("implementation")==SavedProductForwards;
    public static bool DailyThenMonthly(RecipeNode node)=>node.Parameters.GetValueOrDefault("implementation")==DailyThenMonthlyForward;
    public static ImmutableDictionary<string,string> PowerIntervalPorts(RecipeNode node)=>
        (DailyThenMonthly(node)?new[]{"peak","offPeak","dailyPeak","dailyOffPeak"}:new[]{"peak","offPeak"}).ToImmutableDictionary(p=>p,_=>SavedProducts(node)?"panel":"source");

    public static RecipeNode? PowerIntervalOutput(RecipeDefinition definition)
    {
        var output=definition.Nodes.FirstOrDefault(n=>n.Id==definition.Output);
        return output is not null&&output.Inputs.TryGetValue("values",out var id)
            ?definition.Nodes.FirstOrDefault(n=>n.Id==id&&n.Kind==PowerIntervals):null;
    }

    public static PowerIntervalTerms PowerIntervalSettings(RecipeNode node)
    {
        if(node.Kind!=PowerIntervals||Required(node,"implementation") is not("flat_forward_only_v1" or DailyThenMonthlyForward or SavedProductForwards)||
            Required(node,"profileMapping")!="exact_native_grid_terms_v1"||
            !Guid.TryParse(Required(node,"product"),out var product)||product==Guid.Empty||
            Required(node,"market") is not("DA" or "RT")||!Identity(Required(node,"profile"))||
            !DateOnly.TryParseExact(Required(node,"from"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var from)||
            !DateOnly.TryParseExact(Required(node,"to"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var to)||
            from>=to||to.DayNumber-from.DayNumber>1464)
            throw Invalid("Select an exact TOU version, native-grid mapping, market, flat forward policy and bounded delivery range.");
        DateOnly? cutover=null;
        if(DailyThenMonthly(node))
        {
            if(!DateOnly.TryParseExact(Required(node,"cutover"),"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var boundary)||
                boundary.Day!=1||to.Day!=1||boundary<=from||boundary>=to)
                throw Invalid("Daily forwards switch to whole monthly contracts on an explicit first-of-month boundary.");
            cutover=boundary;
        }
        else if(node.Parameters.ContainsKey("cutover"))throw Invalid("A cutover belongs only to daily then monthly forwards.");
        _=PowerSettlementSettings(node);
        _=PowerShapeSettings(node);
        return new(product,Number(node,"productVersion",1,64),node.Parameters["profile"],node.Parameters["market"],from,to,cutover);
    }
    public static PowerIntervalSettlement? PowerSettlementSettings(RecipeNode node)
    {
        if(!PowerSettlementParameters.Any(node.Parameters.ContainsKey))return null;
        if(!Guid.TryParse(Required(node,"settlementPublication"),out var publication)||publication==Guid.Empty||
            !Guid.TryParse(Required(node,"settlementSource"),out var source)||source==Guid.Empty||
            !Identity(Required(node,"settlementIndex"))||Required(node,"settlementPolicy") is not(PowerIntervalSettlement.Captured or PowerIntervalSettlement.PjmDayAheadReplay)||
            node.Parameters["settlementPolicy"]==PowerIntervalSettlement.PjmDayAheadReplay&&node.Parameters.GetValueOrDefault("market")!="DA"||
            Required(node,"projectionPolicy")!=FlatIntervalCurve.ReplaceActuals)
            throw Invalid("Choose exact physical settlement publication/index/source and explicit eligibility and projection policies.");
        return new(publication,Number(node,"settlementVersion",1,1000),node.Parameters["settlementIndex"],source,
            node.Parameters["settlementPolicy"],node.Parameters["projectionPolicy"]);
    }

    // A transparent subtype of the existing splice graph: exact identity maps,
    // sampled at the supplied valuation, with no alternate pricing terms.
    public static (DateOnly Cutover,ImmutableArray<(RecipeNode Source,ImmutableArray<Guid> Contracts,bool Daily)> Sources)
        PowerProductSplice(RecipeDefinition definition,DateOnly from,DateOnly to)
    {
        Validate(definition);
        if(definition.Nodes.Length!=9||definition.Nodes.Any(n=>n.ContractTerms is not null||!n.PanelWeights.IsDefaultOrEmpty||n.PublishedSource is not null))
            throw Invalid("A power product requires a transparent native identity-map splice without pricing overrides.");
        var nodes=definition.Nodes.ToDictionary(n=>n.Id);var used=new HashSet<string>();
        RecipeNode Read(string id,string kind)
        {
            if(!nodes.TryGetValue(id,out var node)||node.Kind!=kind||!used.Add(id))throw Invalid("A power product requires two distinct native splice legs and one context date node.");
            return node;
        }
        var output=Read(definition.Output,"output");var splice=Read(output.Inputs["values"],"splice");var range=SpliceRange(splice);
        if(range.From!=from||range.To!=to||range.Cutover.Day!=1||to.Day!=1)
            throw Invalid("Product curves must share the location domain and switch on a whole-month boundary.");
        string? dates=null;var sources=ImmutableArray.CreateBuilder<(RecipeNode,ImmutableArray<Guid>,bool)>();
        foreach(var port in new[]{"before","after"})
        {
            var sample=Read(splice.Inputs[port],"sample");
            if(dates is null)
            {
                dates=sample.Inputs["dates"];var date=Read(dates,"dates");
                if(date.Parameters.Count!=3||date.Parameters.GetValueOrDefault("mode")!="context"||
                    date.Parameters.GetValueOrDefault("sampling")!="context_utc_time"||date.Parameters.GetValueOrDefault("corrections")!="exact_context_frame_history_latest_visible")
                    throw Invalid("A power product samples the exact valuation context only.");
            }
            else if(sample.Inputs["dates"]!=dates)throw Invalid("Both product legs must use the same context date.");
            var map=Read(sample.Inputs["selection"],"weighted_contracts");
            if(map.Inputs.Count!=1||map.Parameters["aggregation"]!=SignedSum||map.ContractWeights.Any(w=>Weight(w.Weight)!=ExactDecimal.Parse("1"))||
                map.ContractWeights.Length!=map.ContractWeights.Select(w=>w.OutputPosition).Distinct().Count()||
                map.ContractWeights.Length!=map.ContractWeights.Select(w=>w.ContractId).Distinct().Count()||
                !map.ContractWeights.OrderBy(w=>w.OutputPosition).Select(w=>w.OutputPosition).SequenceEqual(Enumerable.Range(1,map.ContractWeights.Length)))
                throw Invalid("A power product maps one exact native contract per output position with weight one.");
            var source=Read(map.Inputs.Values.Single(),"source");
            sources.Add((source,[..map.ContractWeights.OrderBy(w=>w.OutputPosition).Select(w=>w.ContractId)],port=="before"));
        }
        if(used.Count!=nodes.Count)throw Invalid("A power product cannot contain unused operations.");
        return(range.Cutover,sources.ToImmutable());
    }

}
