using System.Collections.Immutable;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Publications;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct PricePublisherKey([property: JsonRequired] Guid CatalogId, [property: JsonRequired] Guid Id, [property: JsonRequired] int Version);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct PricePublicationKey([property: JsonRequired] Guid CatalogId, [property: JsonRequired] Guid Id, [property: JsonRequired] int Version);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PricePublisher([property: JsonRequired] PricePublisherKey Key, [property: JsonRequired] string Name);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PricePublicationIndex([property: JsonRequired] ReferenceKey Key, [property: JsonRequired] ReferenceKey Commodity, [property: JsonRequired] ReferenceKey Location,
    [property: JsonRequired] ReferenceKey Quote, [property: JsonRequired] PriceUnit Unit, [property: JsonRequired] string Symbol, [property: JsonRequired] string Statistic);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PricePublicationDefinition([property: JsonRequired] PricePublicationKey Key, [property: JsonRequired] ReferenceRevision References,
    [property: JsonRequired] PricePublisher Publisher, [property: JsonRequired] string Name,
    [property: JsonRequired] ImmutableArray<ReferenceKey> Commodities, [property: JsonRequired] ImmutableArray<PricePublicationIndex> Indices,
    [property: JsonRequired] ReferenceKey Calendar, [property: JsonRequired] ReferenceKey Clock, [property: JsonRequired] string Implementation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct PublicationPeriod([property: JsonRequired] DateOnly From, [property: JsonRequired] DateOnly To);
public readonly record struct PublicationAssessmentWindow(DateTimeOffset From, DateTimeOffset To);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PricePublicationOccurrence([property: JsonRequired] PricePublicationKey Publication, [property: JsonRequired] DateTimeOffset AssessmentFrom,
    [property: JsonRequired] DateTimeOffset AssessmentTo, [property: JsonRequired] DateOnly AssessmentDate,
    [property: JsonRequired] ImmutableArray<PublicationPeriod> Periods, [property: JsonRequired] DateTimeOffset ExpectedReleaseAt);
public sealed record PricePublicationFeed(PricePublicationKey Publication, ReferenceKey Source);
public sealed record PricePublicationRelease(Guid Id, PricePublicationKey Publication, ReferenceKey Index, ReferenceKey Source,
    PricePublicationOccurrence Occurrence, PublicationState Stage, DateTimeOffset PublishedAt, DateTimeOffset ReceivedAt,
    DateTimeOffset KnownAt, string VendorRevision, Guid? CorrectsId, UnitPrice Price);
public sealed record PricePublicationContext(DateTimeOffset MarketCutoff, DateTimeOffset KnownAt);
public sealed record PublicationFixingTarget(IndexFixing Fixing, ObservationKey Observation, MarketObservation? CorrectedObservation = null);
public sealed record PreparedPublicationFixing(IndexFixing Fixing, MarketObservation Observation, Guid ReleaseId,
    ObservationKey? CorrectsObservation = null);

public abstract class PricePublicationBehavior
{
    public PricePublicationDefinition Definition { get; }
    protected PricePublicationBehavior(PricePublicationDefinition definition) => Definition = definition;
    public abstract ImmutableArray<PublicationPeriod> CoveredPeriods(PricePublicationOccurrence occurrence);
    public abstract PublicationAssessmentWindow AssessmentWindow(PricePublicationOccurrence occurrence);
    public abstract DateTimeOffset ExpectedRelease(PricePublicationOccurrence occurrence);

    public static PricePublicationBehavior Create(PricePublicationDefinition definition)
    {
        ValidateDefinition(definition);
        return definition.Implementation switch
        {
            DatedGasPublication.DailyImplementation or DatedGasPublication.MonthlyImplementation => new DatedGasPublication(definition),
            var value when UnsupportedPricePublication.Implementations.Contains(value) => new UnsupportedPricePublication(definition),
            _ => throw Error("publication_unsupported", "The selected publication implementation is unsupported.")
        };
    }

    static void ValidateDefinition(PricePublicationDefinition d)
    {
        if (d is null || d.Key.CatalogId == Guid.Empty || d.Key.Id == Guid.Empty || d.Key.Version < 1 ||
            d.References.CatalogId == Guid.Empty || d.References.RevisionId == Guid.Empty || d.Publisher is null ||
            d.Publisher.Key.CatalogId != d.Key.CatalogId || d.Publisher.Key.Id == Guid.Empty || d.Publisher.Key.Version < 1 ||
            !Text(d.Name) || !Text(d.Publisher.Name) || d.Commodities.IsDefaultOrEmpty || d.Commodities.Length > 32 ||
            d.Commodities.Distinct().Count() != d.Commodities.Length || d.Indices.IsDefault || d.Indices.Length > 1000 ||
            d.Indices.Any(i => i is null) || d.Indices.Select(i => i.Key).Distinct().Count() != d.Indices.Length ||
            d.Indices.Select(i => i.Symbol).Distinct(StringComparer.Ordinal).Count() != d.Indices.Length)
            throw Error("publication_definition", "A publication requires exact versioned identities, distinct commodities and index members.");
        bool Reference(ReferenceKey key) => key.Revision == d.References && Text(key.Code);
        if (!Reference(d.Calendar) || !Reference(d.Clock) || d.Commodities.Any(c => !Reference(c)) ||
            d.Indices.Any(i => !Reference(i.Key) || !Reference(i.Commodity) || !Reference(i.Location) || !Reference(i.Quote) ||
                !d.Commodities.Contains(i.Commodity) || i.Unit is null || !Reference(i.Unit.Currency) || !Reference(i.Unit.Quantity) ||
                !Text(i.Symbol) || !Text(i.Statistic)))
            throw Error("publication_definition", "Publication references and index commodities must match their exact reference revision and membership.");
        if (d.Indices.IsEmpty && !UnsupportedPricePublication.Implementations.Contains(d.Implementation))
            throw Error("publication_definition", "An executable publication requires an exact index member.");
    }

    protected void ValidateOccurrence(PricePublicationOccurrence o)
    {
        if (o is null || o.Publication != Definition.Key)
            throw Error("publication_occurrence", "The occurrence must name this exact publication version.");
        if (!TradingInstant.IsSupported(o.AssessmentFrom) || !TradingInstant.IsSupported(o.AssessmentTo) ||
            o.AssessmentFrom >= o.AssessmentTo || !TradingInstant.IsSupported(o.ExpectedReleaseAt))
            throw Error("publication_time", "Supply exact UTC assessment boundaries and an independent expected release instant.");
        if (o.Periods.IsDefaultOrEmpty || o.Periods.Length > 366 || o.Periods.Any(p => p.From >= p.To) ||
            o.Periods.Distinct().Count() != o.Periods.Length)
            throw Error("publication_coverage", "Supply distinct, ordered covered delivery periods.");
    }

    // Publication/receipt/knowledge coordinates are evidence. Expected release
    // is operational metadata and never substitutes for any of them.
    public void ValidateRelease(PricePublicationFeed feed, PricePublicationRelease release, PricePublicationContext context,
        PricePublicationRelease? corrected = null)
    {
        ValidateReleaseFacts(feed, release, context);
        if (release.CorrectsId is null)
        {
            if (corrected is not null) throw Error("publication_correction", "An initial release cannot select a correction predecessor.");
            return;
        }
        if (corrected is not null) ValidateReleaseFacts(feed, corrected, context);
        if (corrected is null || release.CorrectsId != corrected.Id || release.Id == corrected.Id ||
            release.VendorRevision == corrected.VendorRevision || release.Publication != corrected.Publication ||
            release.Index != corrected.Index || release.Source != corrected.Source ||
            !SameOccurrence(release.Occurrence, corrected.Occurrence) || release.PublishedAt < corrected.PublishedAt ||
            release.ReceivedAt < corrected.ReceivedAt || release.KnownAt < corrected.KnownAt)
            throw Error("publication_correction", "A correction requires its exact prior release, unchanged target and a distinct revision.");
    }

    void ValidateReleaseFacts(PricePublicationFeed feed, PricePublicationRelease release, PricePublicationContext context)
    {
        // Calling the coverage behavior also makes every placeholder refuse execution.
        if (release is null) throw Error("publication_release", "Supply a normalized release.");
        _ = CoveredPeriods(release.Occurrence);
        if (release.Id == Guid.Empty || release.Publication != Definition.Key || !Text(release.VendorRevision) ||
            !Enum.IsDefined(release.Stage) || release.Price is null)
            throw Error("publication_release", "The release requires exact publication, revision, stage and price evidence.");
        var index = Definition.Indices.SingleOrDefault(i => i.Key == release.Index);
        if (index is null || release.Price.Unit != index.Unit)
            throw Error("publication_index", "The release must match an exact member index and its quote unit.");
        if (feed is null || feed.Publication != Definition.Key || release.Source != feed.Source ||
            feed.Source.Revision != Definition.References || !Text(feed.Source.Code))
            throw Error("publication_source", "Select the publication's exact source binding; a feed does not identify its publisher.");
        if (!TradingInstant.IsSupported(release.PublishedAt) || !TradingInstant.IsSupported(release.ReceivedAt) ||
            !TradingInstant.IsSupported(release.KnownAt) || release.PublishedAt > release.ReceivedAt ||
            release.ReceivedAt > release.KnownAt || context is null || !TradingInstant.IsSupported(context.MarketCutoff) ||
            !TradingInstant.IsSupported(context.KnownAt))
            throw Error("publication_time", "Published, received and certified known instants must be supplied in causal order.");
        if (release.Stage == PublicationState.Final && release.PublishedAt < release.Occurrence.AssessmentTo)
            throw Error("publication_time", "A final release cannot precede the declared end of its assessment window.");
        if (release.PublishedAt > context.MarketCutoff || release.KnownAt > context.KnownAt)
            throw Error("publication_not_visible", "The release is not visible under both selected market and knowledge cutoffs.");
    }

    // This bridge prepares existing v1 actuals; it does not change their economic
    // boundary or admit a publication-driven replacement for historical terms.
    public ImmutableArray<PreparedPublicationFixing> PrepareLegacyFixings(PricePublicationFeed feed, PricePublicationRelease release,
        PricePublicationContext context, ImmutableArray<PublicationFixingTarget> targets, PricePublicationRelease? corrected = null)
    {
        ValidateRelease(feed, release, context, corrected);
        if (release.Stage != PublicationState.Final)
            throw Error("publication_not_final", "A preliminary release cannot satisfy the existing final-fixing contract.");
        var periods = CoveredPeriods(release.Occurrence);
        if (targets.IsDefault || targets.Length != periods.Length || targets.Any(t => t is null || t.Fixing is null) ||
            targets.Select(t => new PublicationPeriod(t.Fixing.From, t.Fixing.To)).Distinct().Count() != periods.Length ||
            !targets.Select(t => new PublicationPeriod(t.Fixing.From, t.Fixing.To)).ToHashSet().SetEquals(periods) ||
            targets.Select(t => t.Observation).Distinct().Count() != targets.Length ||
            targets.Select(t => t.Fixing.Series).Distinct().Count() != targets.Length)
            throw Error("publication_coverage", "The release requires exactly one distinct actual target for every covered period.");
        var index = Definition.Indices.Single(i => i.Key == release.Index);
        var result = ImmutableArray.CreateBuilder<PreparedPublicationFixing>(targets.Length);
        foreach (var target in targets)
        {
            var fixing = target.Fixing;
            if (fixing.Index != index.Key || fixing.Quote != index.Quote || fixing.Source != feed.Source ||
                fixing.Implementation != IndexFixing.SupportedImplementation || fixing.Series.Id == Guid.Empty ||
                fixing.Series.Revision.CatalogId != Definition.Key.CatalogId || fixing.Series.Revision.RevisionId == Guid.Empty ||
                target.Observation.CatalogId != Definition.Key.CatalogId || target.Observation.Id == Guid.Empty)
                throw Error("publication_target", "Each actual target must retain the exact selected index, quote, source and series identity.");
            if (!TradingInstant.IsSupported(fixing.FixingAt) || release.PublishedAt < fixing.FixingAt)
                throw Error("publication_legacy_boundary", "The existing v1 economic boundary remains explicit and cannot be replaced by expected release time.");
            var prior = target.CorrectedObservation;
            if (corrected is null ? prior is not null : prior is null || corrected.Stage != PublicationState.Final ||
                prior.Key.CatalogId != target.Observation.CatalogId || prior.Key.Id == Guid.Empty || prior.Key == target.Observation ||
                prior.Series != fixing.Series || prior.Source != release.Source || prior.Role != ObservationRole.IndexFixing ||
                prior.State != PublicationState.Final || prior.Value != corrected.Price.Value || !TradingInstant.IsSupported(prior.ObservedAt) ||
                prior.ObservedAt < fixing.FixingAt || prior.ObservedAt > corrected.PublishedAt)
                throw Error("publication_correction", "A correction requires the exact prior final observation and retains its source-fact time.");
            result.Add(new(fixing, new(target.Observation, fixing.Series, release.Source, ObservationRole.IndexFixing,
                PublicationState.Final, prior?.ObservedAt ?? release.PublishedAt, release.Price.Value), release.Id, prior?.Key));
        }
        return result.ToImmutable();
    }

    static bool SameOccurrence(PricePublicationOccurrence a, PricePublicationOccurrence b) =>
        a.Publication == b.Publication && a.AssessmentFrom == b.AssessmentFrom && a.AssessmentTo == b.AssessmentTo &&
        a.AssessmentDate == b.AssessmentDate && a.ExpectedReleaseAt == b.ExpectedReleaseAt && a.Periods.SequenceEqual(b.Periods);
    static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 200;
    protected static MarketDataException Error(string code, string message) => new(code, message);
}

// A bounded implementation for materialized publisher calendars. It validates
// supplied dates rather than inventing exchange holidays, release times or prices.
public sealed class DatedGasPublication : PricePublicationBehavior
{
    public const string DailyImplementation = "gas_daily_explicit_v1";
    public const string MonthlyImplementation = "gas_monthly_explicit_v1";
    internal DatedGasPublication(PricePublicationDefinition definition) : base(definition) { }
    public override ImmutableArray<PublicationPeriod> CoveredPeriods(PricePublicationOccurrence occurrence)
    {
        ValidateOccurrence(occurrence);
        var periods = occurrence.Periods;
        if (Definition.Implementation == MonthlyImplementation)
        {
            if (periods.Length != 1 || periods[0].From.Day != 1 || periods[0].From.Year == 9999 && periods[0].From.Month == 12 ||
                periods[0].To != periods[0].From.AddMonths(1))
                throw Error("publication_coverage", "A monthly gas index covers one complete declared delivery month.");
        }
        else
        {
            for (var n = 0; n < periods.Length; n++)
                if (periods[n].To.DayNumber - periods[n].From.DayNumber != 1 || n > 0 && periods[n - 1].To != periods[n].From)
                    throw Error("publication_coverage", "A daily gas package covers explicit consecutive flow days exactly once.");
        }
        return periods;
    }
    public override PublicationAssessmentWindow AssessmentWindow(PricePublicationOccurrence occurrence)
    { _ = CoveredPeriods(occurrence); return new(occurrence.AssessmentFrom, occurrence.AssessmentTo); }
    public override DateTimeOffset ExpectedRelease(PricePublicationOccurrence occurrence)
    { _ = CoveredPeriods(occurrence); return occurrence.ExpectedReleaseAt; }
}

public sealed class UnsupportedPricePublication : PricePublicationBehavior
{
    public static readonly ImmutableArray<string> Implementations = ["crude_placeholder_v1", "ngl_placeholder_v1", "lng_placeholder_v1", "power_placeholder_v1"];
    internal UnsupportedPricePublication(PricePublicationDefinition definition) : base(definition) { }
    static MarketDataException Unsupported() => Error("publication_unsupported", "This publication is a non-executable placeholder; qualify its methodology before use.");
    public override ImmutableArray<PublicationPeriod> CoveredPeriods(PricePublicationOccurrence occurrence) => throw Unsupported();
    public override PublicationAssessmentWindow AssessmentWindow(PricePublicationOccurrence occurrence) => throw Unsupported();
    public override DateTimeOffset ExpectedRelease(PricePublicationOccurrence occurrence) => throw Unsupported();
}
