using OpenEtrm.Shared.Temporal;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.Automation;
using OpenEtrm.MarketData;

if(args is ["--render-power-days",var calendarInput,var calendarOutput]){PowerDayCalendar.Run(calendarInput,calendarOutput);return;}

if(args.Length>0 && args[0]=="--collect-power"){Environment.ExitCode=await PowerCollection.Run(args[1..]);return;}
if(args.Length>0 && args[0]=="--risk-worker"){Environment.ExitCode=await RiskWorker.Run(args[1..]);return;}

if(args.Length!=1 || args[0] is not ("--local-preview" or "--hosted-pilot")) throw new InvalidOperationException("Choose exactly --local-preview or --hosted-pilot.");
var hosted=args[0]=="--hosted-pilot"?HostedConfiguration.Read(Environment.GetEnvironmentVariable("OPENETRM_HOSTED_CONFIG")??throw new InvalidOperationException("Explicit private hosted configuration is required.")):null;
var root=Path.GetFullPath(hosted?.ManifestDirectory ?? Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT") ?? throw new InvalidOperationException("Use tools/run_preview.py."));
var runId=hosted?.InstallationId ?? Guid.Parse(File.ReadAllText(Path.Combine(root,".openetrm-preview")));
var port=hosted?.ListenPort ?? int.Parse(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_PORT") ?? throw new InvalidOperationException("Explicit preview port is required."));
var webRoot=Path.GetFullPath(hosted?.WebDirectory ?? Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_WEB") ?? throw new InvalidOperationException("Explicit built web root is required."));
await using var hostedAuthentication=hosted is null?null:new HostedAuthentication(hosted);
var builder=WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(o=>o.SingleLine=true); builder.Logging.SetMinimumLevel(LogLevel.Warning);
const int bodyLimit=32*1024;
builder.WebHost.ConfigureKestrel(o=> { o.Listen(hosted is null?IPAddress.Loopback:IPAddress.Any,port); o.Limits.MaxRequestBodySize=bodyLimit; });
if(hosted is null)builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
else {HostedAuthentication.Configure(builder,hosted);builder.Services.AddSingleton(hostedAuthentication!);}
builder.Services.AddSingleton<IAntiforgeryAdditionalDataProvider,PreviewAntiforgeryScope>();
builder.Services.AddAntiforgery(o=> { o.HeaderName="X-Preview-CSRF"; o.Cookie.Name=hosted is null?"openetrm_preview_csrf":"__Host-openetrm_csrf"; o.Cookie.HttpOnly=true; o.Cookie.SameSite=SameSiteMode.Strict; o.Cookie.SecurePolicy=hosted is null?CookieSecurePolicy.None:CookieSecurePolicy.Always; });
// A member repeated in any letter case is ambiguous, so it never binds.
builder.Services.ConfigureHttpJsonOptions(o=>{o.SerializerOptions.PropertyNamingPolicy=JsonNamingPolicy.CamelCase;o.SerializerOptions.AllowDuplicateProperties=false;
    o.SerializerOptions.Converters.Add(new ExactDecimalJsonConverter());});
// In every environment, binding refuses what it cannot read with a bare status, which the API then names.
builder.Services.Configure<RouteHandlerOptions>(o=>o.ThrowOnBadRequest=false);
builder.Services.AddResponseCompression(o=>{o.EnableForHttps=true;o.Providers.Add<GzipCompressionProvider>();o.MimeTypes=["application/json"];});
builder.Services.Configure<GzipCompressionProviderOptions>(o=>o.Level=CompressionLevel.Fastest);
var app=builder.Build();
var httpContext=hosted is null?null:app.Services.GetRequiredService<IHttpContextAccessor>();
await using var database=new PreviewDatabase(root,hosted:hosted,hostedSession:hosted is null?null:()=>HostedAuthentication.SessionDigest(httpContext!.HttpContext??throw new PreviewDeniedException()));
if(hosted is not null)await database.ValidateHostedBoundary(CancellationToken.None);
if(hostedAuthentication is not null){hostedAuthentication.Tenant=database.Manifest.TenantId;hostedAuthentication.Workspace=database.Manifest.WorkspaceId;app.UseForwardedHeaders();}
// The page assistant's worker and provider client exist only in the local preview (docs/ai-assistant.md §11). The
// worker starts its generation before serving, closing whatever an earlier one left open. Only checks set the
// preview's provider endpoint override (§5.2).
var checkEndpoint=hosted is null?ModelTransport.CheckEndpoint(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_PROVIDER_ENDPOINT")):null;
await using var agentWorker=hosted is null?new AgentWorker(PreviewDatabase.AgentSource(root),PreviewDatabase.AgentWrites(root),app.Logger):null;
var (custody,adapter)=agentWorker is null?(null,null):(CredentialCustody.FromEnvironment(),new MessagesAdapter(ModelTransport.Handler(checkEndpoint),RuntimeSet.Provider));
var providerTests=agentWorker is null?null:new ProviderTests(custody!,agentWorker,adapter!,app.Logger);
// The live event stream's subscriptions, which the worker tells of what it publishes (§8).
var turnStreams=agentWorker is null?null:new TurnStreams();
await using var turnWorker=agentWorker is null?null:new TurnWorker(agentWorker,custody!,adapter!,turnStreams!,app.Logger);
if(agentWorker is not null)await agentWorker.Generation(CancellationToken.None);

var profiles=new[] { new {id="viewer",name="Viewer",description="Read only"},new {id="editor",name="Market Data Editor",description="Read and correct synthetic prices"},new {id="approver",name="Market Data Approver",description="Read and publish schedule definitions"},new {id="admin",name="Access Administrator",description="Manage workspace identities and roles"},new {id="trader",name="Trader",description="Book and inspect synthetic futures holdings"},new {id="risk",name="Risk Analyst",description="Inspect holdings and run shared market scenarios"} };

app.Use(async (context,next)=>
{
    var origin=hosted?.PublicOrigin ?? $"http://127.0.0.1:{port}";
    var apiPath=context.Request.Path.StartsWithSegments("/examples/exchange/api",out var suffix)?new PathString("/api"+suffix):context.Request.Path;
    if (hosted is null && (context.Connection.RemoteIpAddress is not {} remote || !IPAddress.IsLoopback(remote) ||
        context.Request.Host.Host!="127.0.0.1" || context.Request.Host.Port!=port ||
        context.Request.Headers.TryGetValue("Origin",out var supplied) && supplied!=origin)
        || hosted is not null && (!context.Request.IsHttps || context.Request.Host.Value!=hosted.Origin.Authority || context.Request.Headers.TryGetValue("Origin",out var hostedOrigin)&&hostedOrigin!=origin))
    { context.Response.StatusCode=403; return; }
    context.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers["X-Content-Type-Options"]="nosniff"; context.Response.Headers["Referrer-Policy"]=hosted is null?"no-referrer":"same-origin";
    if(hosted is not null){context.Response.Headers.CacheControl="no-store";context.Response.Headers["Strict-Transport-Security"]="max-age=31536000";}
    if (apiPath.StartsWithSegments("/api")) context.Response.Headers.CacheControl="no-store";
    // A route that must tell the client leaving apart from the request deadline reads the client's own token here.
    context.Items["client_aborted"]=context.RequestAborted;
    // The page assistant's live event stream lasts as long as its turn, so it alone has no request deadline (§8).
    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    if(hosted is not null||!TurnStream.Matches(context.Request.Method,apiPath))deadline.CancelAfter(TimeSpan.FromSeconds(15));
    context.RequestAborted=deadline.Token;
    const string malformed="The request is incomplete or malformed.";
    try
    {
        await next(context);
    }
    catch (Exception e) when (!context.Response.HasStarted)
    {
        var (status,code,message)=e switch
        {
            // A missing or ended session is session_ended on the assistant's routes, which offer a reload; pages keep access_denied (§8).
            PreviewSessionEnded when apiPath.StartsWithSegments("/api/assistant") => (403,"session_ended",TurnStream.Refusal("session_ended")),
            PreviewDeniedException or PreviewSessionEnded => (403,"access_denied","The selected test identity cannot perform this operation."),
            AutomationException a => (422,a.Code,a.Message),
            PostgresException p when p.MessageText is "job_conflict" => (409,p.MessageText,"The job or request changed. Refresh or reconcile the original request."),
            PostgresException p when p.MessageText is "pilot_close_conflict" => (409,p.MessageText,"This close request already has different retained inputs. Reconcile the original request."),
            PostgresException p when p.SqlState is "23503" or "23514" && apiPath.StartsWithSegments("/api/jobs/pilot-close") => (422,"pilot_profile","Choose supported exact books, curves, sources, policies and schedule terms."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("pilot_",StringComparison.Ordinal) => (422,p.MessageText,"Choose complete explicit pilot close inputs, supported policies and an available exact anchor."),
            PostgresException p when p.MessageText is "infrastructure_busy" => (409,p.MessageText,"Wait for active workloads or pending maintenance to finish before requesting node maintenance."),
            PostgresException p when p.MessageText is "infrastructure_conflict" => (409,p.MessageText,"The node changed. Refresh infrastructure before requesting another action."),
            PostgresException p when p.SqlState=="23505" && apiPath.StartsWithSegments("/api/infrastructure") => (409,"infrastructure_busy","Cluster maintenance is already queued, running or awaiting reconciliation."),
            PostgresException p when p.MessageText is "infrastructure_input" => (422,p.MessageText,"Supply a supported infrastructure operation and exact target version."),
            PostgresException p when p.SqlState is "23502" or "23514" or "22P02" or "22007" or "22008" or "22003" && apiPath.StartsWithSegments("/api/jobs") => (422,"job_input","The job contract has missing, malformed or unsupported fields."),
            PostgresException p when p.MessageText is "job_input" or "job_transition" or "job_limit" => (422,p.MessageText,"Supply a supported job contract or an available action within its limits."),
            AntiforgeryValidationException => (400,"request_token","Reload the preview to obtain a valid request token."),
            MarketDataException m when m.Code is "shortcut_conflict" or "tou_conflict" or "table_view_conflict" or "futures_revision_conflict" or "swap_revision_conflict" or "custom_curve_conflict" or "contract_date_conflict" => (409,m.Code,m.Message),
            PostgresException p when p.SqlState is "23505" or "40001" && apiPath.StartsWithSegments("/api/custom-curves") => (409,"custom_curve_conflict","This curve or request changed. Reload or reconcile the original request."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="party_conflict" => (409,p.MessageText,"This party or request changed. Reload the latest revision or retry the original request."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "party_input" or "party_limit" => (422,p.MessageText,"Supply a stable code, name and an effective time at or after the preceding version. Party codes cannot change."),
            MarketDataException m when m.Code=="reference_conflict" => (409,m.Code,m.Message),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="reference_conflict" => (409,p.MessageText,"This reference version or display selection changed. Reload before editing."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "reference_edit" or "reference_limit" or "reference_economic_change" => (422,p.MessageText,"Supply supported display fields within the authoring limits. Financial terms must remain identical before display activation."),
            PostgresException p when p.SqlState is "23505" or "40001" && apiPath.StartsWithSegments("/api/tou-products") => (409,"tou_conflict","This TOU product changed or its code already exists. Reload before editing."),
            MarketDataException m => (422,m.Code,m.Message),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="risk_job_denied" => (403,"access_denied","Current authority does not permit this risk job operation."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="risk_job_conflict" => (409,p.MessageText,"This request identity already belongs to different risk inputs."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("risk_",StringComparison.Ordinal) => (422,p.MessageText,"The risk job does not meet its retained context, input or execution contract."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "swap_conflict" or "swap_revision_conflict" or "trade_operation_conflict" => (409,p.MessageText,"This trade or operation changed. Reconcile the original request or reload the latest revision."),
            PostgresException p when p.SqlState=="P0001" && (p.MessageText.StartsWith("payment_",StringComparison.Ordinal)||p.MessageText=="short_month") => (422,p.MessageText,"The generated payment date does not meet its explicit rule and calendar coverage."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("swap_",StringComparison.Ordinal) => (422,p.MessageText,"The trade request does not meet its explicit side, reference, date or lifecycle terms."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="futures_conflict" => (409,"futures_conflict","This trade request identity already has different economics or ownership."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="futures_revision_conflict" => (409,"futures_revision_conflict","The trade changed. Reload its latest revision before amending."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("futures_",StringComparison.Ordinal) => (422,p.MessageText,"The trade request does not meet its explicit book, contract or execution terms."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "trade_booking_read_only" or "booking_human_required" => (422,p.MessageText,"Booking facts are server-owned and require the authenticated human workspace user."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="trade_party" => (422,p.MessageText,"Select available exact legal entity/counterparty versions."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="trade_annotation" => (422,p.MessageText,"Choose an available exact trader version; memo is limited to 4000 characters."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="historical_read_only" => (422,"historical_read_only","Return to working data before booking or amending."),
            TemporalException t when t.Code is "date_definition_conflict" or "schedule_conflict" => (409,t.Code,t.Message),
            TemporalException t => (422,t.Code,t.Message),
            PostgresException p when p.SqlState is "23505" or "40001" && apiPath.StartsWithSegments("/api/date-schedules") => (409,"schedule_conflict","This schedule changed or the reference already exists. Reload before editing."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="admin_conflict" => (409,"admin_conflict","This account or request changed. Reload administration before editing."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="admin_limit" && context.Items.ContainsKey("model_admin_attempted") => (422,p.MessageText,ModelCatalog.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "last_administrator" or "self_access_change" or "admin_input" or "admin_limit" => (422,p.MessageText,"The administrative change was rejected. Keep a current administrator and supply explicit account details and roles."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="agent_managed" => (422,p.MessageText,ModelCatalog.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="23505" && apiPath.StartsWithSegments("/api/admin") => (409,"admin_conflict","That account or request already exists. Reload administration."),
            ModelAdministrationException m => (422,m.Code,m.Message),
            ModelCallException m => (422,m.Code,m.Message),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="model_admin_conflict" || p.SqlState=="23505" && context.Items.ContainsKey("model_admin_attempted")
                => (409,"model_admin_conflict","This provider, model or request changed, or its current state does not allow the change. Reload Settings › AI before repeating the change under a new request."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "model_provider_unqualified" or "model_provider_disabled" or "model_unqualified" or "model_mapping_unconfirmed"
                or "model_unpriced" or "test_in_progress" or "model_changed" or "model_disabled" or "agent_retired"
                => (409,p.MessageText,ModelCatalog.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="agent_role_lacks_permission" => (422,p.MessageText,AgentAdministration.RoleRefusal(AgentAdministration.Lacking(p.Hint))),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "agent_not_allowed" or "grant_permission_missing" => (403,p.MessageText,AgentGrants.Refusal(p.MessageText,p.Hint)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="grant_unknown" => (404,p.MessageText,AgentGrants.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="grant_conflict" => (409,p.MessageText,AgentGrants.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="grant_input" => (422,p.MessageText,AgentGrants.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "turn_input" or "question_too_long" => (422,p.MessageText,AgentTurns.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "turn_conflict" or "conversation_agent_mismatch" or "knowledge_budget_reserved"
                or "access_changed" => (409,p.MessageText,AgentTurns.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "conversation_not_found" or "turn_not_found" => (404,p.MessageText,AgentTurns.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="grant_required" => (403,p.MessageText,AgentTurns.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "model_admin_input" or "model_identifier_invalid" or "currency_unavailable" or "parameter_required"
                or "model_limit_exceeded" or "instructions_too_long" or "grant_duration_exceeded"
                => (422,p.MessageText,ModelCatalog.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="agent_runtime_unavailable" => (503,p.MessageText,ModelCatalog.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "report_zone_invalid" or "report_range_invalid" => (422,p.MessageText,UsageReports.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="amount_format_conflict" => (409,"amount_format_conflict","This deal display changed. Reload its precision controls before saving."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "amount_format" or "futures_lot" => (422,p.MessageText,"Supply exact supported contract lot terms or deal display precision."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "interval_series_conflict" or "interval_series_revision_conflict" => (409,p.MessageText,IntervalSeriesStore.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "interval_series_values" or "interval_series_window" or "interval_series_grid" or "interval_series_code"
                || p.SqlState=="23514" && p.MessageText=="interval_series_definition" => (422,p.MessageText,IntervalSeriesStore.Refusal(p.MessageText)),
            PostgresException p when p.SqlState=="23514" && p.MessageText=="interval_curve_definition"
                => (422,p.MessageText,"Interval contracts must be forwards on their operating date, alone in their curve and as long as its delivery profile's interval."),
            PostgresException p when p.SqlState=="23505" => (409,"revision_conflict","This observation has already been revised. Inspect its history and explicitly select the intended revision."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "knowledge_coverage" or "knowledge_clock" or "knowledge_tracking" or "knowledge_retention" or "knowledge_limit" or "knowledge_timeline" => (422,p.MessageText,"The requested knowledge cutoff cannot be certified. Check retained coverage, clock health and commit tracking."),
            PostgresException p when p.SqlState=="42501" => (403,"access_denied","Database permissions rejected this operation."),
            PostgresException p when p.SqlState is "23514" or "55000" => (422,"definition_rejected","The value or operation violates an explicit database specification."),
            PostgresException when context.Items.ContainsKey("administration_attempted") => (503,"administration_outcome_unknown","The administrative outcome cannot be confirmed. Retain the original request and inspect the audit before retrying."),
            Exception when context.Items.ContainsKey("model_admin_attempted") && e is NpgsqlException or OperationCanceledException
                => (503,"model_admin_outcome_unknown","The outcome is unknown. Check Settings › AI and its audit, or repeat the identical request."),
            Exception when context.Items.ContainsKey("grant_attempted") && e is NpgsqlException or OperationCanceledException
                => (503,"grant_outcome_unknown","The outcome is unknown. Read your grants, or repeat the identical request."),
            Exception when context.Items.ContainsKey("turn_attempted") && e is NpgsqlException or OperationCanceledException
                => (503,"turn_outcome_unknown","The outcome is unknown. Read your conversations, or repeat the identical request."),
            OperationCanceledException when context.Items.ContainsKey("party_attempted") => (503,"party_outcome_unknown","The party save outcome is unknown. Inspect versions or retry the original request identity."),
            NpgsqlException when context.Items.ContainsKey("party_attempted") => (503,"party_outcome_unknown","The party save outcome is unknown. Inspect versions or retry the original request identity."),
            OperationCanceledException when context.Items.ContainsKey("administration_attempted") => (503,"administration_outcome_unknown","The administrative outcome is unknown. Retain the request identity and inspect the audit before retrying the exact command."),
            NpgsqlException when context.Items.ContainsKey("administration_attempted") => (503,"administration_outcome_unknown","The administrative outcome is unknown. Retain the request identity and inspect the audit before retrying the exact command."),
            OperationCanceledException when context.Items.ContainsKey("correction_attempted") => (503,"mutation_outcome_unknown","The correction outcome is unknown. Reload, inspect the input's revisions and explicitly select any saved successor before submitting again."),
            NpgsqlException when context.Items.ContainsKey("correction_attempted") => (503,"mutation_outcome_unknown","The correction outcome is unknown. Reload, inspect the input's revisions and explicitly select any saved successor before submitting again."),
            OperationCanceledException when context.Items.ContainsKey("swap_attempted") => (503,"swap_outcome_unknown","The trade request outcome is unknown. Reconcile or retry its retained operation identity."),
            NpgsqlException when context.Items.ContainsKey("swap_attempted") => (503,"swap_outcome_unknown","The trade request outcome is unknown. Reconcile or retry its retained operation identity."),
            OperationCanceledException when context.Items.ContainsKey("futures_attempted") => (503,"futures_outcome_unknown","The trade request outcome is unknown. Reconcile or retry the retained operation identity."),
            NpgsqlException when context.Items.ContainsKey("futures_attempted") => (503,"futures_outcome_unknown","The trade request outcome is unknown. Reconcile or retry the retained operation identity."),
            Exception when context.Items.ContainsKey("automation_attempted") && e is NpgsqlException or OperationCanceledException => (503,"job_outcome_unknown","The operation outcome is unknown. Inspect current state or retry the retained exact request identity."),
            Exception when context.Items.ContainsKey("reference_attempted") && e is NpgsqlException or OperationCanceledException => (503,"reference_outcome_unknown","The reference change outcome is unknown. Inspect saved versions or retry the retained exact request identity."),
            Exception when context.Items.ContainsKey("interval_series_attempted") && e is NpgsqlException or OperationCanceledException => (503,"interval_series_outcome_unknown","The save outcome is unknown. Retry the original request to recover its receipt."),
            OperationCanceledException when context.Items.ContainsKey("risk_job_attempted") => (503,"risk_job_outcome_unknown","Job admission could not be confirmed. Reconcile or retry the exact retained request identity."),
            NpgsqlException when context.Items.ContainsKey("risk_job_attempted") => (503,"risk_job_outcome_unknown","Job admission could not be confirmed. Reconcile or retry the exact retained request identity."),
            OperationCanceledException => (503,"request_cancelled","The request was cancelled or exceeded its time limit."),
            BadHttpRequestException or JsonException => (400,"request_format",malformed),
            _ => (500,"preview_error","The preview could not complete the operation.")
        };
        if(e is PostgresException pg)app.Logger.LogWarning("Database operation rejected: {SqlState}",pg.SqlState);
        if (status==500) app.Logger.LogError("Preview operation failed: {ErrorType}",e.GetType().Name);
        context.Response.StatusCode=status;
        // A role refusal also names the role and permission as fields, so a page never parses its message.
        object body=e is PostgresException{SqlState:"P0001",MessageText:"agent_role_lacks_permission"} lacking&&AgentAdministration.Lacking(lacking.Hint) is {} lack
            ?new {code,message,role=lack.Role,permission=lack.Permission}:new {code,message};
        await context.Response.WriteAsJsonAsync(body,CancellationToken.None);
    }
    // Binding refuses a body or query value it cannot read, and a body over the limit, with a bare status before any
    // handler runs. No handler answers a bare 400 or 413, so pages release a request refused this way as one that wrote
    // nothing. An API answer names the refusal instead, echoing nothing that was read.
    if(apiPath.StartsWithSegments("/api")&&!context.Response.HasStarted&&context.Response.StatusCode is 400 or 413)
    {
        var limit=context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize??bodyLimit;
        var (code,message)=context.Response.StatusCode==413?("request_too_large",$"The request body exceeds its {limit/1024} KiB limit."):("request_format",malformed);
        await context.Response.WriteAsJsonAsync(new {code,message},CancellationToken.None);
    }
});

// Interval series imports alone accept bodies up to 8 MiB; every other route keeps the 32 KiB limit.
// Binding refuses a larger body with a bare 413, which the error middleware names request_too_large.
// The hosted gateway (deploy/nginx.conf.template) applies the same limits and answer, so change them together.
app.Use((context,next)=>
{
    var size=context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if(size is {IsReadOnly:false}&&context.Request.Path.Equals("/api/interval-series/revise",StringComparison.Ordinal))size.MaxRequestBodySize=8*1024*1024;
    return next(context);
});

if(hostedAuthentication is not null){app.UseAuthentication();app.UseRateLimiter();}
if(hostedAuthentication is not null)
{
    app.Use(async(c,next)=>
    {
        if(c.Request.Path.StartsWithSegments("/auth")){await next(c);return;}
        if(c.User.Identity?.IsAuthenticated!=true)
        {
            if(c.Request.Path.StartsWithSegments("/api")||c.Request.Path.StartsWithSegments("/examples")){c.Response.StatusCode=401;await c.Response.WriteAsJsonAsync(new{code="sign_in_required",message="Sign in to this private workspace."});}
            else c.Response.Redirect("/auth");
            return;
        }
        await next(c);
    });
    hostedAuthentication.Map(app);
}

app.Use(async(c,next)=>
{
    if(c.Request.Method is not ("GET" or "HEAD"))
    {
        var origin=hosted?.PublicOrigin??$"http://127.0.0.1:{port}";
        if(!c.Request.Headers.TryGetValue("Origin",out var supplied)||supplied!=origin)throw new PreviewDeniedException();
        await c.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(c);
    }
    await next(c);
});

app.UseWhen(c=>c.Request.Method=="POST"&&c.Request.Path.Equals("/api/trading/workspace",StringComparison.Ordinal),branch=>branch.UseResponseCompression());

await using var exchangeDatabase=hosted is null&&File.Exists(Path.Combine(root,"exchange-manifest.json"))?new PreviewDatabase(root,"exchange-manifest.json"):null;
MapPreview(app.MapGroup("/api").WithMetadata(new PreviewScope("baseline")),database,hosted is null?"openetrm_preview":"__Host-openetrm_workspace","api");
if(exchangeDatabase is not null)MapPreview(app.MapGroup("/examples/exchange/api").WithMetadata(new PreviewScope("exchange")),exchangeDatabase,"openetrm_exchange_preview","exchange");
app.MapGet("/api/examples",()=>new[]{new {id="baseline",name=hosted is null?"Original synthetic example":"Private pilot workspace",url="/"},new {id="exchange",name="CME & ICE futures · synthetic prices",url="/?example=exchange"}}.Where(e=>e.id=="baseline"||exchangeDatabase is not null));
void MapPreview(RouteGroupBuilder api,PreviewDatabase database,string cookieName,string turnScope)
{
var sessions=new ConcurrentDictionary<string,PreviewSession>();
var marketReads=new MarketReads(database);
var scheduleReads=new ScheduleReads(database);
var partyReads=new PartyReads(database);
var tradeReads=new TradeReads(database);
async Task<string[]> CurrentPermissions(NpgsqlConnection connection,CancellationToken ct)
{
    await using var command=ScheduleStore.Command(connection,"SELECT security.current_permissions()");
    return (string[])(await command.ExecuteScalarAsync(ct))!;
}
api.MapGet("/health",()=>new {mode=database.Manifest.Mode,runId});
api.MapGet("/bootstrap",async (HttpContext context,IAntiforgery antiforgery,bool includeFrames=true,bool includeAccess=false)=>
{
    foreach(var item in sessions.Where(x=>DateTimeOffset.UtcNow>x.Value.Ends).ToArray()) sessions.TryRemove(item.Key,out _);
    PreviewSession session;
    if (context.Request.Cookies.TryGetValue(cookieName,out var cookie)&&sessions.TryGetValue(cookie,out var existing)) session=existing;
    else
    {
        if (sessions.Count>=64) throw new PreviewDeniedException();
        var id=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); session=new PreviewSession(database.Manifest);
        sessions[id]=session;
        context.Response.Cookies.Append(cookieName,id,new CookieOptions {HttpOnly=true,SameSite=SameSiteMode.Strict,Secure=hosted is not null,MaxAge=PreviewSession.Lifetime,Path="/"});
    }
    await session.Gate.WaitAsync(context.RequestAborted);
    try {
        var identity=hosted is null?null:HostedAuthentication.Current(context);
        if(identity is not null){if(session.Profile is not null&&session.Profile!=identity.Subject)throw new PreviewDeniedException();session.Profile=identity.Subject;}
        object response=new {mode=database.Manifest.Mode,runId,notice=hosted is null?"Disposable synthetic workspace. Test identities are simulations, not production sign-in.":"Private pilot workspace. Financial capability is limited to the supported product conventions.",profiles=identity is null?profiles:profiles.Take(0).Append(new{id=identity.Subject,name=identity.Name,description="Signed-in account"}).ToArray(),
        profile=session.Profile,customProfiles=session.CustomProfiles.Select(p=>new{id=p.Key,name=p.Value}).ToArray(),csrfToken=antiforgery.GetAndStoreTokens(context).RequestToken,frames=includeFrames?session.PersistentFrames.Where(f=>f.ContextId==session.MarketContext?.ContextId).OrderBy(f=>f.At).ToArray():[],
        catalogId=database.Manifest.CatalogId,tenantId=database.Manifest.TenantId,workspaceId=database.Manifest.WorkspaceId};
        if(includeAccess)
        {
            var options=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
            var json=JsonSerializer.SerializeToNode(response,options)!.AsObject();
            json["access"]=session.Profile is null?null:JsonSerializer.SerializeToNode(new {profile=session.Profile,
                permissions=await database.WithIdentity(Profile(session),null,CurrentPermissions,context.RequestAborted)},options);
            response=json;
        }
        return Results.Ok(response); }
    finally { session.Gate.Release(); }
});
PreviewSession Session(HttpContext context)
{
    if (!context.Request.Cookies.TryGetValue(cookieName,out var id)||!sessions.TryGetValue(id,out var session)||DateTimeOffset.UtcNow>session.Ends) throw new PreviewSessionEnded();
    if(hosted is not null&&session.Profile!=HostedAuthentication.Current(context).Subject)throw new PreviewDeniedException();
    return session;
}
async Task<IResult> Locked(HttpContext context,Func<PreviewSession,Task<IResult>> work)
{
    var session=Session(context); await session.Gate.WaitAsync(context.RequestAborted);
    try {return await work(session);} finally {session.Gate.Release();}
}
string Profile(PreviewSession session)=>session.Profile ?? throw new PreviewDeniedException();
api.MapPost("/time/inspect",(HttpContext c,LocalTimeRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),null,(_,_)=>
    Task.FromResult<IResult>(Results.Ok(new{candidates=UserTime.Inspect(body.LocalTime,body.TimeZone)})),c.RequestAborted)));
// Pages pin the session's selected context, or current working data when none is selected.
PinnedContext Pin(PreviewSession session,Guid? frame=null)
{
    var selected=frame is {} id?Frame(session,id):null;
    return session.MarketContext is {} context?PinnedContext.Retained(context,selected):PinnedContext.Current(selected);
}
InputFrame Frame(PreviewSession session,Guid id)=>Pin(session).Require(session.Frames.GetValueOrDefault(id));
async Task<SessionFramePlan> DiscoverFrames(NpgsqlConnection connection,PreviewSession session,CancellationToken ct)
{
    var aliases=session.ContextAliases(session.MarketContext);
    var stored=await MarketCloses.Frames(connection,database.Manifest.CatalogId,database.Manifest.RevisionId,session.MarketContext?.ContextId,ct,session.DurableLimit(aliases));
    return session.PlanFrames(stored,session.MarketContext,aliases);
}
async Task RefreshFrames(PreviewSession session,CancellationToken ct)
{
    var plan=await database.WithIdentity(Profile(session),"ReadMarketData",async(connection,cancellation)=>
    {
        await Pin(session).Bind(connection,database.Manifest.CatalogId,cancellation);
        return await DiscoverFrames(connection,session,cancellation);
    },ct);
    session.ApplyFrames(plan);
}
api.MapMarketDates(database,(c,work)=>Locked(c,async session=>
{
    await RefreshFrames(session,c.RequestAborted);
    return await database.WithIdentity(Profile(session),"ReadMarketData",(connection,ct)=>work(connection,Pin(session),session.VisibleFrames,session.FrameCatalog,ct),c.RequestAborted);
}));
api.MapPost("/market/closes/register",(HttpContext c,MarketCloseRegister body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"PublishMarketData",async(connection,ct)=>
        Results.Ok(await MarketCloses.Register(database,connection,Pin(session),Frame(session,body.SnapshotId),body,ct)),c.RequestAborted)));
api.MapGet("/jobs/pilot-close/catalog",async Task<IResult>(HttpContext c)=>await Locked(c,session=>database.WithIdentity(Profile(session),"RunJobs",async(connection,ct)=>
    Results.Ok(await PilotCloseJobs.Catalog(database,connection,Pin(session),[..session.Frames.Values.Where(f=>f.ContextId==session.MarketContext?.ContextId)],ct)),c.RequestAborted)));
api.MapPost("/jobs/pilot-close/create",(HttpContext c,PilotCloseJobCreate body)=>Locked(c,session=>database.WithIdentity(Profile(session),"RunJobs",async(connection,ct)=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Configure pilot close jobs in the current workspace.");
    return Results.Ok(await PilotCloseJobs.Create(connection,body,ct));
},c.RequestAborted)));
api.MapGet("/jobs/pilot-close/definition",async Task<IResult>(HttpContext c,Guid jobId)=>await Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
{
    await Pin(session).Bind(connection,database.Manifest.CatalogId,ct);
    await using var command=ScheduleStore.Command(connection,"SELECT automation.pilot_job_definition($1)::text",jobId);
    using var document=JsonDocument.Parse((string)(await command.ExecuteScalarAsync(ct)??throw new PreviewDeniedException()));
    return Results.Ok(document.RootElement.Clone());
},c.RequestAborted)));
api.MapGet("/jobs/pilot-close/results",async Task<IResult>(HttpContext c,Guid runId)=>await Locked(c,session=>database.WithIdentity(Profile(session),null,async(connection,ct)=>
{
    var pin=Pin(session);await pin.Bind(connection,database.Manifest.CatalogId,ct);
    return Results.Ok(await PilotCloseReports.Read(connection,runId,pin,ct));
},c.RequestAborted)));
api.MapCustomCurves(database,(c,permission,snapshotId,work)=>Locked(c,session=>
    database.WithIdentity(Profile(session),permission,async(connection,ct)=>
    {
        var pin=Pin(session,snapshotId);
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        if(permission is "WriteMarketDefinitions" or "SealMarketDefinitions" && pin.Context is not null)
            throw new MarketDataException("custom_curve_historical","Author and publish curves in the current workspace.");
        return await work(connection,pin,ct);
    },c.RequestAborted)));
api.MapPost("/profile",(HttpContext c,ProfileRequest body)=>Locked(c,session=>
{
    if(hosted is not null)throw new PreviewDeniedException();
    if (!profiles.Any(p=>p.id==body.Profile) && !session.CustomProfiles.ContainsKey(body.Profile)) throw new PreviewDeniedException();
    var left=session.Profile;
    session.ForgetDiscoveredFrames(resetAliasIdentities:true);session.Profile=body.Profile; session.MarketContext=null;
    // Then the switch ends the streams the session opened under the profile it left, before it cancels the turns the session
    // asked, so no stream carries that cancel instead; a stream still being admitted finds the new profile
    // (docs/ai-assistant.md §7.2, §8).
    turnStreams?.EndSession(session.Digest,left);
    turnWorker?.EndSession(session.Digest);
    return Task.FromResult(Results.Ok(new {profile=session.Profile}));
}));
PreviewSequenceCoverage ScheduleSource(string? code) => (database.Manifest.Schedules ?? []).SingleOrDefault(s=>s.Curve==code)
    ?? throw new MarketDataException("sequence_source","Choose an explicitly configured contract schedule.");
Task<IResult> ScheduleOperation(HttpContext c,Func<PreviewSession,PreviewData,IResult> work) => Locked(c,async session=>
    await database.WithIdentity(Profile(session),"ReadMarketData",async (connection,ct)=>work(session,await marketReads.Data(connection,Pin(session),ct,[])),c.RequestAborted));
api.MapPost("/units/convert",(HttpContext c,UnitConvertRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    var data=await marketReads.Data(connection,Pin(session),ct,[]);
    var units=await database.ReadUnitCatalog(connection,data,ct);
    return Results.Ok(UnitOperations.Convert(units,body,await ReferenceEditors.Display(connection,units.Revision,ct)));
},c.RequestAborted)));
api.MapPost("/amounts/display",(HttpContext c,System.Collections.Immutable.ImmutableArray<DisplayNumberRequest> body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),null,(_,_)=>Task.FromResult<IResult>(Results.Ok(UnitOperations.Display(body))),c.RequestAborted)));
api.MapGet("/power-imports",(Func<HttpContext,Task<IResult>>)(c=>PowerOperation(c,"ReadMarketData",async(_,connection,ct)=>Results.Ok(await PowerHistory.Catalog(connection,ct)))));
api.MapPost("/power-imports/period",(HttpContext c,PowerHistoryPeriodRequest body)=>PowerOperation(c,"ReadMarketData",async(_,connection,ct)=>Results.Ok(await PowerHistory.Period(connection,body,ct))));
api.MapPost("/power-imports/history",(HttpContext c,PowerHistoryRequest body)=>PowerOperation(c,"ReadMarketData",async(_,connection,ct)=>Results.Ok(await PowerHistory.Read(connection,body,ct))));
var touStore=new TouProductStore(database.Manifest.CatalogId,database.Manifest.ReferenceCatalogId,database.Manifest.ReferenceRevisionId);
// TOU products are current definitions; a historical view never substitutes them.
Task<IResult> PowerOperation(HttpContext c,string permission,Func<PreviewSession,NpgsqlConnection,CancellationToken,Task<IResult>> work)=>Locked(c,session=>
{
    ScheduleReads.RequireCurrent(Pin(session));
    return database.WithIdentity(Profile(session),permission,(connection,ct)=>work(session,connection,ct),c.RequestAborted);
});
api.MapGet("/curve-names",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    Results.Ok(await marketReads.CurveNames(connection,Pin(session),ct)),c.RequestAborted))));
api.MapPost("/power/catalog",(HttpContext c,PowerCatalogRequest body)=>PowerOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(await scheduleReads.PowerCatalog(connection,Pin(session,body.SnapshotId),ct))));
api.MapPost("/power/preview",(HttpContext c,PowerPreviewRequest body)=>PowerOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(PowerPreview.Calculate(await database.Read(connection,ct,observationIds:Frame(session,body.SnapshotId).ObservationIds),Frame(session,body.SnapshotId),body,await scheduleReads.PublishedProducts(connection,Pin(session),ct)))));
api.MapPost("/power/settlement",(HttpContext c,PowerActualSettlementRequest body)=>PowerOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(await PowerActualSettlement.Calculate(connection,await PowerActualSettlement.ReadData(database,connection,ct),Frame(session,body.SnapshotId),body,await scheduleReads.PublishedProducts(connection,Pin(session),ct),ct))));
api.MapGet("/tou-products",(Func<HttpContext,Task<IResult>>)(c=>PowerOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(await scheduleReads.TouProducts(connection,Pin(session),ct)))));
api.MapPost("/tou-products/save",(HttpContext c,TouProductSaveRequest body)=>PowerOperation(c,"WriteMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await touStore.Save(connection,body,ct))));
api.MapPost("/tou-products/publish",(HttpContext c,TouProductPublishRequest body)=>PowerOperation(c,"SealMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await touStore.Publish(connection,body,ct))));
var intervalSeries=new IntervalSeriesStore(database);
// Interval series work on current data; history is read with each request's explicit knowledge time.
// Writes never read back in their own transaction, whose revision cannot be certified before it commits.
Task<IResult> IntervalSeriesOperation<T>(HttpContext c,string permission,Func<NpgsqlConnection,CancellationToken,Task<T>> work)=>Locked(c,session=>
{
    if(session.MarketContext is not null)
        throw new MarketDataException("interval_series_historical","Return to working data to edit or read interval series; use Known at on this page for history.");
    if(permission!="ReadMarketData")c.Items["interval_series_attempted"]=true;
    return database.WithIdentity(Profile(session),permission,async(connection,ct)=>Results.Ok(await work(connection,ct)),c.RequestAborted);
});
api.MapGet("/interval-series",(Func<HttpContext,Task<IResult>>)(c=>IntervalSeriesOperation(c,"ReadMarketData",intervalSeries.Catalog)));
api.MapPost("/interval-series/define",(HttpContext c,IntervalSeriesDefineRequest body)=>IntervalSeriesOperation(c,"WriteMarketDefinitions",(connection,ct)=>intervalSeries.Define(connection,body,ct)));
api.MapPost("/interval-series/revise",(HttpContext c,IntervalSeriesReviseRequest body)=>IntervalSeriesOperation(c,"ImportObservations",(connection,ct)=>intervalSeries.Revise(connection,body,ct)));
api.MapPost("/interval-series/values",(HttpContext c,IntervalSeriesReadRequest body)=>IntervalSeriesOperation(c,"ReadMarketData",(connection,ct)=>intervalSeries.Values(connection,body,ct)));
api.MapPost("/interval-series/changes",(HttpContext c,IntervalSeriesReadRequest body)=>IntervalSeriesOperation(c,"ReadMarketData",(connection,ct)=>intervalSeries.Changes(connection,body,ct)));
var intervalCurves=new IntervalCurvesStore(marketReads,touStore);
// Interval curves read published TOU and shape inputs, which are current definitions, so a historical view never substitutes them.
Task<IResult> IntervalCurveRead(HttpContext c,Guid snapshot,Func<NpgsqlConnection,PinnedContext,CancellationToken,Task<object>> work)=>Locked(c,session=>
{
    if(session.MarketContext is not null)
        throw new MarketDataException("interval_curve_historical","Return to working data to read interval curves; their delivery inputs are current definitions.");
    var profile=Profile(session);
    var pin=Pin(session,snapshot);
    return database.WithIdentity(profile,"ReadMarketData",async(connection,ct)=>Results.Ok(await work(connection,pin,ct)),c.RequestAborted);
});
api.MapPost("/interval-curves/catalog",(HttpContext c,IntervalCurveCatalogRequest body)=>IntervalCurveRead(c,body.SnapshotId,intervalCurves.Catalog));
api.MapPost("/interval-curves/values",(HttpContext c,IntervalCurveValuesRequest body)=>IntervalCurveRead(c,body.SnapshotId,(connection,pin,ct)=>intervalCurves.Values(connection,pin,body,ct)));
api.MapGet("/schedules",(Func<HttpContext,Task<IResult>>)(c=>ScheduleOperation(c,(session,data)=>
{
    var sources=(database.Manifest.Schedules??[]).Select(source=>
    {
        var sequence=SchedulePreview.Compile(data,source);
        return new {source.Curve,name=data.Definitions["curves"].Single(c=>c.Text("code")==source.Curve).Text("name"),source.From,source.To,source.ExampleAt,
            entries=sequence.Entries.Select(e=>new {contractId=e.Contract.Key.Id,e.Ordinal,
                label=data.References["futures_series"].Single(s=>s.Text("code")==e.Contract.FuturesSeries!.Value.Code).Text("series_label"),
                e.EligibleFrom,rollAt=e.Contract.Expiry,beforeExpiryAt=e.Contract.Expiry.AddTicks(-1),e.Contract.DeliveryFrom,e.Contract.DeliveryTo,e.LastTradingDate}).ToArray()};
    }).ToArray();
    return Results.Ok(new {sources,shortcuts=session.Shortcuts.For(Profile(session)).Select(SchedulePreview.Describe).ToArray(),
        dateSchedules=session.Dates.For(Profile(session)).ToArray(),draftCurves=session.Dates.Curves(Profile(session)).ToArray(),
        curveTemplates=data.Definitions["curves"].Select(c=>new {code=c.Text("code")}).ToArray(),dateRoles=SchedulePreview.DateRoleChoices});
})));
api.MapPost("/schedules/preview",(HttpContext c,SchedulePreviewRequest body)=>ScheduleOperation(c,(session,data)=>
    Results.Ok(SchedulePreview.Result(SchedulePreview.Create(SchedulePreview.Compile(data,ScheduleSource(body.Curve)),body),SchedulePreview.ParseAt(body.At)))));
api.MapPost("/schedules/keep",(HttpContext c,SchedulePreviewRequest body)=>ScheduleOperation(c,(session,data)=>
{
    var shortcut=SchedulePreview.Create(SchedulePreview.Compile(data,ScheduleSource(body.Curve)),body);
    if(session.Dates.For(Profile(session)).Any(s=>s.Definition.Symbol==shortcut.Symbol))
        throw new TemporalException("date_definition_conflict","A date schedule already uses this shortcut.");
    _=shortcut.Resolve(SchedulePreview.ParseAt(body.At));
    return Results.Ok(SchedulePreview.Describe(session.Shortcuts.Keep(Profile(session),shortcut)));
}));
api.MapPost("/schedules/resolve",(HttpContext c,ShortcutResolveRequest body)=>ScheduleOperation(c,(session,data)=>
{
    var shortcut=session.Shortcuts.Find(Profile(session),body.Symbol);
    var source=ScheduleSource(shortcut.Sequence.Key.Code);
    if(shortcut.Sequence.Key.Revision!=new DefinitionRevision(database.Manifest.CatalogId,database.Manifest.RevisionId) ||
       shortcut.Sequence.References!=new ReferenceRevision(database.Manifest.ReferenceCatalogId,database.Manifest.ReferenceRevisionId) ||
       source.From!=shortcut.Sequence.From || source.To!=shortcut.Sequence.To)
        throw new MarketDataException("sequence_definition","The retained shortcut pins differ from this preview's exact source bundle.");
    return Results.Ok(SchedulePreview.Result(shortcut,SchedulePreview.ParseAt(body.At)));
}));
DateReceipt ResolveDate(PreviewSession session,PreviewData data,string? expression,string? at,CalendarDraft? calendar)
{
    var instant=at is null? (DateTimeOffset?)null:SchedulePreview.ParseAt(at);
    return session.Dates.Resolve(Profile(session),expression,instant,symbol=>
    {
        var shortcut=session.Shortcuts.Find(Profile(session),symbol);
        var source=ScheduleSource(shortcut.Sequence.Key.Code);
        var current=SchedulePreview.Compile(data,source);
        if(current.Key!=shortcut.Sequence.Key || current.References!=shortcut.Sequence.References ||
            current.From!=shortcut.Sequence.From || current.To!=shortcut.Sequence.To)
            throw new MarketDataException("sequence_definition","The shortcut no longer matches the selected exact source bundle.");
        var resolved=shortcut.Resolve(instant!.Value);
        return new(symbol,resolved.Date,symbol,null,null,null,resolved);
    },calendar);
}
api.MapPost("/schedules/dates/preview",(HttpContext c,MonthlyDraft body)=>ScheduleOperation(c,(session,data)=>Results.Ok(DateWorkspace.Compile(body))));
api.MapPost("/schedules/dates/keep",(HttpContext c,MonthlyDraft body)=>ScheduleOperation(c,(session,data)=>
{
    var compiled=DateWorkspace.Compile(body);
    if(session.Shortcuts.For(Profile(session)).Any(s=>s.Symbol==body.Symbol))
        throw new TemporalException("date_definition_conflict","A contract shortcut already uses this name.");
    return Results.Ok(session.Dates.Keep(Profile(session),compiled));
}));
api.MapPost("/schedules/expression",(HttpContext c,DateExpressionRequest body)=>ScheduleOperation(c,(session,data)=>
    Results.Ok(ResolveDate(session,data,body.Expression,body.At,body.RollingCalendar))));
CurveDatesSaved CompileCurve(PreviewSession session,PreviewData data,CurveDatesRequest body)
{
    if(body.Code is null || !System.Text.RegularExpressions.Regex.IsMatch(body.Code,@"\A[a-z][a-z0-9_]{0,31}\z") ||
        string.IsNullOrWhiteSpace(body.Name) || body.Name.Length>80 || body.Contracts is null || body.Contracts.Length is <1 or >120 ||
        body.Contracts.Any(c=>c is null) ||
        body.Contracts.Select(c=>c.Label).Distinct().Count()!=body.Contracts.Length)
        throw new TemporalException("contract_dates","Name the draft curve and supply 1–120 uniquely named contracts.");
    if(data.Definitions["curves"].Any(c=>c.Text("code")==body.Code))
        throw new TemporalException("date_definition_conflict","Choose a new curve code; a retained definition already uses this code.");
    var templates=data.Definitions["curves"].Where(c=>c.Text("code")==body.TemplateCurve).ToArray();
    if(templates.Length!=1)throw new MarketDataException("missing_curve","Select an exact curve specification to reuse.");
    var at=SchedulePreview.ParseAt(body.At);
    var contracts=body.Contracts.Select(d=>DateWorkspace.Contract(d,e=>ResolveDate(session,data,e,body.At,body.RollingCalendar))).ToImmutableArray();
    // A curve's configured contract order must remain deterministic and unambiguous.
    for(var i=1;i<contracts.Length;i++)
        if(contracts[i].DeliveryFrom<contracts[i-1].DeliveryTo || contracts[i].Expiry<=contracts[i-1].Expiry)
            throw new TemporalException("contract_dates","Contracts must have ordered, nonoverlapping delivery periods and increasing expiries.");
    return new(body.Code,body.Name,body.TemplateCurve!,templates[0].Clone(),
        new DefinitionRevision(database.Manifest.CatalogId,database.Manifest.RevisionId),
        new ReferenceRevision(database.Manifest.ReferenceCatalogId,database.Manifest.ReferenceRevisionId),
        at.ToString("O",System.Globalization.CultureInfo.InvariantCulture),body.RollingCalendar,contracts);
}
api.MapPost("/schedules/contracts/preview",(HttpContext c,CurveDatesRequest body)=>ScheduleOperation(c,(session,data)=>Results.Ok(CompileCurve(session,data,body))));
api.MapPost("/schedules/contracts/keep",(HttpContext c,CurveDatesRequest body)=>ScheduleOperation(c,(session,data)=>
    Results.Ok(session.Dates.KeepCurve(Profile(session),CompileCurve(session,data,body)))));
var scheduleStore=new ScheduleStore(database.Manifest.CatalogId,database.Manifest.ReferenceCatalogId,database.Manifest.ReferenceRevisionId);
var knowledgeStore=new KnowledgeStore(database.Manifest.CatalogId);
var contractDateStore=new ContractDateStore(database.Manifest.CatalogId,scheduleStore);
Task<IResult> DateOperation(HttpContext c,string permission,Func<PreviewSession,NpgsqlConnection,CancellationToken,Task<IResult>> work)=>Locked(c,session=>
    database.WithIdentity(Profile(session),permission,async(connection,ct)=>
    {
        if(session.MarketContext is {} selected)await MarketContextStore.Bind(connection,database.Manifest.CatalogId,selected,ct);
        return await work(session,connection,ct);
    },c.RequestAborted));
api.MapGet("/date-schedules",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    var library=await scheduleReads.Library(connection,Pin(session),ct);
    return Results.Ok(new {library.KnownThrough,library.Versions,library.CanEdit,library.CanPublish,choices=DateWorkspace.Choices,
        library.ContractDefinitions,library.Calendars,examples=ScheduleExamples.Catalog});
},c.RequestAborted))));
api.MapPost("/date-schedules/preview",(HttpContext c,DateSchedulePreviewRequest body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(DateWorkspace.Compile(body.Definition??throw new TemporalException("schedule_definition","Supply an explicit schedule definition."),
        await scheduleStore.Calendar(connection,body.Calendar,ct)))));
api.MapPost("/date-schedules/save",(HttpContext c,ScheduleSaveRequest body)=>DateOperation(c,"WriteMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await scheduleStore.Save(connection,body,ct))));
api.MapPost("/date-schedules/publish",(HttpContext c,SchedulePublishRequest body)=>DateOperation(c,"SealMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await scheduleStore.Publish(connection,body,ct))));
api.MapPost("/date-schedules/context",(HttpContext c,ContextCaptureRequest body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(await knowledgeStore.Capture(connection,body,ct))));
async Task<(ImmutableArray<ScheduleVersion> Versions,ScheduleContext Context)> ScheduleView(NpgsqlConnection connection,ScheduleContext? context,CancellationToken ct)
{
    if(context is null)throw new TemporalException("schedule_context","Choose an explicit temporal context.");
    if(context.KnownAt is not null || context.ContextId is not null)
    {
        var resolved=await knowledgeStore.Resolve(connection,context,ct);
        return(await scheduleStore.ReadAt(connection,resolved,ct),resolved);
    }
    // Explicit logical-prefix replay is retained for old API clients; this is
    // a different authoring form from a certified wall-clock request.
    _=PublishedSchedules.ContextDate(context);var head=await scheduleStore.Capture(connection,ct);
    if(context.KnownThrough>head)throw new TemporalException("schedule_context","This publication view has not been captured in this catalog.");
    return(await scheduleStore.Read(connection,ct),context);
}
api.MapPost("/date-schedules/resolve",(HttpContext c,PublishedDateRequest body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
{
    var view=await ScheduleView(connection,body.Context,ct);
    return Results.Ok(PublishedSchedules.Resolve(view.Versions,body.Expression,view.Context));
}));
api.MapPost("/date-schedules/catalog",(HttpContext c,ScheduleCatalogRequest body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
{
    var view=await ScheduleView(connection,body.Context,ct);
    return Results.Ok(new{context=view.Context,references=PublishedSchedules.Catalog(view.Versions,view.Context)});
}));
async Task<BoundContractDefinition> BindContracts(NpgsqlConnection connection,PublishedContractRequest body,CancellationToken ct)
{
    var view=await ScheduleView(connection,body.Context,ct);
    return ContractDateStore.Compile(body,view.Versions,view.Context);
}
api.MapPost("/date-schedules/contracts/preview",(HttpContext c,PublishedContractRequest body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(await BindContracts(connection,body,ct))));
api.MapPost("/date-schedules/contracts/example",(HttpContext c,ContractExampleRequest body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
{
    var view=await ScheduleView(connection,body.Context,ct);
    var example=ScheduleExamples.Contracts(body,view.Versions,view.Context);
    _=await BindContracts(connection,example.Definition,ct);
    return Results.Ok(example);
}));
api.MapPost("/date-schedules/contracts/save",(HttpContext c,ContractDateSaveRequest body)=>DateOperation(c,"WriteMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await contractDateStore.Save(connection,body,ct))));
api.MapPost("/date-schedules/contracts/publish",(HttpContext c,ContractDatePublishRequest body)=>DateOperation(c,"SealMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await contractDateStore.Publish(connection,body,ct))));
api.MapPost("/date-schedules/contracts/resolve",(HttpContext c,ContractDateResolveRequest body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(await contractDateStore.Resolve(connection,body,session.MarketContext,ct))));
api.MapPost("/market/context",(HttpContext c,MarketContextRequest body)=>Locked(c,async session=>
{
    if(body.Selection!="latest_correction")throw new MarketDataException("selection_policy","Choose the explicit latest-known correction policy.");
    var snapshot=await database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    {
        var captured=await knowledgeStore.Capture(connection,new(body.EffectiveOn,body.KnownAt,body.Mode),ct);
        await MarketContextStore.Bind(connection,database.Manifest.CatalogId,captured.Context,ct);
        var data=await database.Read(connection,ct);
        var aliases=session.SelectContextAliases(data,captured.Context);
        var stored=await MarketCloses.Frames(connection,database.Manifest.CatalogId,database.Manifest.RevisionId,captured.Context.ContextId,ct,session.DurableLimit(aliases));
        return (captured,plan:session.PlanFrames(stored,captured.Context,aliases));
    },c.RequestAborted);
    session.ApplyFrames(snapshot.plan);
    session.MarketContext=snapshot.captured.Context;
    return Results.Ok(snapshot.captured);
}));
api.MapPost("/market/current",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,async session=>
{
    await database.WithIdentity(Profile(session),"ReadMarketData",(_,_)=>Task.FromResult(true),c.RequestAborted);
    session.ForgetDiscoveredFrames();session.MarketContext=null;return Results.Ok(new {mode="explicit_input_pins"});
})));
object WorkspaceProjection(PreviewSession session,WorkspaceRead read,bool catalogOnly)
{
    var data=read.Data;
    return new {unitCatalog=read.Units,unitCatalogError=read.Units is null?"unit_catalog":null,referenceDisplay=read.ReferenceDisplay,unitReferenceDisplay=read.UnitReferenceDisplay,context=session.MarketContext,profile=session.Profile,example=database.Manifest.Example,futuresConvention=database.Manifest.FuturesConvention,curveNames=read.CurveNames,powerCurveCodes=PowerPreview.CurveCodes(data),definitions=data.Definitions,references=data.References,referenceScope=catalogOnly?"definition_closure":"all_visible",observations=catalogOnly?ImmutableArray<JsonElement>.Empty:data.Observations,observationScope=catalogOnly?"on_demand":"all_visible",
        frames=session.VisibleFrames,frameCatalog=session.FrameCatalog,initialFrameIds=session.MarketContext is null?session.InitialFrames:session.ContextAliases(session.MarketContext).Select(f=>f.Id).ToArray()};
}
Task<IResult> Workspace(HttpContext c,bool catalogOnly)=>Locked(c,async session=>
{
    var read=await database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    {
        var workspace=await marketReads.Workspace(connection,Pin(session),catalogOnly,ct);
        return(workspace,plan:await DiscoverFrames(connection,session,ct));
    },c.RequestAborted);
    session.ApplyFrames(read.plan);
    return Results.Ok(WorkspaceProjection(session,read.workspace,catalogOnly));
});
api.MapGet("/workspace",(Func<HttpContext,Task<IResult>>)(c=>Workspace(c,false)));
api.MapGet("/workspace/catalog",(Func<HttpContext,Task<IResult>>)(c=>Workspace(c,true)));
api.MapGet("/workspace/navigation",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,async session=>
{
    var read=await database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    {
        var navigation=await marketReads.Navigation(connection,Pin(session),ct);
        return(navigation,plan:await DiscoverFrames(connection,session,ct));
    },c.RequestAborted);
    session.ApplyFrames(read.plan);var frames=session.VisibleFrames;
    return Results.Ok(new MarketNavigation(Profile(session),session.MarketContext,read.navigation.DefinitionRevision,read.navigation.ReferenceRevision,
        database.Manifest.FuturesConvention,database.Manifest.Example,
        session.MarketContext is null?[..session.InitialFrames]:[..session.ContextAliases(session.MarketContext).Select(f=>f.Id)],
        [..frames.Select(f=>new NavigationFrame(f.Id,f.Name,f.At,f.OriginCloseId))],read.navigation.CurveNames,read.navigation.Curves,read.navigation.ReferenceDisplay){FrameCatalog=session.FrameCatalog});
})));
api.MapGet("/reference/catalog",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    var requestedOffset=c.Request.Query["offset"].ToString();
    if(!int.TryParse(c.Request.Query.ContainsKey("offset")?requestedOffset:"0",System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var offset))
        throw new MarketDataException("reference_page",$"Choose an offset from 0 to {PreviewDatabase.ReferenceMaximumOffset}.");
    var page=await marketReads.References(connection,Pin(session),c.Request.Query["table"].ToString(),offset,c.Request.Query["search"].ToString(),ct);
    var referenceDisplay=await ReferenceEditors.Display(connection,page.ReferenceRevision,ct);
    return Results.Ok(new {page.Table,page.Rows,page.References,page.Offset,page.PageSize,page.MaximumOffset,page.HasMore,page.Search,page.ReferenceRevision,page.DefinitionRevisionId,referenceDisplay,
        tables=PreviewDatabase.ReferenceTables,referenceScope="selected_table",context=session.MarketContext,profile=session.Profile});
},c.RequestAborted))));
api.MapGet("/reference/editor",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    await Pin(session).Bind(connection,database.Manifest.CatalogId,ct);
    var code=c.Request.Query.TryGetValue("code",out var selected)?selected.ToString():null;
    return Results.Ok(await ReferenceEditors.Read(connection,database.Manifest,c.Request.Query["table"].ToString(),ct,code));
},c.RequestAborted))));
Task<IResult> ReferenceMutation(HttpContext c,string permission,Func<NpgsqlConnection,CancellationToken,Task<JsonElement>> work)=>Locked(c,session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before saving or applying reference display changes.");
    return database.WithIdentity<IResult>(Profile(session),permission,async(connection,ct)=>
    {
        c.Items["reference_attempted"]=true;
        return Results.Ok(await work(connection,ct));
    },c.RequestAborted);
});
api.MapPost("/reference/save",(HttpContext c,ReferenceSaveRequest body)=>ReferenceMutation(c,"WriteReferenceData",(connection,ct)=>ReferenceEditors.Save(connection,database.Manifest,body,ct)));
api.MapPost("/reference/apply",(HttpContext c,ReferenceApplyRequest body)=>ReferenceMutation(c,"SealReferenceData",(connection,ct)=>ReferenceEditors.Apply(connection,database.Manifest,body,ct)));
api.MapPost("/evaluate",(HttpContext c,BuildRequest body)=>Locked(c,async session=>
{
    var pin=Pin(session,body.SnapshotId);
    var read=await database.WithIdentity(Profile(session),"ReadMarketData",(connection,ct)=>marketReads.Evaluate(connection,pin,body.Curve,ct,body.IncludeWorkspace),c.RequestAborted);
    var result=read.Result;
    if(pin.Context is not null || body.IncludeSources || body.IncludeWorkspace)
    {
        var json=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(result,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}))!.AsObject();
        if(pin.Context is {} view)json["context"]=JsonSerializer.SerializeToNode(view,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        if(body.IncludeSources)
        {
            json["observations"]=JsonSerializer.SerializeToNode(read.Observations);
            json["observationScope"]="selected_frame";
        }
        if(body.IncludeWorkspace)json["workspace"]=JsonSerializer.SerializeToNode(WorkspaceProjection(session,read.Workspace??throw new InvalidOperationException("The requested workspace projection is missing."),true),new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
        return Results.Ok(json);
    }
    return Results.Ok(result);
}));
api.MapPost("/history",(HttpContext c,HistoryRequest body)=>Locked(c,async session=>
{
    await RefreshFrames(session,c.RequestAborted);
    var pin=Pin(session,body.SnapshotId);
    // Current history includes initial inputs and every offered stored close;
    // unselected manual edits remain separate explicit input choices.
    var frames=session.MarketContext is null?session.InitialFrames.Select(id=>Frame(session,id)).Concat(session.VisibleFrames.Where(frame=>frame.OriginCloseId is not null)):session.VisibleFrames;
    var points=await database.WithIdentity(Profile(session),"ReadMarketData",(connection,ct)=>marketReads.History(connection,pin,[..frames],body.Curve,body.ContractId,body.SeriesId,ct),c.RequestAborted);
    return Results.Ok(new {mode="exact_visible_close_history_with_selected_instant_v1",points});
}));
api.MapPost("/corrections",(HttpContext c,CorrectionRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to current input editing before changing observations.");
    var frame=Frame(session,body.SnapshotId);
    // The correction replaces the pin in whichever bundle of this snapshot holds it (D172).
    var held=frame.ObservationIds.Contains(body.ObservationId)?[]:(database.Manifest.SwapMarkets??[])
        .Select(m=>(m.RevisionId,Pins:database.SwapMarketPins(frame,m)??[])).Where(m=>m.Pins.Contains(body.ObservationId)).ToArray();
    if (held.Length>1) throw new MarketDataException("ambiguous_reference","A required exact reference is missing or ambiguous.");
    if (!frame.ObservationIds.Contains(body.ObservationId)&&held.Length==0) throw new MarketDataException("snapshot_input","Only an explicitly selected observation can be corrected here.");
    if (session.Frames.Count>=128) throw new MarketDataException("preview_limit","Restart the disposable preview to create more saved selections.");
    if (string.IsNullOrWhiteSpace(body.Value)) throw new MarketDataException("missing_value","Enter an exact decimal value.");
    c.Items["correction_attempted"]=true;
    var revision=held.Length==1?held[0].RevisionId:database.Manifest.RevisionId;
    var id=await database.WithIdentity(Profile(session),"ImportObservations",(connection,ct)=>database.Correct(connection,revision,body.ObservationId,body.Value,ct),c.RequestAborted);
    var updated=held.Length==0?frame.SelectRevision(body.ObservationId,id,"Edited inputs")
        :frame.SelectSwapMarketRevision(revision,held[0].Pins,body.ObservationId,id,"Edited inputs");
    session.Frames.Add(updated.Id,updated);
    return Results.Ok(new {frame=updated,observationId=id});
}));
// The pin a chosen revision replaces: the one pin of the same exact source fact and time.
static Guid SameFact(PreviewData data,ImmutableArray<Guid> pins,Guid chosenId)
{
    var chosen=data.Observation(chosenId);
    var previous=pins.Where(id=>
    {
        var o=data.Observation(id);
        return o.Id("series_id")==chosen.Id("series_id")&&o.Text("source_event_id")==chosen.Text("source_event_id")&&o.Text("source_code")==chosen.Text("source_code")&&o.Text("role")==chosen.Text("role")&&o.Instant("observed_at")==chosen.Instant("observed_at");
    }).ToArray();
    if (previous.Length!=1) throw new MarketDataException("selection_mismatch","Select a revision of the same exact source fact and time.");
    return previous[0];
}
api.MapPost("/select-observation",(HttpContext c,SelectionRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to current input editing before changing selected revisions.");
    var frame=Frame(session,body.SnapshotId);
    var updated=await database.WithIdentity(Profile(session),"ReadMarketData",async (connection,ct)=>
    {
        // The chosen revision replaces the pin of the same source fact in the bundle that holds it (D172).
        var data=await database.Read(connection,ct);
        if(!data.HasObservation(body.ObservationId))
            foreach(var market in database.Manifest.SwapMarkets??[])
            {
                if(database.SwapMarketPins(frame,market) is not {} pins||!await database.SwapMarketAvailable(connection,market,ct))continue;
                var bundle=await database.Read(connection,ct,market.RevisionId);
                if(bundle.HasObservation(body.ObservationId))
                    return frame.SelectSwapMarketRevision(market.RevisionId,pins,SameFact(bundle,pins,body.ObservationId),body.ObservationId,"Explicit revision selection");
            }
        return frame.SelectRevision(SameFact(data,frame.ObservationIds,body.ObservationId),body.ObservationId,"Explicit revision selection");
    },c.RequestAborted);
    if (session.Frames.Count>=128) throw new MarketDataException("preview_limit","Restart the disposable preview to create more saved selections.");
    session.Frames.Add(updated.Id,updated); return Results.Ok(new {frame=updated});
}));
// The party page reads a historical view's own context, or working data as of current knowledge
// it captures at the selected frame's date.
async Task<TradingView> PartyView(NpgsqlConnection connection,PreviewSession session,Guid frame,CancellationToken ct)
{
    var pin=Pin(session,frame);
    return pin.Context is {} retained?TradingView.Retained(pin.Frame,retained):await TradingView.Capture(connection,knowledgeStore,pin.Frame,ct);
}
api.MapPost("/parties/catalog",(HttpContext c,PartyReadRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTradeParties",async(connection,ct)=>
        Results.Ok(await partyReads.Catalog(connection,await PartyView(connection,session,body.SnapshotId,ct),body.Kind,ct)),c.RequestAborted)));
api.MapPost("/parties/save",(HttpContext c,PartySaveRequest body)=>Locked(c,session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before editing party records.");
    c.Items["party_attempted"]=true;
    return database.WithIdentity(Profile(session),"WriteTradeParties",async(connection,ct)=>Results.Ok(await PartyEditors.Save(connection,database.Manifest.CatalogId,body,ct)),c.RequestAborted);
}));
api.MapGet("/table-views/blotter",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await TableViews.Read(connection,ct)),c.RequestAborted))));
api.MapPost("/table-views/blotter",(HttpContext c,SaveTableView body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await TableViews.Save(connection,body,ct)),c.RequestAborted)));
api.MapPost("/table-views/blotter/delete",(HttpContext c,DeleteTableView body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>{await TableViews.Delete(connection,body,ct);return Results.Ok(new {deleted=true});},c.RequestAborted)));
var futuresStore=new FuturesStore(database.Manifest);
// Working data, the only editable view: current knowledge captured for this read over a working frame.
bool Working(PreviewSession session,FuturesReadRequest body)=>session.MarketContext is null&&body is {KnowledgeMode:"current",ContextId:null};
// Trading pages name their knowledge: current knowledge, captured now at the working frame's date;
// a context captured earlier, re-read over a working frame; or the selected historical view's own.
async Task<TradingView> TradeView(NpgsqlConnection connection,PreviewSession session,FuturesReadRequest body,InputFrame frame,CancellationToken ct)
{
    if(Working(session,body))return await TradingView.Capture(connection,knowledgeStore,frame,ct);
    if(body is not {KnowledgeMode:"retained",ContextId:{} id})throw new MarketDataException("futures_context","Choose current knowledge explicitly or supply the exact retained context.");
    var context=(await knowledgeStore.Read(connection,id,ct)).Context;
    if(session.MarketContext is null)return TradingView.Reread(frame,context);
    return session.MarketContext.ContextId==context.ContextId?TradingView.Retained(frame,context)
        :throw new MarketDataException("futures_context","Trading must use the shared selected historical context.");
}
// Trading pages without a read operation of their own run their store's read on the shared trading read path.
Task<IResult> FuturesRead(HttpContext c,FuturesReadRequest body,Func<NpgsqlConnection,PreviewData,InputFrame,CancellationToken,Task<object>> read,bool scenario=false,Guid? headTrade=null,bool amendmentPreview=false,string? expectedConvention=null,bool dealRisk=false)=>
    TradingRead(c,body,(connection,view,ct)=>tradeReads.Read(connection,view,data=>read(connection,data,view.CalculationFrame,ct),ct),scenario,headTrade,amendmentPreview,expectedConvention,dealRisk);
// The page keeps request policy: conventions, risk and amendment permissions, and the booking and amendment flags.
Task<IResult> TradingRead(HttpContext c,FuturesReadRequest body,Func<NpgsqlConnection,TradingView,CancellationToken,Task<object>> read,bool scenario=false,Guid? headTrade=null,bool amendmentPreview=false,string? expectedConvention=null,bool dealRisk=false,bool combinedWorkspace=false)=>
    Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
    {
        if(expectedConvention is SwapStore.Convention or TradingBlotter.Convention){if(body.Convention!=expectedConvention)throw new MarketDataException("swap_context","Select the explicit supported swap valuation convention.");}
        else FuturesStore.Convention(body.Convention);
        if(scenario && !await PreviewDatabase.HasPermission(connection,"RunScenario",ct)||dealRisk && !await PreviewDatabase.HasPermission(connection,"RunDealRisk",ct))throw new PreviewDeniedException();
        var frame=Frame(session,body.SnapshotId);
        var working=Working(session,body);
        if(amendmentPreview&&!working)
            throw new MarketDataException("historical_read_only","Amendments require working data.");
        if(amendmentPreview&&!await PreviewDatabase.HasPermission(connection,"AmendTrade",ct))throw new PreviewDeniedException();
        int? currentHead=null;
        if(working&&headTrade is {} trade)
        {
            await using var head=ScheduleStore.Command(connection,expectedConvention==SwapStore.Convention?"SELECT security.swap_current_head($1,$2)":"SELECT security.futures_current_head($1,$2)",database.Manifest.CatalogId,trade);
            currentHead=await head.ExecuteScalarAsync(ct) is int revision?revision:null;
        }
        var view=await TradeView(connection,session,body,frame,ct);
        var value=await read(connection,view,ct);
        if(scenario && !await PreviewDatabase.HasPermission(connection,"RunScenario",ct)||dealRisk && !await PreviewDatabase.HasPermission(connection,"RunDealRisk",ct))throw new PreviewDeniedException();
        var response=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(value,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,Converters={new ExactDecimalJsonConverter()}}))!.AsObject();
        foreach(var json in combinedWorkspace?new[]{response["catalog"]!.AsObject(),response["blotter"]!.AsObject()}:new[]{response})
        {
        json["context"]=JsonSerializer.SerializeToNode(view.Context,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});json["asOf"]=JsonSerializer.SerializeToNode(frame.At);
        json["canRunDealRisk"]=JsonSerializer.SerializeToNode(await PreviewDatabase.HasPermission(connection,"RunDealRisk",ct));
        json["canBook"]=JsonSerializer.SerializeToNode(working&&await PreviewDatabase.HasPermission(connection,"BookTrade",ct));
        if(json["books"] is not null)
        {
            json["canAmendTrades"]=JsonSerializer.SerializeToNode(working&&await PreviewDatabase.HasPermission(connection,"AmendTrade",ct));
            json["bookingUnavailableReason"]=JsonSerializer.SerializeToNode(!working
                ?"Historical view is read-only. Return to working data, then select an As of date."
                :!await PreviewDatabase.HasPermission(connection,"BookTrade",ct)?"Your current role does not have Book Trade permission.":null);
        }
        if(headTrade is not null)
        {
            var selectedRevision=json["state"]?["revision"]?.GetValue<int>()??json["expectedRevision"]?.GetValue<int>();
            var lifecycleEligible=json["lifecycleEligible"]?.GetValue<bool>()??true;
            var canAmend=working&&currentHead==selectedRevision&&lifecycleEligible&&await PreviewDatabase.HasPermission(connection,"AmendTrade",ct);
            if(amendmentPreview&&!canAmend)throw new MarketDataException(expectedConvention==SwapStore.Convention?"swap_revision_conflict":"futures_revision_conflict","Reload the latest eligible trade revision before amending.");
            json["canAmend"]=JsonSerializer.SerializeToNode(canAmend);
            json["amendUnavailableReason"]=JsonSerializer.SerializeToNode(!working?"Historical view is read-only. Return to working data to amend."
                :currentHead!=selectedRevision?"This is an earlier trade revision. Open the latest revision at its working As of date."
                :!lifecycleEligible?"This retained state is outside the supported live, unprocessed futures lifecycle. Settlement must be handled separately."
                :!canAmend?"Your current role does not have Amend Trade permission.":null);
        }
        }
        if(scenario)
        {
            // Financial projections and capability labels are now materialized.
            // Independently expiring market/scenario grants must still be live.
            await using var publication=ScheduleStore.Command(connection,
                "SELECT security.trading_access($1,'ReadTrades') AND security.market_access($1,'ReadMarketData') AND security.has_permission('RunScenario')",database.Manifest.CatalogId);
            if(!Equals(await publication.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        }
        return Results.Ok(response);
    },c.RequestAborted));
api.MapPost("/trade-formats/read",(HttpContext c,DealFormatRead body)=>
{
 var context=body.Context??throw new MarketDataException("amount_format","Supply the selected deal context.");
 return FuturesRead(c,context,(connection,data,frame,ct)=>DealAmountFormats.Read(connection,database.Manifest.CatalogId,body.TradeId,ct),expectedConvention:context.Convention==SwapStore.Convention?SwapStore.Convention:null);
});
api.MapPost("/trade-formats/save",(HttpContext c,DealFormatSave body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>
{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data to edit deal presentation.");
 return Results.Ok(await DealAmountFormats.Save(connection,database.Manifest.CatalogId,body,ct));
},c.RequestAborted)));
api.MapPost("/futures/catalog",(HttpContext c,FuturesReadRequest body)=>FuturesRead(c,body,futuresStore.Catalog));
api.MapPost("/futures/editor",(HttpContext c,FuturesEditorRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("futures_trade","Supply the selected context."),
    (connection,view,ct)=>tradeReads.FuturesTrade(connection,view,body.TradeId,body.Revision,ct),headTrade:body.TradeId));
api.MapPost("/futures/amend-preview",(HttpContext c,FuturesAmendRequest body)=>FuturesRead(c,new(body.SnapshotId,body.Convention,body.KnowledgeMode),
    (connection,data,frame,ct)=>futuresStore.PreviewAmendment(connection,data,frame,body,ct),headTrade:body.TradeId,amendmentPreview:true));
api.MapPost("/futures/amend",(HttpContext c,FuturesAmendRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before amending.");
    c.Items["futures_attempted"]=true;
    return await database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>
    {
        if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct)||!await PreviewDatabase.HasPermission(connection,"ReadMarketData",ct))throw new PreviewDeniedException();
        return Results.Ok(await futuresStore.Amend(connection,await database.Read(connection,ct),Frame(session,body.SnapshotId),body,ct));
    },c.RequestAborted);
}));
api.MapPost("/futures/blotter",(HttpContext c,FuturesBlotterRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("blotter_view","Supply the selected context."),(connection,data,frame,ct)=>futuresStore.Blotter(connection,data,frame,body.View,ct)));
api.MapPost("/futures/holdings",(HttpContext c,FuturesReadRequest body)=>FuturesRead(c,body,futuresStore.Holdings));
var swapStore=new SwapStore(database);
api.MapPost("/trading/workspace",(HttpContext c,TradingWorkspaceRequest body)=>
{
 if(body.BookingCatalog is not(null or "directory" or "priced_contracts"))throw new MarketDataException("blotter_view","Choose an available booking catalog projection.");
 return TradingRead(c,body.Context??throw new MarketDataException("blotter_view","Supply the selected context."),
  (connection,view,ct)=>tradeReads.Workspace(connection,view,body.View,body.BookingCatalog=="directory",ct),expectedConvention:TradingBlotter.Convention,combinedWorkspace:true);
});
api.MapPost("/trading/blotter",(HttpContext c,FuturesBlotterRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("blotter_view","Supply the selected context."),
 (connection,view,ct)=>tradeReads.Blotter(connection,view,body.View,ct),expectedConvention:TradingBlotter.Convention));
api.MapPost("/swaps/catalog",(HttpContext c,FuturesReadRequest body)=>FuturesRead(c,body,swapStore.Catalog,expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/editor",(HttpContext c,SwapEditorRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("swap_context","Supply the selected context."),
 (connection,view,ct)=>tradeReads.SwapTrade(connection,view,body.TradeId,body.Revision,ct),headTrade:body.TradeId,expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/preview",(HttpContext c,SwapWriteRequest body)=>FuturesRead(c,new(body.SnapshotId,body.Convention,body.KnowledgeMode),
 (connection,data,frame,ct)=>swapStore.Preview(connection,data,frame,body,ct),headTrade:body.TradeId,amendmentPreview:body.TradeId is not null,expectedConvention:SwapStore.Convention));
Task<IResult> SaveSwap(HttpContext c,SwapWriteRequest body,bool amend)=>Locked(c,session=>database.WithIdentity(Profile(session),amend?"AmendTrade":"BookTrade",async(connection,ct)=>
{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data to book or amend.");
 if(amend!=(body.TradeId is not null))throw new MarketDataException("swap_input","Choose booking or latest-head amendment explicitly.");
 c.Items["swap_attempted"]=true;
 await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102)"))await gate.ExecuteNonQueryAsync(ct);
 return Results.Ok(await swapStore.Save(connection,await database.Read(connection,ct),Frame(session,body.SnapshotId),body,ct));
},c.RequestAborted));
api.MapPost("/swaps/book",(HttpContext c,SwapWriteRequest body)=>SaveSwap(c,body,false));
api.MapPost("/swaps/amend",(HttpContext c,SwapWriteRequest body)=>SaveSwap(c,body,true));
api.MapPost("/swaps/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await swapStore.Status(connection,body.OperationId,ct)),c.RequestAborted)));
api.MapPost("/futures/price",(HttpContext c,FuturesDraftRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("draft_context","Choose the draft market context."),(connection,data,frame,ct)=>futuresStore.PriceDraft(connection,data,frame,body,ct)));
api.MapPost("/futures/draft-risk",(HttpContext c,FuturesDraftRiskRequest body)=>FuturesRead(c,body.Draft?.Context??throw new MarketDataException("draft_context","Choose the draft and market context."),(connection,data,frame,ct)=>futuresStore.RiskDraft(connection,data,frame,body,ct),dealRisk:true));
api.MapPost("/swaps/price",(HttpContext c,SwapDraftRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("draft_context","Choose the draft market context."),(connection,data,frame,ct)=>swapStore.PriceDraft(connection,data,frame,body,ct),expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/draft-risk",(HttpContext c,SwapDraftRiskRequest body)=>FuturesRead(c,body.Draft?.Context??throw new MarketDataException("draft_context","Choose the draft and market context."),(connection,data,frame,ct)=>swapStore.RiskDraft(connection,data,frame,body,ct),expectedConvention:SwapStore.Convention,dealRisk:true));
var futuresRisk=new FuturesRiskPreview(futuresStore);
var portfolioRisk=new PortfolioRiskPreview(database);
Task<IResult> PortfolioRead(HttpContext c,FuturesReadRequest body,Func<NpgsqlConnection,PreviewData,InputFrame,CancellationToken,Task<object>> read)=>
    TradingRead(c,body,(connection,view,ct)=>tradeReads.Read(connection,view,
        data=>read(connection,data,view.CalculationFrame,ct),ct,
        referenceScope:ReferenceReadScope.TradeValuation,observationScope:ObservationReadScope.ExactPins),
        scenario:true,expectedConvention:TradingBlotter.Convention);
api.MapPost("/portfolio/risk/catalog",(HttpContext c,FuturesRiskCatalogRequest body)=>PortfolioRead(c,
    body.Context??throw new MarketDataException("risk_context","Select the shared financial context."),
    (connection,data,frame,ct)=>portfolioRisk.Catalog(connection,data,frame,body,ct)));
api.MapPost("/portfolio/risk",(HttpContext c,PortfolioRiskRequest body)=>PortfolioRead(c,
    body.Context??throw new MarketDataException("risk_context","Select the shared financial context."),
    (connection,data,frame,ct)=>portfolioRisk.Calculate(connection,data,frame,body,ct)));
api.MapPost("/portfolio/risk/jobs",(HttpContext c,SubmitPortfolioRiskJob body)=>PortfolioRead(c,
    body.Request?.Context??throw new MarketDataException("risk_job_context","Supply the risk request and exact retained context."),
    (connection,data,frame,ct)=>
    {
        c.Items["risk_job_attempted"]=true;
        return RiskJobs.Submit(connection,database.Manifest,portfolioRisk,data,frame,body,ct);
    }));
api.MapGet("/portfolio/risk/jobs",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),"RunScenario",async(connection,ct)=>Results.Ok(await RiskJobs.List(connection,database.Manifest.CatalogId,ct,PortfolioRiskPreview.Convention)),c.RequestAborted))));
api.MapGet("/portfolio/risk/jobs/{id:guid}",(HttpContext c,Guid id)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"RunScenario",async(connection,ct)=>Results.Ok(await RiskJobs.Read(connection,database.Manifest.CatalogId,id,ct,expectedConvention:PortfolioRiskPreview.Convention)),c.RequestAborted)));
api.MapPost("/portfolio/risk/jobs/{id:guid}/cancel",(HttpContext c,Guid id)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"RunScenario",async(connection,ct)=>Results.Ok(await RiskJobs.Read(connection,database.Manifest.CatalogId,id,ct,cancel:true,expectedConvention:PortfolioRiskPreview.Convention)),c.RequestAborted)));
api.MapPost("/futures/risk/catalog",(HttpContext c,FuturesRiskCatalogRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("risk_context","Select the shared financial context."),(connection,data,frame,ct)=>futuresRisk.Catalog(connection,data,frame,body,ct),scenario:true));
api.MapPost("/futures/risk",(HttpContext c,FuturesRiskRequest body)=>FuturesRead(c,
    body.Context??throw new MarketDataException("risk_context","Select the shared financial context."),
    (connection,data,frame,ct)=>futuresRisk.Calculate(connection,data,frame,body,ct),scenario:true));
api.MapPost("/futures/risk/jobs",(HttpContext c,SubmitRiskJob body)=>FuturesRead(c,
    body.Request?.Context??throw new MarketDataException("risk_job_context","Supply the risk request and exact retained context."),
    (connection,data,frame,ct)=>
    {
        c.Items["risk_job_attempted"]=true;
        return RiskJobs.Submit(connection,database.Manifest,futuresRisk,data,frame,body,ct);
    },scenario:true));
api.MapGet("/futures/risk/jobs",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),"RunScenario",async(connection,ct)=>Results.Ok(await RiskJobs.List(connection,database.Manifest.CatalogId,ct)),c.RequestAborted))));
api.MapGet("/futures/risk/jobs/{id:guid}",(HttpContext c,Guid id)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"RunScenario",async(connection,ct)=>Results.Ok(await RiskJobs.Read(connection,database.Manifest.CatalogId,id,ct)),c.RequestAborted)));
api.MapPost("/futures/risk/jobs/{id:guid}/cancel",(HttpContext c,Guid id)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"RunScenario",async(connection,ct)=>Results.Ok(await RiskJobs.Read(connection,database.Manifest.CatalogId,id,ct,cancel:true)),c.RequestAborted)));
api.MapPost("/futures/book",(HttpContext c,FuturesBookRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before booking or amending.");
    c.Items["futures_attempted"]=true;
    return await database.WithIdentity(Profile(session),"BookTrade",async(connection,ct)=>
    {
        if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct))throw new PreviewDeniedException();
        return Results.Ok(await futuresStore.Book(connection,await database.Read(connection,ct),Frame(session,body.SnapshotId),body,ct));
    },c.RequestAborted);
}));
api.MapPost("/futures/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
    Results.Ok(await futuresStore.Status(connection,body.OperationId,ct)),c.RequestAborted)));
api.MapGet("/jobs",(HttpContext c,int? page,int? collectionPage)=>Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>Results.Ok(await CollectionJobs.Directory(connection,ct,page??0,collectionPage??0)),c.RequestAborted)));
api.MapPost("/jobs/collection/update",(HttpContext c,CollectionJobUpdate body)=>Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>{c.Items["automation_attempted"]=true;return Results.Ok(await CollectionJobs.Update(connection,body,ct));},c.RequestAborted)));
api.MapPost("/infrastructure/availability",(HttpContext c,NodeCommand body)=>Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>{c.Items["automation_attempted"]=true;return Results.Ok(await JobOperations.NodeAction(connection,body,ct));},c.RequestAborted)));
api.MapPost("/infrastructure/resolve",(HttpContext c,NodeResolution body)=>Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>{c.Items["automation_attempted"]=true;return Results.Ok(await JobOperations.ResolveNode(connection,body,ct));},c.RequestAborted)));
api.MapPost("/jobs/create",(HttpContext c,JobCreate body)=>Locked(c,session=>database.WithIdentity(Profile(session),"RunJobs",
    async(connection,ct)=>{c.Items["automation_attempted"]=true;return Results.Ok(await JobOperations.Create(connection,body,ct));},c.RequestAborted)));
api.MapPost("/jobs/control",(HttpContext c,JobControl body)=>Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>{c.Items["automation_attempted"]=true;return Results.Ok(await JobOperations.Control(connection,body,ct));},c.RequestAborted)));
api.MapPost("/jobs/update",(HttpContext c,JobUpdate body)=>Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>{c.Items["automation_attempted"]=true;return Results.Ok(await JobOperations.Update(connection,body,ct));},c.RequestAborted)));
api.MapGet("/access",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),null,async(connection,ct)=>
        Results.Ok(new {permissions=await CurrentPermissions(connection,ct)}),c.RequestAborted))));
// The principal kinds security.principals accepts; each maps to one login kind. Agents are created through agent administration.
(string Code,string Name,bool Creatable)[] IdentityKinds=[("human","Human",true),("service","Service",true),("agent","Agent",false)];
api.MapGet("/admin",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ManageAccess",async(connection,ct)=>
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.administration_directory()::text");
        var directory=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>((string)(await command.ExecuteScalarAsync(ct))!)!;
        directory["kinds"]=JsonSerializer.SerializeToElement(IdentityKinds.Select(k=>new {code=k.Code,name=k.Name,creatable=k.Creatable}));
        directory["canCreate"]=JsonSerializer.SerializeToElement(hosted is null);
        return Results.Ok(directory);
    },c.RequestAborted))));
async Task<IResult> AdminMutation(HttpContext c,object body,string action)=>await Locked(c,async session=>
{
    c.Items["administration_attempted"]=true;
    var result=await database.WithIdentity(Profile(session),"ManageAccess",async(connection,ct)=>
    {
        var request=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(body,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}))!.AsObject();request["action"]=action;
        await using var command=ScheduleStore.Command(connection,"SELECT security.apply_administration($1::jsonb)::text",request.ToJsonString());
        return JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
    },c.RequestAborted,admission:Admission.Administration);
    if(result.GetProperty("kind").GetString()=="human")session.CustomProfiles[result.GetProperty("login").GetString()!]=result.GetProperty("name").GetString()!;
    return Results.Ok(result);
});
api.MapPost("/admin/create",(HttpContext c,AdminCreate body)=>hosted is null?AdminMutation(c,body,"create"):Task.FromResult<IResult>(Results.Json(new{code="access_denied",message="Hosted accounts require a private invitation."},statusCode:403)));
api.MapPost("/admin/update",(HttpContext c,AdminUpdate body)=>AdminMutation(c,body,"update"));
// Settings › AI administers the page assistant, which exists only in the local preview (P37).
if(hosted is null)
{
    var (modelProviders,modelCatalog)=(new ModelProviders(CredentialCustody.FromEnvironment(),database.Manifest,checkEndpoint),new ModelCatalog());
    Task<IResult> ModelRead(HttpContext c,Func<NpgsqlConnection,CancellationToken,Task<object>> read)=>Locked(c,session=>
        database.WithIdentity(Profile(session),"ManageModelProviders",async(connection,ct)=>Results.Ok(await read(connection,ct)),c.RequestAborted));
    Task<IResult> ModelCommand(HttpContext c,Func<NpgsqlConnection,CancellationToken,Task<JsonElement>> command)=>Locked(c,session=>
    {
        c.Items["model_admin_attempted"]=true;
        return database.WithIdentity(Profile(session),"ManageModelProviders",async(connection,ct)=>Results.Ok(await command(connection,ct)),c.RequestAborted);
    });
    api.MapGet("/settings/ai/providers",(Func<HttpContext,Task<IResult>>)(c=>ModelRead(c,modelProviders.Directory)));
    api.MapGet("/settings/ai/models",(Func<HttpContext,Task<IResult>>)(c=>ModelRead(c,async(connection,ct)=>await modelCatalog.Directory(connection,ct))));
    api.MapPost("/settings/ai/providers/key",(HttpContext c,ReplaceKeyRequest body)=>ModelCommand(c,(connection,ct)=>modelProviders.ReplaceKey(connection,body,ct)));
    api.MapPost("/settings/ai/providers/terms",(HttpContext c,RecordTermsRequest body)=>ModelCommand(c,(connection,ct)=>modelProviders.RecordTerms(connection,body,ct)));
    api.MapPost("/settings/ai/providers/attest",(HttpContext c,ProviderRequest body)=>ModelCommand(c,(connection,ct)=>modelProviders.Attest(connection,body,ct)));
    api.MapPost("/settings/ai/providers/enable",(HttpContext c,ProviderRequest body)=>ModelCommand(c,(connection,ct)=>modelProviders.Enable(connection,body,ct)));
    api.MapPost("/settings/ai/providers/disable",(HttpContext c,ProviderRequest body)=>ModelCommand(c,(connection,ct)=>modelProviders.Disable(connection,body,ct)));
    api.MapPost("/settings/ai/models/add",(HttpContext c,AddModelRequest body)=>ModelCommand(c,(connection,ct)=>modelCatalog.AddModel(connection,body,ct)));
    api.MapPost("/settings/ai/models/rename",(HttpContext c,RenameModelRequest body)=>ModelCommand(c,(connection,ct)=>modelCatalog.RenameModel(connection,body,ct)));
    api.MapPost("/settings/ai/models/price",(HttpContext c,AddPriceRequest body)=>ModelCommand(c,(connection,ct)=>modelCatalog.AddPrice(connection,body,ct)));
    api.MapPost("/settings/ai/models/withdraw",(HttpContext c,WithdrawPriceRequest body)=>ModelCommand(c,(connection,ct)=>modelCatalog.WithdrawPrice(connection,body,ct)));
    api.MapPost("/settings/ai/models/edit",(HttpContext c,EditModelRequest body)=>ModelCommand(c,(connection,ct)=>modelCatalog.EditModel(connection,body,ct)));
    api.MapPost("/settings/ai/models/enable",(HttpContext c,ModelCommandRequest body)=>ModelCommand(c,(connection,ct)=>modelCatalog.EnableModel(connection,body,ct)));
    api.MapPost("/settings/ai/models/disable",(HttpContext c,ModelCommandRequest body)=>ModelCommand(c,(connection,ct)=>modelCatalog.DisableModel(connection,body,ct)));
    // Agents (§5.4, §9). Create and Retire write control tables: the database refuses them with admission_required once
    // every check has passed under the shared admission, and they are repeated under exclusive admission, so a refusal
    // or a repeat never takes the epoch row exclusively.
    Task<IResult> AgentCommand(HttpContext c,Func<NpgsqlConnection,CancellationToken,Task<JsonElement>> command,bool control=false)=>Locked(c,async session=>
    {
        c.Items["model_admin_attempted"]=true;
        try{return Results.Ok(await database.WithIdentity(Profile(session),"ManageAgents",command,c.RequestAborted));}
        catch(PostgresException p) when(control&&p.SqlState=="P0001"&&p.MessageText=="admission_required")
        {return Results.Ok(await database.WithIdentity(Profile(session),"ManageAgents",command,c.RequestAborted,Admission.Agents));}
    });
    api.MapGet("/settings/ai/agents",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
        database.WithIdentity(Profile(session),"ManageAgents",async(connection,ct)=>Results.Ok(await AgentAdministration.Directory(connection,ct)),c.RequestAborted))));
    api.MapPost("/settings/ai/agents/create",(HttpContext c,CreateAgentRequest body)=>AgentCommand(c,(connection,ct)=>AgentAdministration.Create(connection,body,ct),control:true));
    api.MapPost("/settings/ai/agents/save",(HttpContext c,SaveAgentRequest body)=>AgentCommand(c,(connection,ct)=>AgentAdministration.Save(connection,body,ct)));
    api.MapPost("/settings/ai/agents/publish",(HttpContext c,AgentCommandRequest body)=>AgentCommand(c,(connection,ct)=>AgentAdministration.Publish(connection,body,ct)));
    api.MapPost("/settings/ai/agents/retire",(HttpContext c,AgentCommandRequest body)=>AgentCommand(c,(connection,ct)=>AgentAdministration.Retire(connection,body,ct),control:true));
    // Grants (§7.1, §8): the agents a user may use and the user's grants. Grant and Revoke write control tables, so like
    // Create and Retire they repeat under exclusive admission only once every check has passed; Revoke needs no UseAgents.
    Task<IResult> GrantCommand(HttpContext c,string? permission,Func<NpgsqlConnection,CancellationToken,Task<JsonElement>> command)=>Locked(c,async session=>
    {
        c.Items["grant_attempted"]=true;
        try{return Results.Ok(await database.WithIdentity(Profile(session),permission,command,c.RequestAborted));}
        catch(PostgresException p) when(p.SqlState=="P0001"&&p.MessageText=="admission_required")
        {return Results.Ok(await database.WithIdentity(Profile(session),permission,command,c.RequestAborted,Admission.Grants));}
    });
    api.MapGet("/assistant/agents",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
        database.WithIdentity(Profile(session),"UseAgents",async(connection,ct)=>Results.Ok(await AgentGrants.Usable(connection,ct)),c.RequestAborted))));
    api.MapGet("/assistant/grants",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
        database.WithIdentity(Profile(session),null,async(connection,ct)=>Results.Ok(await AgentGrants.Grants(connection,ct)),c.RequestAborted))));
    // A workspace's grants, read-only, for those who administer its access: the database also requires ManageIdentities
    // and refuses a delegated context. Revoke stays the delegator's.
    api.MapGet("/assistant/grants/workspace",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
        database.WithIdentity(Profile(session),"ManageAccess",async(connection,ct)=>Results.Ok(await AgentGrants.Workspace(connection,ct)),c.RequestAborted))));
    api.MapPost("/assistant/grants/grant",(HttpContext c,GrantRequest body)=>GrantCommand(c,"UseAgents",(connection,ct)=>AgentGrants.Grant(connection,body,ct)));
    api.MapPost("/assistant/grants/revoke",(HttpContext c,RevokeRequest body)=>GrantCommand(c,null,(connection,ct)=>AgentGrants.Revoke(connection,body,ct)));
    // Turns (§7.2, §7.4, §8). An ask pins the knowledge its page shows: current knowledge only while no historical context
    // is selected, a retained context only when it is the selected one, and a frame only of the session's current market
    // context. Working data is captured at its frame's date, so it needs one. Nothing else is pinned, and nothing falls back.
    var agentTurns=new AgentTurns(database,turnScope,agentWorker!,turnWorker!,new AgentTools(database,marketReads,scheduleReads,partyReads,tradeReads,app.Logger),app.Logger);
    (string Mode,Guid? Retained,InputFrame? Frame) Knowledge(PreviewSession session,AskKnowledge? knowledge)
    {
        var selected=session.MarketContext?.ContextId;
        var (mode,retained)=knowledge switch
        {
            {Mode:"current",ContextId:null} when selected is null=>("current",(Guid?)null),
            {Mode:"retained",ContextId:{} id} when id==selected=>("retained",id),
            _=>throw new MarketDataException("context_required","Ask over the knowledge the page shows: current knowledge, or the selected historical view.")
        };
        var frame=knowledge.FrameId is {} chosen?Frame(session,chosen):null;
        return mode=="current"&&frame is null?throw PinnedContext.Missing():(mode,retained,frame);
    }
    api.MapPost("/assistant/turns/ask",(HttpContext c,AskRequest body)=>Locked(c,session=>
    {
        var (mode,retained,frame)=Knowledge(session,body.Knowledge);
        c.Items["turn_attempted"]=true;
        return agentTurns.Ask(session,Profile(session),body,mode,retained,frame,c.RequestAborted);
    }));
    api.MapPost("/assistant/turns/stop",(HttpContext c,StopRequest body)=>Locked(c,session=>
        database.WithIdentity(Profile(session),null,async(connection,ct)=>Results.Ok(await agentTurns.Stop(connection,body,ct)),c.RequestAborted)));
    api.MapGet("/assistant/conversations",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
        database.WithIdentity(Profile(session),null,async(connection,ct)=>Results.Ok(await agentTurns.Conversations(connection,ct)),c.RequestAborted))));
    api.MapGet("/assistant/conversations/{id:guid}",(HttpContext c,Guid id,long after)=>Locked(c,session=>
        database.WithIdentity(Profile(session),null,async(connection,ct)=>Results.Ok(await agentTurns.Conversation(connection,id,after,ct)),c.RequestAborted)));
    // The live event stream (§8) takes no session semaphore, so the session's commands, Stop among them, never wait for it.
    var turnStream=new TurnStream(database,turnScope,agentWorker!,turnStreams!,
        app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions,
        TimeProvider.System,app.Lifetime.ApplicationStopping,app.Logger);
    api.MapGet("/assistant/conversations/{id:guid}/events",(HttpContext c,Guid id,long after)=>turnStream.Serve(c,()=>Session(c),id,after));
    api.MapGet("/assistant/turns/unfinished",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
        database.WithIdentity(Profile(session),null,async(connection,ct)=>Results.Ok(await agentTurns.Unfinished(connection,ct)),c.RequestAborted))));
    api.MapPost("/assistant/conversations/read",(HttpContext c,MarkReadRequest body)=>Locked(c,session=>
        database.WithIdentity(Profile(session),null,async(connection,ct)=>Results.Ok(await agentTurns.MarkRead(connection,body,ct)),c.RequestAborted)));
    // Usage (§9) is read under ReadModelUsage alone. An export records the report it carries in the model administration
    // audit, so like a change its outcome can be unknown, and repeating it answers as its original did.
    Task<IResult> UsageRead(HttpContext c,Func<NpgsqlConnection,CancellationToken,Task<object>> read)=>Locked(c,session=>
        database.WithIdentity(Profile(session),"ReadModelUsage",async(connection,ct)=>Results.Ok(await read(connection,ct)),c.RequestAborted));
    api.MapGet("/settings/ai/usage",(Func<HttpContext,Task<IResult>>)(c=>UsageRead(c,async(connection,ct)=>await UsageReports.Directory(connection,ct))));
    api.MapPost("/settings/ai/usage/report",(HttpContext c,UsageReportRequest body)=>UsageRead(c,async(connection,ct)=>await UsageReports.Report(connection,body,ct)));
    api.MapPost("/settings/ai/usage/export",(HttpContext c,UsageExportRequest body)=>
    {
        c.Items["model_admin_attempted"]=true;
        return UsageRead(c,async(connection,ct)=>await UsageReports.Export(connection,body,ct));
    });
    api.MapPost("/settings/ai/providers/test",(HttpContext c,TestRequest body)=>Locked(c,async session=>
    {
        c.Items["model_admin_attempted"]=true;
        return Results.Ok(await providerTests!.Run(database,Profile(session),body,c.RequestAborted,(CancellationToken)c.Items["client_aborted"]!));
    }));
}
}
var files=new PhysicalFileProvider(webRoot);
app.UseDefaultFiles(new DefaultFilesOptions {FileProvider=files});
app.UseStaticFiles(new StaticFileOptions {FileProvider=files});
app.Lifetime.ApplicationStarted.Register(()=>Console.WriteLine($"PREVIEW_READY http://127.0.0.1:{port}"));
await app.RunAsync();

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminCreate(Guid OperationId,string? Login,string? Name,string? Kind,string[]? Roles);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminUpdate(Guid OperationId,Guid Id,int ExpectedVersion,string? Name,bool? Enabled,string[]? Roles);
public sealed record ProfileRequest(string Profile);
// A request whose session is missing, unknown or past its end.
public sealed class PreviewSessionEnded : Exception;
public sealed record LocalTimeRequest(string? LocalTime,string? TimeZone);
public sealed record BuildRequest(Guid SnapshotId,string Curve,bool IncludeSources=false,bool IncludeWorkspace=false);
public sealed record HistoryRequest(Guid SnapshotId,string Curve,Guid? ContractId=null,Guid? SeriesId=null);
public sealed record CorrectionRequest(Guid SnapshotId,Guid ObservationId,string Value);
public sealed record SelectionRequest(Guid SnapshotId,Guid ObservationId);
public sealed partial class PreviewSession
{
    public static readonly TimeSpan Lifetime=TimeSpan.FromMinutes(30);
    public DateTimeOffset Created {get;}=DateTimeOffset.UtcNow;
    public DateTimeOffset Ends=>Created+Lifetime;
    // Names the session to the database, which binds its turns to it (docs/ai-assistant.md §7.2); random, so it reveals
    // nothing of the session's cookie.
    public string Digest {get;}=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public SemaphoreSlim Gate {get;}=new(1,1);
    public string? Profile {get;set;}
    public ScheduleContext? MarketContext {get;set;}
    public Dictionary<string,string> CustomProfiles {get;}=[];
    public Dictionary<Guid,InputFrame> Frames {get;}=[];
    public PreviewShortcutLibrary Shortcuts {get;}=new();
    public DateWorkspace Dates {get;}=new();
    public Guid[] InitialFrames {get;}
    public PreviewSession(PreviewManifest manifest)
    {
        foreach(var f in manifest.Frames) {
            if(f.CurveContracts is null)throw new MarketDataException("missing_curve_view","Every seed frame requires explicit curve contract selections.");
            var frame=new InputFrame(Guid.NewGuid(),"Initial synthetic inputs",f.At,f.ObservationIds.ToImmutableArray(),
                f.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray()));Frames.Add(frame.Id,frame);
        }
        InitialFrames=Frames.Keys.ToArray();
    }
}
