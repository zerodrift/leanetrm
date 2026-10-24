using System.Text.Json;
using Npgsql;

namespace OpenEtrm.Api;

public sealed record PartyRead(JsonElement Versions,ScheduleContext Context,bool CanEdit);

// Trade party reads callable without an HTTP request or workspace session, on the same terms as
// MarketReads. Party versions are knowledge-dated, so a read binds its view's cutoff; editing is
// offered only on working data.
public sealed class PartyReads(PreviewDatabase database)
{
    // The versions in force at the view's frame, as known at its cutoff.
    public async Task<PartyRead> Catalog(NpgsqlConnection connection,TradingView view,string kind,CancellationToken ct)
    {
        await view.Bind(connection,database.Manifest.CatalogId,ct);
        return new(await PartyEditors.Read(connection,database.Manifest.CatalogId,kind,view.Frame.At,ct),view.Context,
            view.Working&&await PreviewDatabase.HasPermission(connection,"WriteTradeParties",ct));
    }
}
