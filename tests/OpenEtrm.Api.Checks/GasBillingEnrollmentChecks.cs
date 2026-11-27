using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasBillingEnrollmentChecks
{
    internal static void Run()
    {
        var source = new GasBillingEnrollmentSource([Guid.NewGuid()], "2021-01-01", "2021-02-01",
            "gas_billing_scope_reassessment_v1", "final_allocated_billing_v1", "gas_final_daily_entry_cash_v1",
            "currency_minor_units_to_even_v1", "manual_scheduled_quantity_v1", "gas_processing_instant_utc_v1",
            "current_knowledge_at_execution_v1", Guid.NewGuid(), 1, "review", 0, 8, 4, 30, 3);
        source.Validate();
        (source with { Mode = "automatic" }).Validate();
        foreach (var invalid in new[] {
            source with { BookIds = [] }, source with { BookIds = [source.BookIds[0], source.BookIds[0]] },
            source with { From = "2020-01-01" }, source with { To = source.From },
            source with { DiscoveryPolicy = "recent_days" }, source with { SelectionPolicy = "operational_projection_v1" },
            source with { ProcessingConvention = "facility_business_date_end_utc_v1" }, source with { KnowledgePolicy = "current" },
            source with { RoundingPolicy = "" }, source with { ScheduledPolicy = "" }, source with { BillingProfile = "" },
            source with { PolicyId = Guid.Empty }, source with { PolicyRevision = 0 }, source with { Mode = "manual" },
            source with { ReadinessDelaySeconds = -1 }, source with { PageLimit = 9 }, source with { DispatchLimit = 9 },
            source with { ChildTimeoutSeconds = 31 }, source with { ChildMaximumAttempts = 4 } })
        {
            try { invalid.Validate(); }
            catch (MarketDataException) { continue; }
            throw new Exception("A gas scope accepted incomplete or unsupported discovery, financial or execution policy.");
        }
        var missing = JsonSerializer.SerializeToNode(source, LifecycleNative.Json)!.AsObject();
        missing.Remove("readinessDelaySeconds");
        try { _ = missing.Deserialize<GasBillingEnrollmentSource>(LifecycleNative.Json); }
        catch (JsonException) { Console.WriteLine("PASS gas enrollment: explicit scope, policy, readiness and execution limits"); return; }
        throw new Exception("Missing readiness delay silently became zero.");
    }
}
