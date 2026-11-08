using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

[EconomicDefinition("retained_gas_member_v1")]
public sealed record RetainedGasMember : IRetainedProgramMember
{
    public Guid SideId { get; }
    public BookedSideProgram Program { get; }
    [IdentityField] public ExactDecimal Direction { get; }
    [IdentityField] public ExactRatio Ratio { get; }
    [IdentityField] public string ProgramDigest { get; }
    ExactDecimal IRetainedProgramMember.ValuationCoefficient => Direction;
    ExactRatio? IRetainedProgramMember.ValuationRatio => Ratio;

    public RetainedGasMember(Guid sideId, ExactDecimal direction, ExactRatio ratio, BookedSideProgram program)
    {
        if (sideId == Guid.Empty || program is null || ratio is null || ratio.Numerator <= 0 ||
            (direction != ExactDecimal.Parse("1") && direction != ExactDecimal.Parse("-1")))
            throw new MarketDataException("gas_membership", "Retained gas members require a program, positive quantity ratio and explicit direction.");
        SideId = sideId; Direction = direction; Ratio = ratio; Program = program;
        ProgramDigest = BookedSideProgram.Digest(program.Write());
    }
}

// Booked programs supply economics; the common PV/factor contracts supply risk.
[EconomicDefinition("retained_gas_instrument_v1")]
public sealed record RetainedGasInstrument : RetainedProgramInstrument<RetainedGasMember>
{
    [IdentityField] public override ImmutableArray<RetainedGasMember> Members { get; }
    [IdentityField] public ImmutableArray<GasValuationAssignment> Assignments { get; }
    public ImmutableArray<IndexPriceLookup> PriceLookups { get; }
    public ImmutableArray<SchedulePriceTerm> ActivePrices => [..PriceLookups.Select(lookup => new SchedulePriceTerm(ExactDecimal.Parse("1"), null, lookup))];
    public override ReferenceKey Currency => Assignments[0].Quote.Currency;
    public override Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Assignments[0].Quote.Quantity);

    public RetainedGasInstrument(Guid id, ImmutableArray<RetainedGasMember> members,
        ImmutableArray<GasValuationAssignment> assignments, ImmutableArray<IndexPriceLookup> priceLookups=default) : base(id)
    {
        if (members.IsDefaultOrEmpty || members.Any(m => m is null) || assignments.IsDefaultOrEmpty || assignments.Any(a => a is null) ||
            !members.Any(m => m.Program.Document.Family == "gas") ||
            members.Any(m => m.Program.Document.Family is not ("gas" or "fixed" or "floating")) ||
            assignments.Select(a => (a.Quote.Currency, a.Quote.Quantity)).Distinct().Count() != 1)
            throw new MarketDataException("gas_membership", "Select complete retained physical, fixed or indexed cash programs with their exact compatible assignments.");
        if(priceLookups.IsDefault&&members.Any(m=>m.Program.Document.Instructions.Any(i=>i.Output.Operation==CurvePriceScheduleSource.Operation)))
            throw new MarketDataException("side_market_sources","Bound programs require their exact authorized retained price lookups.");
        Members = members; Assignments = assignments.Distinct().ToImmutableArray();
        PriceLookups=priceLookups.IsDefault?[..Assignments.Select(a=>a.Lookup).Distinct()]:priceLookups.Distinct().ToImmutableArray();
    }

    // Started-delivery state selects the lifecycle measure, so it is part of the declared model version.
    public override UnitPricingContract Declare(UnitValuationContext context) => new(Id,
        context.BindingState is PhysicalCashState ? PhysicalCashState.Policy + "/retained_gas_unit_pv_v1" : "retained_gas_unit_pv_v1",
        [..PriceLookups.Select(lookup => PeriodPriceSelector.ProjectionAt(lookup, context.Snapshot)).OfType<CurveRequest>().Distinct()],
        UnitQuantity.Specification, Currency) { ScheduleIdentity = RetainedScheduleIdentity };

    // Remaining delivery MTM plus outstanding settlement, in the held binding's cash terms.
    public PhysicalLifecycleValue EvaluateLifecycle(UnitValuationContext context)
    {
        if (context?.BindingState is not PhysicalCashState state)
            throw new MarketDataException(PhysicalLifecycle.ContextRefusal, "Lifecycle valuation requires the held binding's explicit physical cash state.");
        ValidateValuationState(state);
        var evaluations = EvaluateMembers(context);
        return PhysicalLifecycle.Evaluate([..Members.Select((member, n) => new LifecycleMember(member.Program, evaluations[n],
            member.Program.Document.Family == "gas", value => Signed(member, value).Scale(state.Quantity.Amount)))], state, context.Snapshot, Currency);
    }

    public override ExactMoney Value(UnitValuationContext context)
    {
        if (context.BindingState is not PhysicalCashState state)
        {
            PhysicalLifecycle.RequireUnstarted(Members.Select(m => m.Program), context.Snapshot.ValuationAt);
            return base.Value(context);
        }
        var held = state.Quantity.Amount; var negative = held.CompareTo(default) < 0;
        var unit = EvaluateLifecycle(context).Total.DivideBy(negative ? held * ExactDecimal.Parse("-1") : held);
        return negative ? unit.Scale(ExactDecimal.Parse("-1")) : unit;
    }

    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if (state is not PhysicalCashState cash) { base.ValidateValuationState(state); return; }
        if (cash.Quantity.Specification != UnitQuantity.Specification || cash.Payments.Any(p => p.Slot >= Members.Length ||
                Members[p.Slot].Program.Document.Family == "gas" || p.Amount.Currency != Currency ||
                !Members[p.Slot].Program.Document.Instructions.Any(i => i.Output is { Operation: "output_guard", Grid: null } && i.Output.PaymentOrdinal == p.Occurrence)))
            throw new MarketDataException("payment_state", "Each payment allocation must name a retained cash occurrence of this instrument in its currency and quantity.");
    }

    protected override ScheduleMarketSources Sources(CurveBuild basis) => new(basis.Graph.Plan.Context, basis, null, null,
        IntervalKnowledgePolicy.ResolvedCutoff, PendingActualsPolicy.RequireActuals,
        PriceLookups, [], Assignments);
}
