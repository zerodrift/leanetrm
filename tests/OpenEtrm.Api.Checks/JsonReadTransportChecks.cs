using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenEtrm.Api;

internal static class JsonReadTransportChecks
{
    internal static async Task Run()
    {
        bool Compressed(string method,string path)
        {var context=new DefaultHttpContext();context.Request.Method=method;context.Request.Path=path;return JsonReadTransport.Compress(context);}
        foreach(var (method,path) in new[]{("GET","/api/async-reads/events"),("POST","/api/evaluate"),("POST","/api/history"),("POST","/api/portfolio/risk"),("GET","/examples/exchange/api/workspace/catalog")})
            if(!Compressed(method,path))throw new Exception("An explicitly selected read must use compression: "+path);
        foreach(var (method,path) in new[]{("GET","/api/bootstrap"),("POST","/api/profile"),("POST","/api/futures/book"),("POST","/api/portfolio/risk/jobs"),("GET","/api/assistant/turns/events"),("GET","/auth")})
            if(Compressed(method,path))throw new Exception("Compression must remain scoped to the selected read responses: "+path);
        using var result=JsonDocument.Parse("""{"value":"-0.00000000000000000100","zero":"0","empty":"","null":null,"list":[],"date":"2020-12-18","at":"2020-12-18T16:00:00+00:00","unicode":"synthetic · value"}""");
        var channel=Guid.Parse("00000000-0000-0000-0000-000000000001");var request=Guid.Parse("00000000-0000-0000-0000-000000000002");
        foreach(var state in new[]{"completed","failed","cancelled"})
        {
            var frame=new AsyncReadFrame(AsyncReadChannel.Protocol,channel,request,"curve.evaluate.v1",state,state=="completed"?channel:null,
                state=="completed"?(ReadOnlyMemory<byte>?)JsonSerializer.SerializeToUtf8Bytes(result.RootElement):null,state=="completed"?null:new("synthetic_refusal","Synthetic input refused."));
            using var output=new MemoryStream();await JsonReadTransport.WriteEvent(output,"response",frame,CancellationToken.None);
            var encoded=Encoding.UTF8.GetString(output.ToArray());
            if(!encoded.StartsWith("event: response\ndata: ",StringComparison.Ordinal)||!encoded.EndsWith("\n\n",StringComparison.Ordinal))throw new Exception("SSE framing changed.");
            using var actual=JsonDocument.Parse(encoded["event: response\ndata: ".Length..^2]);
            var expected=JsonSerializer.SerializeToElement(new{protocol=frame.Protocol,channelId=channel,requestId=request,operation=frame.Operation,state,snapshotId=frame.SnapshotId,result=state=="completed"?(JsonElement?)result.RootElement:null,error=frame.Error},JsonSerializerOptions.Web);
            if(!JsonElement.DeepEquals(expected,actual.RootElement))throw new Exception("Raw JSON changed a response value, identity or null/missing distinction.");
        }
        var references=new Dictionary<Guid,string>{{channel,"synthetic-coordinate-proof"}};
        var transported=new AsyncReadFrame(AsyncReadChannel.Protocol,channel,request,"curve.evaluate.v1","completed",channel,
            JsonSerializer.SerializeToUtf8Bytes(result.RootElement),null,references);
        using(var output=new MemoryStream())
        {
            await JsonReadTransport.WriteEvent(output,"response",transported,CancellationToken.None);
            var encoded=Encoding.UTF8.GetString(output.ToArray());
            using var value=JsonDocument.Parse(encoded["event: response\ndata: ".Length..^2]);
            if(!JsonElement.DeepEquals(value.RootElement.GetProperty("result"),result.RootElement)||
                value.RootElement.GetProperty("readReferences").GetProperty(channel.ToString()).GetString()!=references[channel])
                throw new Exception("Read metadata must travel beside the unchanged financial result.");
        }
        using var heartbeat=new MemoryStream();await JsonReadTransport.WriteEvent(heartbeat,null,null,CancellationToken.None);
        if(Encoding.UTF8.GetString(heartbeat.ToArray())!=": ping\n\n")throw new Exception("Heartbeat framing changed.");
        await Streaming();
        Console.WriteLine("PASS JSON read transport: scoped compression, exact response parity, missing/null/empty, SSE framing and negotiated gzip flushing");
    }
    static async Task Streaming()
    {
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddResponseCompression(o=>{o.Providers.Add<GzipCompressionProvider>();o.MimeTypes=["application/json","text/event-stream"];});
        builder.Services.Configure<GzipCompressionProviderOptions>(o=>o.Level=CompressionLevel.Fastest);
        await using var app=builder.Build();app.UseWhen(JsonReadTransport.Compress,branch=>branch.UseResponseCompression());
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.MapGet("/api/async-reads/events",async context=>
        {
            var gate=release;context.Response.ContentType="text/event-stream";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();await context.Response.StartAsync(context.RequestAborted);
            await JsonReadTransport.WriteEvent(context.Response.Body,"ready",new{protocol=AsyncReadChannel.Protocol},context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            await gate.Task.WaitAsync(context.RequestAborted);
            await JsonReadTransport.WriteEvent(context.Response.Body,null,null,context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
        });
        app.MapGet("/api/bootstrap",()=>Results.Json(new{csrfToken="synthetic-token"}));
        await app.StartAsync();
        try
        {
            using var client=new HttpClient(new HttpClientHandler{AutomaticDecompression=DecompressionMethods.None}){BaseAddress=new Uri(app.Urls.Single()),Timeout=TimeSpan.FromSeconds(10)};
            foreach(var compressed in new[]{true,false})
            {
                release=new(TaskCreationOptions.RunContinuationsAsynchronously);
                using var request=new HttpRequestMessage(HttpMethod.Get,"/api/async-reads/events");request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(compressed?"gzip":"identity"));
                using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);
                if(!response.IsSuccessStatusCode||!response.Content.Headers.ContentEncoding.SequenceEqual(compressed?["gzip"]:Array.Empty<string>()))throw new Exception("Read compression negotiation changed.");
                await using var stream=await response.Content.ReadAsStreamAsync();
                await using var decoded=compressed?new GZipStream(stream,CompressionMode.Decompress,leaveOpen:true):stream;
                using var reader=new StreamReader(decoded);
                if(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))!="event: ready"||
                    !(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))!.StartsWith("data: {",StringComparison.Ordinal)||
                    await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))!="")throw new Exception("A streaming read did not flush a complete ready event.");
                if(release.Task.IsCompleted)throw new Exception("The streaming check ended before readiness was read.");
                release.SetResult();if(await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5))!=": ping\n\n")throw new Exception("The compressed stream did not finish intact.");
            }
            using var excluded=new HttpRequestMessage(HttpMethod.Get,"/api/bootstrap");excluded.Headers.AcceptEncoding.Add(new("gzip"));
            using var untouched=await client.SendAsync(excluded);if(untouched.Content.Headers.ContentEncoding.Count!=0)throw new Exception("Bootstrap token response entered read compression.");
        }
        finally{release.TrySetResult();await app.StopAsync();}
    }
}
