using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// D213: the valuation failures that concern one position at the selected close. A trading read
// returns such a position with this code instead of failing; every other failure still fails it.
static class ValuationStatus
{
    public static bool PositionSpecific(MarketDataException error)=>error.Code is "source_expired" or "missing_observation";
}
