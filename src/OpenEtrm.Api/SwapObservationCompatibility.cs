using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

public sealed record SwapObservationMode(FloatingAveraging Averaging, string ResetFrequency,
    string AveragingMethod, string Label, bool RequiresObservationCalendar, bool RequiresQuantityWeights);

public sealed record CompatibleSideObservations(FloatingObservationPlan Plan,
    ImmutableArray<SwapProjection> Projections, ImmutableArray<IndexFixing> Fixings,
    SwapObservationCalendar? Calendar);

public sealed record PlannedSideObservationReferences(ImmutableArray<FloatingObservation> Observations,
    ImmutableArray<SwapProjection> Projections, ImmutableArray<IndexFixing> Fixings);

// One admission boundary for observation authoring and submitted Side definitions.
// Payment cadence and market values are deliberately absent from source capability.
public static class SwapObservationCompatibility
{
    public static ImmutableArray<SwapObservationMode> Modes { get; } =
    [
        new(FloatingAveraging.MonthlyIndexPrice, "monthly", "none", "Published monthly index", false, false),
        new(FloatingAveraging.CalendarDayArithmetic, "daily", "arithmetic", "Equal calendar-day observations", false, false),
        new(FloatingAveraging.ObservationArithmetic, "daily", "observation_arithmetic", "Equal scheduled observations", true, false),
        new(FloatingAveraging.CalendarDaysPrevious, "daily", "calendar_days_previous", "Previous observation: represented days", true, false),
        new(FloatingAveraging.CalendarDaysNext, "daily", "calendar_days_next", "Next observation: represented days", true, false),
        new(FloatingAveraging.QuantityWeighted, "daily", "quantity_weighted", "Quantity-weighted observations", false, true)
    ];

    public static SwapObservationMode Mode(FloatingAveraging averaging) =>
        Modes.SingleOrDefault(mode => mode.Averaging == averaging)
        ?? throw Error("swap_averaging", "Select an admitted observation convention.");

    public static FloatingAveraging Parse(string? frequency, string? method) =>
        Modes.SingleOrDefault(mode => mode.ResetFrequency == frequency && mode.AveragingMethod == method)?.Averaging
        ?? throw Error("swap_averaging", "Choose compatible observation frequency and averaging method.");

    public static ImmutableArray<SwapObservationMode> SupportedSamplings(ImmutableArray<IndexFixing> fixings)
    {
        var qualified = fixings.Where(Qualified).ToArray();
        var monthly = qualified.Any(fixing => fixing.From.Day == 1 && fixing.From.Year < 9999 && fixing.From.AddMonths(1) == fixing.To);
        var daily = qualified.Any(fixing => fixing.To.DayNumber - fixing.From.DayNumber == 1);
        return [.. Modes.Where(mode => mode.Averaging == FloatingAveraging.MonthlyIndexPrice ? monthly : daily)];
    }

    public static ImmutableArray<SwapObservationMode> SupportedSamplings(PreviewData data, JsonElement curve)
    {
        var (index, quote) = References(data, curve);
        var periods = data.Definitions["index_fixing_observations"].Where(row => row.Text("index_code") == index.Code)
            .ToLookup(row => (DateWorkspace.Date(row.Text("observation_from")), DateWorkspace.Date(row.Text("observation_to"))));
        var projections = CurveContracts(data, curve).GroupBy(contract => (contract.DeliveryFrom, contract.DeliveryTo))
            .Where(group => group.Count() == 1 && periods[group.Key].Count() == 1)
            .Select(group => group.Single()).Where(contract =>
            {
                var row = periods[(contract.DeliveryFrom, contract.DeliveryTo)].Single();
                return row.Id("reference_catalog_id") == index.Revision.CatalogId && row.Id("reference_revision_id") == index.Revision.RevisionId &&
                    row.Text("quote_code") == quote.Key.Code && row.Text("implementation") == IndexFixing.SupportedImplementation;
            }).Select(contract => new SwapProjection(new(new(data.DefinitionRevision, curve.Text("code")), contract.Key), contract)).ToImmutableArray();
        return SupportedSamplings(SwapStore.Fixings(data, index, quote, projections));
    }

    public static CompatibleSideObservations Validate(PreviewData data, JsonElement curve, ReferenceKey index,
        QuoteSpecification quote, DateOnly from, DateOnly to, FloatingAveraging averaging,
        SwapObservationCalendar? calendar, ImmutableArray<FloatingObservation> quantities,
        CustomCurveProjectionBinding? custom = null)
    {
        var references = References(data, curve);
        if (references.Index != index || references.Quote != quote)
            throw Error("swap_definition", "The observation index, quote and projection must use the selected exact references.");
        ValidateConvention(data, curve, averaging, calendar, custom);
        var plan = FloatingObservationPlan.Generate(from, to, averaging, quote, calendar, quantities);
        var referencesForPlan = Bind(data, curve, index, quote, plan.Observations);
        return new(plan, referencesForPlan.Projections, referencesForPlan.Fixings, calendar);
    }

    public static PlannedSideObservationReferences PreviewReferences(PreviewData data, JsonElement curve,
        ReferenceKey index, QuoteSpecification quote, DateOnly from, DateOnly to, FloatingAveraging averaging,
        SwapObservationCalendar? calendar, CustomCurveProjectionBinding? custom = null)
    {
        var references = References(data, curve);
        if (references.Index != index || references.Quote != quote)
            throw Error("swap_definition", "The observation index, quote and projection must use the selected exact references.");
        ValidateConvention(data, curve, averaging, calendar, custom);
        // These are required input coordinates, not a priced quantity-weighted plan.
        // No placeholder quantities or inferred financial weights are introduced.
        var observations = FloatingObservationPlan.RequiredObservations(from, to, averaging, quote, calendar);
        return Bind(data, curve, index, quote, observations);
    }

    private static void ValidateConvention(PreviewData data, JsonElement curve, FloatingAveraging averaging,
        SwapObservationCalendar? calendar, CustomCurveProjectionBinding? custom)
    {
        var mode = Mode(averaging);
        if (custom is not null && averaging is not (FloatingAveraging.MonthlyIndexPrice or FloatingAveraging.CalendarDayArithmetic))
            throw Error("swap_custom_projection_averaging", "This observation convention has no qualified custom-curve projection mapping.");
        if (custom is not null && (custom.Implementation != CustomCurveProjectionBinding.SupportedImplementation ||
            custom.CatalogId != data.DefinitionRevision.CatalogId ||
            (averaging == FloatingAveraging.MonthlyIndexPrice ? custom.Selection != CustomCurveProjectionBinding.SupportedSelection || custom.Position is null :
                custom.Selection != CustomCurveProjectionBinding.DeliverySelection || custom.Position is not null)))
            throw Error("custom_projection", "Select the qualified custom-curve mapping for this observation convention.");
        if (!SupportedSamplings(data, curve).Any(candidate => candidate.Averaging == averaging))
            throw Error("index_fixing_cadence", "The selected index has no qualified published observations at the required period or cadence.");
        if (mode.RequiresObservationCalendar != (calendar is not null))
            throw Error("swap_observation_calendar", "Select an exact observation calendar only for a calendar-based observation convention.");
    }

    private static PlannedSideObservationReferences Bind(PreviewData data, JsonElement curve, ReferenceKey index,
        QuoteSpecification quote, ImmutableArray<FloatingObservation> observations)
    {
        var candidates = CurveContracts(data, curve).ToLookup(contract => (contract.DeliveryFrom, contract.DeliveryTo));
        var projections = observations.Select(observation =>
        {
            var matching = candidates[(observation.From, observation.To)].Take(2).ToArray();
            if (matching.Length != 1)
                throw Error("swap_projection_coverage", "Every planned observation, including guard observations, requires one exact projection contract.");
            var contract = matching[0];
            return new SwapProjection(new(new(data.DefinitionRevision, curve.Text("code")), contract.Key), contract, observation.AveragingQuantity);
        }).ToImmutableArray();
        var fixings = SwapStore.Fixings(data, index, quote, projections);
        if (fixings.Any(fixing => !Qualified(fixing)))
            throw Error("index_fixing_definition", "Every planned observation requires qualified publication, source and exact series metadata.");
        return new(observations, projections, fixings);
    }

    private static bool Qualified(IndexFixing fixing) => fixing.Implementation == IndexFixing.SupportedImplementation &&
        fixing.From < fixing.To && fixing.Index.Revision == fixing.Quote.Revision && fixing.Source.Revision == fixing.Index.Revision &&
        fixing.Index.Revision.CatalogId != Guid.Empty && fixing.Index.Revision.RevisionId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(fixing.Index.Code) && !string.IsNullOrWhiteSpace(fixing.Quote.Code) &&
        !string.IsNullOrWhiteSpace(fixing.Source.Code) && fixing.Series.Id != Guid.Empty &&
        fixing.Series.Revision.CatalogId != Guid.Empty && fixing.Series.Revision.RevisionId != Guid.Empty &&
        fixing.FixingAt.Offset == TimeSpan.Zero;

    private static (ReferenceKey Index, QuoteSpecification Quote) References(PreviewData data, JsonElement curve)
    {
        var code = curve.Text("code");
        if (data.Definitions["curves"].Count(row => row.Text("code") == code && row.GetRawText() == curve.GetRawText()) != 1)
            throw Error("swap_definition", "Select one authorized exact projection definition.");
        var targets = data.References["settlement_targets"].Where(row => row.Text("code") == curve.Text("settlement_target_code")).ToArray();
        var quotes = data.Quotes.Where(quote => quote.Key.Code == curve.Text("quote_code")).ToArray();
        if (targets.Length != 1 || targets[0].Text("kind") != "index" || quotes.Length != 1 || quotes[0].Kind != QuoteKind.Outright)
            throw Error("swap_definition", "This Side requires an exact outright index quote and declared projection target.");
        var index = new ReferenceKey(data.ReferenceRevision, targets[0].Text("index_a_code"));
        if (!data.References["price_index_definitions"].Any(row => row.Text("code") == index.Code))
            throw Error("swap_definition", "The exact published index definition is unavailable.");
        return (index, quotes[0]);
    }

    private static ImmutableArray<MarketContract> CurveContracts(PreviewData data, JsonElement curve)
    {
        var members = data.Definitions["curve_members"].Where(row => row.Text("curve_code") == curve.Text("code"))
            .Select(row => row.Id("contract_id")).ToHashSet();
        return [.. data.Contracts.Where(contract => members.Contains(contract.Key.Id) && contract.Kind == ContractKind.Forward)
            .OrderBy(contract => contract.DeliveryFrom).ThenBy(contract => contract.DeliveryTo)];
    }

    private static MarketDataException Error(string code, string message) => new(code, message);
}
