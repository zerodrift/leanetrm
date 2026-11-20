using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

internal sealed record AsyncReadError(string Code,string Message);
internal sealed record AsyncReadFailure(string Operation,Guid RequestId,string ExceptionType,string? Method,string? SqlState);
internal sealed record AsyncReadCompletion(AsyncReadRequest Request,ReadPlan Plan,string State,ReadOnlyMemory<byte>? Result,AsyncReadError? Error,
    Func<NpgsqlConnection,CancellationToken,Task>? Recheck=null,IReadOnlyDictionary<Guid,string>? ReadReferences=null);

// Ephemeral reads for the workbench. This channel retains request hashes,
// never durable outcomes, and owns every worker until cancellation and completion.
internal sealed class AsyncReadChannel:IAsyncDisposable
{
    internal const string Protocol="async_reads_v1";
    internal const int MaximumActive=2,MaximumRequests=128,MaximumRequestBytes=32*1024,MaximumResultBytes=16*1024*1024;
    internal static readonly TimeSpan Lifetime=TimeSpan.FromMinutes(5),ReadLimit=TimeSpan.FromSeconds(60);
    static readonly JsonSerializerOptions ResultJson=new(AsyncReadContract.Json){MaxDepth=64,Converters={new ResultStringConverter()}};
    readonly object gate=new();
    readonly CancellationTokenSource lifetime;
    readonly CancellationToken stopping;
    readonly Action<AsyncReadFailure>? reportFailure;
    readonly Channel<AsyncReadCompletion> responses=Channel.CreateBounded<AsyncReadCompletion>(new BoundedChannelOptions(MaximumActive){SingleReader=true,FullMode=BoundedChannelFullMode.Wait});
    readonly Dictionary<Guid,Pending> requests=[];
    readonly HashSet<Guid> notAdmitted=[];
    bool connected,closed;
    Task? disposal;
    int active;
    internal Guid Id{get;}
    internal bool Closed=>closed||stopping.IsCancellationRequested;
    internal CancellationToken Stopping=>stopping;
    internal AsyncReadChannel(Guid id,CancellationToken applicationStopping,Action<AsyncReadFailure>? reportFailure=null)
    {
        Id=id;lifetime=CreateLifetime(applicationStopping);stopping=lifetime.Token;this.reportFailure=reportFailure;
    }
    sealed class Pending(string hash,CancellationTokenSource cancellation)
    {
        internal string Hash=>hash;
        internal CancellationTokenSource Cancellation=>cancellation;
        internal Task Work=Task.CompletedTask;
    }
    static CancellationTokenSource CreateLifetime(CancellationToken stopping)
    {
        var result=CancellationTokenSource.CreateLinkedTokenSource(stopping);result.CancelAfter(Lifetime);return result;
    }
    internal void Connect()
    {
        lock(gate){RequireOpen();if(connected)throw Refusal(409,"async_channel_connected","This response channel already has a listener.");connected=true;}
    }
    internal static void Validate(AsyncReadRequest request)
    {
        if(request.ChannelId==Guid.Empty||request.RequestId==Guid.Empty)throw AsyncReadContract.Invalid();
        _=AsyncReadContract.Decode(request);
        if(JsonSerializer.SerializeToUtf8Bytes(request,JsonSerializerOptions.Web).Length>MaximumRequestBytes)
            throw Refusal(413,"async_request_size","The read request exceeds the request limit.");
    }
    internal void Submit(AsyncReadRequest request,ReadPlan plan)
    {
        Validate(request);
        if(request.ChannelId!=Id||request.Operation!=plan.Input.Operation)throw Refusal(422,"async_request","The request and selected input frame must match.");
        var decoded=AsyncReadContract.Decode(request);
        var hash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {request.ChannelId,request.RequestId,request.Operation,Body=decoded.Body},AsyncReadContract.Json)));
        lock(gate)
        {
            RequireOpen();
            if(!connected)throw Refusal(409,"async_channel_not_ready","Connect the response stream before submitting reads.");
            if(notAdmitted.Contains(request.RequestId))throw Refusal(409,"async_request_cancelled","This request was cancelled before admission and cannot execute.");
            if(requests.TryGetValue(request.RequestId,out var prior))
            {
                if(prior.Hash!=hash)throw Refusal(409,"async_request_conflict","This request identity already names different inputs.");
                return;
            }
            RequireIdentityCapacity();
            if(active>=MaximumActive)throw Refusal(429,"async_busy","This response channel already has two active reads.");
            var cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);cancellation.CancelAfter(ReadLimit);
            var pending=new Pending(hash,cancellation);requests.Add(request.RequestId,pending);active++;
            // The worker owns explicit selection and authority. It must not retain
            // the submitting HttpContext, which ASP.NET can dispose before it runs.
            if(ExecutionContext.IsFlowSuppressed())pending.Work=Task.Run(()=>Evaluate(request,plan,pending));
            else using(ExecutionContext.SuppressFlow())pending.Work=Task.Run(()=>Evaluate(request,plan,pending));
        }
    }
    async Task Evaluate(AsyncReadRequest request,ReadPlan plan,Pending pending)
    {
        AsyncReadCompletion completion;
        try
        {
            var outcome=await AsyncReadVisibility.Observed(plan).Execute(pending.Cancellation.Token);pending.Cancellation.Token.ThrowIfCancellationRequested();
            var output=new ResultBuffer(pending.Cancellation.Token);
            using(var writer=new Utf8JsonWriter(output,new JsonWriterOptions{MaxDepth=ResultJson.MaxDepth}))
            {
                // Use the budgeted writer directly: DOM converters may otherwise
                // buffer the entire value before a Stream sees its first write.
                JsonSerializer.Serialize(writer,outcome.Value,ResultJson);writer.Flush();
            }
            pending.Cancellation.Token.ThrowIfCancellationRequested();
            // The completion owns this buffer until publication or channel disposal.
            completion=new(request,plan,"completed",output.Written,null,outcome.Recheck,outcome.ReadReferences);
        }
        catch(OperationCanceledException){completion=new(request,plan,"cancelled",null,new("request_cancelled","The read was cancelled or exceeded its time limit."));}
        catch(Exception e)
        {
            var error=Error(e);completion=new(request,plan,"failed",null,error);
            if(error.Code=="async_read_failed")ReportFailure(request,e);
        }
        lock(gate)
        {
            active--;
            if(!Closed&&!responses.Writer.TryWrite(completion))Close();
        }
    }
    static AsyncReadException ResultLimit()=>Refusal(422,"async_result_size","The read result exceeds the response limit.");
    sealed class ResultBuffer(CancellationToken ct):IBufferWriter<byte>
    {
        readonly ArrayBufferWriter<byte> buffer=new(16*1024);
        internal ReadOnlyMemory<byte> Written=>buffer.WrittenMemory;
        public void Advance(int count)
        {
            ct.ThrowIfCancellationRequested();
            if(count>MaximumResultBytes-buffer.WrittenCount)throw ResultLimit();
            buffer.Advance(count);
        }
        public Memory<byte> GetMemory(int sizeHint=0)
        {
            ct.ThrowIfCancellationRequested();
            // The JSON writer reserves for worst-case escaping. Honor valid
            // hints without treating reserved capacity as serialized bytes.
            if(sizeHint>6*MaximumResultBytes+256)throw ResultLimit();
            var size=Math.Max(sizeHint,16*1024);
            // Short spans force even a synchronous DOM converter to commit its
            // bytes regularly, where Advance enforces the logical result cap.
            return buffer.GetMemory(size)[..size];
        }
        public Span<byte> GetSpan(int sizeHint=0)=>GetMemory(sizeHint).Span;
    }
    sealed class ResultStringConverter:JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>reader.GetString();
        public override void Write(Utf8JsonWriter writer,string value,JsonSerializerOptions options)
        {
            // Every UTF-16 code unit needs at least one JSON byte. Refuse a
            // definitely oversized token before the JSON writer rents its buffer.
            if(value.Length>MaximumResultBytes)throw ResultLimit();
            writer.WriteStringValue(value);
        }
    }
    void ReportFailure(AsyncReadRequest request,Exception error)
    {
        var method=error.TargetSite;
        var diagnostic=new AsyncReadFailure(request.Operation,request.RequestId,error.GetType().FullName??error.GetType().Name,
            method is null?null:$"{method.DeclaringType?.FullName}.{method.Name}",(error as PostgresException)?.SqlState);
        // Diagnostic delivery must not replace the original terminal response.
        try{reportFailure?.Invoke(diagnostic);}catch{ }
    }
    internal string Cancel(Guid requestId)
    {
        lock(gate)
        {
            RequireOpen();
            if(requestId==Guid.Empty)throw AsyncReadContract.Invalid();
            if(requests.TryGetValue(requestId,out var pending))
            {
                pending.Cancellation.Cancel();return "cancellation_requested";
            }
            // A cancelled HTTP submission may never arrive, or may arrive after
            // this cancellation. Reserve its identity before promising no work.
            if(!notAdmitted.Contains(requestId)){RequireIdentityCapacity();notAdmitted.Add(requestId);}
            return "not_admitted";
        }
    }
    void RequireIdentityCapacity()
    {
        if(requests.Count+notAdmitted.Count>=MaximumRequests)
            throw Refusal(429,"async_channel_limit","Open a fresh response channel after this channel's requests complete.");
    }
    internal async Task<AsyncReadCompletion> Read(CancellationToken ct)=>await responses.Reader.ReadAsync(ct);
    internal ChannelReader<AsyncReadCompletion> Responses=>responses.Reader;
    internal void Close()
    {
        lock(gate){if(closed)return;closed=true;lifetime.Cancel();responses.Writer.TryComplete();}
    }
    public ValueTask DisposeAsync()
    {
        lock(gate)
        {
            // Stream shutdown and explicit replacement can retire the same channel
            // concurrently. Every caller must await the same worker drain.
            if(disposal is null){Close();disposal=Drain([..requests.Values]);}
            return new(disposal);
        }
    }
    async Task Drain(Pending[] pending)
    {
        await Task.WhenAll(pending.Select(request=>request.Work));
        foreach(var request in pending)request.Cancellation.Dispose();
        lifetime.Dispose();
    }
    void RequireOpen(){if(Closed)throw Refusal(410,"async_channel_unavailable","This response channel ended. Open a new channel and explicitly request the read again.");}
    internal static AsyncReadException Refusal(int status,string code,string message)=>new(status,code,message);
    internal static AsyncReadError Error(Exception e)
    {
        AsyncReadError error=e switch
        {
            AsyncReadException a=>new(a.Code,a.Message),
            MarketDataException m=>new(m.Code,m.Message),
            TemporalException t=>new(t.Code,t.Message),
            PreviewDeniedException or PreviewSessionEnded=>new("access_denied","Current authorization no longer permits this read."),
            PostgresException p when p.SqlState=="42501"=>new("access_denied","Current authorization no longer permits this read."),
            _=>new("async_read_failed","The read could not be completed.")
        };
        return error.Code.Length<=128&&error.Message.Length<=1000?error:new("async_read_failed","The read could not be completed.");
    }
}
