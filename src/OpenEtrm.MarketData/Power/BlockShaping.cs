using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;

namespace OpenEtrm.MarketData.Power;

public enum ShapeMethod { Additive = 1, Multiplicative }

// A shaped interval curve: block marks calibrated onto one shape series by an exact method,
// calibration policy and operator version, on one delivery grid.
public sealed record ShapedCurveDefinition(string Code,ShapeMethod Method,string CalibrationPolicy,string OperatorVersion,Guid ShapeSeries,
    DeliveryGrid Grid,ImmutableArray<IntervalBlockDefinition> Blocks)
{
    public ImmutableArray<IntervalBlockDefinition> BalanceBlocks { get; init; } = [];
}

// One shape interval as the revision that last set it at the knowledge cutoff gives it.
public readonly record struct SeriesCell(DateTimeOffset Start,DateTimeOffset End,ExactDecimal Value,int Revision);

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
    public ImmutableArray<CurveRequest> Dependencies => [..Blocks.Select(b=>b.Request)];
    internal ImmutableArray<(CurveRequest Request,ImmutableArray<PowerInterval> Periods,IntervalSelection Cells)> Blocks { get; }
    internal ImmutableDictionary<DateTimeOffset,CurveRequest>? SelectedAnchors { get; init; }
    internal ShapedInputs(ShapedCurveDefinition definition,DateTimeOffset from,DateTimeOffset to,
        ImmutableArray<(CurveRequest,ImmutableArray<PowerInterval>,IntervalSelection)> blocks)
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
internal sealed record CalibratedBlock(UnitPrice Mark,ImmutableHashSet<ObservationKey> Lineage,IntervalSelection Cells,
    ImmutableArray<ShapedIntervalPrice> Prices)
{
    public bool Matches(BuiltValue mark,IntervalSelection cells)=>
        Mark==mark.Price && Lineage.SetEquals(mark.Lineage) && Cells.SameValues(cells);
}

public static class BlockShaping
{
    public static FlatBlockProjection PrepareFlat(DeliveryGrid grid,ImmutableArray<IntervalBlockDefinition> blocks,
        DateTimeOffset from,DateTimeOffset to,DateTimeOffset valuationAt,string policy)
    {
        if(policy!="flat_forward_only_v1")
            throw new MarketDataException("interval_flat_policy","Select the flat forward-only construction policy explicitly.");
        if(valuationAt.Offset!=TimeSpan.Zero)
            throw new MarketDataException("interval_block_partition","Supply an exact UTC valuation.");
        var compiled=CompileFlat(grid,blocks,from,to);
        if(compiled.Anchors.Any(a=>grid.Clock.StartOfDay(a.Contract.DeliveryFrom).ToUniversalTime()<valuationAt||a.Contract.Expiry<=valuationAt))
            throw new MarketDataException("interval_flat_delivered","Flat forward-only construction requires every selected source period to remain entirely in projection.");
        return compiled;
    }

    // Geometry and ownership remain valid after delivery or fixing. Projection
    // admission belongs to the selected pricing policy, after actual eligibility.
    public static FlatBlockProjection CompileFlat(DeliveryGrid grid,ImmutableArray<IntervalBlockDefinition> blocks,
        DateTimeOffset from,DateTimeOffset to)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if(grid.Holidays.IsDefault||blocks.IsDefaultOrEmpty)
            throw new MarketDataException("interval_block_partition","Supply an exact delivery grid and source blocks.");
        IntervalPrices.RequireWindow(grid.Clock,grid.IntervalMinutes,from,to);
        var first=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from,grid.Clock.Zone).DateTime);
        var last=DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to.AddTicks(-1),grid.Clock.Zone).DateTime).AddDays(1);
        var intervals=ImmutableArray.CreateBuilder<PowerInterval>();
        for(var day=first;day<last;)
        {
            var end=new DateOnly(day.Year,day.Month,1).AddMonths(1);if(end>last)end=last;
            intervals.AddRange(PowerHours.Compile(day,end,grid.Clock,TimeOfUse.All(grid.IntervalMinutes),grid.Holidays.ToArray(),grid.IntervalMinutes)
                .Where(p=>p.Start>=from&&p.End<=to));
            day=end;
        }
        var anchors=ImmutableArray.CreateBuilder<(CurveRequest Request,MarketContract Contract)>();
        var anchorIndex=new Dictionary<CurveRequest,int>();
        var owned=new Dictionary<DateTimeOffset,(DateTimeOffset End,int Anchor)>();
        foreach(var block in blocks)
            foreach(var contract in block.Contracts)
            {
                if(contract.Kind is not(ContractKind.Future or ContractKind.Forward)||contract.Interval is not null)
                    throw new MarketDataException("interval_flat_source","Flat power anchors must be date-only futures or forwards, not option premiums or interval contracts.");
                var start=grid.Clock.StartOfDay(contract.DeliveryFrom).ToUniversalTime();
                var end=grid.Clock.StartOfDay(contract.DeliveryTo).ToUniversalTime();
                if(start>=to||end<=from)continue;
                var cells=IntervalBlocks.Compile(grid,block,contract).Where(p=>p.Start>=from&&p.End<=to).ToArray();
                if(cells.Length==0)continue;
                var request=new CurveRequest(block.Curve,contract.Key);
                if(!anchorIndex.TryGetValue(request,out var index))
                {index=anchors.Count;anchorIndex.Add(request,index);anchors.Add((request,contract));}
                else if(anchors[index].Contract!=contract)
                    throw new MarketDataException("interval_price_source","One exact source contract has conflicting terms.");
                foreach(var cell in cells)
                    if(!owned.TryAdd(cell.Start,(cell.End,index)))throw Unpartitioned();
            }
        var owners=ImmutableArray.CreateBuilder<int>(intervals.Count);
        foreach(var interval in intervals)
        {
            if(!owned.TryGetValue(interval.Start,out var owner)||owner.End!=interval.End)throw Unpartitioned();
            owners.Add(owner.Anchor);
        }
        return new(intervals.ToImmutable(),anchors.ToImmutable(),owners.ToImmutable());
    }

    public const string CalibrationPolicy="duration_weighted_block_mean_v1";
    public const string OperatorVersion="block_shape_v1";

    // Additive Pᵢ = M + oᵢ − Σ(dⱼ·oⱼ)/Σdⱼ; multiplicative Pᵢ = M·fᵢ·Σdⱼ/Σ(dⱼ·fⱼ). Either way Σ(dᵢ·Pᵢ)/Σdᵢ = M exactly.
    public static ImmutableArray<ExactUnitPrice> Calibrate(ShapeMethod method,UnitPrice mark,IReadOnlyList<SeriesCell> cells)
    {
        ArgumentNullException.ThrowIfNull(mark);ArgumentNullException.ThrowIfNull(cells);
        if(cells.Count==0)throw new MarketDataException("interval_block","A block needs at least one interval.");
        if(cells is IntervalSelection selected)return CalibrateSelected(method,mark,selected);
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

    private static ImmutableArray<ExactUnitPrice> CalibrateSelected(ShapeMethod method,UnitPrice mark,IntervalSelection cells)
    {
        var sum=cells.SumScaled();var scale=ExactDecimal.WorkingScale;var count=cells.Count;var m=mark.Value.ScaledValue;
        if(method==ShapeMethod.Multiplicative && sum<=0)
            throw new MarketDataException("interval_shape","Multiplicative factors need a positive duration-weighted mean.");
        if(!Enum.IsDefined(method))throw UnknownMethod();
        var result=ImmutableArray.CreateBuilder<ExactUnitPrice>(count);
        for(int i=0;i<count;i++)
        {
            var value=cells.Scaled(i);
            result.Add(method==ShapeMethod.Additive
                ?ExactUnitPrice.FromFraction((m+value)*count-sum,scale*count,mark.Unit)
                :ExactUnitPrice.FromFraction(m*value*count,scale*sum,mark.Unit));
        }
        return result.MoveToImmutable();
    }

    // The span a shape read must cover: the whole delivery period of every block constituent the window overlaps.
    public static (DateTimeOffset From,DateTimeOffset To) ShapeWindow(ShapedCurveDefinition definition,DateTimeOffset from,DateTimeOffset to)
    {
        Validate(definition);
        var periods=Overlapping(definition,from,to).Select(b=>Period(definition,b.Contract)).ToArray();
        if(periods.Length==0)throw new MarketDataException("interval_block_coverage","No period block of the curve covers the requested window.");
        return (periods.Min(p=>p.From),periods.Max(p=>p.To));
    }

    // Every interval in [from, to) must belong to exactly one block, and every interval of a block the window
    // touches needs its shape interval: those blocks are calibrated over their whole periods.
    public static ShapedInputs Prepare(ShapedCurveDefinition definition,IReadOnlyList<SeriesCell> shape,DateTimeOffset from,DateTimeOffset to)
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
        var cells=IntervalData.From(shape,definition.Grid.IntervalMinutes);
        return new(definition,from,to,[..selected.Select(b=>(b.Request,b.Periods,new IntervalSelection(cells,b.Periods)))]);
    }

    // Selected anchors can overlap in calendar coverage. Each still calibrates its whole product period;
    // only the selected owner supplies a particular output interval. Actuals are applied by the resolver afterward.
    internal static ShapedInputs PrepareSelected(ShapedCurveDefinition definition,IReadOnlyList<SeriesCell> shape,
        DateTimeOffset from,DateTimeOffset to,ImmutableDictionary<DateTimeOffset,CurveRequest> selected)
    {
        Validate(definition);
        var requests=selected.Values.ToHashSet();
        var blocks=definition.Blocks.Concat(definition.BalanceBlocks).SelectMany(b=>b.Contracts.Select(c=>(Block:b,Contract:c)))
            .Where(b=>requests.Contains(new(b.Block.Curve,b.Contract.Key))).Select(b=>
                (Request:new CurveRequest(b.Block.Curve,b.Contract.Key),Periods:IntervalBlocks.Compile(definition.Grid,b.Block,b.Contract))).ToArray();
        if(blocks.Select(b=>b.Request).Distinct().Count()!=requests.Count || blocks.Length!=requests.Count)
            throw new MarketDataException("interval_anchor_missing","Every selected anchor must name one declared block constituent.");
        var cells=IntervalData.From(shape,definition.Grid.IntervalMinutes);
        return new(definition,from,to,[..blocks.Select(b=>(b.Request,b.Periods,new IntervalSelection(cells,b.Periods)))])
            {SelectedAnchors=selected};
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
                [..periods.Select((p,i)=>new ShapedIntervalPrice(p.Delivery,prices[i],request,mark.Lineage,cells.Revision(i)))]));
        }
        var window=results.Values.SelectMany(b=>b.Prices).Where(p=>p.Start>=inputs.From && p.End<=inputs.To &&
            (inputs.SelectedAnchors is null || inputs.SelectedAnchors.TryGetValue(p.Start,out var selected) && selected==p.Block)).OrderBy(p=>p.Start).ToImmutableArray();
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
        if(definition.Blocks.Any(b=>(b.Membership is BlockMembership.Peak or BlockMembership.OffPeak)!=(b.Peak is not null)))
            throw new MarketDataException("interval_block","Only a peak or off-peak block is bound to a TOU peak window.");
    }

    private static MarketDataException Unpartitioned()=>
        new("interval_block_partition","Every interval in the window must belong to exactly one marked block.");
    private static MarketDataException UnknownMethod()=>new("interval_shape","Choose the additive or multiplicative method.");
}
