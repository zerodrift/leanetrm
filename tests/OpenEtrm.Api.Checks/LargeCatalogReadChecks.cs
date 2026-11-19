using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenEtrm.Api;

// An opt-in managed HTTP workload, separate from browser and database qualification.
internal static class LargeCatalogReadChecks
{
    internal static async Task Run()
    {
        const int contracts=20000;
        var fixture=PreviewDataChecks.Fixture(contracts);
        var observations=fixture.Observations.Select(row=>JsonSerializer.SerializeToElement(row.EnumerateObject().ToDictionary(property=>property.Name,
            property=>property.Name=="value"?JsonSerializer.SerializeToElement("100.000"):property.Value))).ToImmutableArray();
        var data=new PreviewData(fixture.Manifest,fixture.Definitions,fixture.References,observations);
        var frame=fixture.Frame with{CurveContracts=ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add("future",[fixture.Frame.CurveContracts["future"][0]])};
        var source=new CatalogueSource(data.DefinitionRevision.RevisionId,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId);
        var input=new PriceInputSelection(frame.Id,source,frame.ObservationIds,false);
        object Price()=>AsyncReadProjections.Curve(new(CurveDisplay.Result(data,frame,"future",new(CurveDisplay.Version,CurveDisplay.Live)),data.Observations,input),
            PinnedContext.Current(frame),new(frame.Id,"future",IncludeSources:true));
        var catalog=new{definitions=data.Definitions,references=data.References,frames=new[]{frame},observations=Array.Empty<JsonElement>(),observationScope="on_demand"};
        using var expected=JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(Price(),AsyncReadContract.Json));
        var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddResponseCompression(options=>{options.Providers.Add<GzipCompressionProvider>();options.MimeTypes=["application/json","text/event-stream"];});
        builder.Services.Configure<GzipCompressionProviderOptions>(options=>options.Level=CompressionLevel.Fastest);
        await using var app=builder.Build();app.UseWhen(JsonReadTransport.Compress,branch=>branch.UseResponseCompression());
        app.MapGet("/api/workspace/catalog",()=>Results.Json(catalog,AsyncReadContract.Json));
        Func<HttpContext,Task> events=async context=>
        {
            await using var channel=new AsyncReadChannel(Guid.NewGuid(),context.RequestAborted);channel.Connect();
            var request=new AsyncReadRequest(channel.Id,Guid.NewGuid(),"curve.evaluate.v1",JsonSerializer.SerializeToElement(new{snapshotId=frame.Id,curve="future",includeSources=true}));
            channel.Submit(request,new ReadPlan(AsyncReadContract.Decode(request),(CancellationToken _)=>Task.FromResult(new ReadOutcome(Price(),(_,_)=>Task.CompletedTask))));
            var response=await channel.Read(context.RequestAborted);
            context.Response.ContentType="text/event-stream";context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await AsyncReads.Write(context,"response",new AsyncReadFrame(AsyncReadChannel.Protocol,channel.Id,request.RequestId,request.Operation,
                response.State,frame.Id,response.Result,response.Error),context.RequestAborted);
        };
        app.MapGet("/api/async-reads/events",(Delegate)events);
        await app.StartAsync();
        try
        {
            using var client=new HttpClient(new HttpClientHandler{AutomaticDecompression=DecompressionMethods.None}){BaseAddress=new Uri(app.Urls.Single()),Timeout=TimeSpan.FromSeconds(60)};
            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
            var timer=Stopwatch.StartNew();
            using var response=await client.GetAsync("/api/workspace/catalog");response.EnsureSuccessStatusCode();
            if(!response.Content.Headers.ContentEncoding.SequenceEqual(["gzip"]))throw new Exception("The large catalog must negotiate HTTP gzip.");
            var catalogWire=await response.Content.ReadAsByteArrayAsync();
            using var zipped=new GZipStream(new MemoryStream(catalogWire),CompressionMode.Decompress);
            using var decoded=new MemoryStream();await zipped.CopyToAsync(decoded);decoded.Position=0;
            using var complete=await JsonDocument.ParseAsync(decoded);
            if(decoded.Length<=AsyncReadChannel.MaximumResultBytes||complete.RootElement.GetProperty("definitions").GetProperty("contracts").GetArrayLength()!=contracts||
                complete.RootElement.GetProperty("references").GetProperty("futures_series").GetArrayLength()!=contracts)
                throw new Exception("The complete catalog must exceed 16MiB and retain every synthetic contract and reference.");
            var catalogMilliseconds=timer.Elapsed.TotalMilliseconds;
            var reads=new List<object>();
            for(var i=0;i<3;i++)
            {
                timer.Restart();using var eventResponse=await client.GetAsync("/api/async-reads/events");eventResponse.EnsureSuccessStatusCode();
                if(!eventResponse.Content.Headers.ContentEncoding.SequenceEqual(["gzip"]))throw new Exception("The price event must negotiate HTTP gzip.");
                var wire=await eventResponse.Content.ReadAsByteArrayAsync();using var compressed=new GZipStream(new MemoryStream(wire),CompressionMode.Decompress);
                using var reader=new StreamReader(compressed);var text=await reader.ReadToEndAsync();
                if(!text.StartsWith("event: response\ndata: ",StringComparison.Ordinal)||!text.EndsWith("\n\n",StringComparison.Ordinal))throw new Exception("The price frame was truncated.");
                using var terminal=JsonDocument.Parse(text["event: response\ndata: ".Length..^2]);
                var result=terminal.RootElement.GetProperty("result");
                if(terminal.RootElement.GetProperty("state").GetString()!="completed"||terminal.RootElement.GetProperty("snapshotId").GetGuid()!=frame.Id||
                    !JsonElement.DeepEquals(expected.RootElement,result)||result.TryGetProperty("workspace",out _))throw new Exception("Separate hydration must preserve the exact complete price projection without embedding the catalog.");
                reads.Add(new{milliseconds=timer.Elapsed.TotalMilliseconds,compressedBodyBytes=wire.Length});
            }
            Console.WriteLine("LARGE_CATALOG="+JsonSerializer.Serialize(new{contracts,selectedContracts=1,observations=data.Observations.Length,
                catalogBytes=decoded.Length,catalogCompressedBodyBytes=catalogWire.Length,catalogMilliseconds,reads,
                heapLimit=GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,runtime=Environment.Version.ToString(),platform=System.Runtime.InteropServices.RuntimeInformation.OSDescription}));
        }
        finally{await app.StopAsync();}
    }
}
