using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

public enum QuantityPartKind { Band = 1, Shortfall }

[EconomicDefinition("quantity_part_v1")]
public sealed record QuantityPart
{
    [IdentityField] public QuantityPartKind Kind { get; }
    [IdentityField] public SignedRatio Lower { get; }
    [IdentityField] public SignedRatio? Upper { get; }

    // Levels are nonnegative exact ratios of the held trade's base quantity.
    // ExactRatio remains positive-only; a band's lower level can be zero.
    public QuantityPart(QuantityPartKind kind, SignedRatio lower, SignedRatio? upper)
    {
        if (!Enum.IsDefined(kind) || lower.Denominator <= 0 || lower.Numerator < 0 ||
            upper is {} limit && (limit.Denominator <= 0 || limit.CompareTo(lower) <= 0) ||
            kind == QuantityPartKind.Shortfall && upper is not null)
            throw new MarketDataException("quantity_part", "Select nonnegative exact levels, an increasing band or a shortfall with no upper level.");
        Kind = kind; Lower = lower; Upper = upper;
    }

    public string QuantityBasis => Kind == QuantityPartKind.Band ? "band_v1" : "shortfall_v1";

    public static QuantityPart Band(SignedRatio lower, SignedRatio? upper) => new(QuantityPartKind.Band, lower, upper);
    public static QuantityPart Shortfall(SignedRatio below) => new(QuantityPartKind.Shortfall, below, null);

    public SignedRatio Take(SignedRatio dayQuantity, SignedRatio baseQuantity)
    {
        if (dayQuantity.Denominator <= 0 || dayQuantity.Numerator < 0 || baseQuantity.Denominator <= 0 || baseQuantity.Numerator <= 0)
            throw PhysicalDeliveryQuantities.Invalid("A quantity part requires a nonnegative day quantity and a positive held base quantity.");
        var lower = Lower * baseQuantity;
        var amount = Kind == QuantityPartKind.Shortfall ? lower - dayQuantity : dayQuantity - lower;
        if (amount.Numerator < 0) return SignedRatio.Zero;
        if (Upper is {} upper)
        {
            var width = (upper - Lower) * baseQuantity;
            if (amount.CompareTo(width) > 0) return width;
        }
        return amount;
    }
}

public sealed record EntryDayQuantity(DateOnly Day, SignedRatio Contract, SignedRatio Selected, SignedRatio Taken,
    string Source, ImmutableArray<DeliveryQuantityEvidence> Evidence);

public static class EntryQuantities
{
    public static ImmutableArray<EntryDayQuantity> For(IEnumerable<HeldDayQuantity> sourceDays, QuantityPart? part,
        SignedRatio baseQuantity, DateOnly from, DateOnly to, Func<DateOnly, bool>? counted = null)
    {
        if (sourceDays is null || from == default || from >= to || baseQuantity.Denominator <= 0 || baseQuantity.Numerator <= 0)
            throw PhysicalDeliveryQuantities.Invalid("An entry requires its explicit delivery term, source days and positive held base quantity.");
        var source = sourceDays.ToArray();
        if (source.Any(day => day is null))
            throw PhysicalDeliveryQuantities.Invalid("An entry requires complete identified source days.");
        var days = source.Where(day => day.Day >= from && day.Day < to).OrderBy(day => day.Day).ToArray();
        if (days.Length != to.DayNumber - from.DayNumber ||
            !days.Select(day => day.Day).SequenceEqual(Enumerable.Range(0, to.DayNumber - from.DayNumber).Select(from.AddDays)) ||
            days.Select(day => day.Slot).Distinct().Count() != 1 ||
            days.Any(day => day.Contract is null || day.Selected is null || day.Contract.NumeratorValue <= 0 ||
                day.Selected.NumeratorValue < 0 || day.Contract.Specification != day.Selected.Specification || day.Evidence.IsDefault) ||
            days.Select(day => day.Contract.Specification).Distinct().Count() != 1)
            throw PhysicalDeliveryQuantities.Invalid("An entry requires one physical source with complete, distinct, nonnegative native day quantities over its own dates.");
        return [..days.Select(day =>
        {
            var contract = new SignedRatio(day.Contract.NumeratorValue, day.Contract.DenominatorValue);
            var selected = new SignedRatio(day.Selected.NumeratorValue, day.Selected.DenominatorValue);
            var taken = counted?.Invoke(day.Day) == false ? SignedRatio.Zero : part?.Take(selected, baseQuantity) ?? selected;
            return new EntryDayQuantity(day.Day, contract, selected, taken, day.Source, day.Evidence);
        })];
    }

    internal static ImmutableArray<ImmutableArray<EntryDayQuantity>> ForMembers<TMember>(
        ImmutableArray<TMember> members, PhysicalCashState state, Func<int, DateOnly, bool>? counted = null) where TMember : IRetainedProgramMember
    {
        var owners = state.OwnerDays.ToLookup(d => d.Slot);
        var basis = new SignedRatio(System.Numerics.BigInteger.Abs(state.Quantity.Amount.ScaledValue), ExactDecimal.WorkingScale);
        return [..members.Select((member, slot) =>
        {
            if(member.ValuationSourceSlot is not {} source || !owners.Contains(source)) return default(ImmutableArray<EntryDayQuantity>);
            var rows = member.Program.Document.Instructions.Where(i => i.Output.Operation == "output_guard").Select(i => i.Output).ToArray();
            var from = rows.Min(row => row.Grid is {} grid ? member.Program.Document.Grids[grid].Interval(0).OperatingDate : row.DeliveryFrom!.Value);
            var to = rows.Max(row => row.Grid is {} grid ? member.Program.Document.Grids[grid].Interval(member.Program.Document.Grids[grid].CellCount - 1).OperatingDate.AddDays(1) : row.DeliveryTo!.Value);
            return EntryQuantities.For(owners[source], member.ValuationPart, basis, from, to, counted is null ? null : day => counted(slot, day));
        })];
    }
}
