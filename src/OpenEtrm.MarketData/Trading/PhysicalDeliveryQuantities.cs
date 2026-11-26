using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;
using OpenEtrm.MarketData.Physical;

namespace OpenEtrm.MarketData.Trading;

public sealed record DeliveryQuantityEvidence(string Source,Guid RevisionId,string Reference);
public sealed record HeldDeliveryRecord(string Kind, string Status, ExactQuantity Quantity, DeliveryQuantityEvidence Evidence);
public sealed record HeldDayQuantity(int Slot,DateOnly Day,ExactQuantity Contract,ExactQuantity Selected,string Source,
    ImmutableArray<DeliveryQuantityEvidence> Evidence)
{
    internal ImmutableArray<HeldDeliveryRecord> OperationalRecords { get; init; } = [];
    // The bounded swing policy requires an election even for a nomination or confirmation that has no valuation rank.
    public bool HasOperationalRecord { get; init; }
    // A contract-only owner can have a scheduled or allocated record outside its valuation policy.
    public bool HasContractDeliveryRecord { get; init; }
}

// Held state contains physical-owner facts only. Membership policy and source
// relationships are immutable instrument terms, never copied into this state.
internal static class PhysicalDeliveryQuantities
{
    internal static void RequireFacts(ImmutableArray<HeldDayQuantity> quantities, ReferenceKey unit)
    {
        if(quantities.IsDefault || quantities.Any(q => q is null || q.Slot < 0 || q.Day == default ||
            q.Contract is null || q.Selected is null || q.Contract.Specification != unit || q.Selected.Specification != unit ||
            q.Contract.NumeratorValue <= 0 || q.Selected.NumeratorValue < 0 || q.Source is not(PhysicalLifecycle.ContractQuantity or
                PhysicalLifecycle.ScheduledQuantity or PhysicalLifecycle.AllocatedFinalQuantity or PhysicalLifecycle.AllocatedProvisionalQuantity) ||
            q.Evidence.IsDefault || q.Evidence.Any(e => e is null || string.IsNullOrWhiteSpace(e.Source) || e.RevisionId == Guid.Empty || string.IsNullOrWhiteSpace(e.Reference)) ||
            q.Evidence.Select(e => (e.Source, e.RevisionId)).Distinct().Count() != q.Evidence.Length ||
            (q.Source == PhysicalLifecycle.ContractQuantity ? !q.Evidence.IsEmpty || q.Contract != q.Selected : q.Evidence.IsEmpty)) ||
            quantities.Select(q => (q.Slot, q.Day)).Distinct().Count() != quantities.Length)
            throw Invalid("Physical owner days require distinct, nonnegative native quantities and complete evidence; contract fallback equals its contract.");
    }
    internal static void RequireSchedules<TMember>(ImmutableArray<TMember> members, PhysicalCashState state, ImmutableArray<ExerciseRight> rights = default,
        int? quantityRightSource = null) where TMember : IRetainedProgramMember
    {
        var rightSources = rights.IsDefaultOrEmpty ? [] : rights.SelectMany(right => right.Slots)
            .Select(slot => members[slot].ValuationSourceSlot ?? throw Invalid("An exercised entry requires its explicit physical quantity source.")).ToHashSet();
        if (rightSources.Any(source => members[source].ValuationSourceSlot != source))
            throw Invalid("An exercise quantity source requires its own retained physical day schedule.");
        var required = members.Select((m, slot) => (m, slot)).Where(p => quantityRightSource == p.slot || p.m.ValuationSourceSlot == p.slot &&
            (rightSources.Contains(p.slot) || p.m.ValuationQuantityPolicy is not null || members.Any(m => m.ValuationSourceSlot == p.slot &&
                (m.ValuationPart is not null || m.ValuationBalance is not null)))).ToArray();
        if(!required.Select(p => p.slot).ToHashSet().SetEquals(state.OwnerDays.Select(d => d.Slot)))
            throw Invalid("Held quantities cover exactly the physical owners required by the instrument's quantity terms.");
        foreach(var (member, slot) in required)
        {
            var ratio = member.ValuationRatio ?? ExactRatio.One;
            var scale = new ExactRatio(System.Numerics.BigInteger.Abs(state.Quantity.Amount.ScaledValue) * ratio.Numerator,
                ExactDecimal.WorkingScale * ratio.Denominator);
            var expected = PhysicalDeliverySchedule.ReadLiteralQuantities(member.Program, scale);
            var actual = state.OwnerDays.Where(d => d.Slot == slot).ToDictionary(d => d.Day);
            if(expected.Length != actual.Count || expected.Select(d => d.OperatingDate).Distinct().Count() != expected.Length ||
                expected.Any(d => !actual.TryGetValue(d.OperatingDate, out var q) || q.Contract != d.Quantity) ||
                quantityRightSource != slot && member.ValuationQuantityPolicy is null && actual.Values.Any(d => d.Source != PhysicalLifecycle.ContractQuantity))
                throw Invalid("Owner days must match the complete booked contract; a contract-policy source has only contract-labelled quantities.");
        }
    }
    internal static MarketDataException Invalid(string message) => new("physical_lifecycle_quantity", message);
}

// Ratios over the original contractual quantity, shared by lifecycle value and its schedule projection.
internal readonly record struct DeliveryQuantityFractions(SignedRatio Total,SignedRatio Ended)
{
    internal static DeliveryQuantityFractions EqualDays(int ended,int total) => new(new(1,1),new(ended,total));

    internal static DeliveryQuantityFractions For(IEnumerable<EntryDayQuantity> days,Func<DateOnly,bool> ended)
    {
        var contract=SignedRatio.Zero;var selected=SignedRatio.Zero;var accrued=SignedRatio.Zero;
        foreach(var day in days)
        {
            contract+=day.Contract;
            var quantity=day.Taken;
            selected+=quantity;if(ended(day.Day))accrued+=quantity;
        }
        if(contract.Numerator<=0)throw PhysicalDeliveryQuantities.Invalid("Quantity scaling requires complete positive contractual coverage.");
        return new(Ratio(selected,contract),Ratio(accrued,contract));
    }
    internal static SignedRatio Ratio(SignedRatio selected,SignedRatio contract)
    {
        if(contract.Numerator.IsZero)throw PhysicalDeliveryQuantities.Invalid("Quantity scaling requires nonzero contractual coverage.");
        return selected*new SignedRatio(contract.Numerator.Sign*contract.Denominator,System.Numerics.BigInteger.Abs(contract.Numerator));
    }
}
