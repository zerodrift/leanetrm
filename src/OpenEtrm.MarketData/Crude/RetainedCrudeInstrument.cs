using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Crude;

[EconomicDefinition("retained_crude_member_v1")]
public sealed record RetainedCrudeMember
{
    public Guid SideId { get; }
    public BookedSideProgram Program { get; }
    [IdentityField] public ExactDecimal Coefficient { get; }
    [IdentityField] public string ProgramDigest { get; }

    public RetainedCrudeMember(Guid sideId, ExactDecimal coefficient, BookedSideProgram program)
    {
        if (sideId == Guid.Empty || program is null ||
            (coefficient != ExactDecimal.Parse("1") && coefficient != ExactDecimal.Parse("-1")))
            throw new MarketDataException("crude_membership", "Retained crude memberships require an exact side program and explicit unit direction.");
        SideId = sideId;
        Coefficient = coefficient;
        Program = program;
        ProgramDigest = BookedSideProgram.Digest(program.Write());
    }
}

// A booked execution projection: no Side definition or schedule generator is used.
[EconomicDefinition("retained_crude_instrument_v1")]
public sealed record RetainedCrudeInstrument : Instrument
{
    [IdentityField] public ImmutableArray<RetainedCrudeMember> Members { get; }
    [IdentityField] public CrudeValuationAssignment Assignment { get; }
    public ImmutableArray<SchedulePriceTerm> ActivePrices => [new(ExactDecimal.Parse("1"), null, Assignment.Lookup)];
    public override ReferenceKey Currency => Assignment.Quote.Currency;
    public override Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Assignment.Quantity.Key);
    public override bool SupportsQuantityScaling => true;
    internal override bool RequiresNativeProgramMarket => true;
    public override string RetainedScheduleIdentity => "retained_side_programs_v1/" + string.Join('/', Members.Select(m => m.ProgramDigest));
    public override DefinitionIdentity PricingBindingIdentity => DefinitionIdentity.For(Assignment);

    public RetainedCrudeInstrument(Guid id, CrudeValuationAssignment assignment, ImmutableArray<RetainedCrudeMember> members) : base(id)
    {
        if (assignment is null || members.IsDefault || members.Length is < 2 or > 3 || members.Any(m => m is null) ||
            members[0].Program.Document.Family != "crude" ||
            members.Skip(1).Any(m => m.Program.Document.Family != "fixed"))
            throw new MarketDataException("crude_membership", "Select one retained crude delivery, fixed consideration and optional fixed handling fee.");
        Assignment = assignment;
        Members = members;
    }

    public override void ValidateBinding(Quantity quantity, OpenEtrm.MarketData.Power.DeliveryCoverage? coverage)
    {
        base.ValidateBinding(quantity, coverage);
        if (quantity is null || quantity.Specification != UnitQuantity.Specification || quantity.Amount.CompareTo(default) <= 0)
            throw new MarketDataException("crude_quantity", "Select a positive total in the exact retained standard-volume specification.");
    }

    public override UnitPricingContract Declare(UnitValuationContext context) => new(Id, "retained_crude_unit_pv_v1",
        PeriodPriceSelector.ProjectionAt(Assignment.Lookup, context.Snapshot) is {} market ? [market] : [],
        UnitQuantity.Specification, Currency) { ScheduleIdentity = RetainedScheduleIdentity };

    public override ExactMoney Value(UnitValuationContext context)
    {
        if (context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "Retained crude implements contractual present value only.");
        var (basis, scenario) = context.RetainedProgramMarkets(Declare(context).Dependencies);
        var sources = ScheduleMarketSources.ForPhysicalCrude(basis, [Assignment]);
        var total = ExactMoney.FromMoney(new(default, Currency));
        foreach (var member in Members)
        {
            var value = sources.Evaluate(member.Program, member.SideId, scenario, context.DiscountProvider, context.Cancellation);
            total = total.Add(value.PresentValue.Scale(member.Coefficient));
        }
        return total;
    }
}
