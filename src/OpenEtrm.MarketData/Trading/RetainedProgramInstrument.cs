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
    public virtual ImmutableArray<ExerciseRight> Rights => [];
    public virtual ExerciseMarks? Marks => null;
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
    // The direct read route prepares the same Side occurrence inputs as risk.
    // It exposes a context, never a second evaluation or a program executor.
    public SideValuationContext PrepareValuation(CurveBuild basis, CurveBuild scenario, ISideDiscounting discounting,
        IBindingValuationState? state = null, CancellationToken cancellation = default)
    {
        if(basis is null || scenario is null || !basis.Snapshot.HasSameInputs(scenario.Snapshot))
            throw new MarketDataException("native_program_context", "Retain the exact base and scenario source snapshot.");
        ValidateValuationState(state);
        var context = new UnitValuationContext(scenario.Snapshot,
            new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid,FuturesMarketBinding>.Empty, discounting),
            cancellation: cancellation, bindingState: state);
        return new SideValuationContext(scenario, discounting).WithBindingState(state)
            .WithUnitEvaluation(scenario.Snapshot.ValuationAt, false, ValuationMeasure.PresentValue, cancellation)
            .WithRetainedPrograms(PrepareInputs(context, basis, scenario));
    }
    private RetainedProgramContext PrepareInputs(UnitValuationContext context, CurveBuild basis, CurveBuild scenario)
    {
        var prepared = PrepareSides(context, basis, scenario);
        return prepared.Cash is null ? prepared : prepared with { Owner = new(Id, EconomicIdentity) };
    }
    protected InstrumentValuation EvaluateSides(UnitValuationContext context)
    {
        RequireRetainedSides();
        if(context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "This retained instrument implements contractual present value only.");
        var (basis, scenario) = context.RetainedProgramMarkets(Declare(context).Dependencies);
        var prepared = PrepareInputs(context, basis, scenario);
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
        if((hasQuantityTerms || !retained.Rights.IsEmpty) && context.BindingState is not PhysicalCashState)
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
            var rights = retained.Rights;
            var decisions = rights.IsEmpty && state.Elections.IsEmpty ? null :
                ExerciseDecisions.Resolve(rights, retained.Members, state, context.Snapshot, context.UnitEvaluation.CalculationAt);
            RetainedProgramValuation EvaluateOccurrence(SideValuationContext parent, int slot,
                ImmutableArray<ImmutableArray<EntryDayQuantity>> days)
            {
                var side = (RetainedProgramSide)retained.Sides[slot].Side;
                return (RetainedProgramValuation)parent.ForRetainedOccurrence(slot, side, days,
                    retained.Members[slot].ValuationPart).Evaluate(side);
            }

            ImmutableArray<ExerciseGroupValue> groups = [];
            if (decisions is not null)
            {
                var forwardDays = EntryQuantities.ForMembers(retained.Members, state);
                var exercises = new List<RetainedExerciseValuation>(rights.Length);
                foreach (var right in rights)
                {
                    var exercise = new RetainedExerciseValuation(right, decisions);
                    var allExercised = right.Groups.All(group => decisions.Outcome(right, group) == ExerciseElection.Exercised);
                    var parent = allExercised ? context : context.ExercisedOccurrences();
                    foreach (var slot in right.Slots)
                    {
                        var forward = EvaluateOccurrence(parent, slot, forwardDays);
                        exercise.Add(forward, forwardDays[slot], inputs.HeldFactors[slot] * new SignedRatio(quantity.Numerator.Sign, 1));
                    }
                    exercise.DecideAutomatic();
                    exercises.Add(exercise);
                }
                decisions.RequireCompatiblePayments(retained.Members, state, context.Snapshot, ExerciseSettlement.CashAutomatic);
                groups = [..exercises.SelectMany(exercise => exercise.Values(context, retained.Marks, retained.Currency, quantity.Numerator.Sign))];
            }
            var entryDays = hasQuantityTerms || !rights.IsEmpty ? EntryQuantities.ForMembers(retained.Members, state,
                decisions is null ? null : decisions.Counted) : default;
            ImmutableArray<SideValuation> sides = [..retained.Sides.Select((_, slot) => EvaluateOccurrence(context, slot, entryDays))];
            var value = new InstrumentValuation(retained, context, sides, groups);
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
}
