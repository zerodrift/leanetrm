using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// Quantities are in the same signed held basis as the view's other amounts.
public sealed record ScheduleFollowedDay(SignedRatio Contract,SignedRatio Selected,string Source,ImmutableArray<DeliveryQuantityEvidence> Evidence)
{
    public static ScheduleFollowedDay FromHeld(HeldDayQuantity day,SignedRatio factor)=>new(
        new SignedRatio(day.Contract.NumeratorValue,day.Contract.DenominatorValue)*factor,
        new SignedRatio(day.Selected.NumeratorValue,day.Selected.DenominatorValue)*factor,day.Source,day.Evidence);
}

internal sealed record ScheduleSelectedQuantity(SignedRatio Contract,SignedRatio Selected,SignedRatio Fraction,
    ImmutableArray<DateOnly> Days,ImmutableArray<string> Tags,ImmutableArray<DeliveryQuantityEvidence> Evidence)
{
    internal static ScheduleSelectedQuantity? Read(SideViewInput side,ScheduleDefinitionRow row,int cell)
    {
        if(side.FollowedDays is not {} selected)return null;
        if(side.Recorded is not null)throw Invalid("A followed schedule consumes its selected quantities, independently of the operational display overlay.");
        var day=row.Grid is {} grid?side.Program.Document.Grids[grid].Interval(cell).OperatingDate:(DateOnly?)null;
        var from=day??row.DeliveryFrom??throw Invalid("A followed contribution requires its retained delivery days.");
        var to=day?.AddDays(1)??row.DeliveryTo??throw Invalid("A followed contribution requires its retained delivery days.");
        var days=Enumerable.Range(0,to.DayNumber-from.DayNumber).Select(from.AddDays).ToImmutableArray();
        var values=days.Select(d=>selected.TryGetValue(d,out var value)?value:throw Invalid("A followed schedule is missing a selected delivery day.")).ToArray();
        if(values.Any(v=>v is null||v.Contract.Denominator<=0||v.Selected.Denominator<=0||v.Contract.Numerator.IsZero||
            v.Selected.Numerator.Sign!=0&&v.Contract.Numerator.Sign!=v.Selected.Numerator.Sign||v.Evidence.IsDefault))
            throw Invalid("A followed schedule requires signed contractual and selected quantities with their evidence.");
        var contract=SignedRatio.Sum(values.Select(v=>v.Contract));var quantity=SignedRatio.Sum(values.Select(v=>v.Selected));
        return new(contract,quantity,DeliveryQuantityFractions.Ratio(quantity,contract),days,[..values.Select(v=>v.Source switch
        {
            PhysicalLifecycle.ContractQuantity=>"contract",PhysicalLifecycle.ScheduledQuantity=>"scheduled",
            PhysicalLifecycle.AllocatedFinalQuantity=>"allocated_final",PhysicalLifecycle.AllocatedProvisionalQuantity=>"allocated_provisional",
            _=>throw Invalid("A followed schedule requires a supported selected quantity rank.")
        }).Distinct()], [..values.SelectMany(v=>v.Evidence).Distinct()]);
    }
    internal static MarketDataException Invalid(string message)=>new("schedule_view",message);
}
