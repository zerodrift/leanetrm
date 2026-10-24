using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Power;

public enum ValuedAttribute { Energy=1, Certificate, Capacity }
public sealed record LocationAssignment(string Code,ReferenceKey Location,ValuedAttribute Attribute,DateTimeOffset From,DateTimeOffset To,
    CurveKey Curve,PriceUnit Unit,ExactDecimal Adjustment);

public static class LocationValuation
{
    public static ImmutableArray<LocationAssignment> Partition(IReadOnlyList<LocationAssignment> assignments,ReferenceKey location,
        ValuedAttribute attribute,IReadOnlyList<DeliveryInterval> intervals,DefinitionRevision definitions)
    {
        Money.RequireKey(location);
        foreach(var assignment in assignments)
        {Money.RequireKey(assignment.Location);Money.RequireKey(assignment.Unit.Currency);Money.RequireKey(assignment.Unit.Quantity);}
        if(!Enum.IsDefined(attribute) || assignments.Any(a=>!Enum.IsDefined(a.Attribute) || a.From.Offset!=TimeSpan.Zero ||
            a.To.Offset!=TimeSpan.Zero || a.From>=a.To || string.IsNullOrWhiteSpace(a.Code) || string.IsNullOrWhiteSpace(a.Curve.Code) || a.Curve.Revision!=definitions ||
            a.Location.Revision!=location.Revision || a.Unit.Currency.Revision!=location.Revision || a.Unit.Quantity.Revision!=location.Revision))
            throw new MarketDataException("location_assignment","Assignments require exact definition/reference identities and explicit UTC coverage and price units.");
        var matching=assignments.Where(a=>a.Location==location && a.Attribute==attribute).OrderBy(a=>a.From).ToArray();
        if(matching.Zip(matching.Skip(1)).Any(p=>p.First.To>p.Second.From))
            throw new MarketDataException("location_valuation_overlap","Location/attribute assignments cannot overlap.");
        return [..intervals.Select(interval=>
        {
            if(interval.Start.Offset!=TimeSpan.Zero || interval.End.Offset!=TimeSpan.Zero || interval.Start>=interval.End)
                throw new MarketDataException("location_assignment","Supply complete native UTC delivery intervals.");
            var found=matching.Where(a=>a.From<interval.End && a.To>interval.Start).ToArray();
            if(found.Length==0)throw new MarketDataException("location_valuation_missing","A location/attribute assignment is missing for delivery.");
            if(found.Length!=1 || found[0].From>interval.Start || found[0].To<interval.End)
                throw new MarketDataException("location_assignment","Assignment boundaries must align with the consuming grid; no subdivision is inferred.");
            return found[0];
        })];
    }
}
