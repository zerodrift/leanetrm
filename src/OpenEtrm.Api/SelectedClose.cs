using System.Globalization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// The exact context a read at the header's selected close runs under. A historical view keeps its own context, whose
// effective date must be the close's; current working data captures current knowledge on the close's date. The caller has
// bound the session's pin; a context captured here is bound here, since a request binds its market context exactly once.
public static class SelectedClose
{
    public static async Task<PinnedContext> Pin(NpgsqlConnection connection,PreviewDatabase database,PinnedContext pin,CancellationToken ct)
    {
        var frame=pin.RequireFrame();
        if(pin.Context is null)
        {
            var captured=(await new KnowledgeStore(database.Manifest.CatalogId).Capture(connection,new(frame.At.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),null,"current"),ct)).Context;
            pin=PinnedContext.Retained(captured,frame with{ContextId=captured.ContextId});
            await pin.Bind(connection,database.Manifest.CatalogId,ct);
        }
        if(DateOnly.FromDateTime(frame.At.UtcDateTime)!=DateWorkspace.Date(pin.Context!.EffectiveOn))
            throw new TemporalException("market_date","Choose a close on the selected effective date.");
        return pin;
    }
}
