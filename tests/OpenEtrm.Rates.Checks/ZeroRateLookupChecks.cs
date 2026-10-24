using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.Rates;

internal static class ZeroRateLookupChecks
{
    public static int Run()
    {
        var passed = 0;
        void Check(string label, Action test) { test(); passed++; Console.WriteLine("PASS zero-rate lookup: " + label); }
        void Reject(string code, Action test)
        { try { test(); } catch (MarketDataException e) when (e.Code == code) { return; } throw new Exception("Expected " + code); }
        var revision = new DefinitionRevision(Guid.NewGuid(), Guid.NewGuid());
        var reference = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        ReferenceKey R(string code) => new(reference, code);
        var start = new DateOnly(2040, 1, 1);
        var at = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var pillars = Enumerable.Range(1, 360).Select(n => new RatePillar(new(revision, Guid.NewGuid()), start.AddMonths(n))).ToImmutableArray();
        var pins = pillars.Select(p => new SelectedObservation(new(p.Series, InputUse.Variable),
            new(new(revision.CatalogId, Guid.NewGuid()), p.Series, R("source"), ObservationRole.Parameter,
                PublicationState.Final, at, ExactDecimal.Parse("0.05")))).ToImmutableArray();
        var snapshot = new MarketSnapshot(Guid.NewGuid(), revision, at, at,
            InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering, pins);
        var definition = new ZeroRateDefinition(new(revision, "discount"), R("simple"), R("USD"),
            "actual_365_fixed", "simple", RateConventions.Implementation, "valuation_utc_date", R("source"), pillars);
        var build = new ZeroRateCurve(definition).Build(snapshot);
        var lookup = new ZeroRateLookup(build);
        Check("the thirty-year monthly pillar retains its exact annual zero rate factor and pins", () =>
        {
            var value = lookup.AtPillar(new(2070, 1, 1));
            // Actual/365 Fixed simple: 1/(1+0.05*10958/365) = 3650/9129.
            if (value.AnnualRate != "0.05" || value.RatePercent != "5" || value.Days != 10958 ||
                value.DiscountFactor != "0.399824734363018951" || value.Series != pillars[^1].Series ||
                value.Observation != pins[^1].Observation.Key || lookup.Build != build ||
                lookup.Build.Currency != R("USD") || lookup.Build.SnapshotId != snapshot.Id || lookup.Build.NumericalPolicy != ZeroRateCurve.Policy)
                throw new Exception("Exact dated zero rate or provenance changed");
        });
        Check("first interior and last stored maturities resolve the exact retained entries", () =>
        {
            foreach (var n in new[] { 0, 179, 359 })
                if (!ReferenceEquals(lookup.AtPillar(pillars[n].Maturity), build.Values[n]))
                    throw new Exception("A stored pillar was replaced or inferred");
        });
        Check("a missing exact zero rate never interpolates extrapolates or chooses a neighbor", () =>
        {
            foreach (var date in new[] { start, pillars[0].Maturity.AddDays(1), pillars[^1].Maturity.AddDays(1) })
                Reject("rate_coverage", () => lookup.AtPillar(date));
        });
        Check("malformed complete pillar collections fail before any point is selected", () =>
        {
            Reject("rate_lookup", () => new ZeroRateLookup(build with { Values = default }));
            Reject("rate_lookup", () => new ZeroRateLookup(build with { Values = [] }));
            Reject("rate_lookup", () => new ZeroRateLookup(build with { Values = build.Values.SetItem(2, build.Values[0]) }));
            Reject("rate_lookup", () => new ZeroRateLookup(build with { Values = build.Values.SetItem(1, build.Values[0]) }));
            Reject("rate_lookup", () => new ZeroRateLookup(build with { Values = build.Values.SetItem(0, null!) }));
            Reject("rate_lookup", () => new ZeroRateLookup(null!));
        });
        Check("missing or unsupported build metadata cannot become a successful financial lookup", () =>
        {
            foreach (var invalid in new[] {
                build with { SnapshotId = Guid.Empty },
                build with { Currency = default },
                build with { Currency = new(new(Guid.Empty, reference.RevisionId), "USD") },
                build with { Currency = new(new(reference.CatalogId, Guid.Empty), "USD") },
                build with { Currency = R(" ") },
                build with { NumericalPolicy = "unknown" },
                build with { NumericalPolicy = null! },
                build with { ReferenceDate = default },
                build with { ReferenceDate = new(2200, 1, 1) },
                build with { ReferenceDate = build.Values[0].Maturity },
                build with { Values = build.Values.SetItem(0, build.Values[0] with { Maturity = build.ReferenceDate }) } })
                Reject("rate_lookup", () => new ZeroRateLookup(invalid));
        });
        Check("a record clone gets its own validated values while the original lookup remains immutable", () =>
        {
            var replacement = build.Values[^1] with { Maturity = new(2070, 2, 1) };
            var clone = build with { Values = build.Values.SetItem(359, replacement) };
            var next = new ZeroRateLookup(clone);
            if (!ReferenceEquals(next.AtPillar(replacement.Maturity), replacement) ||
                !ReferenceEquals(lookup.AtPillar(new(2070, 1, 1)), build.Values[^1]))
                throw new Exception("Lookup values leaked between immutable record versions");
            Reject("rate_coverage", () => next.AtPillar(new(2070, 1, 1)));
            Reject("rate_coverage", () => lookup.AtPillar(replacement.Maturity));
        });
        return passed;
    }
}
