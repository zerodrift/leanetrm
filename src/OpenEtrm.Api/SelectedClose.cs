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
            var captured=(await new KnowledgeStore(database.Manifest.CatalogId).Capture(connection,new(PreviewSession.BusinessDay(frame).ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),null,"current"),ct)).Context;
            pin=PinnedContext.Retained(captured,frame with{ContextId=captured.ContextId});
            await pin.Bind(connection,database.Manifest.CatalogId,ct);
        }
        if(PreviewSession.BusinessDay(frame)!=DateWorkspace.Date(pin.Context!.EffectiveOn))
            throw new TemporalException("market_date","Choose a close on the selected effective date.");
        return pin;
    }
    // The session a request reads: "always latest" releases only that session's pinned prices (D334).
    public static async Task BindSession(NpgsqlConnection connection,Guid catalog,string session,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.use_close_session($1,$2)",catalog,session);
        await command.ExecuteNonQueryAsync(ct);
    }
    // New work at a recorded close requires the close in force for its session and business day. A replaced close names the
    // close in force; one this reader cannot see is refused rather than answered with the close it replaced.
    public static async Task RequireInForce(NpgsqlConnection connection,Guid catalog,Guid close,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT f.id,security.market_close_visible($1,f.id) FROM market_data.market_closes s "+
            "CROSS JOIN LATERAL(SELECT security.close_in_force(s.catalog_id,s.session_code,s.business_date) id)f WHERE(s.catalog_id,s.id)=($1,$2)",catalog,close);
        await using var reader=await command.ExecuteReaderAsync(ct);
        if(!await reader.ReadAsync(ct))throw new PreviewDeniedException();
        var (current,visible)=(reader.GetGuid(0),reader.GetBoolean(1));
        if(current==close)return;
        throw visible?new MarketDataException("close_replaced",$"This close has been replaced. Use the close in force for its session and business day: {current}.")
            :new MarketDataException("close_not_visible","The close in force for this session and business day is not visible to you; no older close is used in its place.");
    }
}
