using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal static class TradingContext
{
    internal sealed record SourceAdmission(Guid Catalog,Guid Context,DateTimeOffset At,DateOnly BusinessDate,DateTimeOffset Until):IViewAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT security.require_admitted_business_source($1,$2,$3,$4,$5)",Catalog,Context,At,BusinessDate,Until);
            await command.ExecuteNonQueryAsync(ct);
        }
        // Restoring a view empties the request's source slot. The original read
        // deadline and current market and trade access remain required at commit.
        public ICommitAdmission Retained=>new SourceAuthority(Catalog,Until);
    }
    private sealed record SourceAuthority(Guid Catalog,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.require_futures_margin_preview($1,$2)",Catalog,Until);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
    internal static async Task<DateTimeOffset> AdmitSource(PreviewDatabase database,NpgsqlConnection connection,ScheduleContext context,DateTimeOffset at,CancellationToken ct)
    {
        var exact=PinnedContext.Exact(context);
        return await AdmitSource(database,connection,exact.ContextId!.Value,at,PublishedSchedules.ContextDate(exact),ct);
    }
    internal static async Task<DateTimeOffset> AdmitSource(PreviewDatabase database,NpgsqlConnection connection,Guid context,DateTimeOffset at,DateOnly date,CancellationToken ct)
    {
        var admission=await CaptureSource(database,connection,context,at,date,ct);
        database.RegisterCommitAdmission(connection,admission);
        return admission.Until;
    }
    internal static async Task<SourceAdmission> CaptureSource(PreviewDatabase database,NpgsqlConnection connection,Guid context,DateTimeOffset at,DateOnly date,CancellationToken ct)
    {
        RequireInstant(at);
        var catalog=database.Manifest.CatalogId;
        await using var command=ScheduleStore.Command(connection,"SELECT security.admit_trading_business_source($1,$2,$3,$4)",catalog,context,at,date);
        var until=new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync(ct))!);
        return new SourceAdmission(catalog,context,at,date,until);
    }
    internal static Task Bind(NpgsqlConnection connection,Guid catalog,InputFrame frame,ScheduleContext context,CancellationToken ct)
    {
        frame.Time.RequireDate(PublishedSchedules.ContextDate(context));
        if(frame.ContextId!=context.ContextId)throw new MarketDataException("trading_context","The held frame must retain its exact business and knowledge context.");
        return Bind(connection,catalog,context,frame.At,ct);
    }
    internal static async Task Bind(NpgsqlConnection connection,Guid catalog,ScheduleContext context,DateTimeOffset at,CancellationToken ct)
    {
        RequireInstant(at);
        var exact=PinnedContext.Exact(context);
        await using var command=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2,$3)",catalog,at,PublishedSchedules.ContextDate(exact));
        await command.ExecuteNonQueryAsync(ct);
    }
    private static void RequireInstant(DateTimeOffset at)
    {
        if(!TradingInstant.IsSupported(at))throw new MarketDataException("trading_context","Select an exact supported UTC microsecond instant and explicit business date.");
    }
}
