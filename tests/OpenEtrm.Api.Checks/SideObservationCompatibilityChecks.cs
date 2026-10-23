using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class SideObservationCompatibilityChecks
{
    public static void Run()
    {
        var revision = new ReferenceRevision(Guid.NewGuid(), Guid.NewGuid());
        var definitions = new DefinitionRevision(Guid.NewGuid(), Guid.NewGuid());
        var index = new ReferenceKey(revision, "synthetic_index");
        var quote = new ReferenceKey(revision, "synthetic_quote");
        var source = new ReferenceKey(revision, "synthetic_source");
        IndexFixing Fixing(DateOnly from, DateOnly to) => new(index, quote, from, to,
            new DateTimeOffset(2048, 3, 31, 16, 45, 0, TimeSpan.Zero),
            new(definitions, Guid.NewGuid()), source, IndexFixing.SupportedImplementation);
        var month = Fixing(new(2048, 4, 1), new(2048, 5, 1));
        var days = Enumerable.Range(1, 30).Select(day =>
            Fixing(new(2048, 4, day), new DateOnly(2048, 4, day).AddDays(1))).ToImmutableArray();

        var monthly = SwapObservationCompatibility.SupportedSamplings([month]);
        if (monthly.Length != 1 || monthly[0].Averaging != FloatingAveraging.MonthlyIndexPrice)
            throw new Exception("A monthly published fixing acquired distinct daily observations.");
        var daily = SwapObservationCompatibility.SupportedSamplings(days);
        if (daily.Length != 5 || daily.Any(mode => mode.Averaging == FloatingAveraging.MonthlyIndexPrice))
            throw new Exception("Daily fixing capability was inferred from payment frequency or reconstructed a monthly publication.");
        var all = SwapObservationCompatibility.SupportedSamplings([month, .. days]);
        if (all.Length != 6 || all.Select(mode => mode.Averaging).Distinct().Count() != 6)
            throw new Exception("The qualified mode catalog is incomplete or ambiguous.");
        if (SwapObservationCompatibility.SupportedSamplings([]).Length != 0)
            throw new Exception("Missing publication metadata became a compatible source.");
        foreach (var mode in all)
            if (SwapObservationCompatibility.Parse(mode.ResetFrequency, mode.AveragingMethod) != mode.Averaging)
                throw new Exception("The authoring pair changed the selected convention.");
        foreach (var pair in new[] { ("monthly", "arithmetic"), ("hourly", "arithmetic"), ("daily", "none"), ("daily", "unknown") })
        {
            try { SwapObservationCompatibility.Parse(pair.Item1, pair.Item2); }
            catch (MarketDataException error) when (error.Code == "swap_averaging") { continue; }
            throw new Exception("An unsupported sampling combination was admitted.");
        }
        var unsupported = days.SetItem(0, days[0] with { Implementation = "unqualified_publication" });
        if (SwapObservationCompatibility.SupportedSamplings([unsupported[0]]).Length != 0)
            throw new Exception("An unqualified publication implementation became compatible.");
        CheckReferenceInputs(revision);
        Console.WriteLine("PASS Side observation compatibility: all six explicit authoring pairs; monthly/daily publication roles remain distinct; missing or unsupported metadata and finer cadence reject");
    }

    private static void CheckReferenceInputs(ReferenceRevision revision)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var calendar = JsonSerializer.SerializeToNode(new SwapObservationScheduleInput(SwapStore.ObservationScheduleImplementation,
            new ReferenceKey(revision, "synthetic_calendar"), null), options)!;
        var weighted = JsonSerializer.SerializeToNode(new SwapObservationScheduleInput(SwapStore.ObservationScheduleImplementation,
            null, [new("2048-04-03", "2048-04-04", new("1", new(revision, "synthetic_quantity")))]), options)!;
        foreach (var (template, path) in new[]
        {
            (calendar, new[] { "calendar" }), (calendar, new[] { "calendar", "revision" }),
            (weighted, new[] { "quantities", "0", "quantity", "specification" }),
            (weighted, new[] { "quantities", "0", "quantity", "specification", "revision" })
        })
        {
            var input = template.DeepClone();
            JsonNode target = input;
            foreach (var part in path) target = target is JsonArray array ? array[int.Parse(part)]! : target[part]!;
            target["boundaryPolicy"] = "reject";
            try { input.Deserialize<SwapObservationScheduleInput>(options); }
            catch (JsonException) { continue; }
            throw new Exception("An unknown nested reference field was silently discarded: " + string.Join('.', path));
        }
        if (calendar.Deserialize<SwapObservationScheduleInput>(options)!.Calendar != new ReferenceKey(revision, "synthetic_calendar") ||
            weighted.Deserialize<SwapObservationScheduleInput>(options)!.Quantities!.Value[0].Quantity.Value != "1")
            throw new Exception("A complete exact reference changed during strict input binding.");
        Console.WriteLine("PASS Side observation inputs: unknown nested reference and revision members reject without changing valid exact keys");
    }
}
