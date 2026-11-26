using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

internal static class GasBillingReconciliationChecks
{
    internal static void Run()
    {
        var fixture = new CommodityLookupFixture(1);
        ExactQuantity Q(string value) => ExactQuantity.FromQuantity(new(ExactDecimal.Parse(value), fixture.Quote.Quantity));
        var start = new DateTimeOffset(2021, 11, 7, 5, 0, 0, TimeSpan.Zero);
        var day = new GasDailyContract("gas/0/quantity", 0, 0, new(2021, 11, 7), start, start.AddHours(25), Q("100"));
        var obligation = Guid.NewGuid();
        GasBillingQuantityEvidence Evidence(string kind, string quantity) => new(obligation, day.Day, day.Start, day.End,
            new(Guid.NewGuid(), kind, kind == "allocated" ? "final" : "provisional", Q(quantity), "Synthetic reconciliation source", "manual_daily_v1"));
        var scheduled = Evidence("scheduled", "100");
        var final = Evidence("allocated", "98");
        var mismatch = GasBillingReconciliation.Assess(obligation, day, scheduled, final, GasBillingReconciliation.ManualScheduled);
        Equal(mismatch.Status, "difference");
        Equal(mismatch.FinalLessScheduled!.ToExactString(), "-2");
        Equal(mismatch.ScheduledLessContract!.ToExactString(), "0");
        var missing = GasBillingReconciliation.Assess(obligation, day, null, final, GasBillingReconciliation.ManualScheduled);
        Equal(missing.Status, "incomplete");
        if (missing.Scheduled is not null || missing.FinalLessScheduled is not null) throw new Exception("A missing schedule is not zero.");
        Equal(GasBillingReconciliation.Assess(obligation, day, scheduled, Evidence("allocated", "100"), GasBillingReconciliation.ManualScheduled).Status, "matched");
        Equal(GasBillingReconciliation.Assess(obligation, day, Evidence("scheduled", "98"), final, GasBillingReconciliation.ManualScheduled).Status, "difference");
        Reject(() => GasBillingReconciliation.Assess(obligation, day, scheduled with { ObligationId = Guid.NewGuid() }, final, GasBillingReconciliation.ManualScheduled));
        Reject(() => GasBillingReconciliation.Assess(obligation, day, scheduled with { End = start.AddHours(24) }, final, GasBillingReconciliation.ManualScheduled));
        Reject(() => GasBillingReconciliation.Assess(obligation, day, scheduled, final with { Observation = final.Observation with { Status = "provisional" } }, GasBillingReconciliation.ManualScheduled));
        Reject(() => GasBillingReconciliation.Assess(obligation, day, scheduled, final, "unknown"));
        Console.WriteLine("PASS gas reconciliation: independent exact variance, missing schedule, retained 25-hour day and source scope refusals");
    }
    static void Equal(string actual, string expected) { if (actual != expected) throw new Exception($"Expected {expected}, got {actual}"); }
    static void Reject(Action action) { try { action(); } catch (MarketDataException) { return; } throw new Exception("Expected a reconciliation refusal"); }
}
