using System.Threading.Channels;
using Npgsql;

namespace OpenEtrm.Api;

// One listener per database/API process. The channel is a single coalescing hint,
// never an event log or a queue of calculations. Reconnection wakes every reader
// to reconcile the durable authorized head, even when notifications were lost.
public sealed class CalculationChanges: IAsyncDisposable
{
    private readonly PreviewDatabase database;
    private readonly CancellationTokenSource stopping=new();
    private readonly Dictionary<Guid,Channel<bool>> readers=[];
    private readonly Task listener;
    private readonly object gate=new();
    public CalculationChanges(PreviewDatabase database){this.database=database;listener=Listen();}
    public (Guid Id,ChannelReader<bool> Reader) Subscribe()
    {
        var channel=Channel.CreateBounded<bool>(new BoundedChannelOptions(1){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true});
        var id=Guid.NewGuid();lock(gate)readers.Add(id,channel);return(id,channel.Reader);
    }
    public void Remove(Guid id){lock(gate)if(readers.Remove(id,out var channel))channel.Writer.TryComplete();}
    private void Wake(){lock(gate)foreach(var channel in readers.Values)channel.Writer.TryWrite(true);}
    private async Task Listen()
    {
        var ct=stopping.Token;
        while(!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection=database.CalculationListener();
                connection.Notification+=(_,_)=>Wake();
                await connection.OpenAsync(ct);
                await using(var command=new NpgsqlCommand("LISTEN openetrm_calculation_changes",connection))await command.ExecuteNonQueryAsync(ct);
                Wake();
                while(!ct.IsCancellationRequested)await connection.WaitAsync(ct);
            }
            catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            catch(Exception e)when(e is NpgsqlException or IOException)
            {
                Wake();
                try{await Task.Delay(TimeSpan.FromSeconds(1),ct);}catch(OperationCanceledException)when(ct.IsCancellationRequested){break;}
            }
        }
    }
    public async ValueTask DisposeAsync(){await stopping.CancelAsync();await listener;stopping.Dispose();lock(gate){foreach(var reader in readers.Values)reader.Writer.TryComplete();readers.Clear();}}
}
