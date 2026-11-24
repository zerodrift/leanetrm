using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

public interface IRetainedProgramMember
{
    Guid SideId { get; }
    BookedSideProgram Program { get; }
    string ProgramDigest { get; }
    ExactDecimal ValuationCoefficient { get; }
    ExactRatio? ValuationRatio { get; }
    int? ValuationSourceSlot { get; }
    string? ValuationQuantityPolicy { get; }
    QuantityPart? ValuationPart { get; }
}

// Published Members remain the canonical contract. Sides are a checked,
// deterministic execution view of those same programs, in membership order.
internal interface IRetainedSideComposition
{
    void RequireRetainedSides();
}

public abstract record RetainedProgramInstrument<TMember> : SidedInstrument, IRetainedSideComposition where TMember : IRetainedProgramMember
{
    public abstract ImmutableArray<TMember> Members { get; }
    internal override bool SupportsSettlement => false;
    internal override bool RequiresNativeProgramMarket => true;
    public override string RetainedScheduleIdentity => "retained_side_programs_v1/" + string.Join('/', Members.Select(m => m.ProgramDigest));

    protected RetainedProgramInstrument(Guid id, ImmutableArray<TMember> members, Quantity unit, ReferenceKey currency)
        : base(id, CreateSides(members, unit, currency)) { }
    private static ImmutableArray<SideMembership> CreateSides(ImmutableArray<TMember> members, Quantity unit, ReferenceKey currency)
    {
        if(members.IsDefaultOrEmpty || members.Any(m => m is null || m.Program is null))
            throw new MarketDataException("retained_side_composition", "Retain the complete ordered program memberships.");
        var definitions = new Dictionary<Guid,RetainedProgramSide>();
        return [..members.Select(m =>
        {
            var selected = RetainedProgramSide.Define(m.SideId, m.Program, unit, currency);
            if(definitions.TryGetValue(m.SideId, out var previous))
            {
                if(DefinitionIdentity.For(previous) != DefinitionIdentity.For(selected) ||
                    !previous.Program.Write().AsSpan().SequenceEqual(selected.Program.Write()))
                    throw new MarketDataException("side_identity", "A reused Side identity must retain its exact program, unit, currency and calendar definition.");
                selected = previous;
            }
            else definitions.Add(m.SideId, selected);
            return new SideMembership(selected, m.ValuationCoefficient, m.ValuationRatio);
        })];
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RetainedProgramInstrument<TMember>,object> Proofs = new();
    public void RequireRetainedSides() => _ = Proofs.GetValue(this, static instrument =>
    {instrument.ValidateRetainedSides();return new object();});
    private void ValidateRetainedSides()
    {
        if(Sides.IsDefault || Sides.Length != Members.Length) throw Composition();
        for(var slot = 0; slot < Members.Length; slot++)
        {
            var member = Members[slot]; var actual = Sides[slot];
            if(actual?.Side is not RetainedProgramSide side || side.Id != member.SideId ||
                actual.Coefficient != member.ValuationCoefficient || actual.QuantityRatio != member.ValuationRatio ||
                side.ProgramDigest != member.ProgramDigest || side.Currency != Currency || side.UnitQuantity != UnitQuantity ||
                !ReferenceEquals(side.Program, member.Program) && !side.Program.Write().AsSpan().SequenceEqual(member.Program.Write()) ||
                DefinitionIdentity.For(side.CalendarPolicy) != DefinitionIdentity.For(RetainedSideCalendarPolicy.Read(member.Program)))
                throw Composition();
        }
    }
    private static MarketDataException Composition() => new("retained_side_composition", "Every retained Side must preserve its exact ordered native program, coefficient, ratio, unit and captured calendar policy.");
    protected UnitPricingContract RetainedDeclaration(UnitPricingContract declaration)
    {RequireRetainedSides();return declaration;}
    protected abstract ScheduleMarketSources Sources(CurveBuild basis);
    private protected virtual RetainedProgramContext PrepareSides(UnitValuationContext context, CurveBuild basis, CurveBuild scenario) => new(Sources(basis), scenario);
    protected InstrumentValuation EvaluateSides(UnitValuationContext context)
    {
        RequireRetainedSides();
        if(context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "This retained instrument implements contractual present value only.");
        var (basis, scenario) = context.RetainedProgramMarkets(Declare(context).Dependencies);
        var prepared = PrepareSides(context, basis, scenario);
        if(prepared.Cash is not null) prepared = prepared with { Owner = new(Id, EconomicIdentity) };
        return PV(context.SideContextRetained(prepared));
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RetainedProgramInstrument<TMember>, InstrumentSchedule> Schedules = new();
    public override InstrumentSchedule GenerateSchedule() => Schedules.GetValue(this, static instrument => new RetainedInstrumentSchedule<TMember>(instrument));
    public override ExactMoney Value(UnitValuationContext context) => EvaluateSides(context).PresentValue;
    internal override UnitPricingContract DeclareScope(UnitValuationContext context, bool lifecycle)
    {
        RequireRetainedSides();
        return Declare(context);
    }
    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if(state is PhysicalCashState)
            throw new MarketDataException("physical_lifecycle_unsupported", "This retained instrument has no started-delivery lifecycle policy.");
        if(state is not null)
            throw new MarketDataException("settlement_scope", "This retained instrument does not accept an unrelated settlement state.");
    }
}

internal class RetainedInstrumentSchedule<TMember> : InstrumentSchedule where TMember : IRetainedProgramMember
{
    private readonly RetainedProgramInstrument<TMember> retained;
    internal RetainedInstrumentSchedule(RetainedProgramInstrument<TMember> instrument) : base(instrument) => retained = instrument;
    public override InstrumentValuation Evaluate(SideValuationContext context)
    {
        retained.RequireBeforeValuation(context);
        retained.RequireRetainedSides();
        if(context is null) throw new MarketDataException("side_context", "Supply the exact retained Side valuation context.");
        var hasQuantityTerms = retained.Members.Any(m => m.ValuationQuantityPolicy is not null || m.ValuationPart is not null);
        if(hasQuantityTerms && context.BindingState is not PhysicalCashState)
            throw PhysicalDeliveryQuantities.Invalid("An instrument with quantity terms requires its complete held owner-day inputs.");
        if(context.BindingState is PhysicalCashState state)
        {
            if(context.RetainedPrograms is not { Geometry: not null, Cash: {} cash, Owner: {} owner } inputs ||
                !ReferenceEquals(cash, state) || owner.InstrumentId != retained.Id || owner.Definition != retained.EconomicIdentity ||
                inputs.Occurrence is not null || inputs.HeldFactors.IsDefault || inputs.HeldFactors.Length != retained.Members.Length)
                throw LifecycleContext();
            retained.ValidateValuationState(state);
            var quantity = new SignedRatio(state.Quantity.Amount.ScaledValue, ExactDecimal.WorkingScale);
            for(var slot = 0; slot < retained.Members.Length; slot++)
            {
                var member = retained.Members[slot];
                var factor = quantity * new SignedRatio(member.ValuationCoefficient.ScaledValue, ExactDecimal.WorkingScale) *
                    (member.ValuationRatio is {} ratio ? new SignedRatio(ratio.Numerator, ratio.Denominator) : new SignedRatio(1, 1));
                if(inputs.HeldFactors[slot] != factor) throw LifecycleContext();
            }
            var entryDays = hasQuantityTerms ? EntryDays(state) : default;
            var value = new InstrumentValuation(retained, context, [..retained.Sides.Select((member, slot) =>
                context.ForRetainedOccurrence(slot, (RetainedProgramSide)member.Side, entryDays).Evaluate(member.Side))]);
            retained.RequireAfterValuation(value);
            return value;
        }
        retained.ValidateValuationState(context.BindingState);
        if(context.RetainedPrograms is { Cash: not null } or { Occurrence: not null }) throw LifecycleContext();
        if(retained.Members.Any(m => m.Program.Document.Family == "gas"))
            PhysicalLifecycle.RequireUnstarted(retained.Members.Select(m => m.Program), context.Snapshot.ValuationAt);
        retained.RequireSideComposition();
        var result = new InstrumentValuation(retained, context, [..retained.Sides.Select(member => context.Evaluate(member.Side))]);
        retained.RequireAfterValuation(result);
        return result;
    }
    private static MarketDataException LifecycleContext() => new(PhysicalLifecycle.ContextRefusal,
        "Retain this instrument's exact held cash state, connected geometry and ordered membership normalization.");
    private ImmutableArray<ImmutableArray<EntryDayQuantity>> EntryDays(PhysicalCashState state)
    {
        PhysicalDeliveryQuantities.RequireSchedules(retained.Members, state);
        var owners = state.OwnerDays.ToLookup(d => d.Slot);
        var basis = new SignedRatio(System.Numerics.BigInteger.Abs(state.Quantity.Amount.ScaledValue), ExactDecimal.WorkingScale);
        return [..retained.Members.Select(member =>
        {
            if(member.ValuationSourceSlot is not {} source || !owners.Contains(source)) return default(ImmutableArray<EntryDayQuantity>);
            var rows = member.Program.Document.Instructions.Where(i => i.Output.Operation == "output_guard").Select(i => i.Output).ToArray();
            var from = rows.Min(row => row.Grid is {} grid ? member.Program.Document.Grids[grid].Interval(0).OperatingDate : row.DeliveryFrom!.Value);
            var to = rows.Max(row => row.Grid is {} grid ? member.Program.Document.Grids[grid].Interval(member.Program.Document.Grids[grid].CellCount - 1).OperatingDate.AddDays(1) : row.DeliveryTo!.Value);
            return EntryQuantities.For(owners[source], member.ValuationPart, basis, from, to);
        })];
    }
}
