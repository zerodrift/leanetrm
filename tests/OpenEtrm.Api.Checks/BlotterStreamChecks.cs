using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;

// The Blotter stream's frame writer, with no database (D268):
// the frame grammar, a flush per unit before the next is asked for, the authority recheck before each frame, a refusal,
// pivot frames without rows, the count against the positions, an abort, one terminal frame, a deadline at the terminal
// frame, and PPA bindings grouped by trade.
static class BlotterStreamChecks
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public static async Task Run()
    {
        await FrameGrammar();
        await FlushesEachUnitBeforeNext();
        await AuthorizesBeforeEachFrame();
        await RefusesFrameAfterSessionChange();
        await PivotFramesCarryNoRows();
        await CountMismatch();
        await AbortWritesNothing();
        await TerminalOnce();
        await DeadlineBeforeComplete();
        await DeadlineDuringComplete();
        PpaBindingsGroupedByTrade();
    }

    sealed class Sink:MemoryStream
    {
        public int Flushes;
        public override Task FlushAsync(CancellationToken ct){Flushes++;return base.FlushAsync(ct);}
        public string Text=>Encoding.UTF8.GetString(ToArray());
        public JsonObject[] Frames=>Text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>JsonNode.Parse(line)!.AsObject()).ToArray();
    }

    static JsonObject Row(string id)=>new(){["tradeId"]=id,["note"]="€ \"q\""};
    static ImmutableArray<JsonObject> Unit(params string[] ids)=>[..ids.Select(Row)];
    static Task Authorized(CancellationToken ct)=>Task.CompletedTask;
    static JsonObject Start(int positions)=>new(){["positions"]=positions,["canBook"]=true,["bookingUnavailableReason"]=null};

    static async IAsyncEnumerable<ImmutableArray<JsonObject>> Units(IEnumerable<ImmutableArray<JsonObject>> units,Func<int,Task>? before=null)
    {
        var index=0;
        foreach(var unit in units){if(before is not null)await before(index);index++;yield return unit;}
    }

    static async Task FrameGrammar()
    {
        var sink=new Sink();
        var writer=new BlotterStreamWriter(sink,Json);
        await writer.Start(Start(3),CancellationToken.None);
        var rows=await writer.Rows(Units([Unit("A","B"),Unit("C")]),3,true,Authorized,CancellationToken.None);
        Require(!writer.Terminated,"the rows do not end the stream");
        await writer.Complete(new JsonObject{["valued"]=3,["positions"]=3},CancellationToken.None);
        var frames=sink.Frames;
        Require(frames.Select(f=>f["type"]!.GetValue<string>()).SequenceEqual(["start","rows","rows","complete"]),"a stream is start, rows and a terminal frame");
        Require(frames[0]["positions"]!.GetValue<int>()==3&&frames[0]["bookingUnavailableReason"] is null&&frames[0].ContainsKey("bookingUnavailableReason"),"the start frame carries its fields, nulls included");
        Require(frames[1]["valued"]!.GetValue<int>()==2&&frames[2]["valued"]!.GetValue<int>()==3,"valued is cumulative");
        var sent=frames[1]["rows"]!.AsArray().Concat(frames[2]["rows"]!.AsArray()).Select(r=>r!["tradeId"]!.GetValue<string>());
        Require(sent.SequenceEqual(["A","B","C"])&&rows.Select(r=>r["tradeId"]!.GetValue<string>()).SequenceEqual(["A","B","C"]),"rows keep their order, in the frames and in the result");
        Require(frames[1]["rows"]![0]!["note"]!.GetValue<string>()=="€ \"q\""&&sink.Text.EndsWith("}\n",StringComparison.Ordinal)&&writer.Terminated,"each frame is one line; the stream is terminated");
    }

    static async Task FlushesEachUnitBeforeNext()
    {
        var sink=new Sink();
        var writer=new BlotterStreamWriter(sink,Json);
        var release=new TaskCompletionSource();
        var running=writer.Rows(Units([Unit("A"),Unit("B")],index=>index==1?release.Task:Task.CompletedTask),2,true,Authorized,CancellationToken.None);
        for(var i=0;i<200&&sink.Flushes<1;i++)await Task.Delay(10);
        Require(!running.IsCompleted&&sink.Text.EndsWith("}\n",StringComparison.Ordinal)&&sink.Text.Contains("\"A\"",StringComparison.Ordinal)&&!sink.Text.Contains("\"B\"",StringComparison.Ordinal),
            "the first unit is on the stream, whole, before the second is produced");
        release.SetResult();
        await running;
        Require(sink.Flushes>=2,"every frame is flushed");
    }

    static async Task AuthorizesBeforeEachFrame()
    {
        var sink=new Sink();
        var writer=new BlotterStreamWriter(sink,Json);
        var calls=0;
        var seen=new List<int>();
        await writer.Rows(Units([Unit("A"),Unit("B"),Unit("C")]),3,true,_=>{calls++;seen.Add(sink.Frames.Length);return Task.CompletedTask;},CancellationToken.None);
        Require(calls==3&&seen.SequenceEqual([0,1,2]),"authority is rechecked before each rows frame is written");
    }

    static async Task RefusesFrameAfterSessionChange()
    {
        var sink=new Sink();
        var writer=new BlotterStreamWriter(sink,Json);
        var calls=0;
        await Throws<PreviewDeniedException>(()=>writer.Rows(Units([Unit("A"),Unit("B")]),2,true,_=>++calls==2?throw new PreviewDeniedException():Task.CompletedTask,CancellationToken.None),
            "a changed session refuses the frame");
        Require(sink.Frames.Length==1&&!sink.Text.Contains("\"B\"",StringComparison.Ordinal)&&!writer.Terminated,"only the first frame was sent; the caller ends the stream");
    }

    static async Task PivotFramesCarryNoRows()
    {
        var sink=new Sink();
        var writer=new BlotterStreamWriter(sink,Json);
        var rows=await writer.Rows(Units([Unit("A","B"),Unit("C")]),3,false,Authorized,CancellationToken.None);
        var frames=sink.Frames;
        Require(frames.All(f=>f["rows"]!.AsArray().Count==0)&&frames[0]["valued"]!.GetValue<int>()==2&&frames[1]["valued"]!.GetValue<int>()==3&&rows.Count==3,
            "pivot frames advance valued without rows, and the rows are still returned");
    }

    static async Task CountMismatch()
    {
        await Throws<InvalidOperationException>(()=>new BlotterStreamWriter(new Sink(),Json).Rows(Units([Unit("A","B","C")]),2,true,Authorized,CancellationToken.None),"more rows than positions");
        var sink=new Sink();
        await Throws<InvalidOperationException>(()=>new BlotterStreamWriter(sink,Json).Rows(Units([Unit("A","B","C")]),4,true,Authorized,CancellationToken.None),"fewer rows than positions");
        Require(sink.Frames.Length==1,"a short run sends what it valued, then refuses at the end");
    }

    static async Task AbortWritesNothing()
    {
        var sink=new Sink();
        var writer=new BlotterStreamWriter(sink,Json);
        using var cts=new CancellationTokenSource();
        await Throws<OperationCanceledException>(()=>writer.Rows(Units([Unit("A"),Unit("B")],index=>{if(index==1)cts.Cancel();return Task.CompletedTask;}),2,true,Authorized,cts.Token),"an abort stops the run");
        Require(sink.Frames.Length==1&&!writer.Terminated,"no frame follows the abort, and the writer writes no error of its own");
    }

    static async Task TerminalOnce()
    {
        var sink=new Sink();
        var writer=new BlotterStreamWriter(sink,Json);
        await writer.Complete(new JsonObject{["valued"]=0,["positions"]=0},CancellationToken.None);
        await Throws<InvalidOperationException>(()=>writer.Error("late","too late"),"a second terminal frame");
        Require(sink.Frames.Length==1,"nothing follows the terminal frame");
        var failed=new Sink();
        var other=new BlotterStreamWriter(failed,Json);
        await other.Error("preview_denied","Not allowed.");
        var frame=failed.Frames[0];
        Require(other.Terminated&&frame["type"]!.GetValue<string>()=="error"&&frame["code"]!.GetValue<string>()=="preview_denied"&&frame["message"]!.GetValue<string>()=="Not allowed.","an error frame names its code and message");
        await Throws<InvalidOperationException>(()=>other.Complete(new JsonObject(),CancellationToken.None),"a complete after an error");
    }

    // A request deadline that has passed when complete is due leaves the stream open for its error frame, never truncated.
    static async Task DeadlineBeforeComplete()
    {
        var sink=new Sink();
        var writer=new BlotterStreamWriter(sink,Json);
        using var deadline=new CancellationTokenSource();
        deadline.Cancel();
        await Throws<OperationCanceledException>(()=>writer.Complete(new JsonObject{["valued"]=0,["positions"]=0},deadline.Token),"a passed deadline refuses complete");
        Require(!writer.Terminated&&sink.Text.Length==0,"nothing of complete is written and the stream stays open");
        await writer.Error("request_cancelled","The request was cancelled or exceeded its time limit.");
        Require(sink.Frames.Select(f=>f["type"]!.GetValue<string>()).SequenceEqual(["error"])&&writer.Terminated,"the error frame is the one terminal frame");
    }

    sealed class DeadlineSink(CancellationTokenSource deadline):MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct){await deadline.CancelAsync();await base.WriteAsync(buffer,ct);}
    }

    // A deadline that fires while complete is being written does not cut the frame.
    static async Task DeadlineDuringComplete()
    {
        using var deadline=new CancellationTokenSource();
        var sink=new DeadlineSink(deadline);
        var writer=new BlotterStreamWriter(sink,Json);
        await writer.Complete(new JsonObject{["valued"]=0,["positions"]=0},deadline.Token);
        var text=Encoding.UTF8.GetString(sink.ToArray());
        Require(writer.Terminated&&text.EndsWith("}\n",StringComparison.Ordinal)&&JsonNode.Parse(text)!["type"]!.GetValue<string>()=="complete","complete is written whole");
        await Throws<InvalidOperationException>(()=>writer.Error("request_cancelled","late"),"an error after a written complete");
    }

    // A PPA trade's bindings are one unit only when they arrive together; any other order is a defect, never a reordering.
    static void PpaBindingsGroupedByTrade()
    {
        Guid a=Guid.NewGuid(),b=Guid.NewGuid();
        var units=PpaBlotter.ByTrade([(a,1),(a,2),(b,3)],binding=>binding.Item1);
        Require(units.Select(u=>string.Join(",",u.Select(binding=>binding.Item2))).SequenceEqual(["1,2","3"]),"each trade's bindings are one unit, in order");
        try{PpaBlotter.ByTrade([(a,1),(b,2),(a,3)],binding=>binding.Item1);}
        catch(InvalidOperationException){return;}
        throw new Exception("expected InvalidOperationException: a trade whose bindings are not together");
    }

    static async Task Throws<T>(Func<Task> act,string message)where T:Exception
    {
        try{await act();}
        catch(T){return;}
        throw new Exception($"expected {typeof(T).Name}: {message}");
    }

    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
}
