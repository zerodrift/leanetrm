using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;
using OpenEtrm.MarketData.Physical;

namespace OpenEtrm.MarketData.Trading;

public sealed record DeliveryQuantityEvidence(string Source,Guid RevisionId,string Reference);
public sealed record HeldDayQuantity(int Slot,DateOnly Day,ExactQuantity Contract,ExactQuantity Selected,string Source,
    ImmutableArray<DeliveryQuantityEvidence> Evidence);

// Saved membership terms, independently of whether any operational record was found.
// A physical membership names itself; cash names its physical source and inherits its policy.
public sealed record HeldQuantityMembership(int Slot,int SourceSlot,string? Policy);

internal sealed class PhysicalDeliveryQuantities
{
    internal ImmutableArray<HeldDayQuantity> Days { get; }
    internal ImmutableArray<HeldQuantityMembership> Memberships { get; }
    readonly ImmutableDictionary<(int Slot,DateOnly Day),HeldDayQuantity> days;
    readonly ImmutableHashSet<int> followed;

    internal PhysicalDeliveryQuantities(ImmutableArray<HeldDayQuantity> quantities,ImmutableArray<HeldQuantityMembership> memberships,
        ReferenceKey unit,string policy)
    {
        if(policy!=PhysicalLifecycle.SelectedQuantityPolicy||quantities.IsDefaultOrEmpty||memberships.IsDefaultOrEmpty||
            memberships.Any(m=>m is null||m.Slot<0||m.SourceSlot<0||m.Policy is not(null or PhysicalLifecycle.SelectedQuantityPolicy))||
            !memberships.Select(m=>m.Slot).Order().SequenceEqual(Enumerable.Range(0,memberships.Length)))
            throw Invalid("Select the complete saved membership quantity terms and their labelled day quantities.");
        var terms=memberships.ToImmutableDictionary(m=>m.Slot);
        if(memberships.Any(m=>!terms.TryGetValue(m.SourceSlot,out var source)||source.SourceSlot!=source.Slot||m.SourceSlot!=m.Slot&&m.Policy is not null))
            throw Invalid("Each cash membership must name a physical quantity source and inherit its saved policy.");
        followed=memberships.Where(m=>terms[m.SourceSlot].Policy==PhysicalLifecycle.SelectedQuantityPolicy).Select(m=>m.Slot).ToImmutableHashSet();
        if(followed.IsEmpty||quantities.Any(q=>q is null||!followed.Contains(q.Slot)||q.Day==default||
            q.Contract is null||q.Selected is null||q.Contract.Specification!=unit||q.Selected.Specification!=unit||
            q.Contract.NumeratorValue<=0||q.Selected.NumeratorValue<0||q.Source is not(PhysicalLifecycle.ContractQuantity or PhysicalLifecycle.ScheduledQuantity or
                PhysicalLifecycle.AllocatedFinalQuantity or PhysicalLifecycle.AllocatedProvisionalQuantity)||q.Evidence.IsDefault||
            q.Evidence.Any(e=>e is null||string.IsNullOrWhiteSpace(e.Source)||e.RevisionId==Guid.Empty||string.IsNullOrWhiteSpace(e.Reference))||
            q.Evidence.Select(e=>(e.Source,e.RevisionId)).Distinct().Count()!=q.Evidence.Length||
            (q.Source==PhysicalLifecycle.ContractQuantity? !q.Evidence.IsEmpty||q.Contract!=q.Selected : q.Evidence.IsEmpty))||
            quantities.Select(q=>(q.Slot,q.Day)).Distinct().Count()!=quantities.Length||
            !followed.SetEquals(quantities.Select(q=>q.Slot)))
            throw Invalid("Every followed membership requires distinct, nonnegative native day quantities and complete source evidence; contract fallback must equal its contract.");
        days=quantities.ToImmutableDictionary(q=>(q.Slot,q.Day));
        foreach(var membership in memberships.Where(m=>followed.Contains(m.Slot)&&m.SourceSlot!=m.Slot))
        {
            var source=quantities.Where(q=>q.Slot==membership.SourceSlot).ToArray();
            if(quantities.Count(q=>q.Slot==membership.Slot)!=source.Length||source.Any(s=>
                !days.TryGetValue((membership.Slot,s.Day),out var q)||q.Contract!=s.Contract||q.Selected!=s.Selected||
                q.Source!=s.Source||!q.Evidence.SequenceEqual(s.Evidence)))
                throw Invalid("Each followed cash membership must carry exactly its physical source's days, quantities and evidence.");
        }
        Days=quantities;Memberships=memberships;
    }
    internal bool Follows(int slot)=>followed.Contains(slot);
    internal HeldDayQuantity Day(int slot,DateOnly day)=>days.TryGetValue((slot,day),out var value)?value:
        throw Invalid("A followed membership is missing its selected quantity for a retained delivery day.");
    internal void ValidateSchedules(IReadOnlyList<(BookedSideProgram Program,bool Physical,ExactRatio Scale)> programs)
    {
        if(programs.Count!=Memberships.Length)throw Invalid("Quantity terms must cover every retained membership, including cash sources.");
        foreach(var member in Memberships)
        {
            var program=programs[member.Slot];
            if(program.Physical!=(member.Slot==member.SourceSlot)||!programs[member.SourceSlot].Physical)
                throw Invalid("The quantity source must be a retained physical membership of this composition.");
            if(!Follows(member.Slot))continue;
            if(program.Physical)
            {
                var expected=PhysicalDeliverySchedule.ReadLiteralQuantities(program.Program,program.Scale);
                if(expected.Length!=Days.Count(d=>d.Slot==member.Slot)||expected.Select(d=>d.OperatingDate).Distinct().Count()!=expected.Length||
                    expected.Any(d=>Day(member.Slot,d.OperatingDate).Contract!=d.Quantity))
                    throw Invalid("Selected day coverage and contractual quantities must match the complete retained physical schedule at its held scale.");
            }
            else
            {
                var covered=program.Program.Document.Instructions.Where(i=>i.Output.Operation=="output_guard").SelectMany(i=>
                    i.Output.DeliveryFrom is {} from&&i.Output.DeliveryTo is {} to?
                        Enumerable.Range(0,to.DayNumber-from.DayNumber).Select(from.AddDays):[]).ToHashSet();
                if(!covered.SetEquals(Days.Where(d=>d.Slot==member.Slot).Select(d=>d.Day)))
                    throw Invalid("A followed cash schedule must cover exactly the days of its retained physical source.");
            }
        }
    }
    internal static MarketDataException Invalid(string message)=>new("physical_lifecycle_quantity",message);
}

// Ratios over the original contractual quantity, shared by lifecycle value and its schedule projection.
internal readonly record struct DeliveryQuantityFractions(SignedRatio Total,SignedRatio Ended)
{
    internal static DeliveryQuantityFractions For(IEnumerable<HeldDayQuantity> days,Func<DateOnly,bool> ended)
        => For(days.Select(day => new EntryDayQuantity(day.Day,
            new(day.Contract.NumeratorValue, day.Contract.DenominatorValue),
            new(day.Selected.NumeratorValue, day.Selected.DenominatorValue),
            new(day.Selected.NumeratorValue, day.Selected.DenominatorValue), day.Source, day.Evidence)), ended);

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
