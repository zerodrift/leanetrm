using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Gas;

internal sealed record GasSwingCoverage(GasPhysicalComponent Source, ExactDecimal Reference, int Slot,
    ImmutableArray<int> Slots, ImmutableArray<SwingDay> Days)
{
    internal static GasPhysicalComponent SourceFor(GasCompositionInstrument instrument, string source)
    {
        var physical = instrument.Physical;
        var owner = physical.FirstOrDefault(member => member.Code == source);
        if (owner is null || owner.Link is not null || physical.Any(member => member.Link?.Source == source) ||
            owner.Quantity is null)
            throw SwingFeasibility.InvalidRight("Select one independent physical swing source without a fuel link.");

        return owner;
    }

    internal static GasSwingCoverage Resolve(GasCompositionInstrument instrument, GasPhysicalComponent owner, ImmutableArray<string> entries, int leadHours)
    {
        var physical = instrument.Physical;
        var slot = physical.IndexOf(owner);
        var side = owner.Side;
        var schedule = side.Quantities;
        if (schedule.Basis != GasQuantityBasis.PerOperatingDay || leadHours < 0)
            throw SwingFeasibility.InvalidRight("Select whole gas days and a nonnegative deadline lead.");
        if (side.ForecastCurve is not null || side.Assignments.Any(a => a.Adjustment.Value != default || a.Quote.Kind != QuoteKind.Outright) ||
            side.Assignments.Select(a => a.Projection.Market.Curve).Distinct().Count() != 1)
            throw new MarketDataException("swing_model_scope", "Swing requires one unadjusted point-price curve in the physical source's native quote.");

        var covered = new List<int> { slot };
        for (var i = 0; i < instrument.Cash.Length; i++)
        {
            var member = instrument.Cash[i];
            if (member.Source != owner.Code || member.Balance is not null) continue;
            if (member.Part is not null || member.Side is not FixedSide payment || payment.GasDeliveryClock != schedule.Clock ||
                payment.DeliveryFrom != schedule.From || payment.DeliveryTo != schedule.To)
                throw SwingFeasibility.InvalidRight("Cover each ordinary follower with a fixed per-unit payment on the full source term.");
            covered.Add(physical.Length + i);
        }
        if (entries.IsDefault || entries.Length != covered.Count ||
            !entries.ToHashSet(StringComparer.Ordinal).SetEquals(covered.Select(i => instrument.Roles[i].Code)) ||
            instrument.ResolveRights().Any(r => r.Slots.Any(covered.Contains)))
            throw SwingFeasibility.InvalidRight("Name the physical source and each ordinary following payment exactly once, outside any other exercise right.");

        var days = ImmutableArray.CreateBuilder<SwingDay>();
        foreach (var day in side.GenerateSchedule().Quantities.Rows)
        {
            DateTimeOffset deadline;
            try { deadline = day.Start.AddHours(-(double)leadHours); }
            catch (ArgumentOutOfRangeException) { throw SwingFeasibility.InvalidRight("Every swing deadline must lie in the supported instant range."); }
            days.Add(new(day.OperatingDay, deadline));
        }
        return new(owner, owner.Quantity!.Value, slot, [..covered], days.ToImmutable());
    }
}
