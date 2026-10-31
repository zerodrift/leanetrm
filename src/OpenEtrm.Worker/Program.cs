using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenEtrm.Automation;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using System.Text.Json;

var file = Environment.GetEnvironmentVariable("OPENETRM_WORKER_CONNECTION_FILE")
    ?? throw new InvalidOperationException("An explicit protected worker connection file is required.");
var connection = new NpgsqlConnectionStringBuilder(File.ReadAllText(file).Trim());
if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database) ||
    string.IsNullOrWhiteSpace(connection.Username)) throw new InvalidOperationException("The worker database identity must be explicit.");
if(connection.Host.Contains(','))throw new InvalidOperationException("The bounded worker requires a single explicit PostgreSQL host.");
if (!Path.IsPathFullyQualified(connection.Host) && connection.SslMode != SslMode.VerifyFull)
    throw new InvalidOperationException("Network worker connections require PostgreSQL TLS with full host and certificate verification.");
int Setting(string name, int minimum, int maximum) => int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= minimum && value <= maximum
    ? value : throw new InvalidOperationException($"An explicit {name} between {minimum} and {maximum} is required.");
var pollSeconds = Setting("OPENETRM_WORKER_POLL_SECONDS", 1, 60);
var leaseSeconds = Setting("OPENETRM_WORKER_LEASE_SECONDS", 5, 120);
connection.MaxPoolSize = 2; connection.CommandTimeout = 5; connection.Timeout = 5; connection.Timezone = "UTC";
await using var source = NpgsqlDataSource.Create(connection.ConnectionString);
await DatabaseLoginBoundary.Require(source, DatabaseLoginProfile.Scheduler, CancellationToken.None);
var pilotRuntimeFile=Environment.GetEnvironmentVariable("OPENETRM_PILOT_RUNTIME_CONNECTION_FILE");
var pilotManifestFile=Environment.GetEnvironmentVariable("OPENETRM_PILOT_MANIFEST_FILE");
if((pilotRuntimeFile is null)!=(pilotManifestFile is null))throw new InvalidOperationException("Pilot close execution requires both explicit runtime and manifest files.");
NpgsqlDataSource? pilotSource=null;
PilotCloseProcessor? pilot=null;
try
{
if(pilotRuntimeFile is not null)
{
    var runtime=new NpgsqlConnectionStringBuilder(File.ReadAllText(pilotRuntimeFile).Trim());
    if(runtime.Host!=connection.Host||runtime.Port!=connection.Port||runtime.Database!=connection.Database||string.IsNullOrWhiteSpace(runtime.Username)||runtime.Username==connection.Username||
        !Path.IsPathFullyQualified(runtime.Host!)&&runtime.SslMode!=SslMode.VerifyFull)
        throw new InvalidOperationException("The pilot runtime requires a separate explicit login to the same qualified database host.");
    runtime.MaxPoolSize=2;runtime.CommandTimeout=5;runtime.Timeout=5;runtime.Timezone="UTC";
    pilotSource=NpgsqlDataSource.Create(runtime.ConnectionString);
    await DatabaseLoginBoundary.Require(pilotSource, DatabaseLoginProfile.Runtime, CancellationToken.None);
    var manifest=JsonSerializer.Deserialize<PreviewManifest>(File.ReadAllText(pilotManifestFile!),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
        ??throw new InvalidOperationException("The pilot worker requires its exact protected market manifest.");
    if(manifest.Mode is not("hosted-pilot" or "local-synthetic-preview")||manifest.CatalogId==Guid.Empty||manifest.TenantId==Guid.Empty||manifest.WorkspaceId==Guid.Empty)throw new InvalidOperationException("The pilot market and workspace bindings must be explicit.");
    await using(var workspace=source.CreateCommand("SELECT automation.pilot_worker_matches($1,$2,$3,$4::name)"))
    {workspace.Parameters.AddWithValue(manifest.TenantId);workspace.Parameters.AddWithValue(manifest.WorkspaceId);workspace.Parameters.AddWithValue(manifest.CatalogId);workspace.Parameters.AddWithValue(runtime.Username);if(!Equals(await workspace.ExecuteScalarAsync(),true))throw new InvalidOperationException("The pilot market, workspace and runtime login must match this registered worker.");}
    pilot=new(source,pilotSource,manifest);
}
await using(var configured=source.CreateCommand("SELECT automation.pilot_worker_configured()"))
    if(!Equals(await configured.ExecuteScalarAsync(),pilot is not null))throw new InvalidOperationException("Installed pilot runtime mapping and explicit worker files must agree.");
var store = new JobWorkerStore(source,pilot is not null);
string kind;
await using (var mode = source.CreateCommand("SELECT automation.worker_kind()")) kind = (string)(await mode.ExecuteScalarAsync())!;
using var swarm = kind == "controller" ? SwarmConnection.Load() : null;
if (swarm is not null && leaseSeconds < pollSeconds + 6 * swarm.Client.Timeout.TotalSeconds + 15)
    throw new InvalidOperationException("The controller lease must cover polling, six bounded Docker calls and database publication.");
var gateway = swarm is null ? null : new SwarmGateway(swarm.Client, swarm.ClusterId);
var controller = gateway is null ? null : new InfrastructureController(gateway, new InfrastructureWorkerStore(source, swarm!.ClusterId), leaseSeconds,
    new WorkloadController(gateway, store, new WorkloadStore(source), leaseSeconds));
if (args is ["--once"])
{
    var worker = new Scheduler(store, pollSeconds, leaseSeconds, null, controller,pilot);
    await worker.Tick(CancellationToken.None);
    return;
}
if (args.Length != 0) throw new InvalidOperationException("Choose service mode or --once explicitly.");
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
builder.Services.AddHostedService(services => new Scheduler(store, pollSeconds, leaseSeconds, services.GetRequiredService<ILogger<Scheduler>>(), controller,pilot));
await builder.Build().RunAsync();
}
finally{if(pilotSource is not null)await pilotSource.DisposeAsync();}

sealed class Scheduler(JobWorkerStore store, int pollSeconds, int leaseSeconds, ILogger<Scheduler>? logger, InfrastructureController? controller,PilotCloseProcessor? pilot) : BackgroundService
{
    private readonly Guid id = Guid.NewGuid();
    public async Task Tick(CancellationToken ct)
    {
        if (controller is not null) { await controller.Tick(ct); return; }
        var claim = await store.Claim(id, leaseSeconds, ct);
        if (claim is null) return;
        if(claim.Operation is PilotCloseJobs.Operation or CloseBookRiskJobs.Operation)
        {
            if(pilot is null)throw new AutomationException("pilot_configuration","This worker has no qualified pilot runtime and market manifest.");
            if(claim.Operation==CloseBookRiskJobs.Operation)
            {
                await ExistingClose(claim,ct);
                return;
            }
            try
            {
                await pilot.Process(claim,ct);
                await store.Finish(claim,true,null,ct);
                logger?.LogInformation("Portfolio report completed: {RunId}",claim.Id);
            }
            catch(CloseRiskInputsException)
            {
                await store.Finish(claim,false,CloseRiskInputsException.Code,ct);
                logger?.LogWarning("Portfolio occurrence rejected ({ErrorCode}): {RunId}",CloseRiskInputsException.Code,claim.Id);
            }
            catch(MarketDataException error)
            {
                await store.Finish(claim,false,error.Code,ct);
                logger?.LogWarning("Pilot occurrence rejected ({ErrorCode}): {RunId}",error.Code,claim.Id);
            }
            return;
        }
        if (claim.Operation != "worker_check")
            throw new AutomationException("job_operation", "This service has no handler for the claimed operation.");
        // A real readiness operation verifies this independently running service's
        // restricted database identity and durable admission/publication path.
        await store.Finish(claim, true, null, ct);
        logger?.LogInformation("Readiness job completed: {RunId}", claim.Id);
    }

    private async Task ExistingClose(JobClaim claim,CancellationToken ct)
    {
        using var work=CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var heartbeat=CancellationTokenSource.CreateLinkedTokenSource(ct);
        Exception? renewalFailure=null;
        var completion=new CloseRiskCompletion();
        async Task Renew()
        {
            try
            {
                while(true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(leaseSeconds/3.0),heartbeat.Token);
                    await store.RenewCloseRisk(claim,leaseSeconds,heartbeat.Token);
                }
            }
            catch(OperationCanceledException) when(heartbeat.IsCancellationRequested) { }
            catch(Exception error) when(error is NpgsqlException or AutomationException or OperationCanceledException)
            {
                renewalFailure=error;
                await completion.Cancel(work);
            }
        }
        var renewing=Renew();
        try
        {
            Exception? failure=null;
            try{await pilot!.Process(claim,work.Token);}
            catch(Exception error) when(error is NpgsqlException or AutomationException or MarketDataException or CloseRiskInputsException or OperationCanceledException)
            {failure=error;}
            // Publication and terminal success share one transaction. Callback
            // failures cannot bypass joining renewal and reconciling that commit.
            if(await completion.Reconcile(heartbeat,renewing,token=>store.CloseRiskCompleted(claim,token),ct))
            {
                logger?.LogInformation("Portfolio report completed: {RunId}",claim.Id);
                return;
            }
            if(renewalFailure is not null)throw new AutomationException("job_fence","Risk ownership could not be renewed; no further publication is authorized.");
            if(failure is CloseRiskInputsException)await store.Finish(claim,false,CloseRiskInputsException.Code,ct);
            else if(failure is MarketDataException invalid)await store.Finish(claim,false,invalid.Code,ct);
            else if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            else throw new AutomationException("job_protocol","The exact risk occurrence has no committed complete report.");
        }
        finally
        {
            // Also stop ownership renewal if processing throws an unexpected error.
            // Repeating cancellation/join after reconciliation is idempotent.
            await completion.Stop(heartbeat,renewing);
            if(completion.CancellationFailures.Length>0)
                logger?.LogWarning("Existing-close cancellation callbacks failed ({FailureCount}); the exact durable outcome governs recovery: {RunId}",completion.CancellationFailures.Length,claim.Id);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Tick(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e) when (e is NpgsqlException or AutomationException or HttpRequestException or OperationCanceledException)
            { logger?.LogWarning("Scheduler tick did not complete ({ErrorType}); durable state will be reconciled.", e.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
        }
    }
}
