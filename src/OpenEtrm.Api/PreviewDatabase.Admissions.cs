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
