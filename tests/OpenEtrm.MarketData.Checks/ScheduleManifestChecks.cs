using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class ScheduleManifestChecks
{
    internal static void Run()
    {
        // A literal encoding protects the empty-section shape, header names/order,
        // root property order, UTF-8 and lowercase digest independently of the writer.
        const string canonical = "[{\"family\":\"synthetic\",\"economicIdentity\":\"economic\",\"pricingBindingIdentity\":\"pricing\",\"schemaVersion\":\"schema\",\"generatorVersion\":\"generator\"},[],[],[],[{\"name\":\"pv\",\"row\":0,\"measure\":\"pv\"}],[],[]]";
        using (var writer = new ScheduleManifestWriter("synthetic", "economic", "pricing", "schema", "generator"))
        {
            writer.Append(new ScheduleDefinitionRoot("pv", 0, "pv"));
            writer.Flush();
            Equal(writer.Complete(), Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
            Reject(() => writer.Append(new ScheduleDefinitionRoot("other", 0, "pv")));
            Reject(() => writer.Complete());
        }
        using (var writer = new ScheduleManifestWriter("synthetic", "economic", "pricing", "schema", "generator"))
        {
            writer.Append(new ScheduleDefinitionRoot("pv", 0, "pv"));
            Reject(() => writer.Append(new ScheduleDefinitionEdge(1, "input", 0, 0)));
        }
        using (var writer = new ScheduleManifestWriter("synthetic", "economic", "pricing", "schema", "generator"))
            Reject(() => writer.Append("untyped"));

        var fixture = new CommodityLookupFixture(2, monthly: true);
        var trade = fixture.Swap(monthly: true, monthOrdinal: 0);
        var schedules = new List<ScheduleDefinition> { ScheduleDefinitionCompiler.Generate(trade.Fixed), ScheduleDefinitionCompiler.Generate(trade.Floating) };
        schedules.AddRange(new PpaAcceptanceFixture().Physical().Sides.Select(m => ScheduleDefinitionCompiler.Generate(m.Side)));
        foreach (var schedule in schedules)
        {
            Equal(schedule.Manifest, LegacyEncoding(schedule));
            Equal(Paged(schedule, 1), schedule.Manifest);
            Equal(Paged(schedule, 7), schedule.Manifest);
            var altered = schedule.Rows.ToArray();
            altered[0] = altered[0] with { Key = altered[0].Key + "_changed" };
            if (Paged(schedule, 1, altered) == schedule.Manifest) throw new Exception("A changed persisted row must change the complete manifest.");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try { _ = Paged(schedule, 1, cancellation: cancelled.Token); }
            catch (OperationCanceledException) { continue; }
            throw new Exception("Cancelled manifest streaming must not return a complete digest.");
        }
        Console.WriteLine("PASS retained manifest: literal encoding, legacy parity, paging, mutation, order and cancellation");
    }

    // Independent baseline serialization: no incremental writer or production helper.
    private static string LegacyEncoding(ScheduleDefinition schedule)
    {
        object[] content = [new { schedule.Family, schedule.EconomicIdentity, schedule.PricingBindingIdentity,
            schedule.SchemaVersion, schedule.GeneratorVersion }, schedule.Rows, schedule.Edges,
            schedule.Bindings, schedule.Roots, schedule.Ports, schedule.Grids];
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, ScheduleDefinition.Json)));
    }

    private static string Paged(ScheduleDefinition schedule, int size, IReadOnlyList<ScheduleDefinitionRow>? rows = null,
        CancellationToken cancellation = default)
    {
        using var writer = new ScheduleManifestWriter(schedule);
        void Part<T>(IReadOnlyList<T> values)
        {
            for (var offset = 0; offset < values.Count; offset += size)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (var value in values.Skip(offset).Take(size)) writer.Append(value);
                writer.Flush();
            }
        }
        Part(rows ?? schedule.Rows); Part(schedule.Edges); Part(schedule.Bindings);
        Part(schedule.Roots); Part(schedule.Ports); Part(schedule.Grids);
        return writer.Complete();
    }
    private static void Equal(string actual, string expected)
    { if (actual != expected) throw new Exception($"Expected retained digest {expected}; got {actual}."); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (MarketDataException e) when (e.Code == "side_schedule_manifest") { return; }
        throw new Exception("Invalid manifest streaming must fail explicitly.");
    }
}
