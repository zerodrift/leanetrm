using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenEtrm.Api;

// The streamed Blotter's wire format (D268 in docs/decisions.md):
// newline-delimited JSON, one frame per line, flushed as written: start, rows, then exactly one of complete or error.
// Frames are written only from the request's own flow, so the writer is sequential and takes no lock.
internal sealed class BlotterStreamWriter(Stream body,JsonSerializerOptions json)
{
    internal const string ContentType="application/x-ndjson; charset=utf-8";

    internal bool Terminated{get;private set;}

    internal Task Start(JsonObject start,CancellationToken ct)=>Write("start",start,ct);

    // One rows frame per unit: the authority recheck, the write, the flush, and only then the next unit is asked for.
    // Pivot mode sends no rows, but valued still advances. Returns every row, in order.
    internal async Task<List<JsonObject>> Rows(IAsyncEnumerable<ImmutableArray<JsonObject>> units,int positions,bool rowsInFrames,
        Func<CancellationToken,Task> authorize,CancellationToken ct)
    {
        var all=new List<JsonObject>();
        await foreach(var unit in units.WithCancellation(ct))
        {
            ct.ThrowIfCancellationRequested();
            var valued=all.Count+unit.Length;
            if(valued>positions)throw new InvalidOperationException($"The run valued {valued} positions of the {positions} it announced.");
            await authorize(ct);
            await Write("rows",frame=>
            {
                frame.WriteNumber("valued",valued);
                frame.WriteStartArray("rows");
                if(rowsInFrames)foreach(var row in unit)row.WriteTo(frame,json);
                frame.WriteEndArray();
            },ct);
            all.AddRange(unit);
        }
        if(all.Count!=positions)throw new InvalidOperationException($"The run valued {all.Count} positions of the {positions} it announced.");
        return all;
    }

    // A deadline that has passed by then leaves the stream open for its error frame; once begun, complete is written whole.
    internal Task Complete(JsonObject complete,CancellationToken ct)=>Write("complete",complete,ct,terminal:true);

    // The terminal frame of a failed run is written even when the request has been aborted.
    internal Task Error(string code,string message)=>Write("error",frame=>{frame.WriteString("code",code);frame.WriteString("message",message);},CancellationToken.None,terminal:true);

    Task Write(string type,JsonObject fields,CancellationToken ct,bool terminal=false)=>Write(type,frame=>
    {
        foreach(var (name,value) in fields){frame.WritePropertyName(name);if(value is null)frame.WriteNullValue();else value.WriteTo(frame,json);}
    },ct,terminal);

    async Task Write(string type,Action<Utf8JsonWriter> fields,CancellationToken ct,bool terminal=false)
    {
        if(Terminated)throw new InvalidOperationException("The stream has ended.");
        using var line=new MemoryStream();
        using(var frame=new Utf8JsonWriter(line,new JsonWriterOptions{Encoder=json.Encoder}))
        {
            frame.WriteStartObject();
            frame.WriteString("type",type);
            fields(frame);
            frame.WriteEndObject();
        }
        line.WriteByte((byte)'\n');
        ct.ThrowIfCancellationRequested();
        // A terminal frame is not cancelled part-way: a write may commit its bytes before it observes the token, and a
        // frame cut there, or followed by an error frame, would break the one terminal frame.
        if(terminal){Terminated=true;ct=CancellationToken.None;}
        await body.WriteAsync(line.GetBuffer().AsMemory(0,(int)line.Length),ct);
        await body.FlushAsync(ct);
    }
}
