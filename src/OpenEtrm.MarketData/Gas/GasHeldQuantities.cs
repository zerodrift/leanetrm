using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasFollowedMember(int Slot,ImmutableArray<GasDailyContract> Days,ExactRatio Scale,string? QuantityPolicy);
public sealed record GasQuantityHead(int Slot,DateOnly Day,GasQuantityObservation Observation);

// The gas adapter selects evidence once. Cash and fees consume the same immutable
// selected days; the shared lifecycle owns quantity scaling and accrual.
public static class GasHeldQuantities
{
    public const string DeliverySchedulePolicy = "delivery_schedule_v1";
    public static ImmutableArray<HeldDayQuantity> Build(ImmutableArray<GasFollowedMember> members,ImmutableArray<GasQuantityHead> heads,DateTimeOffset at)
    {
        if(members.IsDefault||heads.IsDefault||members.Any(m=>m is null||m.Slot<0||m.Scale is null||m.Scale.Numerator<=0||
            m.Days.IsDefaultOrEmpty||m.QuantityPolicy is not(null or DeliverySchedulePolicy)||
            m.Days.Any(d=>d is null||d.Quantity is null||d.Quantity.NumeratorValue<=0)||m.Days.Select(d=>d.Day).Distinct().Count()!=m.Days.Length))
            throw Invalid("Select physical owners, their complete retained native days and explicit quantity policies.");
        var slots=members.Select(m=>m.Slot).ToArray();
        if(slots.Distinct().Count()!=slots.Length)throw Invalid("Each followed membership has one independent physical quantity source.");
        var coverage=members.SelectMany(m=>m.Days.Select(d=>(m.Slot,d.Day))).ToHashSet();
        if(heads.Any(h=>h is null||h.Observation is null||!coverage.Contains((h.Slot,h.Day))))
            throw Invalid("A recorded quantity must name a day of a followed physical membership.");
        var lookup=heads.ToLookup(h=>(h.Slot,h.Day),h=>h.Observation);var output=ImmutableArray.CreateBuilder<HeldDayQuantity>();
        foreach(var member in members.OrderBy(m=>m.Slot))
            foreach(var day in member.Days.OrderBy(d=>d.Day))
            {
                ImmutableArray<GasQuantityObservation> observations = [..lookup[(member.Slot, day.Day)]];
                if(member.QuantityPolicy is null)
                {
                    GasDailyQuantitySelection.RequireValuationHeads(day, observations, at);
                    output.Add(new(member.Slot, day.Day, day.Quantity, day.Quantity, PhysicalLifecycle.ContractQuantity, [])
                    {
                        HasContractDeliveryRecord = observations.Any(o => o.Kind is ("scheduled" or "allocated") && o.Status != "withdrawn")
                    });
                    continue;
                }
                var selected=GasDailyQuantitySelection.Select(day,observations,GasDailyQuantitySelection.ValuationPolicy,at,member.Scale);
                var value=new HeldDayQuantity(member.Slot,day.Day,day.Quantity,selected.Quantity,GasDailyQuantitySelection.ValuationSource(selected),
                    [..selected.Provenance.Select(p=>new DeliveryQuantityEvidence(p.Source,p.Id,p.Evidence))]);
                output.Add(value);
            }
        return [..output.OrderBy(q=>q.Slot).ThenBy(q=>q.Day)];
    }
    static MarketDataException Invalid(string message)=>new("gas_quantity_selection",message);
}
