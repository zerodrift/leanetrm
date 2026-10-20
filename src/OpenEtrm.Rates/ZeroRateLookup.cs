using OpenEtrm.MarketData;

namespace OpenEtrm.Rates;

// Order and structural context are checked once over an immutable build. Source
// and authority validation remain the caller's responsibility. Exact lookup does
// not invent interpolation/extrapolation conventions or revalue retained factors.
public sealed class ZeroRateLookup
{
    public ZeroRateBuild Build { get; }

    public ZeroRateLookup(ZeroRateBuild build)
    {
        if (build is null || build.SnapshotId == Guid.Empty ||
            build.Currency.Revision.CatalogId == Guid.Empty || build.Currency.Revision.RevisionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(build.Currency.Code) || build.NumericalPolicy != ZeroRateCurve.Policy ||
            build.ReferenceDate.Year is < 1901 or > 2199 || build.Values.IsDefaultOrEmpty)
            throw new MarketDataException("rate_lookup", "Supply a complete supported rate context and nonempty ordered pillars.");
        DateOnly? previous = null;
        foreach (var value in build.Values)
        {
            if (value is null || value.Maturity.Year is < 1901 or > 2199 || value.Maturity <= build.ReferenceDate ||
                previous is { } date && value.Maturity <= date)
                throw new MarketDataException("rate_lookup", "Stored rate pillars must be nonnull, supported and strictly increasing without duplicates.");
            previous = value.Maturity;
        }
        Build = build;
    }

    public ZeroRateValue AtPillar(DateOnly maturity)
    {
        var index = FindPillar(maturity);
        if (index < 0 || Build.Values[index].Maturity != maturity)
            throw new MarketDataException("rate_coverage", "An exact stored zero-rate pillar is required; no zero-rate interpolation or extrapolation was selected.");
        return Build.Values[index];
    }

    internal int FindPillar(DateOnly maturity) => MemoryExtensions.BinarySearch<ZeroRateValue, PillarDate>(Build.Values.AsSpan(), new(maturity));

    private readonly struct PillarDate(DateOnly date) : IComparable<ZeroRateValue>
    {
        public int CompareTo(ZeroRateValue? value) => value is null ? 1 : date.CompareTo(value.Maturity);
    }
}
