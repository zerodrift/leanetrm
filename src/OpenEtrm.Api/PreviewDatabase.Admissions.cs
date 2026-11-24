using Npgsql;

namespace OpenEtrm.Api;

public sealed partial class PreviewDatabase
{
    // A retained worker binds its lease before it has enough metadata to create
    // this adapter. Adopt that exact live transaction for source-read admission;
    // this scope owns successful commit and never serializes pending callbacks.
    internal OwnedReadTransaction OwnReadTransaction(NpgsqlConnection connection,NpgsqlTransaction transaction)
    {
        if(transaction.Connection!=connection)
            throw new InvalidOperationException("Read admission requires the exact live owned connection and transaction.");
        var admissions=new List<ICommitAdmission>();
        commitAdmissions.Add(connection,admissions);
        return new(this,connection,transaction,admissions);
    }

    // A temporary view binds request state inside a savepoint and restores it
    // when its read ends. An admission that reads that state is required while
    // it exists, and the restore leaves the authority that outlives it, never
    // a callback to rolled-back state. Every other admission stays pending for
    // commit. A refused read returns nothing and keeps no view admission.
    internal async Task<T> TemporaryView<T>(NpgsqlConnection connection,string savepoint,Func<Task<T>> read,CancellationToken ct)
    {
        if(!commitAdmissions.TryGetValue(connection,out var pending))
            throw new InvalidOperationException("A temporary view belongs to this database adapter's live owned transaction.");
        await using(var save=ScheduleStore.Command(connection,"SAVEPOINT "+savepoint))await save.ExecuteNonQueryAsync(ct);
        var outer=pending.Count;var required=false;
        try
        {
            var result=await read();
            foreach(var admission in pending.Skip(outer).OfType<IViewAdmission>().ToArray())await admission.RequireCommit(connection,ct);
            required=true;
            return result;
        }
        finally
        {
            for(var index=pending.Count-1;index>=outer;index--)
                if(pending[index] is IViewAdmission admission)
                {
                    if(required)pending[index]=admission.Retained;
                    else pending.RemoveAt(index);
                }
            await using var restore=ScheduleStore.Command(connection,$"ROLLBACK TO SAVEPOINT {savepoint};RELEASE SAVEPOINT {savepoint}");
            await restore.ExecuteNonQueryAsync(ct);
        }
    }

    internal sealed class OwnedReadTransaction(PreviewDatabase database,NpgsqlConnection connection,NpgsqlTransaction transaction,
        List<ICommitAdmission> admissions):IDisposable
    {
        private bool finished;
        internal async Task Commit(CancellationToken ct,ICommitAdmission? result=null,bool commitUncancelled=false)
        {
            if(finished||transaction.Connection!=connection)
                throw new InvalidOperationException("This read admission no longer owns a live transaction.");
            try
            {
                if(result is not null)await result.RequireCommit(connection,ct);
                foreach(var admission in admissions)await admission.RequireCommit(connection,ct);
                // Native readers also construct operation coordinates. Release
                // every binding after admission checks, including those paths.
                await using(var finish=ScheduleStore.Command(connection,"SELECT security.finish_operation_read()"))
                    await finish.ExecuteNonQueryAsync(ct);
                await transaction.CommitAsync(commitUncancelled?CancellationToken.None:ct);
            }
            finally{Dispose();}
        }
        public void Dispose()
        {
            if(finished)return;
            finished=true;
            database.commitAdmissions.Remove(connection);
            admissions.Clear();
        }
    }
}
