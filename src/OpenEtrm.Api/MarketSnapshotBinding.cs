using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal static class MarketSnapshotBinding
{
    // The operation has already resolved and authorized this knowledge context.
    // Attaching it changes no observation, quote cutoff or market value.
    internal static MarketSnapshot Bind(MarketSnapshot snapshot,Guid context,DateTimeOffset known)
    {
        var selected=new MarketKnowledge(context,known);
        if(snapshot.Knowledge is {} previous&&previous!=selected)
            throw new MarketDataException("market_knowledge_context","A market snapshot cannot enter a different resolved knowledge context.");
        return snapshot.Knowledge is null?snapshot.WithKnowledge(context,known):snapshot;
    }
}
