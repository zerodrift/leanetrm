using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// D213: the valuation failures that concern one position at the selected close. A trading read
// returns such a position with this code instead of failing, and a portfolio risk run leaves it out
// and lists it; every other failure still fails them.
static class ValuationStatus
{
    public static bool PositionSpecific(string? code)=>code is "source_expired" or "missing_observation";
    public static bool PositionSpecific(MarketDataException error)=>PositionSpecific(error.Code);
}
