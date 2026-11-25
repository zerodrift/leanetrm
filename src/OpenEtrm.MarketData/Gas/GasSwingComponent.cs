using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasSwingComponent(string Side, ImmutableArray<string> Entries, ExerciseHolder Holder,
    ExactDecimal Increment, ExactDecimal DailyMinimum, ExactDecimal DailyMaximum, ExactDecimal TotalMinimum,
    ExactDecimal TotalMaximum, int LeadHours, string QuantityPolicy)
{
    internal SwingRight Resolve(GasCompositionInstrument instrument)
    {
        var physical = instrument.Physical;
        var owner = physical.FirstOrDefault(member => member.Code == Side);
        if (owner is null || owner.Link is not null || physical.Any(member => member.Link?.Source == Side) ||
            owner.QuantityPolicy is not null || owner.Quantity is not {} reference)
            throw SwingFeasibility.InvalidRight("Select one independent physical swing source without a fuel link or competing quantity policy.");

        var slot = physical.IndexOf(owner);
        var side = owner.Side;
        var schedule = side.Quantities;
        if (schedule.Basis != GasQuantityBasis.PerOperatingDay || schedule.To.DayNumber - schedule.From.DayNumber is < 1 or > 31 ||
            LeadHours < 0 || Increment.CompareTo(default) <= 0)
            throw SwingFeasibility.InvalidRight("Select one to 31 whole gas days, a nonnegative lead and a positive contractual increment.");
        if (side.ForecastCurve is not null || side.Assignments.Any(a => a.Adjustment.Value != default || a.Quote.Kind != QuoteKind.Outright) ||
            side.Assignments.Select(a => a.Projection.Market.Curve).Distinct().Count() != 1)
            throw new MarketDataException("swing_model_scope", "Swing requires one unadjusted point-price curve in the physical source's native quote.");

        var covered = new List<int> { slot };
        for (var i = 0; i < instrument.Cash.Length; i++)
        {
            var member = instrument.Cash[i];
            if (member.Source != Side || member.Balance is not null) continue;
            if (member.Part is not null || member.Side is not FixedSide payment || payment.GasDeliveryClock != schedule.Clock ||
                payment.DeliveryFrom != schedule.From || payment.DeliveryTo != schedule.To)
                throw SwingFeasibility.InvalidRight("Cover each ordinary follower with a fixed per-unit payment on the full source term.");
            covered.Add(physical.Length + i);
        }
        if (Entries.IsDefault || Entries.Length != covered.Count ||
            !Entries.ToHashSet(StringComparer.Ordinal).SetEquals(covered.Select(i => instrument.Roles[i].Code)) ||
            instrument.ResolveRights().Any(r => r.Slots.Any(covered.Contains)))
            throw SwingFeasibility.InvalidRight("Name the physical source and each ordinary following payment exactly once, outside any other exercise right.");

        var days = ImmutableArray.CreateBuilder<SwingDay>();
        foreach (var day in side.GenerateSchedule().Quantities.Rows)
        {
            DateTimeOffset deadline;
            try { deadline = day.Start.AddHours(-(double)LeadHours); }
            catch (ArgumentOutOfRangeException) { throw SwingFeasibility.InvalidRight("Every swing deadline must lie in the supported instant range."); }
            days.Add(new(day.OperatingDay, deadline));
        }
        var right = new SwingRight(0, slot, [..covered], Holder, new ExactRatio(1, QuantityLots(reference,Increment)),
            QuantityLots(DailyMinimum,Increment), QuantityLots(DailyMaximum,Increment), QuantityLots(TotalMinimum,Increment), QuantityLots(TotalMaximum,Increment), QuantityPolicy, days.ToImmutable());
        SwingFeasibility.RequireRight(right);
        return right;
    }

    public static int QuantityLots(ExactDecimal amount, ExactDecimal increment)
    {
        if (increment.CompareTo(default) <= 0)throw SwingFeasibility.InvalidRight("Select a positive contractual increment.");
        var lots = BigInteger.DivRem(amount.ScaledValue, increment.ScaledValue, out var remainder);
        if (!remainder.IsZero || lots < 0 || lots > int.MaxValue)
            throw SwingFeasibility.InvalidRight("Every swing quantity must be an exact nonnegative integer number of supported contractual increments.");
        return (int)lots;
    }
}
