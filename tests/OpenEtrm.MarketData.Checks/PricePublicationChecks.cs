using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Publications;
using OpenEtrm.MarketData.Trading;

internal static class PricePublicationChecks
{
    public static void Run()
    {
        var f = new PublicationFixture();
        var monthly = PricePublicationBehavior.Create(f.Definition);
        var hourly=PricePublicationBehavior.Create(f.Definition with{Implementation=HourlyPowerPublication.Implementation});
        Equal(hourly is HourlyPowerPublication,true);
        Reject("publication_interval_required",()=>hourly.CoveredPeriods(f.Occurrence));
        Reject("publication_interval_required",()=>hourly.PrepareLegacyFixings(f.Feed,f.Release,f.Context,[f.Target]));

        var period = monthly.CoveredPeriods(f.Occurrence).Single();
        Equal(period.From, new DateOnly(2041, 2, 1)); Equal(period.To, new DateOnly(2041, 3, 1));
        Equal(monthly.ExpectedRelease(f.Occurrence), At("2041-02-01T20:00:00Z"));
        Equal(monthly.AssessmentWindow(f.Occurrence).To, At("2041-01-29T20:00:00Z"));

        // A published final is known before the expected time. The independent
        // legacy economic boundary is explicit; expected polling time cannot replace it.
        var prepared = monthly.PrepareLegacyFixings(f.Feed, f.Release, f.Context, [f.Target]);
        Equal(prepared.Single().Observation.Value.ToString(), "3.27");
        Equal(prepared.Single().Observation.ObservedAt, At("2041-02-01T19:35:00Z"));
        Equal(prepared.Single().Fixing.FixingAt, At("2041-02-01T19:00:00Z"));
        Equal(prepared.Single().Observation.Role, ObservationRole.IndexFixing);
        Equal(prepared.Single().Observation.State, PublicationState.Final);
        Equal(monthly.PrepareLegacyFixings(f.Feed, f.Release, f.Context, [f.Target]).Single(), prepared.Single());
        Reject("publication_not_visible", () => monthly.PrepareLegacyFixings(f.Feed, f.Release,
            f.Context with { KnownAt = At("2041-02-01T19:36:00Z") }, [f.Target]));
        Reject("publication_not_visible", () => monthly.PrepareLegacyFixings(f.Feed, f.Release,
            f.Context with { MarketCutoff = At("2041-02-01T19:34:00Z") }, [f.Target]));
        Reject("publication_not_final", () => monthly.PrepareLegacyFixings(f.Feed,
            f.Release with { Stage = PublicationState.Preliminary }, f.Context, [f.Target]));
        Reject("publication_legacy_boundary", () => monthly.PrepareLegacyFixings(f.Feed, f.Release, f.Context,
            [f.Target with { Fixing = f.Target.Fixing with { FixingAt = At("2041-02-01T20:00:00Z") } }]));
        Reject("publication_source", () => monthly.PrepareLegacyFixings(f.Feed,
            f.Release with { Source = f.Ref("other_feed") }, f.Context, [f.Target]));
        Reject("publication_time", () => monthly.PrepareLegacyFixings(f.Feed,
            f.Release with { ReceivedAt = f.Release.PublishedAt.AddMinutes(-1) }, f.Context, [f.Target]));
        Reject("publication_time", () => monthly.ValidateRelease(f.Feed,
            f.Release with { PublishedAt = f.Occurrence.AssessmentTo.AddMinutes(-1) }, f.Context));
        Reject("publication_coverage", () => monthly.PrepareLegacyFixings(f.Feed, f.Release, f.Context, []));
        Reject("publication_coverage", () => monthly.CoveredPeriods(f.Occurrence with
            { Periods = [new(new(2041, 2, 2), new(2041, 3, 1))] }));

        var correction = f.Release with { Id = Guid.NewGuid(), VendorRevision = "revision-b", CorrectsId = f.Release.Id,
            Price = new(ExactDecimal.Parse("3.28"), f.Index.Unit), PublishedAt = f.Release.PublishedAt.AddMinutes(1) };
        monthly.ValidateRelease(f.Feed, correction, f.Context, f.Release);
        Equal(f.Release.Price.Value.ToString(), "3.27");
        Reject("publication_correction", () => monthly.ValidateRelease(f.Feed, correction, f.Context));
        Reject("publication_correction", () => monthly.ValidateRelease(f.Feed,
            correction with { VendorRevision = f.Release.VendorRevision }, f.Context, f.Release));
        Reject("publication_occurrence", () => monthly.ValidateRelease(f.Feed, correction, f.Context,
            f.Release with { Occurrence = null! }));
        Reject("publication_correction", () => monthly.PrepareLegacyFixings(f.Feed, correction, f.Context, [f.Target], f.Release));
        var correctedActual = monthly.PrepareLegacyFixings(f.Feed, correction, f.Context,
            [f.Target with { Observation = new(f.Target.Observation.CatalogId, Guid.NewGuid()), CorrectedObservation = prepared.Single().Observation }], f.Release).Single();
        Equal(correctedActual.Observation.ObservedAt, prepared.Single().Observation.ObservedAt);
        Equal(correctedActual.CorrectsObservation!.Value, prepared.Single().Observation.Key);
        Equal(correctedActual.Observation.Value.ToString(), "3.28");
        var wireOccurrence = JsonSerializer.SerializeToNode(f.Occurrence, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        wireOccurrence.Remove("assessmentDate"); RejectJson(wireOccurrence.ToJsonString());
        wireOccurrence["assessmentDate"] = "2041-02-01"; wireOccurrence["impliedFinalAt"] = "2041-02-01T20:00:00Z";
        RejectJson(wireOccurrence.ToJsonString());

        var dailyDefinition = f.Definition with { Implementation = DatedGasPublication.DailyImplementation };
        var daily = PricePublicationBehavior.Create(dailyDefinition);
        var weekend = f.Occurrence with { Periods = [new(new(2041, 1, 31), new(2041, 2, 1)),
            new(new(2041, 2, 1), new(2041, 2, 2)), new(new(2041, 2, 2), new(2041, 2, 3))] };
        Equal(daily.CoveredPeriods(weekend).Length, 3);
        Reject("publication_coverage", () => daily.CoveredPeriods(weekend with { Periods = [weekend.Periods[0], weekend.Periods[2]] }));
        Reject("publication_coverage", () => daily.CoveredPeriods(weekend with { Periods = [weekend.Periods[0], weekend.Periods[0]] }));
        foreach (var implementation in UnsupportedPricePublication.Implementations)
        {
            var unsupported = PricePublicationBehavior.Create(f.Definition with { Implementation = implementation });
            Reject("publication_unsupported", () => unsupported.CoveredPeriods(f.Occurrence));
            Reject("publication_unsupported", () => unsupported.PrepareLegacyFixings(f.Feed, f.Release, f.Context, [f.Target]));
        }
        Reject("publication_definition", () => PricePublicationBehavior.Create(f.Definition with { Commodities = [f.Ref("crude")] }));
        Reject("publication_definition", () => PricePublicationBehavior.Create(f.Definition with { Indices = [f.Index, f.Index] }));
        Reject("publication_definition", () => PricePublicationBehavior.Create(f.Definition with
            { Publisher = f.Definition.Publisher with { Key = new(Guid.NewGuid(), Guid.NewGuid(), 1) } }));
        Console.WriteLine("PASS publications: explicit gas coverage, independent expected/economic/release/knowledge times, final-only legacy bridge, corrections and non-executable placeholders");
    }

    static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    static void Equal<T>(T actual, T expected) where T : notnull
    { if (!actual.Equals(expected)) throw new Exception($"Expected {expected}; got {actual}."); }
    static void Reject(string code, Action action)
    { try { action(); } catch (MarketDataException error) when (error.Code == code) { return; } throw new Exception($"Expected {code}."); }
    static void RejectJson(string json)
    { try { _ = JsonSerializer.Deserialize<PricePublicationOccurrence>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return; } throw new Exception("Expected explicit occurrence fields."); }

    sealed class PublicationFixture
    {
        readonly ReferenceRevision revision = new(Guid.NewGuid(), Guid.NewGuid());
        readonly DefinitionRevision market = new(Guid.NewGuid(), Guid.NewGuid());
        public ReferenceKey Ref(string code) => new(revision, code);
        public PricePublicationIndex Index { get; }
        public PricePublicationDefinition Definition { get; }
        public PricePublicationOccurrence Occurrence { get; }
        public PricePublicationFeed Feed { get; }
        public PricePublicationRelease Release { get; }
        public PricePublicationContext Context { get; }
        public PublicationFixingTarget Target { get; }
        public PublicationFixture()
        {
            var unit = new PriceUnit(Ref("usd"), Ref("gas_mmbtu"), ExactDecimal.Parse("1"));
            Index = new(Ref("monthly_gas"), Ref("gas"), Ref("river"), Ref("gas_quote"), unit, "SYNTH_MONTHLY", "monthly_index");
            Definition = new(new(market.CatalogId, Guid.NewGuid(), 1), revision, new(new(market.CatalogId, Guid.NewGuid(), 1), "Synthetic publisher"), "Synthetic monthly gas report",
                [Ref("gas")], [Index], Ref("calendar"), Ref("clock"), DatedGasPublication.MonthlyImplementation);
            Occurrence = new(Definition.Key, At("2041-01-27T12:00:00Z"), At("2041-01-29T20:00:00Z"), new(2041, 2, 1),
                [new(new(2041, 2, 1), new(2041, 3, 1))], At("2041-02-01T20:00:00Z"));
            Feed = new(Definition.Key, Ref("feed"));
            Release = new(Guid.NewGuid(), Definition.Key, Index.Key, Feed.Source, Occurrence, PublicationState.Final,
                At("2041-02-01T19:35:00Z"), At("2041-02-01T19:36:00Z"), At("2041-02-01T19:37:00Z"),
                "revision-a", null, new(ExactDecimal.Parse("3.27"), unit));
            Context = new(At("2041-02-01T19:40:00Z"), At("2041-02-01T19:40:00Z"));
            Target = new(new(Index.Key, Index.Quote, new(2041, 2, 1), new(2041, 3, 1), At("2041-02-01T19:00:00Z"),
                new(market, Guid.NewGuid()), Feed.Source, IndexFixing.SupportedImplementation), new(market.CatalogId, Guid.NewGuid()));
        }
    }
}
