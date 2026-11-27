namespace OpenEtrm.MarketData.Gas;

public sealed record GasBillingQuantityEvidence(Guid ObligationId, DateOnly Day, DateTimeOffset Start,
    DateTimeOffset End, GasQuantityObservation Observation);

public sealed record GasBillingReconciliation(string Status, string ScheduledPolicy, ExactQuantity Contractual,
    GasBillingQuantityEvidence? Scheduled, GasBillingQuantityEvidence? Final,
    ExactQuantity? ScheduledLessContract, ExactQuantity? FinalLessScheduled)
{
    public const string ManualScheduled = "manual_scheduled_quantity_v1";

    public static GasBillingReconciliation Assess(Guid obligation, GasDailyContract day, GasBillingQuantityEvidence? scheduled,
        GasBillingQuantityEvidence? final, string scheduledPolicy)
    {
        if (obligation == Guid.Empty || day is null || day.Day == default || day.Start >= day.End ||
            day.Quantity is null || day.Quantity.NumeratorValue <= 0 || scheduledPolicy != ManualScheduled)
            throw Invalid();
        void Require(GasBillingQuantityEvidence? source, string kind)
        {
            if (source is null) return;
            var observation = source.Observation;
            if (source.ObligationId != obligation || source.Day != day.Day || source.Start != day.Start || source.End != day.End ||
                observation is null || observation.Id == Guid.Empty || observation.Kind != kind ||
                observation.Quantity is null || observation.Quantity.NumeratorValue < 0 || observation.Quantity.Specification != day.Quantity.Specification ||
                string.IsNullOrWhiteSpace(observation.Evidence) ||
                (kind == "scheduled" ? observation.Source != "manual_daily_v1" || observation.Status != "provisional" :
                    observation.Status != "final" || observation.Source is not ("manual_daily_v1" or "facility_allocation_adoption_v1")))
                throw Invalid();
        }
        Require(scheduled, "scheduled"); Require(final, "allocated");
        static ExactQuantity Difference(ExactQuantity a, ExactQuantity b) => a.Add(b.Scale(ExactDecimal.Parse("-1")));
        var scheduleDifference = scheduled is null ? null : Difference(scheduled.Observation.Quantity, day.Quantity);
        var finalDifference = final is null || scheduled is null ? null : Difference(final.Observation.Quantity, scheduled.Observation.Quantity);
        var state = final is null || scheduled is null ? "incomplete" :
            scheduleDifference!.NumeratorValue.IsZero && finalDifference!.NumeratorValue.IsZero ? "matched" : "difference";
        return new(state, scheduledPolicy, day.Quantity, scheduled, final, scheduleDifference, finalDifference);
    }
    static MarketDataException Invalid() => new("gas_billing_reconciliation", "Select identified current evidence for the same obligation, native interval, unit and explicit scheduled/final policies.");
}
