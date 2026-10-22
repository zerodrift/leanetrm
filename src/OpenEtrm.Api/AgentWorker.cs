using Npgsql;

namespace OpenEtrm.Api;

// One agent worker per database (docs/ai-assistant.md §7.4, §11). The API process holds the agent-worker advisory lock on
// a connection of its own, which then listens for the turns whose cancel was requested, and works under the generation
// it started; starting one closes whatever an earlier generation left open. However that connection ends, the
// generation ends with it: this process's turns are told to stop, and the lock goes with the connection, so the next
// start interrupts what they left. While another session holds the lock there is no generation and agent work is
// refused; the next request tries again. Worker writes take no request context and use a pool of their own.
public sealed class AgentWorker(NpgsqlDataSource holder,NpgsqlDataSource writes,ILogger logger):IAsyncDisposable
{
    const long Lock=730849160;
    readonly SemaphoreSlim gate=new(1,1);
    // The current generation's listening and its stop, which marks the generation lost at once.
    CancellationTokenSource? hearing;
    Task? listening;
    long generation;

    // Told of each turn whose cancel was requested.
    public Action<Guid>? Cancelled {get;set;}
    // Told when the generation ends other than by this process stopping, so the turns it ran stop too.
    public Action? Lost {get;set;}

    // This process's current generation, after taking the lock if it can; null while another session holds it, or when
    // the worker cannot start, which is logged. Only the wait for the gate follows the request: no request runs a
    // command on the lock's connection, so a request that gives up never leaves it half-used.
    public async Task<long?> Generation(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if(hearing is {IsCancellationRequested:false})return generation;
            // A lost generation's connection closes, releasing the lock, before the next start tries it.
            if(listening is {} last)await last;
            var connection=await holder.OpenConnectionAsync(CancellationToken.None);
            try
            {
                if(!Equals(await Scalar(connection,$"SELECT pg_try_advisory_lock({Lock})"),true)){await connection.DisposeAsync();return null;}
                generation=(long)(await Scalar(connection,"SELECT assistant.start_worker()"))!;
                connection.Notification+=(_,e)=>{if(Guid.TryParse(e.Payload,out var turn))Cancelled?.Invoke(turn);};
                await Scalar(connection,"LISTEN assistant_turn_cancel");
            }
            catch{await connection.DisposeAsync();throw;}
            hearing=new();
            listening=Listen(connection,hearing);
            return generation;
        }
        catch(NpgsqlException e)
        {
            logger.LogError("The agent worker could not start: {ErrorType}",e.GetType().Name);
            return null;
        }
        finally{gate.Release();}
    }

    // Waits for notifications until stopped. However else the connection ends, the generation is lost: marked so at
    // once, so no request takes it, then its turns are told before the connection closes and releases the lock. It
    // never fails, so the next start can always wait for it.
    async Task Listen(NpgsqlConnection connection,CancellationTokenSource stop)
    {
        try{while(true)await connection.WaitAsync(stop.Token);}
        catch(OperationCanceledException) when(stop.IsCancellationRequested){}
        catch(Exception e){logger.LogError("The agent worker lost its connection: {ErrorType}",e.GetType().Name);}
        var lost=!stop.IsCancellationRequested;
        stop.Cancel();
        try
        {
            if(lost)Lost?.Invoke();
            await connection.DisposeAsync();
        }
        catch(Exception e){logger.LogError("The agent worker's connection did not close cleanly: {ErrorType}",e.GetType().Name);}
    }

    // A worker write, within the runtime set's bound for one.
    public async Task<T> Write<T>(Func<NpgsqlConnection,CancellationToken,Task<T>> work)
    {
        using var scope=new CancellationTokenSource(RuntimeSet.WorkerWrite);
        await using var connection=await writes.OpenConnectionAsync(scope.Token);
        return await work(connection,scope.Token);
    }

    // A worker write that must land: one that ran out of time, lost its connection or met a conflict the database may
    // not repeat is retried until it does. Any other failure, such as a refusal or a violated invariant, is the
    // caller's.
    public async Task<T> Settle<T>(Func<NpgsqlConnection,CancellationToken,Task<T>> work)
    {
        while(true)
        {
            try{return await Write(work);}
            catch(Exception e) when(Temporary(e)){logger.LogWarning("A worker write is retried: {ErrorType}",e.GetType().Name);}
            await Task.Delay(RuntimeSet.WorkerRetry);
        }
    }

    // A failure a later attempt may not repeat: the write's own bound, a lost connection, or the database refusing it
    // for now (serialization, deadlock, lock and statement timeouts, shutdown, idle-session and connection limits).
    internal static bool Temporary(Exception e)=>e switch
    {
        OperationCanceledException=>true,
        PostgresException p=>p.SqlState is "40001" or "40P01" or "55P03" or "57014" or "57P01" or "57P02" or "57P03" or "57P05" or "53300"
            ||p.SqlState.StartsWith("08",StringComparison.Ordinal),
        NpgsqlException=>true,
        _=>false
    };

    static async Task<object?> Scalar(NpgsqlConnection connection,string sql)
    {
        await using var command=new NpgsqlCommand(sql,connection);
        return await command.ExecuteScalarAsync(CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            hearing?.Cancel();
            if(listening is {} last)await last;
        }
        finally{gate.Release();}
        await holder.DisposeAsync();
        await writes.DisposeAsync();
        gate.Dispose();
    }
}
