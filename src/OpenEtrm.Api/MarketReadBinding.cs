using Npgsql;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// Borrow a date context within one authorized transaction. PostgreSQL nests
// same-named savepoints, so disposal restores the immediately enclosing scope.
internal sealed class MarketReadBinding : IAsyncDisposable
{
    readonly NpgsqlConnection connection;
    private MarketReadBinding(NpgsqlConnection connection)=>this.connection=connection;

    internal static async Task<MarketReadBinding> Enter(NpgsqlConnection connection,Guid catalog,
        ScheduleContext? parent,ScheduleContext selected,CancellationToken ct)
    {
        if(parent is not null&&parent!=selected)
            throw new TemporalException("temporal_context","The read cannot widen its selected knowledge view.");
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT market_read_scope"))
            await save.ExecuteNonQueryAsync(ct);
        var scope=new MarketReadBinding(connection);
        try
        {
            if(parent is null)await PinnedContext.BindExact(connection,catalog,selected,ct);
            return scope;
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await using var restore=ScheduleStore.Command(connection,"ROLLBACK TO SAVEPOINT market_read_scope;RELEASE SAVEPOINT market_read_scope");
        await restore.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
