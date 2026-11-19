using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

internal static class ProgressiveCurvePreview
{
    internal static async Task Run()
    {
        const string curve = "ercot_houston_zone_rt_peak";
        var fixture = new BasisRiskFixture(384);
        var requests = fixture.Requests.Where(r => r.Curve.Code == curve).ToImmutableArray();
        var contracts = fixture.Contracts.ToDictionary(c => c.Key);
        var gates = new ConcurrentDictionary<Guid, TaskCompletionSource>();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Services.AddResponseCompression(options => {
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ["text/event-stream"];
        });
        builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        await using var app = builder.Build();
        app.UseResponseCompression();
        using var assets = new PhysicalFileProvider(Path.GetFullPath("web/dist"));
        app.UseStaticFiles(new StaticFileOptions { FileProvider = assets, RequestPath = "/assets" });
        app.MapGet("/ping", () => Results.Text("ok"));
        app.MapGet("/", () => Results.File(Path.GetFullPath("tests/browser/progressive-curve.html"), "text/html"));
        app.MapPost("/release/{id:guid}", (Guid id) => {
            if (!gates.TryGetValue(id, out var gate)) return Results.NotFound();
            gate.TrySetResult();
            return Results.NoContent();
        });
        app.MapGet("/held/{id:guid}", (Guid id) => gates.ContainsKey(id) ? Results.NoContent() : Results.NotFound());

        app.MapGet("/curve/{mode}/{count:int}/{id:guid}", async (HttpContext context, string mode, int count, Guid id) => {
            if (mode is not ("whole" or "progressive" or "hold" or "cut") || count is not (76 or 384))
            {
                context.Response.StatusCode = 400;
                return;
            }
            var token = context.RequestAborted;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (mode == "hold" && !gates.TryAdd(id, gate))
            {
                context.Response.StatusCode = 409;
                return;
            }
            var response = context.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-store";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            async Task Emit(string kind, object data)
            {
                await response.WriteAsync($"event: {kind}\ndata: {JsonSerializer.Serialize(new { id, snapshot = fixture.Snapshot.Id, data })}\n\n", token);
                await response.Body.FlushAsync(token);
            }
            try
            {
                var axis = requests.Take(count).Select(r => new { id = r.Contract.Id, date = contracts[r.Contract].DeliveryFrom });
                await Emit("axis", new { curve, unit = "USD/MWh", axis });
                var calculation = new Stopwatch();
                long allocated = 0;
                for (var offset = 0; offset < count;)
                {
                    var size = offset == 0 && mode != "whole" ? 8 : count - offset;
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    calculation.Start();
                    var result = fixture.Model.Build(fixture.Snapshot, requests.Slice(offset, size), token);
                    calculation.Stop();
                    allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                    var rows = result.Values.Select(v => new { id = v.Request.Contract.Id, price = v.Price.Value.ToString() });
                    await Emit("points", new { offset, rows });
                    offset += size;
                    if (mode == "cut") return;
                    if (mode == "hold" && offset < count)
                        await gate.Task.WaitAsync(TimeSpan.FromSeconds(20), token);
                }
                await Emit("complete", new { count, calculateMs = calculation.Elapsed.TotalMilliseconds, allocated });
            }
            finally
            {
                if (mode == "hold") gates.TryRemove(id, out _);
            }
        });
        await app.StartAsync();
        Console.WriteLine(app.Urls.Single());
        await Console.In.ReadLineAsync();
        await app.StopAsync();
    }
}
