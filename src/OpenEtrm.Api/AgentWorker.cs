using System.Data;
using Npgsql;

namespace OpenEtrm.Api;

// One agent worker per database (docs/ai-assistant.md §7.4). The local preview holds the agent-worker
// advisory lock on a connection of its own and works under the generation it started; starting one closes
// whatever an earlier generation left open. While another session holds the lock, or once this one's
// connection is lost, there is no generation and provider tests are refused; the next request tries again.
public sealed class AgentWorker(NpgsqlDataSource source):IAsyncDisposable
{
    const long Lock=730849160;
    readonly SemaphoreSlim gate=new(1,1);
    NpgsqlConnection? held;
    long generation;

    // This process's current generation, after taking the lock if it can; null while another session holds it.
    // Only the wait for the gate follows the request: the probe and the start run to completion within their own
    // timeouts, so a request that gives up never leaves the worker's connection half-used.
    public async Task<long?> Generation(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(held is not null)
            {
                // A session keeps its lock until it ends, so a connection that still answers still holds it.
                try
                {
                    if(held.State==ConnectionState.Open){await Scalar(held,"SELECT 1");return generation;}
                }
                catch(Exception e) when(e is NpgsqlException or InvalidOperationException){}
                await held.DisposeAsync();
                held=null;
            }
            var connection=await source.OpenConnectionAsync(CancellationToken.None);
            try
            {
                if(!Equals(await Scalar(connection,$"SELECT pg_try_advisory_lock({Lock})"),true)){await connection.DisposeAsync();return null;}
                generation=(long)(await Scalar(connection,"SELECT assistant.start_worker()"))!;
                held=connection;
                return generation;
            }
            catch{await connection.DisposeAsync();throw;}
        }
        finally{gate.Release();}
    }

    // A connection for worker writes, which take no request context.
    public ValueTask<NpgsqlConnection> Connection(CancellationToken ct)=>source.OpenConnectionAsync(ct);

    static async Task<object?> Scalar(NpgsqlConnection connection,string sql)
    {
        await using var command=new NpgsqlCommand(sql,connection);
        return await command.ExecuteScalarAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if(held is not null)await held.DisposeAsync();
        await source.DisposeAsync();
        gate.Dispose();
    }
}
