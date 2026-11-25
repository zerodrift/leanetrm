using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData.Options;
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
    [IdentityField] public int? SourceSlot { get; }
    [IdentityField] public string? QuantityPolicy { get; }
    [IdentityField] public QuantityPart? Part { get; }
    [IdentityField] public OwnedQuantity? Owned { get; }
    [IdentityField] public ReportedBalanceFollow? Balance { get; }
    ExactDecimal IRetainedProgramMember.ValuationCoefficient => Direction;
    ExactRatio? IRetainedProgramMember.ValuationRatio => Ratio;
    int? IRetainedProgramMember.ValuationSourceSlot => SourceSlot;
    string? IRetainedProgramMember.ValuationQuantityPolicy => QuantityPolicy;
    QuantityPart? IRetainedProgramMember.ValuationPart => Part;
    OwnedQuantity? IRetainedProgramMember.ValuationOwned => Owned;
    ReportedBalanceFollow? IRetainedProgramMember.ValuationBalance => Balance;

    public RetainedGasMember(Guid sideId, ExactDecimal direction, ExactRatio ratio, BookedSideProgram program,
        int? sourceSlot = null, string? quantityPolicy = null, QuantityPart? part = null, OwnedQuantity? owned = null, ReportedBalanceFollow? balance = null)
    {
        if (sideId == Guid.Empty || program is null || ratio is null || ratio.Numerator <= 0 ||
            (direction != ExactDecimal.Parse("1") && direction != ExactDecimal.Parse("-1")))
            throw new MarketDataException("gas_membership", "Retained gas members require a program, positive quantity ratio and explicit direction.");
        if(sourceSlot is < 0 || quantityPolicy is not null && (quantityPolicy != GasHeldQuantities.DeliverySchedulePolicy || program.Document.Family != "gas") ||
            part is not null && (program.Document.Family == "gas" || sourceSlot is null))
            throw new MarketDataException("gas_membership", "A delivery policy belongs to a physical member; a quantity part requires a payment with an explicit physical source.");
        if (owned is not null && (sourceSlot is not null || quantityPolicy is not null || part is not null ||
            program.Document.Family != "fixed" || !Enum.IsDefined(owned.Basis) || owned.Clock is null ||
            owned.DailyQuantity is null || owned.Basis == ChargeBasis.LumpSum && owned.DailyQuantity != ExactRatio.One))
            throw new MarketDataException("gas_membership", "An owned charge requires fixed terms, its captured clock and an exact daily quantity with no source, policy or part.");
        if (balance is not null && (sourceSlot is null || part is not null || quantityPolicy is not null || owned is not null ||
            program.Document.Family != "floating" || direction != ExactDecimal.Parse(balance.OwedToUs ? "1" : "-1")))
            throw new MarketDataException("gas_membership", "A reported balance requires an indexed payment with its physical source and fixed direction, without a part, policy or owned quantity.");
        balance?.Require("gas_membership");
        SideId = sideId; Direction = direction; Ratio = ratio; Program = program;
        SourceSlot = sourceSlot; QuantityPolicy = quantityPolicy; Part = part; Owned = owned; Balance = balance;
        ProgramDigest = BookedSideProgram.Digest(program.Write());
    }
}

[EconomicDefinition("retained_gas_pricing_bindings_v1")]
internal sealed record RetainedGasPricingBindings(
    [property: IdentityField] SwingPricingBinding Swing,
    [property: IdentityField] ExerciseMarks Exercise);

// Booked programs supply economics; the common PV/factor contracts supply risk.
[EconomicDefinition("retained_gas_instrument_v1")]
public sealed record RetainedGasInstrument : RetainedProgramInstrument<RetainedGasMember>
{
    [IdentityField] public override ImmutableArray<RetainedGasMember> Members { get; }
    [IdentityField] public ImmutableArray<GasValuationAssignment> Assignments { get; }
    [IdentityField] public override ImmutableArray<ExerciseRight> Rights { get; }
    [IdentityField] public override ImmutableArray<SwingRight> SwingRights { get; }
    public override ImmutableArray<SwingPricingBinding> SwingPricingBindings { get; }
    public override ExerciseMarks? Marks { get; }
    public override DefinitionIdentity? PricingBindingIdentity => (SwingPricingBindings.IsEmpty, Marks) switch
    {
        (true, null) => null,
        (true, {} marks) => DefinitionIdentity.For(marks),
        (false, null) => DefinitionIdentity.For(SwingPricingBindings[0]),
        (false, {} marks) => DefinitionIdentity.For(new RetainedGasPricingBindings(SwingPricingBindings[0], marks))
    };
    // Existing component terms already own this connection. Keep legacy economic
    // bytes, while retaining the connection in the schedule's execution identity.
    // An explicit map has a null slot only for a charge that owns its quantity.
    public ImmutableArray<int?> PhysicalSources { get; }
    public override string RetainedScheduleIdentity =>
        PhysicalSources.IsDefault && Rights.IsEmpty && SwingRights.IsEmpty && Members.All(m => m.Owned is null) ? base.RetainedScheduleIdentity :
        base.RetainedScheduleIdentity + "/physical_sources_v1/" + string.Join('/', Members.Select((member, slot) =>
            member.Owned is not null ? "owned" : GeometrySource(slot)?.ToString(CultureInfo.InvariantCulture) ?? "pooled"));
    public ImmutableArray<IndexPriceLookup> PriceLookups { get; }
    public ImmutableArray<SchedulePriceTerm> ActivePrices => PhysicalLifecycle.ActivePrices(Members.Select(m=>m.Program),PriceLookups);
    public override ReferenceKey Currency => Assignments[0].Quote.Currency;
    public override Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Assignments[0].Quote.Quantity);

    public RetainedGasInstrument(Guid id, ImmutableArray<RetainedGasMember> members,
        ImmutableArray<GasValuationAssignment> assignments, ImmutableArray<IndexPriceLookup> priceLookups = default,
        ImmutableArray<ExerciseRight> rights = default, ExerciseMarks? marks = null, ImmutableArray<int?> physicalSources = default,
        ImmutableArray<SwingRight> swingRights = default, ImmutableArray<SwingPricingBinding> swingPricingBindings = default)
        : base(id, members, new(ExactDecimal.Parse("1"), First(assignments).Quote.Quantity), First(assignments).Quote.Currency)
    {
        if (members.IsDefaultOrEmpty || members.Any(m => m is null) || assignments.IsDefaultOrEmpty || assignments.Any(a => a is null) ||
            !members.Any(m => m.Program.Document.Family == "gas") ||
            members.Any(m => m.Program.Document.Family is not ("gas" or "fixed" or "floating")) ||
            assignments.Select(a => (a.Quote.Currency, a.Quote.Quantity)).Distinct().Count() != 1)
            throw new MarketDataException("gas_membership", "Select complete retained physical, fixed or indexed cash programs with their exact compatible assignments.");
        if(priceLookups.IsDefault&&members.Any(m=>m.Program.Document.Instructions.Any(i=>i.Output.Operation==CurvePriceScheduleSource.Operation)))
            throw new MarketDataException("side_market_sources","Bound programs require their exact authorized retained price lookups.");
        var hasQuantityTerms = members.Any(m => m.QuantityPolicy is not null || m.Part is not null || m.Balance is not null);
        var allowSources = hasQuantityTerms || !rights.IsDefaultOrEmpty || !swingRights.IsDefaultOrEmpty;
        if (members.Where((m, slot) => m.SourceSlot is {} source
                ? !allowSources || source >= members.Length || members[source].Program.Document.Family != "gas" ||
                    m.Program.Document.Family == "gas" && source != slot
                : hasQuantityTerms && m.Owned is null).Any())
            throw new MarketDataException("gas_membership", "Retain each required physical source and its exact membership slot.");
        Members = members; Assignments = assignments.Distinct().ToImmutableArray();
        if (physicalSources.IsDefault && hasQuantityTerms) physicalSources = [..members.Select(m => m.SourceSlot)];
        if (!physicalSources.IsDefault && (physicalSources.Length != members.Length || physicalSources.Where((source, slot) =>
            members[slot].Owned is not null ? source is not null :
            source is not {} owner || owner < 0 || owner >= members.Length || members[owner].Program.Document.Family != "gas" ||
            members[slot].Program.Document.Family == "gas" && owner != slot ||
            members[slot].SourceSlot is {} declared && declared != owner).Any()))
            throw new MarketDataException("gas_membership", "Every execution entry requires physical geometry consistent with its quantity terms; an owned charge cannot name a physical source.");
        PhysicalSources=physicalSources;
        PriceLookups=priceLookups.IsDefault?[..Assignments.Select(a=>a.Lookup).Distinct()]:priceLookups.Distinct().ToImmutableArray();
        GasImbalanceCashoutEntries.RequireRetained(members);
        Rights = rights.IsDefault ? [] : rights;
        if (!Rights.IsEmpty) RequireRights();
        marks?.Validate();
        Marks = marks;
        SwingRights = swingRights.IsDefault ? [] : swingRights;
        SwingPricingBindings = swingPricingBindings.IsDefault ? [] : swingPricingBindings;
        GasSwingAdmission.Require(this);
    }

    public ImmutableArray<(ExerciseGroupKey Group, CurveKey Curve, DateOnly Month)> ExerciseMarkInputs(MarketSnapshot snapshot, PhysicalCashState held) =>
        RetainedExerciseValuation.Scope(this, snapshot, snapshot.ValuationAt, held).Inputs;

    private void RequireRights()
    {
        var claimedSlots = new HashSet<int>();
        for (var ordinal = 0; ordinal < Rights.Length; ordinal++)
        {
            var right = Rights[ordinal];
            if (right is null || right.Ordinal != ordinal || right.Slots.IsDefaultOrEmpty || right.Groups.IsDefaultOrEmpty ||
                !Enum.IsDefined(right.Period) || !Enum.IsDefined(right.Holder) || !Enum.IsDefined(right.Settlement) ||
                right.Model != KirkSpreadCall.Implementation || right.DayCount != OptionNumericalPolicies.ActualOver365FixedInstant)
                throw InvalidRight("Select ordered exercise rights with explicit members, groups and installed exercise terms.");
            foreach (var slot in right.Slots)
            {
                if ((uint)slot >= (uint)Members.Length || !claimedSlots.Add(slot))
                    throw InvalidRight("Every exercise right must name existing membership slots claimed exactly once.");
                if (Members[slot].Owned is not null || Members[slot].Balance is not null)
                    throw InvalidRight("An exercise right cannot cover an independently owned charge or a reported-balance cash-out.");
            }

            var hasPhysical = right.Slots.Any(slot => Members[slot].Program.Document.Family == "gas");
            if (hasPhysical != (right.Settlement == ExerciseSettlement.Physical))
                throw InvalidRight("Physical exercise requires a physical member and cash automatic exercise requires payment members.");
            RequireGroupCoverage(right);
        }
    }

    private void RequireGroupCoverage(ExerciseRight right)
    {
        var groupDays = new HashSet<DateOnly>();
        ExerciseGroup? previous = null;
        foreach (var group in right.Groups)
        {
            if (group is null || group.ExerciseAt == default || !TradingInstant.IsSupported(group.ExerciseAt) || group.From >= group.To || previous is not null && previous.To > group.From)
                throw InvalidRight("Exercise groups must be nonempty, ordered and disjoint.");
            if (right.Period == ExercisePeriod.DeliveryDay && group.To != group.From.AddDays(1))
                throw InvalidRight("A daily exercise group must cover exactly one gas day.");
            if (right.Period == ExercisePeriod.DeliveryMonth &&
                (!SameMonth(group.From, group.To.AddDays(-1)) || previous is not null && SameMonth(previous.From, group.From)))
                throw InvalidRight("A monthly exercise right must have one group per represented delivery month.");
            for (var day = group.From; day < group.To; day = day.AddDays(1))
                groupDays.Add(day);
            previous = group;
        }

        var deliveryDays = new HashSet<DateOnly>();
        foreach (var slot in right.Slots)
        {
            var memberDays = new Dictionary<DateOnly, (DateTimeOffset? NativeStart, bool Dated)>();
            foreach (var row in Sides[slot].Side.GenerateSchedule().Rows)
            {
                switch (row.DeliveryWindow)
                {
                    case IntervalScheduleWindow { Interval: var interval }:
                        var day = interval.OperatingDate;
                        var retained = memberDays.GetValueOrDefault(day);
                        if (retained.NativeStart is not {} start || interval.Start < start)
                            memberDays[day] = (interval.Start, retained.Dated);
                        break;
                    case DatedScheduleWindow(var from, var to):
                        for (var covered = from; covered < to; covered = covered.AddDays(1))
                            memberDays[covered] = (memberDays.GetValueOrDefault(covered).NativeStart, true);
                        break;
                    default:
                        throw InvalidRight("Exercise rights require retained delivery geometry for every selected member.");
                }
            }
            deliveryDays.UnionWith(memberDays.Keys);
            if (right.Settlement == ExerciseSettlement.Physical)
                RequirePhysicalBoundaries(right.Groups, memberDays, Members[slot].Program);
        }
        if (right.Settlement == ExerciseSettlement.CashAutomatic) RequireCashBoundaries(right);
        if (!groupDays.SetEquals(deliveryDays))
            throw InvalidRight("Exercise groups must cover exactly the selected members' retained delivery days.");
    }

    private void RequireCashBoundaries(ExerciseRight right)
    {
        var index = PriceLookups.ToLookup(p => (p.Market, p.Contract.DeliveryFrom, p.Contract.DeliveryTo));
        var latest = new Dictionary<DateOnly, DateTimeOffset>();
        foreach (var slot in right.Slots)
        {
            var program = Members[slot].Program;
            var sources = PhysicalLifecycle.DatedInputs(program, index).ToLookup(p => p.Instruction.Output.Ordinal, p => p.Lookup);
            var occurrences = new Dictionary<(int? Ordinal, DateOnly? From, DateOnly? To), HashSet<IndexFixing>>();
            foreach (var instruction in program.Document.Instructions.Where(i => i.Output is { Role: "source", OutputKind: "price" } &&
                i.Output.Operation != "constant_price"))
            {
                var matches = sources[instruction.Output.Ordinal].ToArray();
                if (matches.Length != 1 || matches[0].Fixing is not {} fixing || fixing.Implementation != IndexFixing.SupportedImplementation ||
                    fixing.Index != matches[0].Index || fixing.Quote != matches[0].Quote.Key ||
                    fixing.From != matches[0].Contract.DeliveryFrom || fixing.To != matches[0].Contract.DeliveryTo ||
                    fixing.FixingAt == default || !TradingInstant.IsSupported(fixing.FixingAt))
                    throw InvalidRight("Each cash exercise price requires its exact retained index fixing.");
                foreach (var address in instruction.Consumers)
                {
                    var row = program.Document.Instructions[address].Output;
                    var key = (row.PaymentOrdinal, row.DeliveryFrom, row.DeliveryTo);
                    if (!occurrences.TryGetValue(key, out var fixings)) occurrences.Add(key, fixings = []);
                    fixings.Add(fixing);
                }
            }
            if (program.Document.Family == "floating" || occurrences.Count != 0)
                AddBookedCashFixings(program, occurrences, latest);
        }
        foreach (var group in right.Groups)
        {
            var deadline = Enumerable.Range(0, group.To.DayNumber - group.From.DayNumber)
                .Select(i => latest.GetValueOrDefault(group.From.AddDays(i))).Max();
            if (deadline != group.ExerciseAt)
                throw InvalidRight("A cash exercise group must retain its latest covered index fixing instant.");
        }
    }

    // Each booked period owns its first fixing and all of its exact observation records.
    // A monthly observation may price several daily periods, each with its own occurrence.
    private static void AddBookedCashFixings(BookedSideProgram program,
        Dictionary<(int? Ordinal, DateOnly? From, DateOnly? To), HashSet<IndexFixing>> occurrences, Dictionary<DateOnly, DateTimeOffset> latest)
    {
        static MarketDataException Inconsistent() => InvalidRight("Each cash exercise occurrence must match its booked fixing terms.");
        static DateOnly Date(JsonElement period, string field) =>
            DateOnly.ParseExact(period.GetProperty(field).GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            using var definition = JsonDocument.Parse(program.Document.EconomicIdentity);
            var periods = definition.RootElement.GetProperty(nameof(FloatingSide.Periods));
            if (occurrences.Count == 0 || periods.GetArrayLength() != occurrences.Count) throw Inconsistent();
            var ordinal = 0;
            foreach (var period in periods.EnumerateArray())
            {
                var from = Date(period, nameof(FloatingSidePeriod.DeliveryFrom));
                var to = Date(period, nameof(FloatingSidePeriod.DeliveryTo));
                if (!occurrences.TryGetValue((ordinal++, from, to), out var fixings)) throw Inconsistent();
                var saved = period.GetProperty(nameof(FloatingSidePeriod.Fixings));
                var supplied = fixings.Select(DefinitionIdentity.CanonicalTerm).ToHashSet(StringComparer.Ordinal);
                var first = period.GetProperty(nameof(FloatingSidePeriod.FirstFixingAt));
                if (first.GetRawText() != DefinitionIdentity.CanonicalTerm(fixings.Min(f => f.FixingAt)) ||
                    saved.GetArrayLength() != supplied.Count || !supplied.SetEquals(saved.EnumerateArray().Select(f => f.GetRawText())))
                    throw Inconsistent();
                var last = fixings.Max(f => f.FixingAt);
                for (var day = from; day < to; day = day.AddDays(1))
                    if (last > latest.GetValueOrDefault(day)) latest[day] = last;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentNullException)
        {
            throw Inconsistent();
        }
    }

    private static void RequirePhysicalBoundaries(ImmutableArray<ExerciseGroup> groups,
        Dictionary<DateOnly, (DateTimeOffset? NativeStart, bool Dated)> memberDays, BookedSideProgram program)
    {
        GasDayClock? clock = null;
        foreach (var group in groups)
        {
            for (var day = group.From; day < group.To; day = day.AddDays(1))
            {
                if (!memberDays.TryGetValue(day, out var retained)) continue;
                if (retained.NativeStart is {} start && group.ExerciseAt > start ||
                    retained.Dated && group.ExerciseAt > CashDayStart(clock ??= ReadCashDayClock(program), day))
                    throw LatePhysicalExercise();
                break;
            }
        }
    }

    private static bool SameMonth(DateOnly left, DateOnly right) => left.Year == right.Year && left.Month == right.Month;

    private static GasDayClock ReadCashDayClock(BookedSideProgram program)
    {
        try
        {
            using var definition = JsonDocument.Parse(program.Document.EconomicIdentity);
            return ReadRetainedClock(definition.RootElement.GetProperty(nameof(FixedSide.GasDeliveryClock)), MissingCashDayClock);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException ||
            error is MarketDataException market && market.Code != "exercise_right")
        {
            throw MissingCashDayClock();
        }
    }

    internal static GasDayClock ReadRetainedClock(JsonElement clock, Func<MarketDataException> invalid)
    {
        var key = clock.GetProperty(nameof(GasDayClock.Key));
        if (key.GetArrayLength() != 3 || key[0].GetString() != "reference" ||
            key[1].GetArrayLength() != 3 || key[1][0].GetString() != "reference_revision")
            throw invalid();
        var retained = new GasDayClock(new(new(key[1][1].GetGuid(), key[1][2].GetGuid()), key[2].GetString()!),
            clock.GetProperty(nameof(GasDayClock.Rules)).GetString()!, clock.GetProperty(nameof(GasDayClock.StartMinute)).GetInt32());
        return DefinitionIdentity.For(retained).Canonical == clock.GetRawText() ? retained : throw invalid();
    }

    private static DateTimeOffset CashDayStart(GasDayClock clock, DateOnly day)
    {
        try { return clock.Boundary(day); }
        catch (MarketDataException error) when (error.Code == "gas_day_boundary") { throw MissingCashDayClock(); }
    }

    private static MarketDataException MissingCashDayClock() =>
        InvalidRight("Physical exercise requires an exact retained gas-day boundary for each selected member's first day covered by the group.");
    private static MarketDataException LatePhysicalExercise() =>
        InvalidRight("Physical exercise must occur at or before each selected member's first retained gas-day start within the group.");
    private static MarketDataException InvalidRight(string message) => new("exercise_right", message);

    private static GasValuationAssignment First(ImmutableArray<GasValuationAssignment> assignments)=>
        !assignments.IsDefaultOrEmpty&&assignments[0] is {} first?first:
        throw new MarketDataException("gas_membership","Select complete retained gas assignment terms before constructing their Side definitions.");

    // Runtime Side execution retains the selected started-delivery measure
    // and original whole-day delivery-mark policy in its explicit model version.
    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        if (!Rights.IsEmpty || !SwingRights.IsEmpty || context.BindingState is PhysicalCashState cash &&
            (!cash.Elections.IsEmpty || !cash.SwingElections.IsEmpty || cash.NextSwingElectionEffectiveAt is not null))
            ValidateValuationState(context.BindingState);
        var declaration = RetainedDeclaration(new(Id,
            (context.BindingState is PhysicalCashState ? PhysicalCashState.Policy + "/" : "") +
            (Members.Select(m => m.QuantityPolicy).FirstOrDefault(p => p is not null) is {} policy ? policy + "/" : "") +
            (Members.Any(m => PhysicalLifecycle.ReadsDeliveryMarks(m.Program)) ? PeriodPriceSelector.DeliveryMarkPolicy + "/" : "")+"retained_gas_side_pv_v3",
            PhysicalLifecycle.PriceDependencies(Members.Select(member => member.Program), PriceLookups, context.Snapshot),
            UnitQuantity.Specification, Currency) { Time = ValuationTimeDependency.Pinned, ScheduleIdentity = RetainedScheduleIdentity });
        if (Rights.IsEmpty && SwingRights.IsEmpty) return declaration;
        return RetainedExerciseValuation.Declare(declaration, this, context);
    }

    protected internal override void RequireBeforeValuation(SideValuationContext context)
    {
        if (Rights.IsEmpty && SwingRights.IsEmpty || context is null) return;
        if (context.Discounting is ExplicitDiscounting scalar)
        {
            if (scalar.Undiscounted) return;
        }
        else
        {
            var keys = Sides.SelectMany(member => member.Side.GenerateSchedule().Rows.Select(row => row.Key))
                .Where(key => !key.ReachedValuationBoundary(context.Snapshot.ValuationAt)).Distinct().ToImmutableArray();
            var factors = context.Discounting.Resolve(keys, context.Snapshot);
            if (!keys.IsEmpty && !factors.IsDefault && factors.Length == keys.Length &&
                factors.All(f => f is not null && f.Source == DiscountFactorSource.Undiscounted &&
                    f.DiscountFactor == ExactDecimal.Parse("1") && f.Snapshot is not null && f.Snapshot.HasSameInputs(context.Snapshot)) &&
                keys.ToHashSet().SetEquals(factors.Select(f => f.Payment))) return;
        }
        throw new MarketDataException(SwingRights.IsEmpty ? "exercise_model_scope" : "swing_model_scope", "The selected choice model requires explicitly undiscounted valuation.");
    }

    private protected override RetainedProgramContext PrepareSides(UnitValuationContext context,CurveBuild basis,CurveBuild scenario)
    {
        var input=new RetainedProgramContext(Sources(basis),scenario);
        if(context.BindingState is not PhysicalCashState cash)return input;
        var quantity=new SignedRatio(cash.Quantity.Amount.ScaledValue,ExactDecimal.WorkingScale);
        var owners = new Dictionary<int, PhysicalLifecycleGeometry>();
        PhysicalLifecycleGeometry? pooled = null;
        PhysicalLifecycleGeometry? owned = null;
        PhysicalLifecycleGeometry Geometry(int slot)
        {
            if (Members[slot].Owned is not null)
                return owned ??= PhysicalLifecycle.PrepareGeometry([], Members.Select((member, index) => (member, index))
                    .Where(entry => entry.member.Owned is not null)
                    .Select(entry => (entry.index, entry.member.Program, entry.member.Owned!)), context.Snapshot);
            if (GeometrySource(slot) is not {} owner)
                return pooled ??= PhysicalLifecycle.PrepareGeometry(Members.Where(m => m.Program.Document.Family == "gas").Select(m => m.Program), [], context.Snapshot);
            if (!owners.TryGetValue(owner, out var geometry))
                owners.Add(owner, geometry = PhysicalLifecycle.PrepareGeometry([Members[owner].Program], [], context.Snapshot));
            return geometry;
        }
        ImmutableArray<PhysicalLifecycleGeometry> geometries = [..Enumerable.Range(0, Members.Length).Select(Geometry)];
        return input with{Cash=cash,Geometries=geometries,ReportedDays=GasBalanceQuantities.Build(Members,cash,geometries),
            HeldFactors=[..Members.Select(m=>quantity*new SignedRatio(m.Direction.ScaledValue,ExactDecimal.WorkingScale)*new SignedRatio(m.Ratio.Numerator,m.Ratio.Denominator))]};
    }
    internal int? GeometrySource(int slot)
    {
        if (!PhysicalSources.IsDefault) return PhysicalSources[slot];
        if (Rights.IsEmpty && SwingRights.IsEmpty) return null; // Legacy direct callers have pooled compatible geometry.
        var member = Members[slot];
        return member.Program.Document.Family == "gas" ? slot : member.SourceSlot;
    }

    // Each Side owns its own accrued/paid rows. The Instrument only aggregates
    // signed membership amounts in the held binding's exact quantity basis.
    public PhysicalLifecycleValue EvaluateLifecycle(UnitValuationContext context)
    {
        if(context?.BindingState is not PhysicalCashState state)
            throw new MarketDataException(PhysicalLifecycle.ContextRefusal,"Lifecycle valuation requires the held binding's explicit physical cash state.");
        ValidateValuationState(state);
        return PhysicalLifecycle.FromValuation(EvaluateSides(context));
    }
    public override ExactMoney Value(UnitValuationContext context)
    {
        if(context.BindingState is not PhysicalCashState)
        {
            ValidateValuationState(context.BindingState);
            PhysicalLifecycle.RequireUnstarted(Members.Select(m=>m.Program),context.Snapshot.ValuationAt);
            var unstarted=EvaluateSides(context);context.RecordEntries(unstarted);return unstarted.PresentValue;
        }
        var value=EvaluateSides(context);context.RecordEntries(value);var zero=ExactMoney.Parse("0",Currency);
        ExactMoney Component(Func<PhysicalLifecycleValue,ExactMoney> select)=>value.Sides.Aggregate(zero,(sum,side)=>
            sum.Add(select(((RetainedProgramValuation)side.Value).Lifecycle!).Scale(side.QuantityRatio).Scale(side.Coefficient)));
        context.RecordComponents([new(PhysicalLifecycle.UnrealizedComponent,Component(p=>p.Unrealized).Add(value.UndecidedUnitValue)),new(PhysicalLifecycle.RealizedUnsettledComponent,Component(p=>p.RealizedUnsettled))]);
        return value.PresentValue;
    }

    public override void ValidateValuationState(IBindingValuationState? state)
    {
        if (state is not PhysicalCashState cash)
        {
            if (Members.Any(m => m.Owned is not null))
                throw new MarketDataException("gas_delivery_lifecycle", "A gas instrument with owned charges requires its held cash state from booking.");
            if(!Rights.IsEmpty || !SwingRights.IsEmpty || Members.Any(m => m.QuantityPolicy is not null || m.Part is not null || m.Balance is not null))
                throw PhysicalDeliveryQuantities.Invalid("An instrument with quantity terms requires its complete held owner-day inputs.");
            base.ValidateValuationState(state); return;
        }
        ExerciseElection.RequireRetainedGroups(Rights, cash.Elections);
        if (cash.Quantity.Specification != UnitQuantity.Specification || cash.Payments.Any(p => p.Slot >= Members.Length ||
                Members[p.Slot].Program.Document.Family == "gas" || p.Amount.Currency != Currency ||
                !Members[p.Slot].Program.Document.Instructions.Any(i => i.Output is { Operation: "output_guard", Grid: null } && i.Output.PaymentOrdinal == p.Occurrence)))
            throw new MarketDataException("payment_state", "Each payment allocation must name a retained cash occurrence of this instrument in its currency and quantity.");
        PhysicalDeliveryQuantities.RequireSchedules(Members, cash, Rights, SwingRights);
        if (cash.SwingElections.Any(e => e is null || !SwingRights.Any(r => r.Ordinal == e.Right)) ||
            SwingRights.IsEmpty && cash.NextSwingElectionEffectiveAt is not null)
            throw new MarketDataException("swing_election", "Swing facts require their exact retained right.");
    }

    protected override ScheduleMarketSources Sources(CurveBuild basis) => new(basis.Graph.Plan.Context, basis, null, null,
        IntervalKnowledgePolicy.ResolvedCutoff, PendingActualsPolicy.RequireActuals,
        PriceLookups, [], Assignments);
}
