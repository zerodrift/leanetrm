using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasBillingOperatingChecks
{
    internal static void Run()
    {
        var late = new GasBillingOperatingContext("gas_processing_instant_utc_v1", "2021-03-10", "2021-03-10T09:00:00Z", "current", null);
        var selected = GasBillingOperatingStore.ValidateContext(late);
        if (selected.At != DateTimeOffset.Parse("2021-03-10T09:00:00Z") || selected.Day != new DateOnly(2021, 3, 10))
            throw new Exception("Processing an older gas day retains the independently selected later processing clock.");
        foreach (var context in new[] { late with { Convention = "facility_business_date_end_utc_v1" },
            late with { EvaluationAt = "2021-03-10T09:00:00" }, late with { EvaluationAt = "2021-03-10T09:00:00.0000001Z" },
            late with { BusinessDate = "2021-03-09" }, late with { KnowledgeMode = "retained" }, late with { KnowledgeMode = "latest" } })
        {
            try { GasBillingOperatingStore.ValidateContext(context); }
            catch (MarketDataException) { continue; }
            throw new Exception("An ambiguous or unsupported gas processing context was accepted.");
        }
        var definition = System.Text.Json.JsonSerializer.Deserialize<LifecycleScheduleDefinition>("""
            {"closeId":null,"target":{"handler":"gas_billing","scopeId":"a1000000-0000-0000-0000-000000000001","bindingId":"a1000000-0000-0000-0000-000000000002"},
             "action":"gas.bill_daily","occurrenceId":"a1000000-0000-0000-0000-000000000003","policyId":"a1000000-0000-0000-0000-000000000004","policyRevision":1,
             "knowledgePolicy":"current_knowledge_at_execution_v1","settingsPolicy":"source_known_exact_action_settings_v1","inputPolicy":"source_derived_only_no_cash_corrections_v1",
             "settings":{"tradeRevision":1,"optionProfileRevision":null,"swapProfileId":null,"swapProfileRevision":null,"marginPolicyId":null,"marginPolicyRevision":null,
              "gasSource":{"cashSlot":1,"day":"2021-01-01","processingConvention":"gas_processing_instant_utc_v1","businessDate":"2021-03-10","evaluationAt":"2021-03-10T09:00:00Z",
               "selectionPolicy":"final_allocated_billing_v1","billingProfile":"gas_final_daily_entry_cash_v1","decimalPlaces":2,"roundingPolicy":"currency_minor_units_to_even_v1","scheduledPolicy":"manual_scheduled_quantity_v1"}}}
            """, LifecycleNative.Json)!;
        definition.Validate();
        try { (definition with { CloseId = Guid.NewGuid() }).Validate(); }
        catch (MarketDataException) { Console.WriteLine("PASS gas schedule: explicit operating source, no borrowed financial close"); return; }
        throw new Exception("A gas billing task accepted a financial-close identity.");
    }
}
