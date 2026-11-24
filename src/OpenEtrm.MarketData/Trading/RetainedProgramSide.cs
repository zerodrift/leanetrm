using System.Collections.Immutable;
using OpenEtrm.Shared.Numerics;
using System.Text.Json;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// Admitted source values and connected physical geometry, never an Instrument
// or a resolver for another Side. Each Side executes only its own saved program.
internal sealed record RetainedProgramContext(ScheduleMarketSources Sources, CurveBuild Scenario)
{
    internal ImmutableArray<PhysicalLifecycleGeometry> Geometries { get; init; }
    internal PhysicalCashState? Cash { get; init; }
    internal RetainedProgramOwner? Owner { get; init; }
    internal RetainedProgramOccurrence? Occurrence { get; init; }
    internal ImmutableArray<SignedRatio> HeldFactors { get; init; } = [];
    internal ImmutableArray<ImmutableArray<EntryDayQuantity>> EntryDays { get; init; }
    internal bool CashAutomatic { get; init; }
    internal QuantityPart? EntryPart { get; init; }
}
internal sealed record RetainedProgramOwner(Guid InstrumentId, DefinitionIdentity Definition, string ScheduleIdentity);
internal sealed record RetainedProgramOccurrence(int Slot, Guid SideId, string ProgramDigest);
public sealed partial class SideValuationContext
{
    internal RetainedProgramContext? RetainedPrograms { get; private set; }
    private sealed class RetainedOccurrenceContexts(SideValuationContext owner)
    {
        internal SideValuationContext Owner { get; } = owner;
        internal Dictionary<int,SideValuationContext> Children { get; } = [];
        internal SideValuationContext? Exercised { get; set; }
    }
    private RetainedOccurrenceContexts? retainedOccurrences;
    private SideValuationContext? retainedOccurrenceParent;
    internal int RetainedOccurrenceCalculations => retainedOccurrences is {} scopes && ReferenceEquals(scopes.Owner, this)
        ? scopes.Children.Values.Sum(child => child.EvaluatedSides) + (scopes.Exercised?.EvaluatedSides ?? 0) : 0;
    public static SideValuationContext ForRetainedPrograms(CurveBuild basis,CurveBuild scenario,ISideDiscounting discounting,CancellationToken cancellation=default)
    {
        if(basis is null || scenario is null || !basis.Snapshot.HasSameInputs(scenario.Snapshot))
            throw new MarketDataException("native_program_context", "Retain the exact base and scenario source snapshot.");
        return new SideValuationContext(scenario,discounting)
            .WithUnitEvaluation(scenario.Snapshot.ValuationAt,false,ValuationMeasure.PresentValue,cancellation)
            .WithRetainedPrograms(new(ScheduleMarketSources.ForOperating(basis),scenario));
    }

    internal SideValuationContext WithRetainedPrograms(RetainedProgramContext inputs)
    {
        var copy=(SideValuationContext)MemberwiseClone();
        copy.RetainedPrograms=inputs;
        copy.sideValues=new(ReferenceEqualityComparer.Instance);
        copy.retainedOccurrences=null;
        copy.retainedOccurrenceParent=null;
        return copy;
    }
    private RetainedOccurrenceContexts Occurrences()
    {
        if (retainedOccurrences is null || !ReferenceEquals(retainedOccurrences.Owner, this)) retainedOccurrences = new(this);
        return retainedOccurrences;
    }

    internal SideValuationContext ExercisedOccurrences()
    {
        var scopes = Occurrences();
        if (scopes.Exercised is not null) return scopes.Exercised;
        var inputs = RetainedPrograms!;
        // A hypothetical exercise selects gross obligations. Recorded cash belongs only to the ordinary occurrences.
        if (inputs.Cash is { Payments.IsEmpty: false } cash)
        {
            var gross = new PhysicalCashState(cash.TradeId, cash.BindingId, cash.Quantity, cash.BookId, [],
                PhysicalCashState.Policy, cash.OwnerDays, cash.Elections, cash.NextElectionEffectiveAt);
            return scopes.Exercised = WithRetainedPrograms(inputs with { Cash = gross }).WithBindingState(gross);
        }
        return scopes.Exercised = WithRetainedPrograms(inputs);
    }

    internal SideValuationContext ForRetainedOccurrence(int slot, RetainedProgramSide side, ImmutableArray<ImmutableArray<EntryDayQuantity>> entryDays = default, QuantityPart? part = null, bool cashAutomatic = false)
    {
        if(RetainedPrograms is not { Cash: not null, Owner: not null, Occurrence: null } inputs ||
            inputs.HeldFactors.IsDefault || slot < 0 || slot >= inputs.HeldFactors.Length)
            throw new MarketDataException(PhysicalLifecycle.ContextRefusal, "Select the exact prepared retained membership occurrence.");
        var scopes = Occurrences();
        if(!scopes.Children.TryGetValue(slot, out var child))
        {
            child=WithRetainedPrograms(inputs with { Occurrence=new(slot, side.Id, side.ProgramDigest), EntryDays=entryDays, EntryPart=part, CashAutomatic=cashAutomatic });
            child.retainedOccurrenceParent=this;
            scopes.Children.Add(slot, child);
        }
        if(child.RetainedPrograms!.Occurrence != new RetainedProgramOccurrence(slot, side.Id, side.ProgramDigest) || child.RetainedPrograms.EntryPart != part || child.RetainedPrograms.CashAutomatic != cashAutomatic)
            throw new MarketDataException(PhysicalLifecycle.ContextRefusal, "A retained occurrence cannot change its exact Side program.");
        var previous = child.RetainedPrograms.EntryDays;
        if(previous.IsDefault != entryDays.IsDefault || !previous.IsDefault &&
            (previous.Length != entryDays.Length || previous[slot].IsDefault != entryDays[slot].IsDefault ||
                !previous[slot].IsDefault && !previous[slot].SequenceEqual(entryDays[slot])))
            throw PhysicalDeliveryQuantities.Invalid("A retained occurrence cannot change its selected entry quantities.");
        return child;
    }
    internal bool IsRegisteredRetainedOccurrence(RetainedProgramSide side) =>
        RetainedPrograms?.Occurrence is {} occurrence && retainedOccurrenceParent?.OwnsRetainedOccurrence(this, occurrence.Slot, side) == true;
    internal bool OwnsRetainedOccurrence(SideValuationContext child, int slot, Side side) =>
        retainedOccurrences is {} scopes && ReferenceEquals(scopes.Owner, this) &&
        scopes.Children.TryGetValue(slot, out var selected) && ReferenceEquals(selected, child) &&
        side is RetainedProgramSide retained && child.RetainedPrograms?.Occurrence == new RetainedProgramOccurrence(slot, side.Id, retained.ProgramDigest);
}

[EconomicDefinition("retained_program_side_v1")]
public sealed record RetainedProgramSide : Side
{
    public BookedSideProgram Program { get; }
    [IdentityField] public string ProgramDigest { get; }
    [IdentityField] public ReferenceKey Currency { get; }
    [IdentityField] public Quantity UnitQuantity { get; }
    public override ImmutableArray<CurveRequest> Dependencies => [..Program.Document.Instructions.SelectMany(i => i.Bindings)
        .Where(b => b.Market is not null).Select(b => b.Market!.Value).Distinct()];
    // Structure-only construction uses the same definition as valued retained
    // Instruments. Authority and executable source inputs stay with the caller.
    public static RetainedProgramSide Define(Guid sideId,BookedSideProgram program,Quantity normalizedUnit,ReferenceKey currency)
    {
        if(program is null)throw new MarketDataException("retained_side_composition","Retain an exact admitted Side program.");
        return new(sideId,program,normalizedUnit,currency);
    }
    public static RetainedProgramSide ForOperating(Guid sideId,BookedSideProgram program,Quantity unit,ReferenceKey currency)
    {
        if(program is null || program.Document.Family != "operating" || unit is null ||
            program.Document.Instructions.Any(i => i.Output.Currency is {} selected && selected != currency))
            throw new MarketDataException("retained_side", "Retain the admitted operating program and its exact unit and currency.");
        try
        {
            using var economic = JsonDocument.Parse(program.Document.EconomicIdentity);
            if(!economic.RootElement.TryGetProperty(nameof(FixedSide.OperatingDefinition), out var terms) ||
                terms.ValueKind != JsonValueKind.Object || !terms.TryGetProperty(nameof(OperatingSideTerms.UnitQuantity), out var declared) ||
                declared.GetRawText() != DefinitionIdentity.CanonicalTerm(unit))
                throw new MarketDataException("retained_side", "The operating Side requires the exact complete unit sealed in its program.");
        }
        catch(JsonException error)
        { throw new MarketDataException("retained_side", "Retain the operating program's exact sealed economic terms.", error); }
        return Define(sideId,program,unit,currency);
    }
    private RetainedProgramSide(Guid id, BookedSideProgram program, Quantity unit, ReferenceKey currency)
        : base(id, RetainedSideCalendarPolicy.Read(program))
    {
        if(unit is null || unit.Amount != ExactDecimal.Parse("1"))
            throw new MarketDataException("retained_side_composition", "A retained Side requires its exact normalized unit.");
        Program = program; ProgramDigest = BookedSideProgram.Digest(program.Write());
        UnitQuantity = unit; Currency = currency;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<RetainedProgramSide,RetainedProgramSchedule> Schedules = new();
    public override RetainedProgramSchedule GenerateSchedule() => Schedules.GetValue(this, static side => new(side));
    public override RetainedProgramValuation PV(SideValuationContext context) => GenerateSchedule().Evaluate(context);
}

public sealed record RetainedProgramSchedule : SideSchedule
{
    public RetainedProgramSide Side { get; }
    public BookedSideProgram Program => Side.Program;
    public override ImmutableArray<CurveRequest> Dependencies => Side.Dependencies;
    private readonly Lazy<IReadOnlyList<ScheduleRow>> rows;
    public override IReadOnlyList<ScheduleRow> Rows => rows.Value;
    internal RetainedProgramSchedule(RetainedProgramSide side)
        : base(side.Id, side.Currency, side.UnitQuantity, "retained_program_side_execution_v1")
    {
        Side = side;
        // Exact output addresses are views of the stored program, not a newly
        // generated schedule. A grid contribution retains every native cell.
        rows = new(() => [..Program.Document.Instructions.Where(i => i.Output.Operation == "output_guard").SelectMany(i =>
            Enumerable.Range(0, i.Output.Grid is {} grid ? Program.Document.Grids[grid].CellCount : 1)
                .Select(cell => new ScheduleRow(new RetainedProgramRow(SideId, Program, i.Output.Ordinal, cell, UnitQuantity, Currency))))]);
    }
    public override RetainedProgramValuation Evaluate(SideValuationContext context)
    {
        if(context?.RetainedPrograms is not {} inputs)
            throw new MarketDataException("native_program_context", "A retained Side requires its exact admitted base/scenario source values.");
        if(!context.Snapshot.HasSameInputs(inputs.Scenario.Snapshot))
            throw new MarketDataException("native_program_context", "Retain this Side's exact scenario source snapshot.");
        if(inputs.HeldFactors.IsDefault)
            throw new MarketDataException(PhysicalLifecycle.ContextRefusal, "Supply the explicit retained occurrence normalization array.");
        if(context.BindingState is not null && context.BindingState is not PhysicalCashState)
            throw new MarketDataException("settlement_scope", "A retained Side cannot consume unrelated settlement state.");
        if(inputs.Cash is {} state)
        {
            if(!ReferenceEquals(context.BindingState, state) || inputs.Geometries.IsDefault || inputs.Geometries.Length!=inputs.HeldFactors.Length ||
                inputs.Geometries.Any(g=>g is null) || inputs.Owner is not { InstrumentId: var ownerId } || ownerId == Guid.Empty ||
                inputs.Occurrence is not {} occurrence || occurrence.SideId != SideId || occurrence.ProgramDigest != Side.ProgramDigest ||
                occurrence.Slot < 0 || occurrence.Slot >= inputs.HeldFactors.Length || inputs.HeldFactors[occurrence.Slot].Numerator.IsZero ||
                !context.IsRegisteredRetainedOccurrence(Side))
                throw new MarketDataException(PhysicalLifecycle.ContextRefusal, "The Side requires its exact connected gas-day geometry and held cash normalization.");
        }
        else if(context.BindingState is not null || !inputs.Geometries.IsDefault || inputs.Owner is not null || inputs.Occurrence is not null || !inputs.HeldFactors.IsEmpty)
            throw new MarketDataException(PhysicalLifecycle.ContextRefusal, "A held retained Side requires its complete explicit occurrence inputs.");
        // Admission precedes any program execution or price/discount demand.
        var execution = inputs.CashAutomatic
            ? inputs.Sources.EvaluateCashExercise(Program, SideId, inputs.Scenario, context.Discounting, context.UnitEvaluation.Cancellation)
            : inputs.Sources.Evaluate(Program, SideId, inputs.Scenario, context.Discounting, context.UnitEvaluation.Cancellation);
        var lifecycle = inputs.Cash is {} cash ? PhysicalLifecycle.EvaluateSide(Program, execution, Program.Document.Family == "gas", inputs.Occurrence!.Slot,
            inputs.HeldFactors[inputs.Occurrence.Slot], inputs.Geometries[inputs.Occurrence.Slot], cash, context.Snapshot, Currency,
            inputs.EntryDays.IsDefault ? default : inputs.EntryDays[inputs.Occurrence.Slot], inputs.CashAutomatic) : null;
        return new(this, context, execution, lifecycle);
    }
}
public sealed record RetainedProgramValuation : SideValuation
{
    public BookedSideEvaluation Execution { get; }
    public PhysicalLifecycleValue? Lifecycle { get; }
    internal RetainedProgramValuation(RetainedProgramSchedule schedule, SideValuationContext context,
        BookedSideEvaluation execution, PhysicalLifecycleValue? lifecycle)
        : base(schedule, context, lifecycle?.Total ?? execution.PresentValue)
    { Execution = execution; Lifecycle = lifecycle; }
    public SideViewInput ViewInput(string code, ExactDecimal scale, SignedRatio coefficient,
        ImmutableArray<(string Column,ScheduleViewMeasure Measure)> columns)
    {
        ImmutableDictionary<DateOnly,EntryDayQuantity>? selected = null;
        if(Context.RetainedPrograms is { Occurrence: {} occurrence, EntryDays.IsDefault: false } inputs && !inputs.EntryDays[occurrence.Slot].IsDefault)
            selected = inputs.EntryDays[occurrence.Slot].ToImmutableDictionary(d => d.Day);
        return new(code, Execution, ((RetainedProgramSchedule)Schedule).Program, scale, coefficient, columns, FollowedDays: selected, Part: Context.RetainedPrograms?.EntryPart);
    }
    public override SideScheduleSnapshot InspectSchedule(string manifest, ExactDecimal scale, SignedRatio coefficient, Func<DateOnly, DateTimeOffset> boundary)
    {
        var program = ((RetainedProgramSchedule)Schedule).Program;
        if(manifest != program.Document.ScheduleManifest || !Context.IsBaseScenario)
            throw new MarketDataException("side_schedule_inspection", "Inspect the exact base-scenario retained Side program.");
        if(Lifecycle is null) return SideScheduleSnapshot.FromProgramValue(this, Execution, program, manifest, scale, coefficient, boundary);
        var factor = new SignedRatio(scale.ScaledValue, ExactDecimal.WorkingScale) * coefficient;
        var rows = PhysicalLifecycle.Scale(Lifecycle, factor).Rows;
        return SideScheduleSnapshot.FromLifecycle(SideId, Snapshot.Id, ViewInput(SideId.ToString(), scale, coefficient, []),
            rows, ImmutableDictionary<int, ExactMoney>.Empty, boundary);
    }
}

// Native program address, retained for schedules and inspection without
// reconstructing dated terms or recompiling an already certified program.
public sealed record RetainedProgramRow(Guid SideId, BookedSideProgram Program, int Instruction, int Cell, Quantity UnitQuantity, ReferenceKey Currency)
{
    internal ScheduleDefinitionRow Output => Program.Document.Instructions[Instruction].Output;
    internal DeliveryInterval? Interval => Output.Grid is {} grid ? Program.Document.Grids[grid].Interval(Cell) : Output.Interval;
    internal SidePaymentKey Payment => Output.ValueAt is {} at && Output.PaymentOrdinal is {} occurrence
        ? new(SideId, occurrence, at, Currency){Timing=Output.Parameters["lifecycle"]==OperatingContractScheduleSource.DeliveryLifecycle?ValueTimingKind.Instant:ValueTimingKind.UtcPaymentDay}
        : throw new MarketDataException("schedule_row_kind", "This retained output has no payment/value-time identity.");
}
