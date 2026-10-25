using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class CommodityLookupChecks
{
    public static int Run()
    {
        var count = 0;
        void Check(string name, Action action) { action(); count++; Console.WriteLine("PASS commodity lookup: " + name); }
        static void Equal(string actual, string expected)
        { if (actual != expected) throw new Exception($"Expected {expected}, got {actual}"); }
        var f = new CommodityLookupFixture(7305);
        Check("far-future monthly index uses its exact dated contract", () =>
        {
            var monthly = new CommodityLookupFixture(360, monthly: true);
            // December2070 is ordinal359: outright3.25+0.359=3.609, fixed4, quantity100.
            var value = monthly.Swap(monthly: true).Price(monthly.Build([monthly.Request(359)]),
                new(ExactDecimal.Parse("100"), monthly.Quote.Quantity), ExactDecimal.Parse("1"), FinancialSwapInstrument.Undiscounted);
            Equal(value.Amount.ToString(), "-39.1");
        });
        Check("far-future daily swap retains the exact complete-month arithmetic", () =>
        {
            // Last 31 days: outright=3.25+i/1000, i=7274..7304. Average 10.539.
            // Receive floating/pay fixed4 over31totalunits gives (10.539-4)*31.
            var value = f.Swap().Price(f.Build(f.MonthRequests), new(ExactDecimal.Parse("31"), f.Quote.Quantity),
                ExactDecimal.Parse("1"), FinancialSwapInstrument.Undiscounted);
            Equal(value.Amount.ToString(), "202.709");
        });
        Check("requests and shuffled explicit maps retain exact endpoints and lineage", () =>
        {
            var indices = new[] { 7304, 0, 3652 };
            var market = f.ModelWithReversedMappings().Build(f.Snapshot, indices.Select(f.Request).ToImmutableArray());
            Equal(string.Join(',', market.Values.Select(v => v.Price.Value.ToString())), "10.554,3.25,6.902");
            foreach (var (value, index) in market.Values.Zip(indices))
                if (!value.Lineage.SetEquals(new[] { f.Selection(index, false).Observation.Key, f.Selection(index, true).Observation.Key }))
                    throw new Exception("The exact constituent's source lineage was lost.");
        });
        Check("missing late source never substitutes an adjacent constituent", () =>
        {
            var absent = f.Snapshot with { Selections = f.Snapshot.Selections.Remove(f.Selection(7304, true)) };
            try { f.Model.Build(absent, [f.Request(7304)]); }
            catch (MarketDataException error) when (error.Code == "missing_observation") { return; }
            throw new Exception("Missing late source did not fail explicitly.");
        });
        Check("far-future actuals and forecasts keep their original denominator and exact pins", () =>
        {
            var snapshot = f.WithActuals(15);
            var swap = f.Swap(indexed: true);
            var market = f.Model.Build(snapshot, swap.DependenciesAt(snapshot));
            var value = swap.PV(new SideValuationContext(market, new ExplicitDiscounting(ExactDecimal.Parse("1"), DiscountFactorSource.Undiscounted)));
            // Actuals20..34 sum405; remaining i7289..7304 sum168.744; fixed4*31=124.
            Equal(value.PresentValue.Scale(ExactDecimal.Parse("31")).ToMoney().Amount.ToString(), "449.744");
            var resets = ((PaymentSideValuation)value.Sides[1].Value).Payments[0].Resets;
            if (resets.Take(15).Any(r => r.Price.State != SchedulePriceState.Actual) ||
                resets.Skip(15).Any(r => r.Price.State != SchedulePriceState.Projected))
                throw new Exception("Actual/projected boundaries changed.");
            for (var n = 0; n < 15; n++)
                if (!resets[n].Price.Lineage.SetEquals(new[] { f.Actual(n).Observation.Key }))
                    throw new Exception("Actual reset lost its exact frozen observation.");
        });
        return count;
    }

    public static void Benchmark()
    {
        foreach (var days in new[] { 1826, 3652, 7305 })
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            var allocation = GC.GetAllocatedBytesForCurrentThread();
            var timer = Stopwatch.StartNew(); var f = new CommodityLookupFixture(days); timer.Stop();
            var modelMs = timer.Elapsed.TotalMilliseconds;
            var requests = Enumerable.Range(0, days).Select(f.Request).ToImmutableArray();
            _ = f.Build(requests); // JIT/warm-up is outside the measured builds.
            var samples = new List<double>(); var evaluated = 0;
            var buildAllocation = GC.GetAllocatedBytesForCurrentThread();
            for (var n = 0; n < 3; n++)
            {
                timer.Restart(); var result = f.Build(requests); timer.Stop();
                samples.Add(timer.Elapsed.TotalMilliseconds); evaluated = result.EvaluatedConstituents;
            }
            Console.WriteLine(JsonSerializer.Serialize(new {
                workload = "synthetic_exact_daily_base_plus_basis_v1", contracts = days, curves = 3,
                snapshotInputs = days * 2, outputRequests = days, evaluatedNodes = evaluated, scenarios = 1,
                repeats = samples.Count, modelMilliseconds = modelMs, buildMilliseconds = samples,
                buildAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - buildAllocation,
                totalAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocation,
                runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(), processors = Environment.ProcessorCount,
                managedHeapBytes = GC.GetTotalMemory(false),
                peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64 is > 0 and var peak ? peak : (long?)null
            }));
        }
    }
}

internal sealed class CommodityLookupFixture
{
    private readonly DefinitionRevision revision = new(Guid.Parse("dbbd2280-812e-4421-b037-d21e5ed95b71"), Guid.Parse("b1b34601-46ed-4169-9baf-6574ffea746b"));
    private readonly ReferenceRevision reference = new(Guid.Parse("af839777-9f42-49f4-bf48-9a240bfe80b7"), Guid.Parse("ddba8ff1-d44c-44c6-a653-42cccd1831db"));
    private readonly ImmutableArray<MarketContract> contracts;
    private readonly ImmutableArray<MarketSeries> series;
    private readonly ImmutableArray<Curve> curves;
    private readonly ImmutableArray<QuantitySpecification> quantities;
    private readonly ImmutableArray<QuoteSpecification> quotes;
    private readonly int firstMonth;
    private readonly DateTimeOffset valuation = new(2039, 12, 1, 0, 0, 0, TimeSpan.Zero);
    public CurveModel Model { get; }
    public MarketSnapshot Snapshot { get; }
    public QuoteSpecification Quote => quotes[0];
    public ImmutableArray<CurveRequest> MonthRequests => Enumerable.Range(firstMonth, 31).Select(Request).ToImmutableArray();
    private ReferenceKey R(string code) => new(reference, code);
    private CurveKey C(string code) => new(revision, code);
    private static Guid Id(int n) => Guid.Parse($"c42f0d87-254d-4ba3-a452-{n:000000000000}");
    public CurveRequest Request(int n) => new(C("local"), contracts[n].Key);
    public SelectedObservation Selection(int n, bool spread) => Snapshot.Selections[n * 2 + (spread ? 1 : 0)];

    public CommodityLookupFixture(int days, bool monthly = false)
    {
        var from = new DateOnly(2041, 1, 1); firstMonth = days - 31;
        quantities = [new(R("quantity"), R("gas"), R("energy"), R("unit"), R("energy_dimension"))];
        QuoteSpecification Q(string code, QuoteKind kind) => new(R(code), kind, R("currency"), R("quantity"),
            ExactDecimal.Parse("1"), ExactDecimal.Parse("0.001"), ExactDecimal.Parse("0.001"), ExactDecimal.Parse("-1000"), ExactDecimal.Parse("1000"));
        quotes = [Q("outright", QuoteKind.Outright), Q("basis", QuoteKind.Basis)];
        DateOnly Boundary(int n) => monthly ? from.AddMonths(n) : from.AddDays(n);
        contracts = Enumerable.Range(0, days).Select(n => new MarketContract(new(revision, Id(n + 1)), ContractKind.Forward,
            Boundary(n), Boundary(n + 1), new DateTimeOffset(Boundary(n + 1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), null)).ToImmutableArray();
        var members = contracts.Select(c => c.Key).ToImmutableArray();
        Curve Direct(string code, ReferenceKey quote, SettlementTarget settlement) => new(C(code), quote, R("location"), R("uniform"), settlement,
            CurveRule.Direct, members, [], ObservationRole.TraderMark, R("source"), null);
        var maps = members.Select(k => new ConstituentMapping(k, k)).ToImmutableArray();
        curves = [Direct("base", Quote.Key, new(SettlementKind.Index, R("base_index"), null)),
            Direct("basis", quotes[1].Key, new(SettlementKind.IndexDifference, R("local_index"), R("base_index"))),
            new(C("local"), Quote.Key, R("location"), R("uniform"), new(SettlementKind.Index, R("local_index"), null), CurveRule.Add,
                members, [new(InputSlot.Base, C("base"), SourceExpiryRule.RequireLive, maps),
                          new(InputSlot.Spread, C("basis"), SourceExpiryRule.RequireLive, maps)], null, null, null)];
        series = contracts.SelectMany((c, n) => new[] { new MarketSeries(new(revision, Id(days + n * 2 + 1)), c.Key, Quote.Key, null),
            new MarketSeries(new(revision, Id(days + n * 2 + 2)), c.Key, quotes[1].Key, null) }).ToImmutableArray();
        var selections = series.Select((s, n) => new SelectedObservation(new(s.Key, InputUse.Variable),
            new(new(revision.CatalogId, Id(days * 3 + n + 1)), s.Key, R("source"), ObservationRole.TraderMark,
                PublicationState.Final, valuation, ExactDecimal.Parse(n % 2 == 1 ? "0.25" : (3m + n / 2 / 1000m).ToString(CultureInfo.InvariantCulture))))).ToImmutableArray();
        Snapshot = new(Guid.NewGuid(), revision, valuation, valuation, InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering, selections);
        Model = MakeModel(curves);
    }
    private CurveModel MakeModel(ImmutableArray<Curve> values) => new(revision, reference, quantities, quotes, contracts, values, [], [], series);
    public CurveModel ModelWithReversedMappings() => MakeModel(curves.Select(c => c with {
        Inputs = c.Inputs.Select(i => i with { Mappings = i.Mappings.Reverse().ToImmutableArray() }).ToImmutableArray()
    }).ToImmutableArray());
    public CurveBuild Build(ImmutableArray<CurveRequest> requests) => Model.Build(Snapshot, requests);
    private IndexFixing Fixing(int n)
    {
        var c = contracts[firstMonth + n];
        return new(R("local_index"), Quote.Key, c.DeliveryFrom, c.DeliveryTo,
            new DateTimeOffset(c.DeliveryFrom.AddDays(-1).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc)),
            series[(firstMonth + n) * 2].Key, R("source"), IndexFixing.SupportedImplementation);
    }
    public SelectedObservation Actual(int n) => new(new(Fixing(n).Series, InputUse.FrozenFact),
        new(new(revision.CatalogId, Id(100000 + n)), Fixing(n).Series, R("source"), ObservationRole.IndexFixing,
            PublicationState.Final, Fixing(n).FixingAt, ExactDecimal.Parse((20 + n).ToString(CultureInfo.InvariantCulture))));
    public MarketSnapshot WithActuals(int count) => Snapshot with {
        Id = Guid.NewGuid(), ValuationAt = Fixing(count - 1).FixingAt, MarketCutoff = Fixing(count - 1).FixingAt,
        Selections = Snapshot.Selections.AddRange(Enumerable.Range(0, count).Select(Actual)) };
    public FinancialSwapInstrument Swap(bool indexed = false, bool monthly = false, int? monthOrdinal = null)
    {
        var first = monthly ? monthOrdinal ?? contracts.Length - 1 : firstMonth;
        var count = monthly ? 1 : 31;
        var averaging = monthly ? FloatingAveraging.MonthlyIndexPrice : FloatingAveraging.CalendarDayArithmetic;
        var from = contracts[first].DeliveryFrom; var to = monthly ? contracts[first].DeliveryTo : contracts[^1].DeliveryTo;
        var payment = new DateTimeOffset(to.AddDays(2).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var unit = new Quantity(ExactDecimal.Parse("1"), Quote.Quantity);
        var fixedSide = new FixedSide(Guid.NewGuid(), new(ExactDecimal.Parse("4"), Quote), unit, from, to, payment, FixedSide.Implementation, Quote);
        var projections = Enumerable.Range(first, count).Select(n => new SwapProjection(Request(n), contracts[n])).ToImmutableArray();
        var floating = indexed
            ? new FloatingSide(Guid.NewGuid(), R("local_index"), Quote, unit, from, to, Enumerable.Range(first - firstMonth, count).Select(Fixing).ToImmutableArray(), payment,
                averaging, projections, FloatingSide.IndexedImplementation)
            : new FloatingSide(Guid.NewGuid(), R("local_index"), Quote, unit, from, to, Fixing(first - firstMonth).FixingAt, payment,
                averaging, projections, FloatingSide.Implementation);
        return new(Guid.NewGuid(), [new(fixedSide, ExactDecimal.Parse("-1")), new(floating, ExactDecimal.Parse("1"))], FinancialSwapInstrument.Implementation, false);
    }
}
