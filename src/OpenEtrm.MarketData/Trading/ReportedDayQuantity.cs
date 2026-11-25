using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Trading;

// An absolute quantity from operating evidence, distinct from a delivery quantity
// and its contractual denominator. Unknown and not-yet-applicable are not zero.
public sealed record ReportedDayQuantity
{
    public const string Assessed = "assessed", Unassessed = "unassessed", NotApplicable = "not_applicable";
    public int SourceSlot { get; }
    public DateOnly Day { get; }
    public string State { get; }
    public ExactQuantity? Quantity { get; }
    public ImmutableArray<DeliveryQuantityEvidence> Evidence { get; }

    public ReportedDayQuantity(int sourceSlot, DateOnly day, string state, ExactQuantity? quantity,
        ImmutableArray<DeliveryQuantityEvidence> evidence)
    {
        if (sourceSlot < 0 || day == default || state is not (Assessed or Unassessed or NotApplicable) ||
            (state == Assessed ? quantity is null || quantity.NumeratorValue < 0 || evidence.IsDefaultOrEmpty : quantity is not null) ||
            evidence.IsDefault || evidence.Any(item => item is null || item.RevisionId == Guid.Empty ||
                string.IsNullOrWhiteSpace(item.Source) || string.IsNullOrWhiteSpace(item.Reference)))
            throw PhysicalDeliveryQuantities.Invalid("A reported day requires its state, source and evidence; only an assessed day has a nonnegative absolute quantity.");
        SourceSlot = sourceSlot; Day = day; State = state; Quantity = quantity; Evidence = evidence;
    }
}
