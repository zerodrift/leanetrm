using System.Collections.Immutable;
using System.Globalization;
using OpenEtrm.MarketData.Power;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

public enum ScheduleViewMeasure { Quantity,Price,Amount }
// A numeric Value is an exact decimal string, or an exact reduced "a/b" ratio when it
// is not exact at the working scale or lies beyond the working decimal range.
// trade.payment_at holds an ISO-8601 UTC instant instead. A null Value means the
// level has no value there, never zero; delivered and remaining split whole pieces.
public sealed record ScheduleViewCell(string? Value,ImmutableArray<string> Sources);
public sealed record ScheduleViewRow(string Key,string? Parent,string Level,DateTimeOffset Start,DateTimeOffset End,string? OperatingDate,
    ImmutableDictionary<string,ScheduleViewCell> Cells,bool Delivered);
public sealed record ScheduleView(ImmutableArray<string> Levels,ImmutableArray<ScheduleViewRow> Rows,ImmutableDictionary<string,ScheduleViewCell> Totals,
    ImmutableDictionary<string,ScheduleViewCell>? Delivered,ImmutableDictionary<string,ScheduleViewCell>? Remaining,int DeliveredLeaves,int Leaves,DateTimeOffset ValuationAt);
// Scale is the trade's booked quantity scaling and Coefficient the side's signed
// membership, an exact ratio; both apply to quantities and amounts, never to prices.
public sealed record SideViewInput(string Code,BookedSideEvaluation Evaluation,BookedSideProgram Program,ExactDecimal Scale,SignedRatio Coefficient,
    ImmutableArray<(string Column,ScheduleViewMeasure Measure)> Columns);

// Every value comes from an evaluated contribution: its amount, and the quantity
// and price operands of its amount instruction, on the contribution's own grid
// cell or scalar delivery window. Coarser rows hold exact sums; a coarser price
// is shown only when every child agrees. Nothing is averaged or inferred.
// The boundary maps an operating date to its starting instant (for gas, the
// gas-day clock's Boundary); scalar windows and day cells are anchored on it.
// Every side must be evaluated under one pinned context, whose valuation instant
// marks the rows already delivered.
public static class ScheduleViews
{
    public const string NetCash="trade.net_cash",PaymentAt="trade.payment_at";
    private static readonly string[] Supported=["month","day"];
    // One canonical tag order; a set of tags is a bit mask over it.
    private static readonly string[] Tags=["contract","forecast","meter","fwd","actual","modeled","fixed","estimated"];
    private static readonly (SchedulePriceFacts Fact,int Tag)[] PriceTags=[(SchedulePriceFacts.Fixed,0),(SchedulePriceFacts.Projected,3),
        (SchedulePriceFacts.Actual,4),(SchedulePriceFacts.Modeled,5),(SchedulePriceFacts.PublishedFixing,6),(SchedulePriceFacts.Estimated,7)];
    private sealed record Piece(SideViewInput Side,string Level,string Month,string? Day,DateTimeOffset Start,DateTimeOffset End,
        SignedRatio Quantity,SignedRatio Price,SignedRatio Amount,int QuantityTags,int PriceTags,DateTimeOffset? Payment,ReferenceKey? Currency);

    public static ScheduleView Build(ImmutableArray<SideViewInput> sides,ImmutableArray<string> levels,Func<DateOnly,DateTimeOffset> boundary)
    {
        if(sides.IsDefaultOrEmpty||sides.Any(side=>side?.Evaluation is null)||levels.IsDefaultOrEmpty||boundary is null)
            throw Error("Select the evaluated sides, row levels and operating-day boundary of the schedule view.");
        var context=sides[0].Evaluation.Context;
        if(sides.Any(side=>side.Evaluation.Context!=context))throw Error("A schedule view shows sides evaluated under one pinned calculation context.");
        var valuationAt=context.ValuationAt;
        if(!levels.SequenceEqual(Supported.Where(levels.Contains)))throw Error("Row levels must be supported, distinct and ordered from coarse to fine.");
        if(sides.Select(side=>side.Code).Distinct(StringComparer.Ordinal).Count()!=sides.Length)throw Error("Each side requires a distinct code.");
        var pieces=sides.SelectMany(side=>Pieces(side,boundary)).ToArray();
        if(pieces.Any(piece=>!levels.Contains(piece.Level)))throw Error("A side reports values at a granularity outside the selected row levels.");
        if(pieces.Where(piece=>piece.Payment is not null).Select(piece=>piece.Currency).Distinct().Count()>1)
            throw Error("Net cash requires every paying side to settle in one currency.");
        var rows=ImmutableArray.CreateBuilder<ScheduleViewRow>();var leaves=0;var deliveredLeaves=0;
        void Add(IEnumerable<Piece> scope,int depth,string? parent)
        {
            var level=levels[depth];
            foreach(var group in scope.GroupBy(piece=>level=="month"?piece.Month:piece.Day!).OrderBy(group=>group.Key,StringComparer.Ordinal))
            {
                var finer=group.Where(piece=>piece.Level!=level).ToArray();
                var spans=group.Where(piece=>piece.Level==level).Select(piece=>(piece.Start,piece.End));
                if(finer.Length>0)spans=spans.Append((finer.Min(piece=>piece.Start),finer.Max(piece=>piece.End)));
                var span=spans.Distinct().ToArray();
                if(span.Length!=1)throw Error($"Values on schedule row {group.Key} disagree on its clock interval or are finer than its level.");
                var delivered=span[0].End<=valuationAt;
                rows.Add(new(group.Key,parent,level,span[0].Start,span[0].End,level=="day"?group.Key:null,Cells(sides,group.ToArray()),delivered));
                if(finer.Length>0)Add(finer,depth+1,group.Key);
                else{leaves++;if(delivered)deliveredLeaves++;}
            }
        }
        Add(pieces,0,null);
        var split=deliveredLeaves>0;
        return new(levels,rows.ToImmutable(),Cells(sides,pieces),split?Cells(sides,[..pieces.Where(piece=>piece.End<=valuationAt)]):null,
            split?Cells(sides,[..pieces.Where(piece=>piece.End>valuationAt)]):null,deliveredLeaves,leaves,valuationAt);
    }

    private static IEnumerable<Piece> Pieces(SideViewInput side,Func<DateOnly,DateTimeOffset> boundary)
    {
        if(string.IsNullOrWhiteSpace(side.Code)||side.Code.Contains('.')||side.Code=="trade"||side.Program is null||
            side.Columns.IsDefaultOrEmpty||side.Columns.Any(column=>string.IsNullOrWhiteSpace(column.Column)||!Enum.IsDefined(column.Measure))||
            side.Columns.Select(column=>column.Column).Distinct(StringComparer.Ordinal).Count()!=side.Columns.Length||
            side.Scale.CompareTo(default)<=0||side.Coefficient.Denominator.Sign<=0||side.Coefficient.Numerator.IsZero)
            throw Error("Each side requires a plain code, its evaluated program, distinct explicit columns, a positive scale and a nonzero coefficient.");
        var evaluation=side.Evaluation;var document=side.Program.Document;
        if(evaluation.ScheduleManifest!=document.ScheduleManifest||evaluation.Values.Length!=document.Instructions.Length||evaluation.Contributions.IsDefaultOrEmpty)
            throw Error("A side evaluation must belong to its program and report its contributions.");
        if(evaluation.BaseScenarioId!=evaluation.ScenarioId)throw Error("A schedule view shows one scenario; its base and scenario lanes must be the same.");
        var scale=Ratio(side.Scale)*side.Coefficient;
        (ReferenceKey?,ReferenceKey?,ReferenceKey?,string?,ReferenceKey?)? units=null;
        var pieces=new List<Piece>();
        foreach(var contribution in evaluation.Contributions)
        {
            if((uint)contribution.Instruction>=(uint)document.Instructions.Length||document.Instructions[contribution.Instruction] is not {Output.Operation:"output_guard"} guard||
                Operand(document,guard.Inputs,"amount") is not {Output.Operation:"amount"} amount)
                throw Error("Each contribution requires one priced amount whose quantity and price can be shown.");
            var quantities=evaluation.Values[Operand(document,amount.Inputs,"quantity").Output.Ordinal];var prices=evaluation.Values[Operand(document,amount.Inputs,"price").Output.Ordinal];
            var cash=contribution.Amount;
            if(cash.Kind!="money"||cash.Presence.Length!=cash.Values.Length||contribution.States.Length!=cash.Values.Length)
                throw Error("Each contribution requires one amount and state per cell.");
            for(var cell=0;cell<cash.Values.Length;cell++)
            {
                var(quantity,q)=Resolved(quantities,cell,cash.Values.Length);var(price,p)=Resolved(prices,cell,cash.Values.Length);
                if(cash.Presence[cell]!=ScheduleCellPresence.Resolved||quantity.States[q].Quantity is not {} volume||price.States[p].Prices==SchedulePriceFacts.None)
                    throw Error("Every shown cell requires its resolved quantity, price and amount; evaluate the side's complete term.");
                if(quantity.Kind!="quantity"||price.Kind!="price"||(units??=(quantity.Unit,price.Unit,price.Currency,price.QuoteMultiplier,cash.Currency))!=
                    (quantity.Unit,price.Unit,price.Currency,price.QuoteMultiplier,cash.Currency))
                    throw Error("A side's shown quantities, prices and amounts must keep one exact unit, quote scale and currency.");
                var interval=Interval(document,guard.Output,cell,boundary);
                var payment=contribution.States[cell].Kind==ComponentKind.Payment?guard.Output.ValueAt??throw Error("A payment requires its value date."):(DateTimeOffset?)null;
                pieces.Add(new(side,interval.Level,interval.Month,interval.Day,interval.Start,interval.End,Ratio(quantity.Values[q])*scale,Ratio(price.Values[p]),
                    Ratio(cash.Values[cell])*scale,VolumeTag(volume),PriceTag(price.States[p].Prices),payment,cash.Currency));
            }
        }
        return pieces;
    }
    // The program's single in-range operand on a port.
    private static BookedSideInstruction Operand(BookedSideProgramDocument document,ImmutableSortedDictionary<string,ImmutableArray<int>> inputs,string port)=>
        inputs.TryGetValue(port,out var operands)&&operands.Length==1&&(uint)operands[0]<(uint)document.Instructions.Length?document.Instructions[operands[0]]:
        throw Error("Each contribution requires one priced amount whose quantity and price can be shown.");
    // Demand resolves a cell in the scenario lane, or only in the base lane once it is paid; both lanes are one scenario here.
    // A one-value operand broadcasts to every cell.
    private static (ScheduleValueColumn Column,int Index) Resolved(ScheduleLaneColumns lanes,int cell,int cells)
    {
        foreach(var column in new[]{lanes.Scenario,lanes.Base})
        {
            var length=column.Values.Length;
            if(length!=1&&length!=cells||column.States.Length!=length||column.Presence.Length!=length)
                throw Error("A shown operand requires one value or one value per contribution cell.");
            var index=length==1?0:cell;
            if(column.Presence[index]==ScheduleCellPresence.Resolved)return(column,index);
        }
        throw Error("Every shown cell requires its resolved quantity, price and amount; evaluate the side's complete term.");
    }
    private static (string Level,string Month,string? Day,DateTimeOffset Start,DateTimeOffset End) Interval(BookedSideProgramDocument document,ScheduleDefinitionRow row,int cell,Func<DateOnly,DateTimeOffset> boundary)
    {
        if(row.Grid is {} grid)
        {
            var interval=document.Grids[grid].Interval(cell);var day=interval.OperatingDate;
            if(interval.Start!=boundary(day)||interval.End!=boundary(day.AddDays(1)))
                throw Error("A shown grid cell must cover exactly one operating day on the selected boundary.");
            return("day",Month(day),day.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),interval.Start,interval.End);
        }
        if(row.DeliveryFrom is not {} from||row.DeliveryTo is not {} to)throw Error("A scalar contribution requires its delivery window.");
        var level=to==from.AddDays(1)?"day":Month(from)==Month(to.AddDays(-1))?"month":throw Error("A scalar contribution must lie within one day or one calendar month.");
        return(level,Month(from),level=="day"?from.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture):null,boundary(from),boundary(to));
    }
    private static ImmutableDictionary<string,ScheduleViewCell> Cells(ImmutableArray<SideViewInput> sides,IReadOnlyCollection<Piece> scope)
    {
        var cells=ImmutableDictionary.CreateBuilder<string,ScheduleViewCell>(StringComparer.Ordinal);
        foreach(var side in sides)
        {
            var own=scope.Where(piece=>ReferenceEquals(piece.Side,side)).ToArray();
            foreach(var(column,measure) in side.Columns)
            {
                var key=side.Code+"."+column;
                if(own.Length==0){cells.Add(key,new(null,[]));continue;}
                cells.Add(key,measure switch
                {
                    ScheduleViewMeasure.Quantity=>new(Text(Sum(own,piece=>piece.Quantity)),Names(own,piece=>piece.QuantityTags)),
                    ScheduleViewMeasure.Price=>new(own.Select(piece=>piece.Price).Distinct().Count()==1?Text(own[0].Price):null,Names(own,piece=>piece.PriceTags)),
                    _=>new(Text(Sum(own,piece=>piece.Amount)),Names(own,piece=>piece.QuantityTags|piece.PriceTags))
                });
            }
        }
        var cash=scope.Where(piece=>piece.Payment is not null).ToArray();
        var payments=cash.Select(piece=>piece.Payment!.Value).Distinct().ToArray();
        cells.Add(NetCash,cash.Length==0?new(null,[]):new(Text(Sum(cash,piece=>piece.Amount)),Names(cash,piece=>piece.QuantityTags|piece.PriceTags)));
        cells.Add(PaymentAt,new(payments.Length==1?payments[0].ToString("O",CultureInfo.InvariantCulture):null,[]));
        return cells.ToImmutable();
    }
    private static SignedRatio Sum(IEnumerable<Piece> pieces,Func<Piece,SignedRatio> value)=>SignedRatio.Sum(pieces.Select(value));
    private static ImmutableArray<string> Names(IEnumerable<Piece> pieces,Func<Piece,int> tags)
    {var mask=pieces.Aggregate(0,(all,piece)=>all|tags(piece));return[..Tags.Where((_,n)=>(mask&1<<n)!=0)];}
    private static int VolumeTag(VolumeState state)=>1<<(state switch
    {
        VolumeState.Contractual=>0,VolumeState.Forecast=>1,VolumeState.Metered=>2,VolumeState.Estimated=>7,
        _=>throw Error("A shown quantity requires a supported volume state.")
    });
    private static int PriceTag(SchedulePriceFacts facts)
    {
        if(((int)facts&~63)!=0)throw Error("A shown price requires supported price facts.");
        return PriceTags.Where(tag=>(facts&tag.Fact)!=0).Aggregate(0,(mask,tag)=>mask|1<<tag.Tag);
    }
    private static string Month(DateOnly date)=>date.ToString("yyyy-MM",CultureInfo.InvariantCulture);
    private static SignedRatio Ratio(ExactDecimal value)=>new(value.ScaledValue,ExactDecimal.WorkingScale);
    private static SignedRatio Ratio(ScheduleNumber value)=>new(value.Numerator,value.Denominator);
    private static string Text(SignedRatio value)
    {
        try{if(value.TryScaleToInteger(ExactDecimal.WorkingScale,out var scaled))return ExactDecimal.FromScaled(scaled).ToString();}
        catch(MarketDataException e)when(e.Code=="numeric_range"){}
        return value.Numerator.ToString(CultureInfo.InvariantCulture)+"/"+value.Denominator.ToString(CultureInfo.InvariantCulture);
    }
    private static MarketDataException Error(string message)=>new("schedule_view",message);
}
