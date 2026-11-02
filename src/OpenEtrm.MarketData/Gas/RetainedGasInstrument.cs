using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

[EconomicDefinition("retained_gas_member_v1")]
public sealed record RetainedGasMember
{
    public Guid SideId { get; }
    public BookedSideProgram Program { get; }
    [IdentityField] public ExactDecimal Direction { get; }
    [IdentityField] public ExactRatio Ratio { get; }
    [IdentityField] public string ProgramDigest { get; }

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
public sealed record RetainedGasInstrument : Instrument
{
    [IdentityField] public ImmutableArray<RetainedGasMember> Members { get; }
    [IdentityField] public ImmutableArray<GasValuationAssignment> Assignments { get; }
    public ImmutableArray<SchedulePriceTerm> ActivePrices => [..Assignments.Select(a => new SchedulePriceTerm(ExactDecimal.Parse("1"), null, a.Lookup)).Distinct()];
    public override ReferenceKey Currency => Assignments[0].Quote.Currency;
    public override Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Assignments[0].Quote.Quantity);
    public override bool SupportsQuantityScaling => true;
    internal override bool RequiresNativeProgramMarket => true;
    public override string RetainedScheduleIdentity => "retained_side_programs_v1/" + string.Join('/', Members.Select(m => m.ProgramDigest));

    public RetainedGasInstrument(Guid id, ImmutableArray<RetainedGasMember> members,
        ImmutableArray<GasValuationAssignment> assignments) : base(id)
    {
        if (members.IsDefaultOrEmpty || members.Any(m => m is null) || assignments.IsDefaultOrEmpty || assignments.Any(a => a is null) ||
            !members.Any(m => m.Program.Document.Family == "gas") ||
            members.Any(m => m.Program.Document.Family is not ("gas" or "fixed")) ||
            assignments.Select(a => (a.Quote.Currency, a.Quote.Quantity)).Distinct().Count() != 1)
            throw new MarketDataException("gas_membership", "Select complete retained gas and fixed cash programs with their exact compatible assignments.");
        Members = members; Assignments = assignments.Distinct().ToImmutableArray();
    }

    public override UnitPricingContract Declare(UnitValuationContext context) => new(Id, "retained_gas_unit_pv_v1",
        [..Assignments.Select(a => PeriodPriceSelector.ProjectionAt(a.Lookup, context.Snapshot)).OfType<CurveRequest>().Distinct()],
        UnitQuantity.Specification, Currency) { ScheduleIdentity = RetainedScheduleIdentity };

    public override ExactMoney Value(UnitValuationContext context)
    {
        if (context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "Retained gas implements contractual present value only.");
        var (basis, scenario) = context.RetainedProgramMarkets(Declare(context).Dependencies);
        var sources = new ScheduleMarketSources(basis.Graph.Plan.Context, basis, null, null,
            IntervalKnowledgePolicy.ResolvedCutoff, PendingActualsPolicy.RequireActuals,
            [..Assignments.Select(a => a.Lookup).Distinct()], [], Assignments);
        var total = ExactMoney.FromMoney(new(default, Currency));
        foreach (var member in Members)
        {
            var value = sources.Evaluate(member.Program, member.SideId, scenario, context.DiscountProvider, context.Cancellation);
            total = total.Add(value.PresentValue.Scale(member.Ratio).Scale(member.Direction));
        }
        return total;
    }
}
