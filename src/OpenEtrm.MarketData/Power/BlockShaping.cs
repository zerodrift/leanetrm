using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Power;

public enum ShapeMethod { Additive = 1, Multiplicative }

// A shaped interval curve: block marks calibrated onto one shape series by an exact method,
// calibration policy and operator version, on one delivery grid.
public sealed record ShapedCurveDefinition(string Code,ShapeMethod Method,string CalibrationPolicy,string OperatorVersion,Guid ShapeSeries,
    DeliveryGrid Grid,ImmutableArray<IntervalBlockDefinition> Blocks);

// One shape interval as the revision that last set it at the knowledge cutoff gives it.
public sealed record ShapeCell(DateTimeOffset Start,DateTimeOffset End,ExactDecimal Value,int Revision);

public sealed record ShapedIntervalPrice(DeliveryInterval Interval,ExactUnitPrice Price,CurveRequest Block,
    ImmutableHashSet<ObservationKey> Lineage,int ShapeRevision)
{
    public DateTimeOffset Start=>Interval.Start;
    public DateTimeOffset End=>Interval.End;
}

// The blocks a window touches, each compiled over its whole period with its shape intervals:
// prepared once per window and shape, then evaluated for every market scenario.
public sealed class ShapedInputs
{
    public ShapedCurveDefinition Definition { get; }
    public DateTimeOffset From { get; }
    public DateTimeOffset To { get; }
    internal ImmutableArray<(CurveRequest Request,ImmutableArray<PowerInterval> Periods,ImmutableArray<ShapeCell> Cells)> Blocks { get; }
    internal ShapedInputs(ShapedCurveDefinition definition,DateTimeOffset from,DateTimeOffset to,
        ImmutableArray<(CurveRequest,ImmutableArray<PowerInterval>,ImmutableArray<ShapeCell>)> blocks)
    {
        Definition=definition;From=from;To=to;Blocks=blocks;
    }
}

public sealed class ShapedCurveBuild
{
    public ImmutableArray<ShapedIntervalPrice> Prices { get; }
    public int CalibratedBlocks { get; }
    public int ReusedBlocks { get; }
    internal ShapedCurveDefinition Definition { get; }
    internal ImmutableDictionary<CurveRequest,CalibratedBlock> Blocks { get; }
    internal ShapedCurveBuild(ShapedCurveDefinition definition,ImmutableArray<ShapedIntervalPrice> prices,
        ImmutableDictionary<CurveRequest,CalibratedBlock> blocks,int calibrated,int reused)
    {
        Definition=definition;Prices=prices;Blocks=blocks;CalibratedBlocks=calibrated;ReusedBlocks=reused;
    }
}

// A whole block's prices and the pinned inputs that produced them.
internal sealed record CalibratedBlock(UnitPrice Mark,ImmutableHashSet<ObservationKey> Lineage,ImmutableArray<ShapeCell> Cells,
    ImmutableArray<ShapedIntervalPrice> Prices)
{
    public bool Matches(BuiltValue mark,ImmutableArray<ShapeCell> cells)=>
        Mark==mark.Price && Lineage.SetEquals(mark.Lineage) && Cells.SequenceEqual(cells);
}

public static class BlockShaping
{
    public const string CalibrationPolicy="duration_weighted_block_mean_v1";
    public const string OperatorVersion="block_shape_v1";

    // Additive Pᵢ = M + oᵢ − Σ(dⱼ·oⱼ)/Σdⱼ; multiplicative Pᵢ = M·fᵢ·Σdⱼ/Σ(dⱼ·fⱼ). Either way Σ(dᵢ·Pᵢ)/Σdᵢ = M exactly.
    public static ImmutableArray<ExactUnitPrice> Calibrate(ShapeMethod method,UnitPrice mark,IReadOnlyList<ShapeCell> cells)
    {
        ArgumentNullException.ThrowIfNull(mark);ArgumentNullException.ThrowIfNull(cells);
        if(cells.Count==0)throw new MarketDataException("interval_block","A block needs at least one interval.");
        var scale=ExactDecimal.WorkingScale;
        var (mean,divisor)=DurationWeights.Mean([..cells.Select(c=>(c.End-c.Start,c.Value.ScaledValue,scale))]);
        var m=mark.Value.ScaledValue;
        return method switch
        {
            ShapeMethod.Additive=>[..cells.Select(c=>ExactUnitPrice.FromFraction((m+c.Value.ScaledValue)*divisor-mean*scale,scale*divisor,mark.Unit))],
            ShapeMethod.Multiplicative when mean>0=>[..cells.Select(c=>ExactUnitPrice.FromFraction(m*c.Value.ScaledValue*divisor,scale*scale*mean,mark.Unit))],
            ShapeMethod.Multiplicative=>throw new MarketDataException("interval_shape","Multiplicative factors need a positive duration-weighted mean."),
            _=>throw UnknownMethod()
        };
    }

    // The span a shape read must cover: the whole delivery period of every block constituent the window overlaps.
    public static (DateTimeOffset From,DateTimeOffset To) ShapeWindow(ShapedCurveDefinition definition,DateTimeOffset from,DateTimeOffset to)
    {
        var periods=Overlapping(definition,from,to).Select(b=>Period(definition,b.Contract)).ToArray();
        return periods.Length==0?throw Unpartitioned():(periods.Min(p=>p.From),periods.Max(p=>p.To));
    }

    // Every interval in [from, to) must belong to exactly one block, and every interval of a block the window
    // touches needs its shape interval: those blocks are calibrated over their whole periods.
    public static ShapedInputs Prepare(ShapedCurveDefinition definition,IReadOnlyList<ShapeCell> shape,DateTimeOffset from,DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(shape);
        Validate(definition);
        IntervalPrices.RequireWindow(definition.Grid.Clock,definition.Grid.IntervalMinutes,from,to);
        var selected=Overlapping(definition,from,to).Select(b=>(Request:new CurveRequest(b.Block.Curve,b.Contract.Key),
            Periods:IntervalBlocks.Compile(definition.Grid,b.Block,b.Contract))).Where(b=>b.Periods.Any(p=>p.Start>=from && p.End<=to)).ToArray();
        var owners=new Dictionary<DateTimeOffset,PowerInterval>();
        foreach(var period in selected.SelectMany(b=>b.Periods))
            if(!owners.TryAdd(period.Start,period))throw Unpartitioned();
        var step=TimeSpan.FromMinutes(definition.Grid.IntervalMinutes);
        for(var at=from;at<to;at+=step)
            if(!owners.TryGetValue(at,out var owned) || owned.End!=at+step)throw Unpartitioned();
        var cells=IntervalAlignment.Align(owners.Values.ToArray(),shape,c=>c.Start,c=>c.End,"interval_shape_coverage",
            "Shape intervals must have distinct exact UTC boundaries.","Every block interval needs a shape value with its exact boundaries.");
        return new(definition,from,to,[..selected.Select(b=>(b.Request,b.Periods,b.Periods.Select(p=>cells[p.Start]).ToImmutableArray()))]);
    }

    // One market scenario's prices in the prepared window. With a base build of the same definition, a block whose
    // mark, lineage and shape intervals are unchanged is reused; every other block is calibrated once.
    public static ShapedCurveBuild Evaluate(ShapedInputs inputs,CurveBuild blocks,ShapedCurveBuild? @base=null)
    {
        ArgumentNullException.ThrowIfNull(inputs);ArgumentNullException.ThrowIfNull(blocks);
        var reusable=@base is not null && @base.Definition==inputs.Definition?@base.Blocks:ImmutableDictionary<CurveRequest,CalibratedBlock>.Empty;
        var marks=blocks.Values.ToDictionary(v=>v.Request);
        var results=ImmutableDictionary.CreateBuilder<CurveRequest,CalibratedBlock>();
        var reused=0;
        foreach(var (request,periods,cells) in inputs.Blocks)
        {
            if(!marks.TryGetValue(request,out var mark))throw new MarketDataException("request_member","The build lacks a requested block mark.");
            if(reusable.TryGetValue(request,out var prior) && prior.Matches(mark,cells))
            {
                results.Add(request,prior);reused++;continue;
            }
            var prices=Calibrate(inputs.Definition.Method,mark.Price,cells);
            results.Add(request,new(mark.Price,mark.Lineage,cells,
                [..periods.Select((p,i)=>new ShapedIntervalPrice(p.Delivery,prices[i],request,mark.Lineage,cells[i].Revision))]));
        }
        var window=results.Values.SelectMany(b=>b.Prices).Where(p=>p.Start>=inputs.From && p.End<=inputs.To).OrderBy(p=>p.Start).ToImmutableArray();
        return new(inputs.Definition,window,results.ToImmutable(),inputs.Blocks.Length-reused,reused);
    }

    // Block constituents whose delivery period overlaps [from, to).
    private static IEnumerable<(IntervalBlockDefinition Block,MarketContract Contract)> Overlapping(ShapedCurveDefinition definition,
        DateTimeOffset from,DateTimeOffset to)=>
        definition.Blocks.SelectMany(b=>b.Contracts.Select(c=>(Block:b,Contract:c))).Where(b=>
        {
            var (start,end)=Period(definition,b.Contract);
            return start<to && end>from;
        });

    private static (DateTimeOffset From,DateTimeOffset To) Period(ShapedCurveDefinition definition,MarketContract contract)=>
        (definition.Grid.Clock.StartOfDay(contract.DeliveryFrom).ToUniversalTime(),definition.Grid.Clock.StartOfDay(contract.DeliveryTo).ToUniversalTime());

    private static void Validate(ShapedCurveDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if(string.IsNullOrWhiteSpace(definition.Code) || definition.CalibrationPolicy!=CalibrationPolicy || definition.OperatorVersion!=OperatorVersion)
            throw new MarketDataException("interval_shape","Choose the supported calibration policy and operator version.");
        if(!Enum.IsDefined(definition.Method))throw UnknownMethod();
        if(definition.Grid is null || definition.Blocks.IsDefaultOrEmpty)
            throw new MarketDataException("interval_shape","A shaped curve needs a delivery grid and its blocks.");
        if(definition.Blocks.Any(b=>(b.Membership==BlockMembership.All)!=(b.Peak is null)))
            throw new MarketDataException("interval_block","Only a peak or off-peak block is bound to a TOU peak window.");
    }

    private static MarketDataException Unpartitioned()=>
        new("interval_block_partition","Every interval in the window must belong to exactly one marked block.");
    private static MarketDataException UnknownMethod()=>new("interval_shape","Choose the additive or multiplicative method.");
}
