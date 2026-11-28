using OpenEtrm.Shared.Temporal;
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

if(args.Length>0&&args[0]=="--import-reference"){Environment.ExitCode=await ReferenceImportCommand.Run(args[1..]);return;}
if(args.Length>0&&args[0]=="--admin-load-gas-in-term"){Environment.ExitCode=await GasInTermLoad.Run(args[1..]);return;}
if(args.Length>0&&args[0]=="--capture-reference"){Environment.ExitCode=await ReferenceCaptureCommand.Run(args[1..]);return;}

if(args is ["--compile-date-seed",var dateSeedInput,var dateSeedOutput]){DateSeedCommand.Run(dateSeedInput,dateSeedOutput);return;}

if(args is ["--render-clock-captures",var captureInput,var captureOutput]){ClockCapturePublication.Run(captureInput,captureOutput);return;}
if(args is ["--render-power-days",var calendarInput,var calendarOutput]){PowerDayCalendar.Run(calendarInput,calendarOutput);return;}

if(args.Length>0 && args[0]=="--collect-power"){Environment.ExitCode=await PowerCollection.Run(args[1..]);return;}
if(args.Length>0 && args[0]=="--risk-worker"){Environment.ExitCode=await RiskWorker.Run(args[1..]);return;}

if(args.Length!=1 || args[0] is not ("--local-preview" or "--hosted-pilot")) throw new InvalidOperationException("Choose exactly --local-preview or --hosted-pilot.");
var hosted=args[0]=="--hosted-pilot"?HostedConfiguration.Read(Environment.GetEnvironmentVariable("OPENETRM_HOSTED_CONFIG")??throw new InvalidOperationException("Explicit private hosted configuration is required.")):null;
var root=Path.GetFullPath(hosted?.ManifestDirectory ?? Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT") ?? throw new InvalidOperationException("Use tools/run_preview.py."));
var runId=hosted?.InstallationId ?? Guid.Parse(File.ReadAllText(Path.Combine(root,".openetrm-preview")));
var port=hosted?.ListenPort ?? int.Parse(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_PORT") ?? throw new InvalidOperationException("Explicit preview port is required."));
var webRoot=Path.GetFullPath(hosted?.WebDirectory ?? Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_WEB") ?? throw new InvalidOperationException("Explicit built web root is required."));
var processLimits=hosted?.Limits??ProcessLimits.Read(Path.Combine(root,"process-limits.json"));
Console.WriteLine("PROCESS_LIMITS "+processLimits.Digest);
await using var hostedAuthentication=hosted is null?null:new HostedAuthentication(hosted);
var builder=WebApplication.CreateBuilder(args);
builder.Services.AddHttpContextAccessor();
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(o=>o.SingleLine=true); builder.Logging.SetMinimumLevel(LogLevel.Warning);
var bodyLimit=processLimits.RequestBytes;
builder.WebHost.ConfigureKestrel(o=> { o.Listen(hosted is null?IPAddress.Loopback:IPAddress.Any,port); o.Limits.MaxRequestBodySize=bodyLimit; });
if(hosted is null)ApplicationDataProtection.ConfigurePreview(builder.Services.AddDataProtection(),root);
else {HostedAuthentication.Configure(builder,hosted);builder.Services.AddSingleton(hostedAuthentication!);}
builder.Services.AddSingleton<IAntiforgeryAdditionalDataProvider,PreviewAntiforgeryScope>();
builder.Services.AddAntiforgery(o=> { o.HeaderName="X-Preview-CSRF"; o.Cookie.Name=hosted is null?"openetrm_preview_csrf":"__Host-openetrm_csrf"; o.Cookie.HttpOnly=true; o.Cookie.SameSite=SameSiteMode.Strict; o.Cookie.SecurePolicy=hosted is null?CookieSecurePolicy.None:CookieSecurePolicy.Always; });
// A member repeated in any letter case is ambiguous, so it never binds.
builder.Services.ConfigureHttpJsonOptions(o=>{o.SerializerOptions.PropertyNamingPolicy=JsonNamingPolicy.CamelCase;o.SerializerOptions.AllowDuplicateProperties=false;
    o.SerializerOptions.Converters.Add(new ExactDecimalJsonConverter());});
// In every environment, binding refuses what it cannot read with a bare status, which the API then names.
builder.Services.Configure<RouteHandlerOptions>(o=>o.ThrowOnBadRequest=false);
builder.Services.AddResponseCompression(o=>{o.EnableForHttps=true;o.Providers.Add<GzipCompressionProvider>();o.MimeTypes=["application/json","text/event-stream"];});
builder.Services.Configure<GzipCompressionProviderOptions>(o=>o.Level=CompressionLevel.Fastest);
var app=builder.Build();
var httpContext=hosted is null?null:app.Services.GetRequiredService<IHttpContextAccessor>();
await using var database=new PreviewDatabase(root,hosted:hosted,hostedSession:hosted is null?null:()=>HostedAuthentication.SessionDigest(httpContext!.HttpContext??throw new PreviewDeniedException()));
if(hosted is not null)await database.ValidateHostedBoundary(CancellationToken.None);
if(hostedAuthentication is not null){hostedAuthentication.Tenant=database.Manifest.TenantId;hostedAuthentication.Workspace=database.Manifest.WorkspaceId;app.UseForwardedHeaders();}
// The page assistant's worker and provider client (docs/ai-assistant.md §7.4, §11). The worker starts its generation
// before serving, closing whatever an earlier one left open. Only owned checks set the provider check endpoint (§5.2).
var checkEndpoint=ModelTransport.CheckEndpoint(Environment.GetEnvironmentVariable("OPENETRM_PROVIDER_CHECK_ENDPOINT"));
var (workerHolder,workerWrites)=PreviewDatabase.WorkerSources(root,hosted);
await using var agentWorker=new AgentWorker(workerHolder,workerWrites,app.Logger);
// Provider keys are sealed under the installation's key files when hosted, else the preview runner's (§5.2, §11).
var custody=hosted is null?CredentialCustody.FromEnvironment():new CredentialCustody(hosted.CredentialKeyFile,hosted.CredentialRetryKeyFile);
var adapter=new ModelAdapters(ModelTransport.Handler(checkEndpoint,TimeSpan.FromSeconds(processLimits.ProviderConnectSeconds)),new(TimeSpan.FromSeconds(processLimits.ProviderIdleSeconds),processLimits.ProviderResponseBytes));
var providerTests=new ProviderTests(custody,agentWorker,adapter,app.Logger);
// The live event stream's subscriptions, which the worker tells of what it publishes (§8).
var turnStreams=new TurnStreams();
await using var turnWorker=new TurnWorker(agentWorker,custody,adapter,turnStreams,app.Logger);
await agentWorker.Generation(CancellationToken.None);
// Signing out ends the session's streams at once; revoking its sign-in session has cancelled its turns (§11).
var pageRegistries=new List<PageSessionRegistry>();
if(hostedAuthentication is not null)hostedAuthentication.SignedOut=(digest,subject)=>
{
    turnStreams.EndSession(digest,subject);
    foreach(var registry in pageRegistries)registry.Retire(digest);
};

var profiles=new[] { new {id="viewer",name="Viewer",description="Read only"},new {id="editor",name="Market Data Editor",description="Read and correct synthetic prices"},new {id="approver",name="Market Data Approver",description="Read and publish schedule definitions"},new {id="admin",name="Access Administrator",description="Manage workspace identities and roles"},new {id="trader",name="Trader",description="Book and inspect synthetic futures holdings"},new {id="risk",name="Risk Analyst",description="Inspect holdings and run shared market scenarios"},new {id="operator",name="Operator",description="Record and set up REC inventory, facility quantities and gas operations"} };

const string malformed="The request is incomplete or malformed.";
// The HTTP status, code and message an API failure is reported with: as an error body, or as the error frame of a started Blotter stream.
(int Status,string Code,string Message) Failure(Exception e,PathString apiPath,HttpContext context)=>e switch
{
    MarketDataException m when m.Code=="desk_profile_changed"=>(409,m.Code,m.Message),
    PostgresException p when p.MessageText.StartsWith("desk_",StringComparison.Ordinal)
        =>(p.SqlState=="42501"?403:p.MessageText is "desk_stale" or "desk_request_conflict"?409:422,p.MessageText,BilateralDesk.Error(p.MessageText)),
    PostgresException p when apiPath.StartsWithSegments("/api/desk") && (p.SqlState.StartsWith("22",StringComparison.Ordinal)||p.SqlState.StartsWith("23",StringComparison.Ordinal))
        =>(422,"desk_terms",BilateralDesk.Error("desk_terms")),
    AsyncReadException refusal=>(refusal.Status,refusal.Code,refusal.Message),
    MarketDataException m when m.Code is "rec_request_conflict" or "rec_generation_conflict" or "rec_trade_conflict" or "rec_forward_request_conflict" or "rec_forward_revision_conflict"
        => (409,m.Code,m.Message),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "rec_request_conflict" or "rec_generation_conflict" or "rec_trade_conflict" or "rec_forward_request_conflict" or "rec_forward_revision_conflict"
        => (409,p.MessageText,"Recover the original certificate command or reload its exact retained revision before recording another fact."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("rec_",StringComparison.Ordinal)
        => (422,p.MessageText,"Supply exact certificate references, native serial ranges, eligible evidence and explicit future delivery terms."),
    PostgresException p when p.MessageText.StartsWith("gas_storage_",StringComparison.Ordinal)
        =>(p.SqlState=="42501"?403:p.MessageText.Contains("conflict",StringComparison.Ordinal)||p.MessageText.Contains("stale",StringComparison.Ordinal)?409:422,
            p.MessageText,GasStorageStore.Refusal(p.MessageText)),
    TicketFieldException {Code:"swing_payload"} => (413,"swing_payload","The election command exceeds 4 MiB."),
    PostgresException p when p.MessageText=="swing_booking_frame" => (422,p.MessageText,"Select the owned booking frame and its exact valuation instant."),
    PostgresException p when p.MessageText=="swing_election_conflict" => (409,p.MessageText,"Reload the trade and the current revision for every swing day."),
    PostgresException p when p.MessageText=="swing_feasibility" => (422,p.MessageText,"The elections leave no feasible completion of the term quantity."),
    PostgresException p when p.MessageText=="swing_election" => (422,p.MessageText,"Select retained swing days, admissible quantities or withdrawals, the contractual holder and timely acceptance evidence."),
    PostgresException p when p.MessageText=="exercise_election_limit" => (422,p.MessageText,"This trade has reached the command, fact or group revision limit."),
    PostgresException p when p.MessageText=="exercise_election_conflict" => (409,p.MessageText,"Reload the trade and its current election revisions."),
    PostgresException p when p.MessageText=="exercise_election_denied" => (403,"access_denied","This account cannot access the selected trade and exercise facts."),
    PostgresException p when p.MessageText=="exercise_election" => (422,p.MessageText,"Select a retained physical group, its outcome or withdrawal, evidence and an effective UTC instant by exercise."),
    MarketDataException m when m.Code=="curve_mark_request_conflict" => (409,m.Code,m.Message),
    MarketDataException m when m.Code=="curve_mark_receipt" => (503,m.Code,m.Message),
    PostgresException p when p.MessageText.StartsWith("curve_mark_",StringComparison.Ordinal)
        =>(p.SqlState=="42501"?403:p.MessageText is "curve_mark_stale" or "curve_mark_request_conflict"?409:422,
            p.MessageText,p.MessageText switch {
                "curve_mark_owner_changed"=>"Return to the workspace and account that submitted the publication.",
                "curve_mark_denied"=>"Current market authority no longer permits this operation for the exact source.",
                "curve_mark_context"=>"Select the certified business date, instant and knowledge cutoff for this source.",
                "curve_mark_bounds"=>"Choose a page, month range and number of cells within the editor limits.",
                "curve_mark_source"=>"Select visible outright curves under their exact definition revision.",
                "curve_mark_pair"=>"Select distinct curves with a common reference revision and quote currency.",
                "curve_mark_time"=>"A revision cannot take effect before the mark it replaces.",
                "curve_mark_request_conflict"=>"This request identity already recorded another command.",
                "curve_mark_stale"=>"Reload the current mark revision before recording a different value.",
                _=>"Supply exact delivery months, volatilities from 0 to 10 and correlations from -1 to 1."}),
    PostgresException p when p.MessageText.StartsWith("heat_rate_call_",StringComparison.Ordinal)||p.MessageText.StartsWith("spread_mark_",StringComparison.Ordinal)
        =>(p.SqlState=="42501"?403:p.MessageText.Contains("conflict",StringComparison.Ordinal)||p.MessageText.Contains("stale",StringComparison.Ordinal)?409:422,
            p.MessageText,HeatRateCallStore.Refusal(p.MessageText)),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "power_facility_request_conflict" or "power_facility_revision_conflict"
        => (409,p.MessageText,"This facility or request changed. Reload its exact version or recover the original save outcome."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("power_facility_",StringComparison.Ordinal)
        => (422,p.MessageText,"Choose an available exact facility version and compatible operating parameters under the selected context."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "operating_request_conflict" or "operating_right_overlap"
        => (409,p.MessageText,"Recover the original command or select an uncommitted complete operating-right horizon."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="operating_booking_clock"
        => (422,p.MessageText,"Booking operating plans on non-UTC clocks is Coming soon. This profile requires an explicitly selected UTC fixed-offset clock."),
    PostgresException p when p.SqlState=="P0001" && (p.MessageText.StartsWith("operating_booking_",StringComparison.Ordinal)||
        p.MessageText.StartsWith("operating_schedule",StringComparison.Ordinal)||p.MessageText.StartsWith("operating_battery_",StringComparison.Ordinal)||
        p.MessageText.StartsWith("operating_thermal_",StringComparison.Ordinal)||p.MessageText.StartsWith("operating_solar_",StringComparison.Ordinal)||
        p.MessageText is "operating_right_quantity" or "operating_delivery_lifecycle" or "operating_facility_unavailable" or "operating_position_scope" or "operating_position_metadata" or "operating_quantity")
        => (422,p.MessageText,"Supply exact available operating references, a feasible future plan, explicit prices and complete retained Side memberships."),
    PostgresException p when p.MessageText=="point_trader_mark_denied"=>(403,p.MessageText,"The original market publication authority is unavailable."),
    PostgresException p when p.SqlState=="P0001"&&p.MessageText.StartsWith("point_trader_mark_",StringComparison.Ordinal)
        =>(p.MessageText is "point_trader_mark_stale" or "point_trader_mark_conflict"?409:422,p.MessageText,
            "Retain the exact market point, explicit policy and original request. Refresh changed sources before reviewing a new publication."),
    MarketDataException m when m.Code is "point_trader_mark_owner_changed" or "point_trader_mark_conflict" or "point_trader_mark_stale"=>(409,m.Code,m.Message),
    Exception when context.Items.ContainsKey("curve_mark_record_attempted")&&e is (NpgsqlException and not PostgresException) or OperationCanceledException
        =>(503,"curve_mark_outcome_unknown","Retain the publication request and check its status to reconcile the outcome."),
    Exception when context.Items.ContainsKey("point_trader_mark_attempted")&&e is NpgsqlException or OperationCanceledException
        =>(503,"point_trader_mark_outcome_unknown","The publication outcome is unknown. Retain the original request and check its status."),
            ToolRefusal refusal=>(422,refusal.Code,AgentTools.Text(refusal.Code)),
    PostgresException p when p.SqlState=="P0001"&&p.MessageText=="catalogue_choice_changed"
        => (409,p.MessageText,"The offered product definition changed. Reload the available trade terms before booking."),
    PostgresException p when p.SqlState=="P0001"&&p.MessageText is "catalogue_context" or "catalogue_missing" or "catalogue_source" or "catalogue_publication" or "catalogue_conflict"
        => (p.MessageText=="catalogue_conflict"?409:422,p.MessageText,CatalogueStore.Refusal(p.MessageText)),
    PostgresException p when p.SqlState=="P0001"&&p.MessageText=="temporal_inputs"
        => (422,p.MessageText,"The saved inputs do not match their business date, exact source versions or recorded close. Select a complete input snapshot."),
    PostgresException p when p.SqlState=="P0001"&&p.MessageText is "operating_policy_conflict" or "user_preferences_conflict" or "user_preferences_owner_changed"
        =>(409,p.MessageText,"These settings or this request changed. Reload the current version, or retry the original request."),
    PostgresException p when p.SqlState=="P0001"&&p.MessageText is "operating_policy_input" or "user_preferences_input"
        =>(422,p.MessageText,"Supply the complete supported settings within the stated bounds."),
    PostgresException p when p.SqlState=="P0001"&&p.MessageText=="operating_policy_missing"
        =>(409,p.MessageText,"The workspace operating policy must be explicitly provisioned before this operation."),
    PostgresException p when p.SqlState=="P0001"&&p.MessageText=="page_prompt_required"=>(409,p.MessageText,"Configure the provider-test question in Settings → AI → Prompts before running a test."),
    PostgresException p when p.SqlState=="P0001"&&p.MessageText.StartsWith("page_prompt_",StringComparison.Ordinal)=>(409,p.MessageText,"The prompt or request changed. Reload its current version or retry the same request."),
    PostgresException p when p.SqlState=="P0001"&&p.MessageText=="proposal_not_ready"=>(409,p.MessageText,"Wait for the assistant answer to complete before reviewing this proposal."),
    // A missing or ended session is session_ended on the assistant's routes, which offer a reload; pages keep access_denied (§8).
    PreviewSessionEnded when apiPath.StartsWithSegments("/api/assistant") => (403,"session_ended",TurnStream.Refusal("session_ended")),
    PreviewDeniedException or PreviewSessionEnded => (403,"access_denied","The selected test identity cannot perform this operation."),
    MarketDataException m when m.Code is "gas_location_conflict" or "swap_conflict" or "ppa_conflict" or "pnl_save_conflict" => (409,m.Code,m.Message),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "cash_payment_conflict" or "cash_payment_revision"
        => (409,p.MessageText,"The payment revision or request changed. Reload the current allocation or retry the unchanged original request."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="cash_payment_input"
        => (422,p.MessageText,"Supply exact payment references, an explicit paid instant, signed amount and the supported cash policy."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="cash_payment_membership_time"
        => (422,p.MessageText,"The payment cannot precede the recorded cash obligation. Check the payment time and its allocation."),
    PostgresException p when p.MessageText.StartsWith("lifecycle_",StringComparison.Ordinal)
        => (p.SqlState=="42501"?403:p.MessageText.Contains("conflict",StringComparison.Ordinal)||p.MessageText.Contains("stale",StringComparison.Ordinal)?409:422,
            p.MessageText,LifecycleNative.Refusal(p.MessageText)),
    PostgresException p when p.MessageText.StartsWith("gas_park_loan_",StringComparison.Ordinal)||p.MessageText.StartsWith("gas_balance_fee_",StringComparison.Ordinal)
        =>(p.SqlState=="42501"?403:p.MessageText is "gas_park_loan_stale" or "gas_park_loan_context" or "gas_park_loan_conflict" or "gas_park_loan_policy" or "gas_balance_fee_context" or "gas_balance_fee_source" or "gas_balance_fee_stale" or "gas_balance_fee_conflict" or "gas_balance_fee_policy" or "gas_balance_fee_policy_changed"?409:422,
            p.MessageText,GasParkLoanActions.Refusal(p.MessageText)),
    PostgresException p when p.MessageText.StartsWith("gas_custody_",StringComparison.Ordinal)
        =>(p.SqlState=="42501"?403:p.MessageText is "gas_custody_stale" or "gas_custody_source" or "gas_custody_context" or "gas_custody_conflict" or "gas_custody_policy_changed" or "gas_custody_reconciliation"?409:422,
            p.MessageText,GasParkCustodyActions.Refusal(p.MessageText)),
    PostgresException p when p.MessageText.StartsWith("gas_transport_financial_",StringComparison.Ordinal)
        =>(p.SqlState=="42501"?403:p.MessageText.Contains("conflict",StringComparison.Ordinal)||p.MessageText.Contains("stale",StringComparison.Ordinal)?409:422,
            p.MessageText,GasTransportFinancialStore.Refusal(p.MessageText)),
    MarketDataException m when m.Code is "gas_custody_source" or "gas_custody_reconciliation" => (409,m.Code,m.Message),
    MarketDataException m when m.Code is "gas_park_loan_context" or "gas_park_loan_reconciliation" => (409,m.Code,m.Message),
    PostgresException p when p.MessageText.StartsWith("gas_quantity_",StringComparison.Ordinal)
        => (p.SqlState=="42501"?403:p.MessageText is "gas_quantity_revision" or "gas_quantity_stale_review" or "gas_quantity_conflict"?409:422,
            p.MessageText,"Review the current physical quantity, source allocations and principal version again."),
    MarketDataException m when m.Code is "gas_quantity_revision" or "gas_quantity_stale_review" => (409,m.Code,m.Message),
    PostgresException p when p.MessageText.StartsWith("gas_owned_",StringComparison.Ordinal)||p.MessageText.StartsWith("gas_stock_",StringComparison.Ordinal)||
        p.MessageText.StartsWith("gas_operator_",StringComparison.Ordinal)||p.MessageText.StartsWith("gas_loan_",StringComparison.Ordinal)
        =>(p.SqlState=="42501"?403:p.MessageText is "gas_owned_context" or "gas_owned_preview_stale" or "gas_owned_conflict" or "gas_owned_policy_changed" or
            "gas_owned_source_already_applied" or "gas_operator_reconciliation" or "gas_owned_reconciliation"?409:422,p.MessageText,GasOwnedOperations.Refusal(p.MessageText)),
    MarketDataException m when m.Code=="contract_materialization_conflict" => (409,m.Code,m.Message),
    PostgresException p when p.SqlState is "23503" or "23514" && (apiPath.StartsWithSegments("/api/date-schedules/contracts/materialize")||apiPath.StartsWithSegments("/api/curve-definitions/admit-contracts")) => (422,"contract_materialization","Select compatible exact reference terms and published date-list occurrences."),
    PostgresException p when apiPath.StartsWithSegments("/api/price-publications") && p.SqlState=="40001" && p.MessageText.StartsWith("publication_",StringComparison.Ordinal)
        => (409,p.MessageText,"The publication, schedule or request changed. Reload its current version or retry the unchanged original request."),
    PostgresException p when apiPath.StartsWithSegments("/api/price-publications") && p.SqlState=="22023" && p.MessageText.StartsWith("publication_",StringComparison.Ordinal)
        => (422,p.MessageText,"Check the exact publication references, covered periods and release evidence. The requested change was refused."),
    PostgresException p when apiPath.StartsWithSegments("/api/price-publications") && p.SqlState=="42501" && p.MessageText.StartsWith("publication_",StringComparison.Ordinal)
        => (403,p.MessageText,"This publication operation requires current authorization and an editable working context."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("side_schedule_",StringComparison.Ordinal) && p.MessageText.Contains("conflict",StringComparison.Ordinal)
        => (409,p.MessageText,"The request, retained content or trade revision changed. Reconcile the original operation or reload its current revision."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("side_schedule_",StringComparison.Ordinal)
        => (422,p.MessageText,"The complete retained schedule requires compatible exact references, typed operations and an immutable validated manifest."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "settlement_version_conflict" or "settlement_operation_conflict" => (409,p.MessageText,"Reconcile the original operation or reload the settlement version."),
    MarketDataException m when m.Code is "facility_quantity_conflict" or "facility_quantity_owner_changed" => (409,m.Code,m.Message),
    MarketDataException m when (m.Code.StartsWith("delivery_workbench_",StringComparison.Ordinal)||m.Code.StartsWith("delivery_nomination_batch_",StringComparison.Ordinal))&&
        (m.Code.EndsWith("stale",StringComparison.Ordinal)||m.Code.EndsWith("stale_preview",StringComparison.Ordinal)||m.Code.EndsWith("conflict",StringComparison.Ordinal)||m.Code.EndsWith("owner_changed",StringComparison.Ordinal)) => (409,m.Code,m.Message),
    PostgresException p when p.MessageText.StartsWith("delivery_workbench_",StringComparison.Ordinal)||p.MessageText.StartsWith("delivery_nomination_batch_",StringComparison.Ordinal)
        =>(p.SqlState=="42501"?403:p.MessageText.Contains("conflict",StringComparison.Ordinal)||p.MessageText.Contains("stale",StringComparison.Ordinal)?409:422,
            p.MessageText,"Review the exact delivery sources, allocation plan, native targets and current authority before recording the operation."),
    PostgresException p when apiPath.StartsWithSegments("/api/delivery/workbench")&&p.SqlState is "23503" or "23514" or "22P02" or "22003" or "22001" or "22008"
        =>(422,"delivery_workbench_input","Supply compatible exact delivery sources, target occurrences, quantities and evidence."),
    PostgresException p when apiPath.StartsWithSegments("/api/delivery/workbench")&&p.SqlState=="23505"
        =>(409,"delivery_workbench_conflict","The delivery request or source head changed. Reconcile the original command or review its current successor."),
    MarketDataException m when m.Code is "gas_transport_route_stale" or "gas_transport_route_conflict" or "gas_transport_route_owner_changed" or "gas_transport_route_context" or "gas_transport_route_source" => (409,m.Code,m.Message),
    PostgresException p when apiPath.StartsWithSegments("/api/gas/transport/routes")&&p.SqlState is "23503" or "23514" or "22P02" or "22003" or "22001" or "22008"
        =>(422,"gas_transport_route_input",GasTransportRouteStore.Refusal("gas_transport_route_input")),
    PostgresException p when apiPath.StartsWithSegments("/api/gas/transport/routes")&&p.SqlState=="23505"
        =>(409,"gas_transport_route_conflict",GasTransportRouteStore.Refusal("gas_transport_route_conflict")),
    PostgresException p when p.MessageText.StartsWith("gas_transport_route_",StringComparison.Ordinal)
        =>(p.SqlState=="42501"?403:p.MessageText is "gas_transport_route_stale" or "gas_transport_route_conflict" or "gas_transport_route_context" or "gas_transport_route_source"?409:422,
            p.MessageText,GasTransportRouteStore.Refusal(p.MessageText)),
    MarketDataException m when m.Code is "gas_transport_capacity_stale" or "gas_transport_capacity_conflict" or "gas_transport_capacity_owner_changed" or "gas_transport_capacity_context" or "gas_transport_capacity_source" => (409,m.Code,m.Message),
    PostgresException p when apiPath.StartsWithSegments("/api/gas/transport/capacity")&&p.SqlState is "23503" or "23514" or "22P02" or "22003" or "22001" or "22008"
        =>(422,"gas_transport_capacity_input",GasTransportCapacityStore.Refusal("gas_transport_capacity_input")),
    PostgresException p when apiPath.StartsWithSegments("/api/gas/transport/capacity")&&p.SqlState=="23505"
        =>(409,"gas_transport_capacity_conflict",GasTransportCapacityStore.Refusal("gas_transport_capacity_conflict")),
    PostgresException p when p.MessageText.StartsWith("gas_transport_capacity_",StringComparison.Ordinal)
        =>(p.SqlState=="42501"?403:p.MessageText is "gas_transport_capacity_stale" or "gas_transport_capacity_conflict" or "gas_transport_capacity_context" or "gas_transport_capacity_source"?409:422,
            p.MessageText,GasTransportCapacityStore.Refusal(p.MessageText)),
    PostgresException p when apiPath.StartsWithSegments("/api/facilities/operations")&&p.SqlState is "23503" or "23514" or "22P02" or "22003" or "22001" or "22008"
        =>(422,"storage_facility_input","Select compatible exact physical references, native quantities, intervals and evidence within the installed profile."),
    PostgresException p when apiPath.StartsWithSegments("/api/facilities/operations")&&p.SqlState=="23505"
        =>(409,"storage_facility_conflict",StorageFacilityStore.Refusal("storage_facility_conflict")),
    PostgresException p when p.MessageText.StartsWith("storage_facility_",StringComparison.Ordinal)
        => (p.SqlState=="42501"?403:p.MessageText is "storage_facility_stale" or "storage_facility_conflict"?409:422,p.MessageText,StorageFacilityStore.Refusal(p.MessageText)),
    PostgresException p when p.MessageText.StartsWith("facility_quantity_",StringComparison.Ordinal)
        => (p.SqlState=="42501"?403:p.MessageText is "facility_quantity_conflict" or "facility_quantity_owner_changed"?409:422,p.MessageText,FacilityQuantityStore.Refusal(p.MessageText)),
    PostgresException p when p.MessageText=="crude_mapped_lifecycle" => (422,p.MessageText,"Delivery-to-cash operations for separately maintained crude delivery masters are not yet qualified. Contractual booking and pre-delivery valuation remain available."),
    PostgresException p when p.MessageText=="crude_setup_market_scope" => (422,p.MessageText,"Current crude bindings must use one original pricing revision. Multiple grades and curves within it are supported; saved trades keep earlier revisions."),
    PostgresException p when p.MessageText=="crude_setup_conflict" => (409,p.MessageText,"The setup head or request changed. Reload or retry the original request."),
    PostgresException p when p.MessageText=="crude_price_mapping_missing" => (422,p.MessageText,"No pricing mapping is maintained for the selected physical parameters."),
    PostgresException p when p.MessageText=="crude_price_mapping_ambiguous" => (422,p.MessageText,"More than one pricing mapping applies to the physical parameters and delivery interval. Correct the maintained mapping."),
    PostgresException p when p.MessageText=="crude_price_mapping_coverage" => (422,p.MessageText,"One pricing mapping must cover the complete delivery interval."),
    PostgresException p when p.MessageText=="crude_delivery_relationship_missing" => (422,p.MessageText,"The selected grade, facility and custody point do not have a maintained physical delivery relationship."),
    PostgresException p when p.MessageText=="crude_reference_input" => (422,p.MessageText,"Choose compatible physical parameters from the exact delivery reference tables."),
    PostgresException p when p.MessageText.StartsWith("crude_setup_",StringComparison.Ordinal) => (422,p.MessageText,"Choose compatible exact delivery references and an admitted monthly index source."),
    MarketDataException m when m.Code is "crude_conflict" or "crude_invoice_preview" or "crude_setup_conflict" => (409,m.Code,m.Message),
    MarketDataException m when m.Code is "settlement_stale_preview" or "settlement_version_conflict" => (409,m.Code,m.Message),
    PostgresException p when p.SqlState=="P0001" && (p.MessageText.StartsWith("settlement_",StringComparison.Ordinal) || p.MessageText.StartsWith("meter_finality_",StringComparison.Ordinal)) => (422,p.MessageText,"Settlement requires compatible, final, certified evidence and explicit policies."),
    CloseRiskInputsException c => (422,CloseRiskInputsException.Code,c.Message),
    RiskRunTieException t => (409,RiskRunTieException.Code,t.Message),
    AutomationException a => (422,a.Code,a.Message),
    OpenEtrm.Shared.Calculation.CalculationException g => (422,g.Code,g.Message),
    OpenEtrm.Shared.Analytics.OverlayException o => (422,o.Code,o.Message),
    PostgresException p when p.SqlState=="P0001" && (p.MessageText.StartsWith("power_prefix_",StringComparison.Ordinal)||p.MessageText=="power_location_conflict")
        => (422,p.MessageText,"The activated power output must preserve its declared actual-only history and exact location coverage."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("power_price_shape_",StringComparison.Ordinal)
        =>(422,p.MessageText,"Select a compatible exact hourly shape series and the supported daily-native calibration policy."),
    PostgresException p when p.MessageText=="curve_activation_conflict" => (409,p.MessageText,"The activation timeline or request changed. Reload or retry the exact original request."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("curve_activation_",StringComparison.Ordinal) => (422,p.MessageText,"Supply disjoint effective intervals and exact published outputs under the complete timeline policy."),
    PostgresException p when p.MessageText is "job_conflict" => (409,p.MessageText,"The job or request changed. Refresh or reconcile the original request."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="close_conflict" => (409,p.MessageText,"This close session, grant or request changed. Reload it or retry the exact original request."),
    PostgresException p when p.SqlState=="42501" && p.MessageText is "close_denied" or "close_grant" => (403,p.MessageText=="close_grant"?"close_grant":"access_denied",
        p.MessageText=="close_grant"?"Recording this session's close needs a grant for the session.":"The selected test identity cannot perform this operation."),
    // A close refusal may name what it found after its code: "close_incomplete: missing curves future".
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("close_",StringComparison.Ordinal) && p.MessageText.Split(':',2) is var parts
        => (422,parts[0],CloseSessions.Refusal(parts[0])+(parts.Length>1?" ("+parts[1].Trim()+")":"")),
    PostgresException p when p.MessageText is "pilot_close_conflict" => (409,p.MessageText,"This close request already has different retained inputs. Reconcile the original request."),
    PostgresException p when p.SqlState is "23503" or "23514" && apiPath.StartsWithSegments("/api/jobs/pilot-close") => (422,"pilot_profile","Choose supported exact books, curves, sources, policies and schedule terms."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="pilot_close_source_scope" => (422,p.MessageText,"This generated close requires an anchor and session curves from one exact definition."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("pilot_",StringComparison.Ordinal) => (422,p.MessageText,"Choose complete explicit pilot close inputs, supported policies and an available exact anchor."),
    PostgresException p when p.MessageText is "infrastructure_busy" => (409,p.MessageText,"Wait for active workloads or pending maintenance to finish before requesting node maintenance."),
    PostgresException p when p.MessageText is "infrastructure_conflict" => (409,p.MessageText,"The node changed. Refresh infrastructure before requesting another action."),
    PostgresException p when p.SqlState=="23505" && apiPath.StartsWithSegments("/api/infrastructure") => (409,"infrastructure_busy","Cluster maintenance is already queued, running or awaiting reconciliation."),
    PostgresException p when p.MessageText is "infrastructure_input" => (422,p.MessageText,"Supply a supported infrastructure operation and exact target version."),
    PostgresException p when p.SqlState is "23502" or "23514" or "22P02" or "22007" or "22008" or "22003" && apiPath.StartsWithSegments("/api/jobs") => (422,"job_input","The job contract has missing, malformed or unsupported fields."),
    PostgresException p when p.MessageText is "job_input" or "job_transition" or "job_limit" => (422,p.MessageText,"Supply a supported job contract or an available action within its limits."),
    AntiforgeryValidationException => (400,"request_token","Reload the preview to obtain a valid request token."),
    MarketDataException m when m.Code is "shortcut_conflict" or "tou_conflict" or "table_view_conflict" or "dashboard_layout_conflict" or "dashboard_request_conflict" or "risk_definition_conflict" or "risk_definition_request_conflict" or "futures_revision_conflict" or "swap_revision_conflict" or "custom_curve_conflict" or "contract_date_conflict" => (409,m.Code,m.Message),
    PostgresException p when p is {SqlState:"P0001",MessageText:"contract_date_conflict"} or {SqlState:"23505",ConstraintName:"contract_date_versions_pkey" or "one_contract_date_draft"} => (409,"contract_date_conflict","The selected date definition changed. Reload before editing."),
    PostgresException p when p is {SqlState:"P0001",MessageText:"custom_curve_conflict"} or {SqlState:"23505",ConstraintName:"custom_curve_versions_pkey" or "one_custom_curve_draft"} => (409,"custom_curve_conflict","The selected curve changed. Reload before editing."),
    PostgresException p when p.SqlState=="42501" && p.MessageText=="gas_operations_denied" => (403,"access_denied","Your roles do not permit this gas operations read or change for this delivery."),
    PostgresException p when p.MessageText.StartsWith("gas_operations_") => (p.MessageText.Contains("conflict")||p.MessageText.Contains("stale")?409:422,p.MessageText,"The manual operation requires compatible current service terms, explicit evidence and the reviewed source heads. Refresh and review the operation."),
    PostgresException p when apiPath.StartsWithSegments("/api/gas/operations") && p.SqlState is "23505" or "23514" or "23503" or "22P02" or "22003" or "22007" or "22008" or "P0002" => (422,"gas_operations_input","Supply the complete explicit manual operation with exact sources, canonical dates and evidence."),
    PostgresException p when p.SqlState is "23505" or "40001" && apiPath.StartsWithSegments("/api/curve-definitions") => (409,"curve_definition_conflict","This curve definition or its mapping changed. Reload or retry the exact original command."),
    MarketDataException m when m.Code is "curve_definition_conflict" or "rate_admission_conflict" or "contract_curve_admission_conflict" or "contract_curve_admission_name" => (409,m.Code,m.Message),
    PostgresException p when p.SqlState is "23505" or "40001" && apiPath.StartsWithSegments("/api/custom-curves") => (409,"custom_curve_conflict","This curve or request changed. Reload or reconcile the original request."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="party_conflict" => (409,p.MessageText,"This party or request changed. Reload the latest revision or retry the original request."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "party_input" or "party_limit" => (422,p.MessageText,"Supply a stable code, name and an effective time at or after the preceding version. Party codes cannot change."),
    MarketDataException m when m.Code=="reference_conflict" => (409,m.Code,m.Message),
    MarketDataException m when m.Code=="reference_item" => (404,m.Code,m.Message),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="reference_conflict" => (409,p.MessageText,"This reference version or display selection changed. Reload before editing."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "reference_edit" or "reference_limit" or "reference_economic_change" => (422,p.MessageText,"Supply supported display fields within the authoring limits. Financial terms must remain identical before display activation."),
    PostgresException p when p.SqlState is "23505" or "40001" && apiPath.StartsWithSegments("/api/tou-products") => (409,"tou_conflict","This TOU product changed or its code already exists. Reload before editing."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="ppa_conflict" => (409,p.MessageText,"The trade or operation changed. Reconcile its original request or current head."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("ppa_",StringComparison.Ordinal) => (422,p.MessageText,"The PPA operation does not meet its explicit terms, pins, coverage or limits."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="lng_conflict" => (409,p.MessageText,"This LNG operation already belongs to different terms or ownership."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("lng_",StringComparison.Ordinal) => (422,p.MessageText,"The LNG operation requires compatible exact physical, pricing, quantity and Incoterms terms. Preview the complete current terms again."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="crude_conflict" => (409,p.MessageText,"This crude operation already belongs to different terms or ownership."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("crude_",StringComparison.Ordinal) => (422,p.MessageText,"The crude operation requires complete compatible terms and retained schedules."),
    MarketDataException m when m.Code=="gas_revision_conflict" => (409,m.Code,m.Message),
    PostgresException p when p.SqlState=="P0001" && GasAmendments.Message(p.MessageText) is {} message => (p.MessageText=="gas_revision_conflict"?409:422,p.MessageText,message),
    PostgresException p when p.MessageText=="gas_location_input" => (422,p.MessageText,"The native trades must match the selected confirmations and exact booking context."),
    PostgresException p when p.MessageText=="gas_location_denied" => (403,"access_denied","The selected account cannot access this gas booking."),
    PostgresException p when p.MessageText is "gas_location_conflict" or "gas_conflict" or "gas_setup_conflict" or "gas_quantity_conflict" or "gas_billing_conflict" or "gas_bill_conflict" => (409,p.MessageText,"This operation already belongs to different terms or ownership."),
    PostgresException p when p.SqlState is "23505" or "23514" or "23503" or "22P02" or "22003" or "22007" or "22008" && apiPath.StartsWithSegments("/api/gas/billing") => (422,"gas_billing_input","Supply compatible exact source, cash terms and a new correction occurrence."),
    PostgresException p when p.SqlState is "23505" or "23514" or "23503" or "22P02" or "22003" or "22007" or "22008" && apiPath.StartsWithSegments("/api/gas/bills") => (422,"gas_bill_input","Supply an exact trade, delivery month, scope policy and reviewed document."),

    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("gas_",StringComparison.Ordinal) => (422,p.MessageText,"The gas operation requires complete compatible terms and retained schedules."),
    MarketDataException m when m.Code is "gas_billing_conflict" or "gas_billing_stale_preview" or "gas_bill_conflict" or "gas_bill_stale_preview" or "gas_operations_conflict" or "gas_operations_stale_preview" => (409,m.Code,m.Message),
    MarketDataException m when m.Code=="catalogue_conflict" => (409,m.Code,m.Message),
    MarketDataException m => (422,m.Code,m.Message),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="risk_job_denied" => (403,"access_denied","Current authority does not permit this risk job operation."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="risk_job_conflict" => (409,p.MessageText,"This request identity already belongs to different risk inputs."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "vol_surface_product" or "vol_surface_context" or "vol_surface_input" or "vol_model_unknown"
        => (422,p.MessageText,"Name an option product of this market, the context's business date and a supported vol model."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("risk_",StringComparison.Ordinal) => (422,p.MessageText,"The risk job does not meet its retained context, input or execution contract."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="swap_amendment_locked" => (422,p.MessageText,"A month of this swap that has started would change. An amendment changes only months that start after its effective date."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="swap_amendment_backdated" => (422,p.MessageText,"This swap has settlement activity after the working As of. Amend it at an As of at or after that activity; an earlier one needs the Amend behind settlement permission."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="swap_amendment_term" => (422,p.MessageText,"A swap that has started cannot end earlier by amendment. Ending it early is a termination."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "swap_conflict" or "swap_revision_conflict" or "trade_operation_conflict" => (409,p.MessageText,"This trade or operation changed. Reconcile the original request or reload the latest revision."),
    PostgresException p when p.SqlState=="P0001" && (p.MessageText.StartsWith("clock_",StringComparison.Ordinal)||p.MessageText.StartsWith("calendar_",StringComparison.Ordinal)||p.MessageText.StartsWith("side_calendar_",StringComparison.Ordinal)||p.MessageText=="delivery_completion")
        => (422,p.MessageText,"Schedules require exact published calendars and clocks, covered dates and matching contractual completion terms."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("scheduled_",StringComparison.Ordinal) => (422,p.MessageText,"The scheduled swap requires complete, compatible quantity, delivery, observation and payment terms."),
    PostgresException p when p.SqlState=="P0001" && (p.MessageText.StartsWith("payment_",StringComparison.Ordinal)||p.MessageText=="short_month") => (422,p.MessageText,"The generated payment date does not meet its explicit rule and calendar coverage."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("swap_",StringComparison.Ordinal) => (422,p.MessageText,"The trade request does not meet its explicit side, reference, date or lifecycle terms."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="futures_conflict" => (409,"futures_conflict","This trade request identity already has different economics or ownership."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="option_product_margined" => (422,p.MessageText,"This option product's premium is margined daily, which booking does not support yet; the product cannot be booked."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="option_unsupported" => (422,p.MessageText,"Margined premium is not supported; book the option with premium paid up front."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="option_conflict" => (409,p.MessageText,"This trade request identity already has different economics or ownership."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "option_instruction_conflict" or "option_expiry_conflict" => (409,p.MessageText,FuturesOptionStore.EventRefusal(p.MessageText)!),
    PostgresException p when p.SqlState=="P0001" && FuturesOptionStore.EventRefusal(p.MessageText) is not null => (422,p.MessageText,FuturesOptionStore.EventRefusal(p.MessageText)!),
    PostgresException p when p.SqlState=="P0001" && p.MessageText.StartsWith("option_",StringComparison.Ordinal)
        => (422,p.MessageText,"The option booking needs a listed series, a side and a whole number of lots, a premium on its tick, an explicit payment date on or after the execution date and premium paid up front before expiry."),
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
        or "model_unpriced" or "test_in_progress" or "model_changed" or "model_disabled" or "agent_retired" or "agent_operating_role_conflict"
        => (409,p.MessageText,ModelCatalog.Refusal(p.MessageText)),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="agent_role_lacks_permission" => (422,p.MessageText,AgentAdministration.RoleRefusal(AgentAdministration.Lacking(p.Hint))),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "agent_not_allowed" or "grant_permission_missing" => (403,p.MessageText,AgentGrants.Refusal(p.MessageText,p.Hint)),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="grant_unknown" => (404,p.MessageText,AgentGrants.Refusal(p.MessageText)),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="grant_conflict" => (409,p.MessageText,AgentGrants.Refusal(p.MessageText)),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="grant_input" => (422,p.MessageText,AgentGrants.Refusal(p.MessageText)),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "turn_input" or "question_too_long" => (422,p.MessageText,AgentTurns.Refusal(p.MessageText)),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "turn_conflict" or "conversation_agent_mismatch"
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
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="trading_history_archived" => (422,p.MessageText,"Trading history at this knowledge cutoff is archived. Select a context at or after the rebooking cutover; market and reference history remain available."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "knowledge_coverage" or "knowledge_clock" or "knowledge_tracking" or "knowledge_retention" or "knowledge_timeline" => (422,p.MessageText,"The requested knowledge cutoff cannot be certified. Check retained coverage, clock health and commit tracking."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "read_inputs_required" or "temporal_context"
        => (422,p.MessageText,"Supply the complete original input coordinates or refresh the view for its exact read reference."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText is "read_inputs_conflict" or "occurrence_inputs_conflict"
        => (409,p.MessageText,"These inputs differ from the original recorded selection. Reopen its source or submit a new operation."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="occurrence_inputs_required"
        => (422,p.MessageText,"The job occurrence requires its admitted inputs before it can execute."),
    PostgresException p when p.SqlState=="P0001" && p.MessageText=="read_source_limit"
        => (422,p.MessageText,"This operation requests too many distinct input selections. Reduce its scope."),
    PostgresException p when p.SqlState=="42501" => (403,"access_denied","Database permissions rejected this operation."),
    PostgresException p when p.SqlState is "23514" or "55000" => (422,"definition_rejected","The value or operation violates an explicit database specification."),
    Exception when context.Items.ContainsKey("rec_inventory_attempted") && e is NpgsqlException or OperationCanceledException
        => (503,"rec_inventory_outcome_unknown","The certificate evidence outcome is unknown. Recover the original command before retrying."),
    Exception when context.Items.ContainsKey("rec_trade_attempted") && e is NpgsqlException or OperationCanceledException
        => (503,"rec_trade_outcome_unknown","The certificate trade outcome is unknown. Recover the original booking command before retrying."),
    Exception when context.Items.ContainsKey("operating_trade_attempted") && e is NpgsqlException or OperationCanceledException
        => (503,"operating_trade_outcome_unknown","The operating-right outcome is unknown. Recover the original booking command before retrying."),
    Exception when context.Items.ContainsKey("power_facility_attempted") && e is NpgsqlException or OperationCanceledException
        => (503,"power_facility_outcome_unknown","The facility save outcome is unknown. Recover the original save command before retrying."),
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
// A started Blotter stream whose client still reads it ends with an error frame, since its HTTP status has been sent.
BlotterStreamWriter? OpenStream(HttpContext context)=>context.Response.HasStarted&&context.Items[typeof(BlotterStreamWriter)] is BlotterStreamWriter{Terminated:false} writer
    &&context.Items["client_aborted"] is CancellationToken{IsCancellationRequested:false}?writer:null;

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
    // Authorized event streams have their own session, heartbeat and write bounds.
    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var intervalRead=apiPath.StartsWithSegments("/api/ppa")||string.Equals(apiPath.Value,"/api/trading/position",StringComparison.OrdinalIgnoreCase);
    if(!LiveContexts.Matches(context.Request.Method,apiPath)&&!TurnStream.Matches(context.Request.Method,apiPath)&&!AsyncReads.Matches(context.Request.Method,apiPath))deadline.CancelAfter(TimeSpan.FromSeconds(intervalRead?processLimits.ExtendedRequestSeconds:processLimits.RequestSeconds));
    context.RequestAborted=deadline.Token;
    try
    {
        await next(context);
    }
    // A client that left a started Blotter stream is sent nothing more, and its cancelled run is no failure to report.
    catch (OperationCanceledException) when (context.Response.HasStarted&&context.Items.ContainsKey(typeof(BlotterStreamWriter))
        &&context.Items["client_aborted"] is CancellationToken{IsCancellationRequested:true}) {}
    catch (Exception e) when (!context.Response.HasStarted||OpenStream(context) is not null)
    {
        var (status,code,message)=Failure(e,apiPath,context);
        if(e is PostgresException pg)app.Logger.LogWarning("Database operation rejected: {SqlState}",pg.SqlState);
        if (status is 500 or 503)
        {
            app.Logger.LogError("Preview operation failed: {ErrorType}",e.GetType().Name);
            var inner=e.InnerException;
            for(var depth=0;inner is not null&&depth<4;inner=inner.InnerException,depth++)
            {
                app.Logger.LogError("Preview operation failed: {ErrorType}",inner.GetType().Name);
                if(inner is PostgresException nested)app.Logger.LogWarning("Database operation rejected: {SqlState}",nested.SqlState);
            }
            foreach(var codeLocation in PreviewFailureDiagnostics.Codes(e))
                app.Logger.LogError("Preview failure code: {CodeLocation}",codeLocation);
        }
        if(OpenStream(context) is {} stream){await stream.Error(code,message);return;}
        context.Response.StatusCode=status;
        // A role refusal also names the role and permission as fields, so a page never parses its message.
        object body=e is PostgresException{SqlState:"P0001",MessageText:"agent_role_lacks_permission"} lacking&&AgentAdministration.Lacking(lacking.Hint) is {} lack
            ?new {code,message,role=lack.Role,permission=lack.Permission}:new {code,message};
        if(e is CurveMarkEditorException editor)body=new{code,message,editor.Key,editor.Address};
        if(e is CloseRiskInputsException closeRisk)body=new{code,message,affectedBindings=closeRisk.AffectedBindings};
        if(e is PostgresException {MessageText:"desk_agreement_ambiguous",Hint:{} selection})
            body=new{code,message,agreementSelection=JsonSerializer.Deserialize<JsonElement>(selection)};
        if(e is GasBookingAgreementException agreement)
            body=new{code,message,confirmation=agreement.Confirmation,agreementSelection=agreement.Selection};
        if(e is OpenEtrm.MarketData.Trading.SwingTermFieldException swingTerm)
            body=new{code,message,termSection="swingRights",termRow=0,field=apiPath.StartsWithSegments("/api/tickets")
                ?GasSwingTicket.FieldId(swingTerm.Field):swingTerm.Field};
        if(e is TicketFieldException ticket)body=ticket switch{
            {TermRow:{} row,TermSection:{} section}=>new{code,message,termSection=section,termRow=row,field=ticket.Field},
            {TermRow:{} row}=>new{code,message,termRow=row,field=ticket.Field},
            _=>new{code,message,side=ticket.Side,field=ticket.Field}};
        if(e is OpenEtrm.MarketData.Gas.GasExerciseTermException term&&apiPath.StartsWithSegments("/api/tickets"))
            body=new{code,message,termRow=term.Ordinal,field=term.LeadHours?"right_lead_hours":"right_sides"};
        if(e is RiskRunTieException tie)body=new{code,message,definition=tie.Definition,runs=tie.Runs};
        await context.Response.WriteAsJsonAsync(body,CancellationToken.None);
    }
    // Binding refuses a body or query value it cannot read, and a body over the limit, with a bare status before any
    // handler runs. No handler answers a bare 400 or 413, so pages release a request refused this way as one that wrote
    // nothing. An API answer names the refusal instead, echoing nothing that was read.
    if(apiPath.StartsWithSegments("/api")&&!context.Response.HasStarted&&context.Response.StatusCode is 400 or 413)
    {
        var limit=context.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize??bodyLimit;
        var (code,message)=context.Response.StatusCode==413?("request_too_large",$"The request body exceeds its {limit/1024} KiB limit."):("request_format",malformed);
        if(context.Response.StatusCode==413 && apiPath=="/api/gas/swing/record")
            await context.Response.WriteAsJsonAsync(new {code="swing_payload",field="elections",message="The election command exceeds 4 MiB."},CancellationToken.None);
        else await context.Response.WriteAsJsonAsync(new {code,message},CancellationToken.None);
    }
});

// Allow larger requests only on the routes with a dedicated contract.
// Binding refuses a larger body with a bare 413, which the error middleware names request_too_large.
// The hosted gateway (deploy/nginx.conf.template) applies the same limits and answer, so change them together.
app.Use((context,next)=>
{
    var size=context.Features.Get<IHttpMaxRequestBodySizeFeature>();
    if(size is {IsReadOnly:false}&&context.Request.Path.Equals("/api/interval-series/revise",StringComparison.Ordinal))size.MaxRequestBodySize=processLimits.IntervalRevisionBytes;
    if(size is {IsReadOnly:false}&&context.Request.Path.Equals("/api/gas/swing/record",StringComparison.Ordinal))size.MaxRequestBodySize=GasSwingStore.ElectionCommandBytes;
    if(size is {IsReadOnly:false}&&context.Request.Path.Equals("/api/desk/agreement",StringComparison.Ordinal))size.MaxRequestBodySize=12*1024*1024;
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

app.UseWhen(JsonReadTransport.Compress,branch=>branch.UseResponseCompression());

await using var exchangeDatabase=hosted is null&&File.Exists(Path.Combine(root,"exchange-manifest.json"))?new PreviewDatabase(root,"exchange-manifest.json"):null;
await using var calculationChanges=new CalculationChanges(database);
await using var exchangeCalculationChanges=exchangeDatabase is null?null:new CalculationChanges(exchangeDatabase);
var asyncReadServices=new List<AsyncReads>();
MapPreview(app.MapGroup("/api").WithMetadata(new PreviewScope("baseline")),database,hosted is null?"openetrm_preview":"__Host-openetrm_workspace","api",calculationChanges);
if(exchangeDatabase is not null)MapPreview(app.MapGroup("/examples/exchange/api").WithMetadata(new PreviewScope("exchange")),exchangeDatabase,"openetrm_exchange_preview","exchange",exchangeCalculationChanges!);
app.MapGet("/api/examples",()=>new[]{new {id="baseline",name=hosted is null?"Original synthetic example":"Private pilot workspace",url="/"},new {id="exchange",name="CME & ICE futures · synthetic prices",url="/?example=exchange"}}.Where(e=>e.id=="baseline"||exchangeDatabase is not null));
void MapPreview(RouteGroupBuilder api,PreviewDatabase database,string cookieName,string turnScope,CalculationChanges changes)
{
var sessions=new PageSessionRegistry(processLimits.PageSessions);
pageRegistries.Add(sessions);
api.AddEndpointFilter(async(invocation,next)=>
{
    var http=invocation.HttpContext;
    var untaggedHealth=http.Request.Path.Value!.EndsWith("/health",StringComparison.Ordinal)&&WorkspaceTab(http) is null;
    if(http.Request.Path.Value.EndsWith("/bootstrap",StringComparison.Ordinal)||untaggedHealth||http.Features.Get<AssistantInvocation>() is not null)
        return await next(invocation);
    var prior=WorkspaceReadContext.Page.Value;
    var priorReads=ReadReferences.Operation.Value;
    var reads=new ReadReferences.Scope();
    if(http.Request.Headers.TryGetValue(ReadReferences.Header,out var metadata))reads.Receive(metadata.ToString());
    http.Response.OnStarting(()=>
    {
        if(!reads.References.IsEmpty)http.Response.Headers[ReadReferences.Header]=JsonSerializer.Serialize(reads.References);
        return Task.CompletedTask;
    });
    try{WorkspaceReadContext.Page.Value=Session(http);ReadReferences.Operation.Value=reads;return await next(invocation);}
    finally{WorkspaceReadContext.Page.Value=prior;ReadReferences.Operation.Value=priorReads;}
});
var marketReads=new MarketReads(database);
var scheduleReads=new ScheduleReads(database);
var partyReads=new PartyReads(database);
var tradeReads=new TradeReads(database);
var liveContexts=new LiveContexts(database,changes,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});
var assistantWorkspace=new AssistantWorkspace(database,turnScope,turnScope=="exchange"?"/examples/exchange/api":"/api",app.Services,
    ()=>((IEndpointRouteBuilder)app).DataSources.SelectMany(source=>source.Endpoints),app.Services.GetRequiredService<IHttpContextAccessor>());
database.AssistantRequest=()=>app.Services.GetRequiredService<IHttpContextAccessor>().HttpContext?.Features.Get<AssistantInvocation>();
async Task<string[]> CurrentPermissions(NpgsqlConnection connection,CancellationToken ct)
{
    await using var command=ScheduleStore.Command(connection,"SELECT security.current_permissions()");
    return (string[])(await command.ExecuteScalarAsync(ct))!;
}
api.MapGet("/health",()=>new {mode=database.Manifest.Mode,runId});
api.MapGet("/bootstrap",async (HttpContext context,IAntiforgery antiforgery,bool includeFrames=true,bool includeAccess=false)=>
{
    // Capacity admission is atomic across concurrent page opens.
    if(hostedAuthentication is not null)
        foreach(var digest in await hostedAuthentication.EndedSessions(sessions.ReclaimCandidates(),context.RequestAborted))sessions.Retire(digest);
    context.Request.Cookies.TryGetValue(cookieName,out var cookie);
    var tab=WorkspaceTab(context);
    var document=WorkspaceDocument(context);
    PreviewSession session;
    if(context.Request.Headers.TryGetValue("X-OpenETRM-Reattach",out var restore))
    {
        if(restore.Count!=1||tab is null||document is null)throw new PreviewDeniedException();
        Guid? previous=restore[0]=="attach"?null:
            Guid.TryParseExact(restore[0],"D",out var id)&&id!=Guid.Empty?id:throw new PreviewDeniedException();
        try
        {
            var existing=Session(context);
            session=sessions.View(cookie!,existing.Account,tab,document:document,previousDocument:previous);
        }
        catch(PreviewSessionEnded)
        {
            return Results.Conflict(new {code="workspace_view_expired",message="This workspace selection expired. Open a new tab to start with working data."});
        }
    }
    else
    {
        var admitted=sessions.GetOrCreate(cookie,existing=>hosted is null||existing.Digest==HostedAuthentication.SessionDigest(context),
            ()=>new PreviewSession(database.Manifest,hosted is null?null:HostedAuthentication.SignIn(context)));
        var account=admitted.Session;
        session=sessions.View(admitted.Id,account,tab,()=>new PreviewSession(database.Manifest,account:account),document);
        if(admitted.Created)context.Response.Cookies.Append(cookieName,admitted.Id,new CookieOptions {HttpOnly=true,SameSite=SameSiteMode.Strict,Secure=hosted is not null,MaxAge=session.Ends-DateTimeOffset.UtcNow,Path="/"});
    }
    await session.Gate.WaitAsync(context.RequestAborted);
    try {
        var identity=hosted is null?null:HostedAuthentication.Current(context);
        if(identity is not null){if(session.Profile is not null&&session.Profile!=identity.Subject)throw new PreviewDeniedException();session.Profile=identity.Subject;}
        object response=new {asyncReadProtocol=AsyncReadChannel.Protocol,mode=database.Manifest.Mode,runId,notice=hosted is null?"Disposable synthetic workspace. Test identities are simulations, not production sign-in.":"Private pilot workspace. Financial capability is limited to the supported product conventions.",profiles=identity is null?profiles:profiles.Take(0).Append(new{id=identity.Subject,name=identity.Name,description="Signed-in account"}).ToArray(),
        requestDeadlines=processLimits.RequestDeadlines,processLimits=processLimits.Digest,profile=session.Profile,customProfiles=session.CustomProfiles.Select(p=>new{id=p.Key,name=p.Value}).ToArray(),csrfToken=antiforgery.GetAndStoreTokens(context).RequestToken,frames=includeFrames?session.PersistentFrames.Where(f=>f.PpaBundle is null && f.ContextId==session.MarketContext?.ContextId).OrderBy(f=>f.At).ToArray():[],
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
Guid? WorkspaceDocument(HttpContext context)
{
    var supplied=context.Request.Headers["X-OpenETRM-Document"];
    if(supplied.Count==0)return null;
    return supplied.Count==1&&Guid.TryParseExact(supplied[0],"D",out var id)&&id!=Guid.Empty?id:throw new PreviewDeniedException();
}
api.MapPost("/workspace/view/close",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
{
    if(WorkspaceTab(c) is not {} tab||WorkspaceDocument(c) is not {} document)throw new PreviewDeniedException();
    sessions.Detach(c.Request.Cookies[cookieName]!,tab,document,TimeSpan.FromSeconds(processLimits.RequestSeconds));
    return Task.FromResult(Results.Ok() as IResult);
})));
Guid? WorkspaceTab(HttpContext context)
{
    var supplied=context.Request.Headers["X-OpenETRM-View"];
    if(supplied.Count==0)supplied=context.Request.Query["workspaceView"];
    if(supplied.Count==0)return null;
    return supplied.Count==1&&Guid.TryParseExact(supplied[0],"D",out var id)&&id!=Guid.Empty?id:throw new PreviewDeniedException();
}
PreviewSession Session(HttpContext context)
{
    if(context.Features.Get<AssistantInvocation>() is {} invocation)
        return invocation.Session.Ends>DateTimeOffset.UtcNow?invocation.Session:throw new PreviewSessionEnded();
    if (!context.Request.Cookies.TryGetValue(cookieName,out var id)||!sessions.TryGetValue(id,out var session)||DateTimeOffset.UtcNow>session.Ends) throw new PreviewSessionEnded();
    if(hosted is not null&&session.Profile!=HostedAuthentication.Current(context).Subject)throw new PreviewDeniedException();
    if(hosted is not null&&session.Digest!=HostedAuthentication.SessionDigest(context))throw new PreviewSessionEnded();
    return sessions.View(id,session,WorkspaceTab(context));
}
async Task<T> WithSession<T>(HttpContext context,Func<PreviewSession,Task<T>> work)
{
    var session=Session(context); await session.Gate.WaitAsync(context.RequestAborted);
    try {return await work(session);} finally {session.Gate.Release();}
}
Task<IResult> Locked(HttpContext context,Func<PreviewSession,Task<IResult>> work)=>WithSession(context,work);
string Profile(PreviewSession session)=>session.Profile ?? throw new PreviewDeniedException();
api.MapPost("/time/inspect",(HttpContext c,LocalTimeRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),null,(_,_)=>
    Task.FromResult<IResult>(Results.Ok(new{candidates=UserTime.Inspect(body.LocalTime,body.TimeZone)})),c.RequestAborted)));
// Pages pin the session's selected context, or current working data when none is selected.
PinnedContext Pin(PreviewSession session,Guid? frame=null)
{
    if(frame is {} live&&session.LivePin(live) is {} retained)return retained;
    var selected=frame is {} id?Frame(session,id):null;
    return session.MarketContext is {} context?PinnedContext.Retained(context,selected):PinnedContext.Current(selected);
}
// An identity that could not discover the session's closes holds no market inputs: an unknown frame is then a denial (D334).
InputFrame PpaFrame(PreviewSession session,Guid id)=>session.Frames.GetValueOrDefault(id) is {} frame||session.HasDiscoveredFrames
    ?Pin(session).Require(session.Frames.GetValueOrDefault(id)):throw new PreviewDeniedException();
InputFrame Frame(PreviewSession session,Guid id)
{
    var frame=PpaFrame(session,id);
    return frame.PpaBundle is null?frame:throw new MarketDataException("snapshot_inputs","This exact bundle frame belongs to the bounded PPA consumer.");
}
LifecycleEndpoints.Map(api,database,Locked,Profile,Frame);
PointTraderMarkEndpoints.Map(api,database,Locked,Profile);
CurveMarkEditorEndpoints.Map(api,database,Locked,Profile);
async Task<SessionFramePlan> DiscoverFrames(NpgsqlConnection connection,PreviewSession session,CancellationToken ct,bool reuseRecordedInputs=false)
{
    if(session.Live is not null)return new([],session.FrameCatalog);
    var aliases=session.ContextAliases(session.MarketContext);
    var stored=await MarketCloses.Frames(connection,database.Manifest.CatalogId,session.MarketContext?.ContextId,ct,session.DurableLimit(aliases),
        reuseRecordedInputs?session.RecordedInputs:null);
    return session.PlanFrames(stored,session.MarketContext,aliases);
}
async Task RefreshFrames(PreviewSession session,CancellationToken ct,bool reuseRecordedInputs=false)
{
    var plan=await database.WithIdentity(Profile(session),"ReadMarketData",async(connection,cancellation)=>
    {
        await Pin(session).Bind(connection,database.Manifest.CatalogId,cancellation);
        return await DiscoverFrames(connection,session,cancellation,reuseRecordedInputs);
    },ct);
    session.ApplyFrames(plan);
}
api.MapMarketDates(database,async(c,closeSession,customCalendar,work)=>
{
    async Task<ReadPlan> Capture(PreviewSession session)
    {
        // Discovery assigns session state. Only this capture and application of
        // its plan hold the gate; qualification runs on immutable selections.
        await RefreshFrames(session,c.RequestAborted,reuseRecordedInputs:true);
        var pin=Pin(session);var frames=session.VisibleFrames;var catalog=session.FrameCatalog;
        var profile=Profile(session);var digest=hosted is null?null:session.Digest;
        return new(new("market.calendar.v1",new AsyncReadEmpty(),null,["ReadMarketData"]),async ct=>
        {
            // Certify in a short transaction. A custom calendar can then inspect
            // every candidate without holding the publication gate during pricing.
            var cut=customCalendar is not null&&pin.Context is null&&!frames.IsEmpty
                ?await database.WithIdentity(profile,"ReadMarketData",(connection,t)=>
                    MarketDates.PrepareCut(database,connection,pin,frames,customCalendar,t),ct,hostedDigest:digest):null;
            return await database.WithIdentity(profile,"ReadMarketData",async(connection,t)=>
            {
                var value=await work(connection,pin,frames,catalog,cut,t);
                return new ReadOutcome(value,MarketDates.AnswerCheck(database,pin,AsyncReadVisibility.CalendarFrames(frames),closeSession,value));
            },ct,hostedDigest:digest);
        });
    }
    if(c.Features.Get<AssistantInvocation>() is not null)
    {
        return await Locked(c,async session=>Results.Ok((await (await Capture(session)).Execute(c.RequestAborted)).Value));
    }
    var selected=Session(c);var hostedDigest=hosted is null?null:selected.Digest;
    return Results.Ok(await UngatedRead.Run(selected,async session=>AsyncReadVisibility.Observed(await Capture(session),marketDates:true,context:session.MarketContext),
        (profile,plan,outcome,ct)=>ReadAuthority.Require(database,profile,hostedDigest,plan.Input.Permissions,ct,outcome.Recheck),c.RequestAborted));
});
api.MapGet("/close-sessions/catalog",(Func<HttpContext,Task<IResult>>)(c=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(await CloseSessions.Catalog(connection,database.Manifest.CatalogId,ct)))));
api.MapPost("/close-sessions/save",(HttpContext c,CloseSessionSave body)=>DateOperation(c,"WriteMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await CloseSessions.Save(connection,database,body,ct))));
api.MapPost("/close-sessions/publish",(HttpContext c,CloseSessionPublish body)=>DateOperation(c,"SealMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await CloseSessions.Publish(connection,database.Manifest.CatalogId,body,ct))));
api.MapPost("/close-sessions/listing",(HttpContext c,CloseSessionListing body)=>DateOperation(c,"SealMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await CloseSessions.PublishListing(connection,database.Manifest.CatalogId,body,ct))));
api.MapGet("/close-sessions/choice",(Func<HttpContext,Task<IResult>>)(c=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(await CloseSessions.Choice(connection,database.Manifest.CatalogId,ct)))));
api.MapPost("/close-sessions/choice",(HttpContext c,CloseSessionChoice body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    Results.Ok(await CloseSessions.Choose(connection,database.Manifest.CatalogId,body,ct)),c.RequestAborted)));
Task<IResult> CloseGrantOperation(HttpContext c,Func<NpgsqlConnection,CancellationToken,Task<object>> work)=>Locked(c,session=>
{
    c.Items["administration_attempted"]=true;
    return database.WithIdentity(Profile(session),"ManageAccess",async(connection,ct)=>Results.Ok(await work(connection,ct)),c.RequestAborted,admission:Admission.Administration);
});
api.MapGet("/close-sessions/grants/list",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ManageAccess",async(connection,ct)=>
    Results.Ok(await CloseSessions.Grants(connection,ct)),c.RequestAborted))));
api.MapPost("/close-sessions/grants",(HttpContext c,CloseSessionGrant body)=>CloseGrantOperation(c,async(connection,ct)=>await CloseSessions.Grant(connection,database.Manifest.CatalogId,body,ct)));
api.MapPost("/close-sessions/grants/revoke",(HttpContext c,CloseSessionRevoke body)=>CloseGrantOperation(c,async(connection,ct)=>await CloseSessions.Revoke(connection,body,ct)));
api.MapPost("/market/closes/history",(HttpContext c,MarketCloseHistoryRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    await Pin(session).Bind(connection,database.Manifest.CatalogId,ct);
    return Results.Ok(await MarketCloses.History(connection,database.Manifest.CatalogId,body,ct));
},c.RequestAborted)));
api.MapPost("/market/closes/register",(HttpContext c,MarketCloseRegister body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"PublishMarketData",async(connection,ct)=>
        Results.Ok(await MarketCloses.Register(database,connection,Pin(session),id=>Frame(session,id),body,ct)),c.RequestAborted)));
api.MapPost("/market/closes/capture-live",(HttpContext c,MarketCloseRegister body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"PublishMarketData",async(connection,ct)=>
        Results.Ok(await MarketCloses.CaptureLive(database,connection,Pin(session),id=>Frame(session,id),body,ct)),c.RequestAborted)));
api.MapPost("/jobs/close-book-risk/create",(HttpContext c,CloseBookRiskCreate body)=>Locked(c,session=>database.WithIdentity(Profile(session),"RunJobs",async(connection,ct)=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Admit existing-close jobs in the current workspace.");
    return Results.Ok(await CloseBookRiskJobs.Create(database,connection,body,ct));
},c.RequestAborted)));
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
CustomCurveOperation CurrentMarketOperation(string code,string message)=>(c,permission,snapshotId,work,replay)=>Locked(c,session=>
    database.WithIdentity(Profile(session),permission,async(connection,ct)=>
    {
        // Accepted commands resolve under current authority before looking up an
        // ephemeral page snapshot; a retry never requires a new pricing context.
        if(permission is "WriteMarketDefinitions" or "SealMarketDefinitions" or "ImportObservations" && Pin(session).Context is not null)
            throw new MarketDataException(code,message);
        if(replay is not null&&await replay(connection,ct) is {} accepted)return accepted;
        var pin=Pin(session,snapshotId);
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        return await work(connection,pin,ct);
    },c.RequestAborted));
api.MapCustomCurves(database,CurrentMarketOperation("custom_curve_historical","Author and publish curves in the current workspace."));
var priceAuthoringOperation=CurrentMarketOperation("price_authoring_historical","Return to working data to change pricing terms or ingest publication prices.");
api.MapPricePublications(database,priceAuthoringOperation);
api.MapContractPriceTerms(database,priceAuthoringOperation);
api.MapPost("/profile",(HttpContext c,ProfileRequest body)=>Locked(c,async session=>
{
    if(hosted is not null)throw new PreviewDeniedException();
    if (!profiles.Any(p=>p.id==body.Profile) && !session.CustomProfiles.ContainsKey(body.Profile)) throw new PreviewDeniedException();
    var left=session.Profile;
    sessions.ResetViews(session.Account);session.Profile=body.Profile;
    // Then the switch ends the streams the session opened under the profile it left, before it cancels the turns the session
    // asked, so no stream carries that cancel instead; a stream still being admitted finds the new profile
    // (docs/ai-assistant.md §7.2, §8).
    turnStreams.EndSession(session.Digest,left);
    turnWorker.EndSession(session.Digest);
    // Closes are the identity's own discovery (D334): one that can read market data holds them at once; one that cannot holds none.
    try{await RefreshFrames(session,c.RequestAborted);}catch(PreviewDeniedException){}
    return Results.Ok(new {profile=session.Profile});
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
var powerFacilities=new PhysicalOperatingFacilityStore(database.Manifest.CatalogId);
// Capture current knowledge once per read, or bind the exact retained context. Authoring
// stays in current control state; the facility store certifies its committed revision.
Task<IResult> PhysicalPowerOperation(HttpContext c,Guid snapshotId,string permission,bool authoring,
    Func<NpgsqlConnection,PreviewData,InputFrame,ScheduleContext?,bool,CancellationToken,Task<IResult>> work)=>Locked(c,session=>
        PhysicalPowerSessionOperation(c,session,snapshotId,permission,authoring,work));
Task<IResult> PhysicalPowerSessionOperation(HttpContext c,PreviewSession session,Guid snapshotId,string permission,bool authoring,
    Func<NpgsqlConnection,PreviewData,InputFrame,ScheduleContext?,bool,CancellationToken,Task<IResult>> work)=>
    database.WithIdentity(Profile(session),permission,async(connection,ct)=>
    {
        if(!await PreviewDatabase.HasPermission(connection,"ReadPhysical",ct)||!await PreviewDatabase.HasPermission(connection,"ReadMarketData",ct))
            throw new PreviewDeniedException();
        var pin=Pin(session,snapshotId);var frame=pin.RequireFrame();var current=pin.Context is null;
        ScheduleContext? context=null;
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        if(authoring)
        {
            if(pin.Context is not null)throw new MarketDataException("historical_read_only","Return to working data before changing operating facilities.");
        }
        else
        {
            var view=pin.Context is {} retained?TradingView.Retained(frame,retained):
                await TradingView.Capture(connection,new KnowledgeResolver(database.Manifest.CatalogId),frame,ct);
            context=view.Context;
            if(pin.Context is null)await view.Bind(connection,database.Manifest.CatalogId,ct);
            frame=view.CalculationFrame;
        }
        if(context is not null&&await PreviewDatabase.HasPermission(connection,"ReadTrades",ct))
            await TradingContext.Bind(connection,database.Manifest.CatalogId,frame,context,ct);
        using var visibility=new AsyncReadVisibility();
        var data=await MarketInputSelection.Root(database,connection,frame,ct,
            referenceScope:ReferenceReadScope.AllVisible,includeCalendarDays:true);
        var result=await work(connection,data,frame,context,current,ct);
        await visibility.Seal().Require(connection,ct);
        if(!await PreviewDatabase.HasPermission(connection,"ReadPhysical",ct)||!await PreviewDatabase.HasPermission(connection,"ReadMarketData",ct))
            throw new PreviewDeniedException();
        return result;
    },c.RequestAborted);
api.MapPost("/physical/power-facilities/catalog",(HttpContext c,PhysicalPowerCatalogRequest body)=>
    PhysicalPowerOperation(c,body.SnapshotId,"ReadPhysical",false,async(connection,data,frame,_,current,ct)=>
        Results.Ok(StructuredPowerPreview.Catalog(data,current&&await PreviewDatabase.HasPermission(connection,"ManagePhysicalSetup",ct),
            await PreviewDatabase.HasPermission(connection,"RunScenario",ct)) with{PriceContracts=StructuredPowerTradeCatalog.Prices(data,frame)})));
api.MapPost("/physical/power-trades/catalog",(HttpContext c,PhysicalPowerCatalogRequest body)=>
    PhysicalPowerOperation(c,body.SnapshotId,"ReadTrades",false,async(connection,data,frame,context,current,ct)=>
        Results.Ok(await StructuredPowerTradeCatalog.Read(connection,data,frame,context!.KnownAt,current,ct))));
var operatingTrades=new StructuredPowerBookingStore(database);
api.MapPost("/physical/power-trades/preview",(HttpContext c,StructuredPowerBookRequest body)=>
    PhysicalPowerOperation(c,(body.Plan??throw new MarketDataException("operating_booking","Supply the complete reviewed plan.")).SnapshotId,
        "BookTrade",false,async(connection,data,frame,context,current,ct)=>
    {
        if(!current)throw new MarketDataException("historical_read_only","Return to working data to prepare a new operating-right trade.");
        var saved=await powerFacilities.ReadExact(connection,body.Plan.FacilityId,body.Plan.FacilityVersion,
            context!.ContextId!.Value,frame.At,frame.Time.Date,ct);
        return Results.Ok((await operatingTrades.Prepare(connection,data,frame,saved,SchedulePreview.ParseAt(context.KnownAt),body,ct)).Preview);
    }));
api.MapPost("/physical/power-trades/valuation",(HttpContext c,StructuredPowerTradeReadRequest body)=>
{
    body.RequireCurrentValuation();
    return PhysicalPowerOperation(c,body.SnapshotId,"ReadTrades",false,async(connection,_,frame,_,_,ct)=>
        Results.Ok(await operatingTrades.Value(connection,frame,body.TradeId,ct)));
});
api.MapPost("/physical/power-trades/directory",(HttpContext c,StructuredPowerTradeDirectoryRequest body)=>
    PhysicalPowerOperation(c,body.SnapshotId,"ReadTrades",false,async(connection,_,frame,_,_,ct)=>
        Results.Ok(await operatingTrades.Directory(connection,frame,body.BookIds,body.Offset,body.Limit,ct))));
async Task RequireOperatingTradeAuthority(NpgsqlConnection connection,CancellationToken ct)
{
    if(!await PreviewDatabase.HasPermission(connection,"ReadPhysical",ct)||
       !await PreviewDatabase.HasPermission(connection,"ReadMarketData",ct)||
       !await PreviewDatabase.HasPermission(connection,"ReadTrades",ct))throw new PreviewDeniedException();
}
api.MapPost("/physical/power-trades/status",(HttpContext c,StructuredPowerTradeStatusRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"BookTrade",async(connection,ct)=>
    {
        if(Pin(session).Context is not null)throw new MarketDataException("historical_read_only","Recover booking outcomes from current working data.");
        await RequireOperatingTradeAuthority(connection,ct);
        var result=await operatingTrades.OperationStatus(connection,body.OperationId,ct);
        await RequireOperatingTradeAuthority(connection,ct);
        return Results.Ok(result);
    },c.RequestAborted)));
api.MapPost("/physical/power-trades/book",(HttpContext c,StructuredPowerBookRequest body)=>Locked(c,async session=>
{
    c.Items["operating_trade_attempted"]=true;
    if(Pin(session).Context is not null)throw new MarketDataException("historical_read_only","Return to working data to book an operating right.");
    var profile=Profile(session);var retained=new RetainedScheduleStore(database);
    var staged=await database.WithIdentity(profile,"BookTrade",async(connection,ct)=>
    {
        await RequireOperatingTradeAuthority(connection,ct);
        // Retry first: its original outcome does not depend on a new market
        // capture or a snapshot that the workspace may since have replaced.
        var prior=await operatingTrades.RequestStatus(connection,body,ct);
        if(prior is not null)return (Receipt:prior,Plan:(StructuredPowerBookingPrepared?)null,Frame:(InputFrame?)null);
        var pin=Pin(session,(body.Plan??throw new MarketDataException("operating_booking","Supply a complete operating plan.")).SnapshotId);
        if(pin.Context is not null)throw new MarketDataException("historical_read_only","Book from the selected working financial snapshot.");
        var frame=pin.RequireFrame();
        var view=await TradingView.Capture(connection,new KnowledgeResolver(database.Manifest.CatalogId),frame,ct);
        await view.Bind(connection,database.Manifest.CatalogId,ct);
        var selected=view.CalculationFrame;
        await TradingContext.Bind(connection,database.Manifest.CatalogId,selected,view.Context,ct);
        using var visibility=new AsyncReadVisibility();
        var data=await MarketInputSelection.Root(database,connection,selected,ct,referenceScope:ReferenceReadScope.AllVisible,includeCalendarDays:true);
        var facility=await powerFacilities.ReadExact(connection,body.Plan.FacilityId,body.Plan.FacilityVersion,
            view.Context.ContextId!.Value,selected.At,selected.Time.Date,ct);
        var prepared=await operatingTrades.Prepare(connection,data,selected,facility,SchedulePreview.ParseAt(view.Context.KnownAt),body,ct);
        await visibility.Seal().Require(connection,ct);
        await RequireOperatingTradeAuthority(connection,ct);
        return (Receipt:(StructuredPowerBookingReceipt?)null,Plan:(StructuredPowerBookingPrepared?)prepared,Frame:(InputFrame?)selected);
    },c.RequestAborted);
    if(staged.Receipt is not null)return Results.Ok(staged.Receipt);
    var plan=staged.Plan!;
    await retained.Stage(profile,body.OperationId,body,"BookTrade",null,null,plan.Retained.Generate(c.RequestAborted),c.RequestAborted);
    return await database.WithIdentity(profile,"BookTrade",async(connection,ct)=>
    {
        await RequireOperatingTradeAuthority(connection,ct);
        await retained.Lock(connection,body.OperationId,ct);
        var receipt=await operatingTrades.Save(connection,staged.Frame!,body,plan,ct);
        if(receipt.Changed)
        {
            var instrument=await retained.Publish(connection,body.OperationId,ct);
            if(instrument!=receipt.InstrumentId)throw new MarketDataException("operating_booking","The retained programs must publish the exact accepted instrument.");
        }
        await RequireOperatingTradeAuthority(connection,ct);
        return Results.Ok(receipt);
    },c.RequestAborted);
}));
api.MapPost("/physical/power-facilities/list",(HttpContext c,PhysicalPowerListRequest body)=>
    PhysicalPowerOperation(c,body.SnapshotId,"ReadPhysical",false,async(connection,_,frame,context,_,ct)=>
        Results.Ok(await powerFacilities.List(connection,context!.ContextId!.Value,frame.At,frame.Time.Date,body.SelectionPolicy,ct))));
api.MapPost("/physical/power-facilities/get",(HttpContext c,PhysicalPowerGetRequest body)=>
    PhysicalPowerOperation(c,body.SnapshotId,"ReadPhysical",false,async(connection,_,frame,context,_,ct)=>
        Results.Ok(await powerFacilities.ReadExact(connection,body.FacilityId,body.Version,context!.ContextId!.Value,frame.At,frame.Time.Date,ct))));
api.MapPost("/physical/power-facilities/save",(HttpContext c,PhysicalPowerSaveRequest body)=>Locked(c,async session=>
{
    c.Items["power_facility_attempted"]=true;
    if(Pin(session).Context is not null)throw new MarketDataException("historical_read_only","Return to working data before changing operating facilities.");
    var request=new PhysicalOperatingFacilitySaveRequest(body.RequestId,body.ExpectedVersion,body.Facility);
    var prior=await database.WithIdentity(Profile(session),"ManagePhysicalSetup",async(connection,ct)=>
        await powerFacilities.Replay(connection,request,ct),c.RequestAborted);
    if(prior is not null)return Results.Ok(prior);
    return await PhysicalPowerSessionOperation(c,session,body.SnapshotId,"ManagePhysicalSetup",true,async(connection,data,_,_,_,ct)=>
        Results.Ok(await powerFacilities.Save(connection,data,request,ct)));
}));
api.MapPost("/physical/power-facilities/status",(HttpContext c,PhysicalPowerStatusRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ManagePhysicalSetup",async(connection,ct)=>
    {
        if(Pin(session).Context is not null)throw new MarketDataException("historical_read_only","Recover facility save outcomes from current working data.");
        return Results.Ok(await powerFacilities.RequestStatus(connection,body.RequestId,ct));
    },c.RequestAborted)));
api.MapPost("/physical/power-facilities/preview",(HttpContext c,StructuredPowerPreviewRequest body)=>
    PhysicalPowerOperation(c,body.SnapshotId,"RunScenario",false,async(connection,data,frame,context,_,ct)=>
    {
        var facility=await powerFacilities.ReadExact(connection,body.FacilityId,body.FacilityVersion,context!.ContextId!.Value,frame.At,frame.Time.Date,ct);
        return Results.Ok(StructuredPowerPreview.Calculate(data,frame,facility,SchedulePreview.ParseAt(context.KnownAt),body));
    }));
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
{
    var products=await scheduleReads.PublishedProducts(connection,Pin(session),ct);
    var product=PowerPreview.SelectProduct(products,body.Product,body.ProductVersion);
    var data=await database.Read(connection,ct,observationIds:Frame(session,body.SnapshotId).ObservationIds);
    data=await SideCalendarPolicies.PrepareReferences(connection,data,[product.Calendar],ct);
    return Results.Ok(PowerPreview.Calculate(data,Frame(session,body.SnapshotId),body,products));
}));
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
api.MapPost("/interval-series/latest",(HttpContext c,IntervalSeriesLatestRequest body)=>IntervalSeriesOperation(c,"ReadMarketData",(connection,ct)=>intervalSeries.Latest(connection,body,ct)));
api.MapPost("/interval-series/values",(HttpContext c,IntervalSeriesReadRequest body)=>IntervalSeriesOperation(c,"ReadMarketData",(connection,ct)=>intervalSeries.Values(connection,body,ct)));
api.MapPost("/interval-series/changes",(HttpContext c,IntervalSeriesReadRequest body)=>IntervalSeriesOperation(c,"ReadMarketData",(connection,ct)=>intervalSeries.Changes(connection,body,ct)));
var intervalCurves=new IntervalCurvesStore(marketReads,touStore);
var ppaQuantities=new PpaQuantitiesStore(marketReads,touStore);
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
api.MapPost("/period-curves/values",(HttpContext c,PeriodCurveValuesRequest body)=>IntervalCurveRead(c,body.SnapshotId,(connection,pin,ct)=>intervalCurves.PeriodValues(connection,pin,body,ct)));
var ppaStore=new PpaStore(database,touStore);
var settlementPreview=new SettlementPreview(database,ppaStore);
Task<IResult> SettlementOperation(HttpContext c,Guid snapshot,Func<NpgsqlConnection,InputFrame,CancellationToken,Task<(object Value,Guid Trade)>> work)=>Locked(c,session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("settlement_context","Use an explicit preview knowledge cutoff from current working data.");
    var frame=PpaFrame(session,snapshot);
    return database.WithIdentity(Profile(session),SettlementPreview.Permission,async(connection,ct)=>
    {
        var result=await work(connection,frame,ct);ct.ThrowIfCancellationRequested();
        var bytes=PpaStore.Response(result.Value,SettlementPreview.JsonOptions,SettlementPreview.MaximumResponseBytes);ct.ThrowIfCancellationRequested();
        await settlementPreview.Recheck(connection,result.Trade,ct);
        return (IResult)new PpaJsonResult(bytes,200);
    },c.RequestAborted);
});
api.MapPost("/settlement/payment-lineages/read",(HttpContext c,PaymentLineagesReadRequest body)=>SettlementOperation(c,body.SnapshotId,async(connection,frame,ct)=>(await settlementPreview.ReadLineages(connection,frame,body,ct),body.TradeId)));
api.MapPost("/settlement/payment-lineages/plan",(HttpContext c,PaymentLineageRequest body)=>SettlementOperation(c,body.SnapshotId,async(connection,frame,ct)=>(await settlementPreview.Plan(connection,frame,body,ct),body.TradeId)));
api.MapPost("/settlement/ppa/preview",(HttpContext c,SettlementPreviewRequest body)=>SettlementOperation(c,body.SnapshotId,(connection,frame,ct)=>settlementPreview.Preview(connection,frame,body,ct)));

Task<IResult> PpaOperation(HttpContext c,Guid? snapshot,string permission,Func<NpgsqlConnection,InputFrame?,CancellationToken,Task<object>> work,Func<NpgsqlConnection,InputFrame?,CancellationToken,Task>? recheck=null)=>Locked(c,session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("ppa_historical","Return to current working data; PPA history uses an explicit known-at cutoff.");
    var frame=snapshot is {} id?PpaFrame(session,id):null;
    return database.WithIdentity(Profile(session),permission,async(connection,ct)=>
    {
        if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct) || !await PreviewDatabase.HasPermission(connection,"ReadMarketData",ct))throw new PreviewDeniedException();
        var value=await work(connection,frame,ct);ct.ThrowIfCancellationRequested();
        var bytes=PpaStore.Response(value);ct.ThrowIfCancellationRequested();
        if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct) || !await PreviewDatabase.HasPermission(connection,"ReadMarketData",ct))throw new PreviewDeniedException();
        await ppaStore.Recheck(connection,frame,ct);
        if(value is PpaRevisionRefusal refusal)await ppaStore.RecheckRefusal(connection,refusal,ct);
        if(recheck is not null)await recheck(connection,frame,ct);ct.ThrowIfCancellationRequested();
        return (IResult)new PpaJsonResult(bytes,value is PpaRevisionRefusal?422:200);
    },c.RequestAborted);
});
api.MapGet("/ppa/frames",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
{
    if(session.MarketContext is not null)throw new MarketDataException("ppa_historical","PPA frames use explicit current-session bundle selection.");
    var frames=await ppaStore.FramesRead(connection,session.Frames.Values.Where(f=>f.ContextId is null).OrderBy(f=>f.At).ThenBy(f=>f.Id),ct);
    var bytes=PpaStore.Response(frames);
    await ppaStore.Recheck(connection,null,ct);
    foreach(var frame in frames)await ppaStore.Recheck(connection,frame,ct);
    return (IResult)new PpaJsonResult(bytes,200);
},c.RequestAborted))));
api.MapPost("/ppa/catalog",(HttpContext c,IntervalCurveCatalogRequest body)=>PpaOperation(c,body.SnapshotId,"ReadTrades",(connection,frame,ct)=>ppaStore.CatalogRead(connection,frame!,ct)));
api.MapPost("/ppa/directory",(HttpContext c,PpaDirectoryRequest body)=>PpaOperation(c,body.SnapshotId,"ReadTrades",(connection,frame,ct)=>ppaStore.Directory(connection,frame!,body,ct),(connection,frame,ct)=>ppaStore.RecheckScope(connection,frame!,body.KnownAt,"books",body.BookIds,ppaStore.ReferenceFor(frame!),ct)));
api.MapPost("/ppa/inspect",(HttpContext c,PpaInspectRequest body)=>PpaOperation(c,body.Preview?.SnapshotId,"ReadTrades",(connection,frame,ct)=>ppaStore.Inspect(connection,frame!,body,ct),(connection,frame,ct)=>ppaStore.RecheckScope(connection,frame!,body.Preview!.KnownAt,body.Preview.Scope,body.Preview.SelectedIds,body.Preview.ReferenceCatalogId,ct)));
api.MapPost("/ppa/break-even",(HttpContext c,PpaBreakEvenRequest body)=>PpaOperation(c,body.SnapshotId,"ReadTrades",(connection,frame,ct)=>new PpaBreakEvenStore(database,ppaStore).Solve(connection,frame!,body,ct)));
api.MapPost("/ppa/validate",(HttpContext c,PpaValidateRequest body)=>PpaOperation(c,body.SnapshotId,"ReadTrades",(connection,frame,ct)=>ppaStore.Validate(connection,frame!,body,ct)));
api.MapPost("/ppa/preview",(HttpContext c,PpaPreviewRequest body)=>PpaOperation(c,body.SnapshotId,"ReadTrades",(connection,frame,ct)=>ppaStore.Preview(connection,frame!,body,ct),(connection,frame,ct)=>ppaStore.RecheckScope(connection,frame!,body.KnownAt,body.Scope,body.SelectedIds,body.ReferenceCatalogId,ct)));
api.MapPost("/ppa/pnl",(HttpContext c,PpaContractChangeRequest body)=>Locked(c,session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("ppa_historical","PPA close comparisons require explicit known-at cutoffs in working mode.");
    if(body.Opening is null||body.Closing is null)throw PpaCompiler.Error("ppa_pnl_scope","Supply both exact close contexts.");
    var opening=PpaFrame(session,body.Opening.SnapshotId);var closing=PpaFrame(session,body.Closing.SnapshotId);
    return database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
    {
        if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct)||!await PreviewDatabase.HasPermission(connection,"ReadMarketData",ct))throw new PreviewDeniedException();
        var value=await ppaStore.ContractChange(connection,opening,closing,body,ct);
        if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct)||!await PreviewDatabase.HasPermission(connection,"ReadMarketData",ct))throw new PreviewDeniedException();
        var bytes=PpaStore.Response(value);ct.ThrowIfCancellationRequested();
        await ppaStore.RecheckScope(connection,opening,body.Opening.KnownAt,body.Opening.Scope,body.Opening.SelectedIds,body.Opening.ReferenceCatalogId,ct);
        await ppaStore.RecheckScope(connection,closing,body.Closing.KnownAt,body.Closing.Scope,body.Closing.SelectedIds,body.Closing.ReferenceCatalogId,ct);
        await ppaStore.Recheck(connection,opening,ct);await ppaStore.Recheck(connection,closing,ct);
        await ppaStore.RequireContractClose(connection,opening,body.Opening.KnownAt,ct);
        await ppaStore.RequireContractClose(connection,closing,body.Closing.KnownAt,ct);ct.ThrowIfCancellationRequested();
        return(IResult)new PpaJsonResult(bytes,200);
    },c.RequestAborted);
}));
Task<IResult> SavePpa(HttpContext c,PpaWriteRequest body,string action)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("ppa_historical","Return to current working data to book or amend.");
    var frame=PpaFrame(session,body.SnapshotId);var profile=Profile(session);var permission=action=="booked"?"BookTrade":"AmendTrade";
    var retained=new RetainedScheduleStore(database);var staged=false;
    if(action!="terminated")
    {
        var prepared=await database.WithIdentity(profile,permission,async(connection,ct)=>
        {
            var status=JsonSerializer.SerializeToElement(await ppaStore.Status(connection,body.OperationId,ct),PpaStore.Json);
            if(status.TryGetProperty("tradeId",out _))return (Terms:(RetainedScheduleTerms?)null,Refusal:(IResult?)null);
            if(action=="amended"&&await ppaStore.AmendmentRefusal(connection,frame,body,ct) is {} refusal)
            {
                var bytes=PpaStore.Response(refusal);
                await ppaStore.Recheck(connection,frame,ct);await ppaStore.RecheckRefusal(connection,refusal,ct);
                return (Terms:(RetainedScheduleTerms?)null,Refusal:(IResult?)new PpaJsonResult(bytes,422));
            }
            return (Terms:(RetainedScheduleTerms?)await ppaStore.PrepareScheduleTerms(connection,frame,body,action,ct),Refusal:(IResult?)null);
        },c.RequestAborted);
        if(prepared.Refusal is not null)return prepared.Refusal;
        var terms=prepared.Terms;
        if(terms is not null)
        {
            var preparation=terms.Generate(c.RequestAborted);
            await retained.Stage(profile,body.OperationId,PpaStore.BookingCommand(frame,body,action),permission,body.TradeId,body.ExpectedRevision,preparation,c.RequestAborted);
            staged=true;
        }
    }
    return await database.WithIdentity(profile,permission,async(connection,ct)=>
    {
        if(staged)await retained.Lock(connection,body.OperationId,ct);
        var value=await ppaStore.Save(connection,frame,body,action,ct);
        if(staged&&value is not PpaRevisionRefusal)
        {
            var instrument=await retained.Publish(connection,body.OperationId,ct);
            await ppaStore.VerifyPublishedPrograms(connection,frame,body.OperationId,instrument,ct);
        }
        ct.ThrowIfCancellationRequested();var bytes=PpaStore.Response(value);
        await ppaStore.Recheck(connection,frame,ct);
        if(value is PpaRevisionRefusal refusal)await ppaStore.RecheckRefusal(connection,refusal,ct);
        else await ppaStore.RecheckWrite(connection,frame,body,ct);
        return (IResult)new PpaJsonResult(bytes,value is PpaRevisionRefusal?422:200);
    },c.RequestAborted);
});
api.MapPost("/ppa/book",(HttpContext c,PpaWriteRequest body)=>SavePpa(c,body,"booked"));
api.MapPost("/ppa/amend",(HttpContext c,PpaWriteRequest body)=>SavePpa(c,body,"amended"));
api.MapPost("/ppa/terminate",(HttpContext c,PpaWriteRequest body)=>SavePpa(c,body,"terminated"));
api.MapPost("/ppa/status",(HttpContext c,PpaStatusRequest body)=>PpaOperation(c,null,"ReadTrades",(connection,frame,ct)=>ppaStore.Status(connection,body.OperationId,ct),async(connection,frame,ct)=>{await ppaStore.Status(connection,body.OperationId,ct);}));
api.MapPost("/ppa/editor",(HttpContext c,PpaEditorRequest body)=>PpaOperation(c,body.SnapshotId,"ReadTrades",(connection,frame,ct)=>ppaStore.Editor(connection,frame!,body,ct),(connection,frame,ct)=>ppaStore.RecheckScope(connection,frame!,body.KnownAt,"trades",[body.TradeId],ppaStore.ReferenceFor(frame!),ct)));
api.MapPost("/ppa/legs",(HttpContext c,PpaLegsRequest body)=>PpaOperation(c,body.SnapshotId,"ReadTrades",(connection,frame,ct)=>ppaStore.Legs(connection,frame!,body,ct),(connection,frame,ct)=>ppaStore.RecheckScope(connection,frame!,body.KnownAt,"trades",[body.TradeId],ppaStore.ReferenceFor(frame!),ct)));
api.MapPost("/ppa/quantities",(HttpContext c,PpaQuantityRequest body)=>IntervalCurveRead(c,body.SnapshotId,(connection,pin,ct)=>ppaQuantities.Quantities(connection,pin,body,ct)));
api.MapPost("/ppa/capacity",(HttpContext c,PpaCapacityRequest body)=>IntervalCurveRead(c,body.SnapshotId,(connection,pin,ct)=>ppaQuantities.Capacity(connection,pin,body,ct)));
api.MapPost("/ppa/profile",(HttpContext c,PpaProfileRequest body)=>IntervalCurveRead(c,body.SnapshotId,(connection,pin,ct)=>ppaQuantities.Profile(connection,pin,body,ct)));
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
api.MapPost("/schedules/observation-contracts",(HttpContext c,ObservationContractPreviewRequest body)=>ScheduleOperation(c,(session,data)=>
    Results.Ok(ObservationContractPreview.Resolve(SchedulePreview.Compile(data,ScheduleSource(body.Curve)),body))));
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
api.MapPost("/schedules/resolve",(HttpContext c,ShortcutResolveRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    {
        var shortcut=session.Shortcuts.Find(Profile(session),body.Symbol);
        var sequence=shortcut.Sequence;
        if(sequence.Key.Revision.CatalogId!=database.Manifest.CatalogId)
            throw new MarketDataException("sequence_definition","The retained shortcut belongs to another catalogue.");
        await Pin(session).Bind(connection,database.Manifest.CatalogId,ct);
        var source=new CatalogueSource(sequence.Key.Revision.RevisionId,sequence.References.CatalogId,sequence.References.RevisionId);
        await MarketBundle.Require(connection,source.Apply(database.Manifest),ct);
        return Results.Ok(SchedulePreview.Result(shortcut,SchedulePreview.ParseAt(body.At)));
    },c.RequestAborted)));
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
        data.DefinitionRevision,data.ReferenceRevision,
        at.ToString("O",System.Globalization.CultureInfo.InvariantCulture),body.RollingCalendar,contracts);
}
api.MapPost("/schedules/contracts/preview",(HttpContext c,CurveDatesRequest body)=>ScheduleOperation(c,(session,data)=>Results.Ok(CompileCurve(session,data,body))));
api.MapPost("/schedules/contracts/keep",(HttpContext c,CurveDatesRequest body)=>ScheduleOperation(c,(session,data)=>
    Results.Ok(session.Dates.KeepCurve(Profile(session),CompileCurve(session,data,body)))));
var scheduleStore=new ScheduleStore(database.Manifest.CatalogId,database.Manifest.ReferenceCatalogId,database.Manifest.ReferenceRevisionId);
var knowledgeStore=new KnowledgeResolver(database.Manifest.CatalogId);
var contractDateStore=new ContractDateStore(database.Manifest.CatalogId,scheduleStore);
var curveDefinitionStore=new CurveDefinitionStore(database.Manifest.CatalogId,contractDateStore);
Task<IResult> DateOperation(HttpContext c,string permission,Func<PreviewSession,NpgsqlConnection,CancellationToken,Task<IResult>> work)=>Locked(c,session=>
    database.WithIdentity(Profile(session),permission,async(connection,ct)=>
    {
        if(session.MarketContext is {} selected)await MarketContextStore.Bind(connection,database.Manifest.CatalogId,selected,ct);
        return await work(session,connection,ct);
    },c.RequestAborted));
api.MapGet("/date-schedules",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    var library=ContextVersions.SnapshotId(c.Request.Query) is {} selectedSnapshot
        ?await scheduleReads.ContextLibrary(connection,await PartyView(connection,session,selectedSnapshot,ct),ct)
        :await scheduleReads.Library(connection,Pin(session),ct);
    return Results.Ok(new {library.KnownThrough,library.Versions,library.ContextVersions,library.ContextContracts,library.CanEdit,library.CanPublish,choices=DateWorkspace.Choices,
        library.ContractDefinitions,library.Calendars,examples=ScheduleExamples.Catalog});
},c.RequestAborted))));
api.MapPost("/date-schedules/preview",(HttpContext c,DateSchedulePreviewRequest body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(DateWorkspace.Compile(body.Definition??throw new TemporalException("schedule_definition","Supply an explicit schedule definition."),
        body.Definition is {Dates:not null}&&body.Calendar is null?null:await scheduleStore.Calendar(connection,body.Calendar,ct)))));
api.MapPost("/date-schedules/import",(HttpContext c,ScheduleImportRequest body)=>DateOperation(c,"WriteMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await ScheduleImports.Import(connection,scheduleStore,body,ct))));
api.MapPost("/date-schedules/save",(HttpContext c,ScheduleSaveRequest body)=>DateOperation(c,"WriteMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await scheduleStore.Save(connection,body,ct))));
api.MapPost("/date-schedules/publish",(HttpContext c,SchedulePublishRequest body)=>DateOperation(c,"SealMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await scheduleStore.Publish(connection,body,ct))));
api.MapPost("/date-schedules/context",(HttpContext c,ContextCaptureRequest body)=>Locked(c,async session=>
{
    var selected=await database.WithIdentity(Profile(session),"ReadMarketData",(connection,ct)=>WorkspaceReadContext.Resolve(connection,database.Manifest.CatalogId,body,ct),c.RequestAborted);
    session.Knowledge.Prepared=selected;
    return Results.Ok(selected);
}));
var recInventory=new RecInventoryStore(database);
Task<IResult> RecInventoryOperation(HttpContext c,string permission,
    Func<NpgsqlConnection,ScheduleContext?,CancellationToken,Task<object>> work)=>Locked(c,session=>
    database.WithIdentity(Profile(session),permission,async(connection,ct)=>
    {
        async Task Require()
        {
            if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct)||
               !await PreviewDatabase.HasPermission(connection,"ReadMarketData",ct))throw new PreviewDeniedException();
        }
        await Require();
        if(permission is "ConfigureRecInventory" or "RecordRecInventory")c.Items["rec_inventory_attempted"]=true;
        // The inventory owns its explicit business-date capture/bind. Binding
        // the session here as well would change the native retained-read contract.
        var result=await work(connection,session.MarketContext,ct);
        await Require();return Results.Ok(result);
    },c.RequestAborted));
api.MapPost("/rec/directory",(HttpContext c,RecInventoryDirectoryRequest body)=>RecInventoryOperation(c,"ReadRecInventory",
    (connection,selected,ct)=>recInventory.Directory(connection,body,selected,ct)));
api.MapPost("/rec/read",(HttpContext c,RecInventoryReadRequest body)=>RecInventoryOperation(c,"ReadRecInventory",
    (connection,selected,ct)=>recInventory.Read(connection,body,selected,ct)));
api.MapPost("/rec/account",(HttpContext c,RecInventoryAccountRequest body)=>RecInventoryOperation(c,"ConfigureRecInventory",
    async(connection,selected,ct)=>await recInventory.ConfigureAccount(connection,body,selected,ct)));
api.MapPost("/rec/destination",(HttpContext c,RecInventoryDestinationRequest body)=>RecInventoryOperation(c,"ConfigureRecInventory",
    async(connection,selected,ct)=>await recInventory.ConfigureDestination(connection,body,selected,ct)));
api.MapPost("/rec/record",(HttpContext c,RecInventoryWriteRequest body)=>RecInventoryOperation(c,"RecordRecInventory",
    async(connection,selected,ct)=>await recInventory.Record(connection,body,selected,ct)));
api.MapPost("/rec/status",(HttpContext c,RecInventoryStatusRequest body)=>RecInventoryOperation(c,"ReadRecInventory",
    async(connection,selected,ct)=>
    {
        if(selected is not null)throw new MarketDataException("historical_read_only","Recover certificate command outcomes from current working data.");
        return await recInventory.Status(connection,body,ct);
    }));
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
    Results.Ok(await contractDateStore.Resolve(connection,body,session.MarketContext,includeDrafts:false,ct))));
// D357: a date list is one labelled date per row; it saves and publishes through the contract routes above.
api.MapPost("/date-schedules/date-lists/preview",(HttpContext c,PublishedContractRequest body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
{
    var view=await ScheduleView(connection,body.Context,ct);
    return Results.Ok(ContractDateStore.CompileDateList(body,view.Versions,view.Context));
}));
api.MapPost("/date-schedules/date-lists/resolve",(HttpContext c,ContractDateResolveRequest body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(await contractDateStore.ResolveDateList(connection,body,session.MarketContext,includeDrafts:false,ct))));
var contractDateMaterialization=new ContractDateMaterialization(database.Manifest.CatalogId,contractDateStore);
api.MapPost("/date-schedules/contracts/materialize",(HttpContext c,ContractMaterializationRequest body)=>DateOperation(c,"WriteMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await contractDateMaterialization.Materialize(connection,body,ct))));
api.MapPost("/date-schedules/contracts/admissions",(HttpContext c,ContractAdmissionRead body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(await contractDateMaterialization.Read(connection,body,session.MarketContext,ct))));
// D318: the curve editor's choices, under the page's pin, and a read-only preview of a definition's rolling contract window. A preview
// reads its reference terms and resolves its mapping in the one exact context of the header's selected close (SelectedClose.Pin).
api.MapGet("/curve-definitions/catalog",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    var data=await marketReads.Data(connection,Pin(session),ct,ImmutableArray<Guid>.Empty,ReferenceReadScope.AllVisible);
    return Results.Ok(new{choices=CurveDefinitions.Choices,maximumContracts=CurveDefinitions.MaximumContracts,references=CurveReferenceTerms.From(data.References),data.ReferenceRevision});
},c.RequestAborted))));
// D318/D358: a definition's preview at the header's selected close, in that close's one exact context. Its mapping is the stored version
// it names (an author's draft included), or the rows being edited, compiled in memory and never saved here. Authoring validates in the
// current workspace at current knowledge on the close's date, captured without binding it so the request can still write.
async Task<(object Preview,ScheduleContext Context,ReferenceRevision Reference)> CurvePreview(NpgsqlConnection connection,PreviewSession session,Guid? snapshotId,
    CurveDefinitionDraft? definition,CurveMappingEdit? edit,bool authoring,CancellationToken ct)
{
    if(definition is null)throw new MarketDataException("curve_definition","Supply a complete curve definition.");
    var window=CurveDefinitions.Window(definition);
    if(edit is not null&&edit.Id!=window.MappingId)throw new MarketDataException("curve_definition","Edit the rows of the mapping this curve names.");
    var pin=Pin(session,snapshotId);
    await pin.Bind(connection,database.Manifest.CatalogId,ct);
    if(authoring&&pin.Context is not null)throw new MarketDataException("curve_definition_historical","Author and publish curve definitions in the current workspace.");
    var context=authoring?await SelectedClose.Capture(connection,database,pin.RequireFrame(),ct):(pin=await SelectedClose.Pin(connection,database,pin,ct)).Context!;
    var data=await database.Read(connection,ct,observationIds:[],referenceScope:ReferenceReadScope.AllVisible);
    var terms=CurveReferenceTerms.From(data.References);
    async Task<(ImmutableArray<ScheduleVersion> Versions,PublishedContractRequest Rows)> Edited()=>
        ((await ScheduleView(connection,context,ct)).Versions,new(edit!.Code,edit.Name,context,edit.Contracts,edit.Dates));
    if(definition.ContractKind=="interest_rate")
    {
        // t0 is the close's date: the date list's context date and the rate engine's valuation UTC date must agree, never be chosen between.
        var valuation=DateOnly.FromDateTime(pin.RequireFrame().At.UtcDateTime);
        if(valuation!=DateWorkspace.Date(context.EffectiveOn))
            throw new MarketDataException("rate_context",$"This close values on {valuation:yyyy-MM-dd} UTC but belongs to business date {context.EffectiveOn}; choose a close whose dates agree.");
        BoundDateList tenors;
        if(edit is null)tenors=await contractDateStore.ResolveDateList(connection,new(window.MappingId,window.MappingVersion,context),context,includeDrafts:true,ct);
        else {var (versions,rows)=await Edited();tenors=ContractDateStore.CompileDateList(rows,versions,context);}
        return (CurveDefinitions.PreviewRates(definition,terms,tenors,valuation),context,data.ReferenceRevision);
    }
    BoundContractDefinition mapping;
    if(edit is null)mapping=await contractDateStore.Resolve(connection,new(window.MappingId,window.MappingVersion,context),context,includeDrafts:true,ct);
    else {var (versions,rows)=await Edited();mapping=ContractDateStore.Compile(rows,versions,context);}
    return (CurveDefinitions.Preview(definition,terms,mapping),context,data.ReferenceRevision);
}
api.MapPost("/curve-definitions/preview",(HttpContext c,CurveDefinitionPreviewRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    Results.Ok((await CurvePreview(connection,session,body.SnapshotId,body.Definition,body.Mapping,authoring:false,ct)).Preview),c.RequestAborted)));
// D358: saved curve definitions. Readers see published versions; authors and publishers also see drafts.
api.MapGet("/curve-definitions",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    if(session.MarketContext is {} selected)await MarketContextStore.Bind(connection,database.Manifest.CatalogId,selected,ct);
    return Results.Ok(new{definitions=await curveDefinitionStore.Read(connection,ct),canEdit=await PreviewDatabase.HasPermission(connection,"WriteMarketDefinitions",ct),
        canPublish=await PreviewDatabase.HasPermission(connection,"SealMarketDefinitions",ct)});
},c.RequestAborted))));
api.MapPost("/curve-definitions/save",(HttpContext c,CurveDefinitionSaveRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"WriteMarketDefinitions",async(connection,ct)=>
    Results.Ok(await curveDefinitionStore.Save(connection,body,async()=>
    {
        var (_,context,reference)=await CurvePreview(connection,session,body.SnapshotId,body.Definition,body.Mapping,authoring:true,ct);
        return (context,reference);
    },ct)),c.RequestAborted)));
api.MapPost("/curve-definitions/publish",(HttpContext c,CurveDefinitionPublishRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"SealMarketDefinitions",async(connection,ct)=>
    Results.Ok(await curveDefinitionStore.Publish(connection,body,ct)),c.RequestAborted)));
// D359: admits a published interest-rate definition into a draft definition revision, one stable series per tenor.
var rateCurveAdmission=new RateCurveAdmission(database.Manifest.CatalogId);
api.MapPost("/curve-definitions/admit",(HttpContext c,RateCurveAdmissionRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"WriteMarketDefinitions",async(connection,ct)=>
    Results.Ok(await rateCurveAdmission.Admit(connection,body,ct)),c.RequestAborted)));
// D362: admits a published futures or forward definition into a draft definition revision, at the window it resolves at the named close.
var contractCurveAdmission=new ContractCurveAdmission(database.Manifest.CatalogId,curveDefinitionStore,contractDateMaterialization);
api.MapPost("/curve-definitions/admit-contracts",(HttpContext c,ContractCurveAdmissionRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"WriteMarketDefinitions",async(connection,ct)=>
    Results.Ok(await contractCurveAdmission.Admit(connection,body,async definition=>
    {
        var (preview,_,reference)=await CurvePreview(connection,session,body.SnapshotId,definition,null,authoring:true,ct);
        return ((CurveDefinitionPreview)preview,reference);
    },ct)),c.RequestAborted)));
// Selects a historical view: binds the context, under the explicit latest-known correction policy, and shows its frames.
async Task<IResult> SelectContext(HttpContext c,PreviewSession session,string? selection,Func<NpgsqlConnection,CancellationToken,Task<KnowledgeSnapshot>> resolve,Guid? selectedSnapshot=null,string? framePolicy=null)
{
    if(selection!="latest_correction")throw new MarketDataException("selection_policy","Choose the explicit latest-known correction policy.");
    if(framePolicy is not null&&framePolicy!=PreviewSession.ContextFramePolicy||selectedSnapshot is not null&&framePolicy is null)
        throw new MarketDataException("selection_policy","Select the installed contextual frame-identity policy explicitly.");
    var previous=selectedSnapshot is {} selected?Frame(session,selected):null;
    var snapshot=await database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    {
        var captured=await resolve(connection,ct);
        await MarketContextStore.Bind(connection,database.Manifest.CatalogId,captured.Context,ct);
        var data=await database.Read(connection,ct,observationIds:[..session.InitialFrames.SelectMany(id=>session.Frames[id].ObservationIds).Distinct()]);
        var aliases=session.SelectContextAliases(data,captured.Context);
        var stored=await MarketCloses.Frames(connection,database.Manifest.CatalogId,captured.Context.ContextId,ct,session.DurableLimit(aliases));
        return (captured,plan:session.PlanFrames(stored,captured.Context,aliases));
    },c.RequestAborted);
    session.StopLive();session.ApplyFrames(snapshot.plan);
    session.MarketContext=snapshot.captured.Context;
    session.Knowledge.Applied=snapshot.captured;
    return Results.Ok(new{snapshot.captured.Context,snapshot.captured.CoverageFrom,snapshot.captured.CertifiedThrough,snapshot.captured.RestoreKey,frameSelectionPolicy=framePolicy,snapshotId=framePolicy is null?null:session.ContextFrame(previous,snapshot.plan,snapshot.captured.Context)});
}
var catalogueStore=new CatalogueStore(database.Manifest.CatalogId);
api.MapGet("/catalogue",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    await Pin(session).Bind(connection,database.Manifest.CatalogId,ct);
    return Results.Ok(await catalogueStore.ReadBound(connection,ct));
},c.RequestAborted))));
api.MapPost("/catalogue/publish",(HttpContext c,CataloguePublishRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"SealMarketDefinitions",async(connection,ct)=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Publish catalogue membership in current knowledge.");
    return Results.Ok(await catalogueStore.Publish(connection,body,ct));
},c.RequestAborted)));
api.MapPost("/market/context",(HttpContext c,MarketContextRequest body)=>Locked(c,session=>
    SelectContext(c,session,body.Selection,(connection,ct)=>session.Knowledge.Select(connection,database.Manifest.CatalogId,body,ct),body.SnapshotId,body.FrameSelectionPolicy)));
api.MapPost("/market/current",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,async session=>
{
    await database.WithIdentity(Profile(session),"ReadMarketData",(_,_)=>Task.FromResult(true),c.RequestAborted);
    session.StopLive();session.ForgetDiscoveredFrames();session.MarketContext=null;session.Knowledge.Applied=null;
    await RefreshFrames(session,c.RequestAborted);
    return Results.Ok(new {mode="explicit_input_pins"});
})));
api.MapPost("/market/live",(HttpContext c,LiveSelectRequest body)=>Locked(c,async session=>
{
    var seed=Frame(session,body.SnapshotId);
    var resolved=await liveContexts.Resolve(Profile(session),LiveSelection.Start(seed,body.Policy),c.RequestAborted);
    session.SelectLive(resolved);
    return Results.Ok(new{live=resolved.Summary(),context=resolved.Context,frame=new NavigationFrame(resolved.Frame!.Id,resolved.Frame.Name,resolved.Frame.At,resolved.Frame.BusinessDate)});
}));
api.MapGet("/market/live/events",(HttpContext c,Guid selection)=>liveContexts.Serve(c,()=>Session(c),selection,app.Lifetime.ApplicationStopping));
api.MapPost("/market/live/pause",(HttpContext c,LivePauseRequest body)=>Locked(c,async session=>
{
    if(session.Live?.Id!=body.Selection)throw PinnedContext.Missing();
    var pin=session.LivePin(body.SnapshotId)??throw PinnedContext.Missing();
    await database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    {
        await pin.Bind(connection,database.Manifest.CatalogId,ct);
        await MarketBundle.RequireObservationPins(connection,database.Manifest,pin.RequireFrame().ObservationIds,ct);return true;
    },c.RequestAborted);
    session.PauseLive(pin);return Results.Ok(new{context=pin.Context,frame=new NavigationFrame(pin.RequireFrame().Id,pin.RequireFrame().Name,pin.RequireFrame().At,pin.RequireFrame().BusinessDate)});
}));
Func<WorkspaceRead,bool,object> CaptureWorkspaceProjection(PreviewSession session,PinnedContext? exact=null,bool frameSummaries=false)
{

    var context=exact is null?session.MarketContext:exact.Context;
    var prior=exact?.Frame is {} selected&&selected.ContextId!=session.MarketContext?.ContextId?selected:null;
    var profile=session.Profile;var frames=prior is null?session.VisibleFrames:[prior];
    var frameCatalog=session.FrameCatalog;var initialFrameIds=prior is not null?[prior.Id]:session.InitialFrameIds.ToArray();
    return (read,catalogOnly)=>
    {
        var data=read.Data;var referenceSources=read.ReferenceSources;var references=read.References(catalogOnly);
    return new {referenceSources,unitCatalog=read.Units,unitCatalogError=read.Units is null?"unit_catalog":null,referenceDisplay=read.ReferenceDisplay,unitReferenceDisplay=read.UnitReferenceDisplay,context,profile,example=database.Manifest.Example,futuresConvention=database.Manifest.FuturesConvention,curveNames=read.CurveNames,powerCurveCodes=PowerPreview.CurveCodes(data),definitions=data.Definitions,references,referenceScope=catalogOnly?"definition_closure":"all_visible",observations=catalogOnly?ImmutableArray<JsonElement>.Empty:data.Observations,observationScope=catalogOnly?"on_demand":"all_visible",
        // Each offered frame names its recorded close; one without a close is a working snapshot (D334).
        frames=frameSummaries?(object)frames.Select(f=>new NavigationFrame(f.Id,f.Name,f.At,f.BusinessDate,f.OriginCloseId,f.Close)).ToArray():frames,
        frameCatalog,initialFrameIds};
    };
}
Task<IResult> Workspace(HttpContext c,bool catalogOnly)=>Locked(c,async session=>
{
    var detail=c.Request.Query["frameDetail"];
    var frameSummaries=catalogOnly&&detail.Count==1&&detail[0]=="summary";
    if(detail.Count!=0&&!frameSummaries)throw new AsyncReadException(422,"workspace_frame_detail","Choose the supported catalog frame detail.");

    var read=await database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    {
        var workspace=await marketReads.Workspace(connection,Pin(session,ContextVersions.SnapshotId(c.Request.Query)),catalogOnly,ct,c.Request.Query["curve"].FirstOrDefault());
        return(workspace,plan:await DiscoverFrames(connection,session,ct));
    },c.RequestAborted);
    session.ApplyFrames(read.plan);
    return Results.Ok(CaptureWorkspaceProjection(session,frameSummaries:frameSummaries)(read.workspace,catalogOnly));
});
api.MapGet("/workspace",(Func<HttpContext,Task<IResult>>)(c=>Workspace(c,false)));
api.MapGet("/workspace/catalog",(Func<HttpContext,Task<IResult>>)(c=>Workspace(c,true)));
api.MapGet("/workspace/navigation",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,async session=>
{
    var read=await database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    {
        var navigation=await marketReads.Navigation(connection,Pin(session,ContextVersions.CatalogueSnapshot(c.Request.Query)),ct);
        // Keep inspection available when authoring is unavailable, with the exact reason.
        var configurations=navigation.Configurations;
        return(navigation,configurations,plan:await DiscoverFrames(connection,session,ct,reuseRecordedInputs:true));
    },c.RequestAborted);
    session.ApplyFrames(read.plan);var frames=session.VisibleFrames;
    return Results.Ok(new MarketNavigation(Profile(session),session.MarketContext,read.navigation.DefinitionRevision,read.navigation.ReferenceRevision,
        database.Manifest.FuturesConvention,database.Manifest.Example,
        [..session.InitialFrameIds],
        [..frames.Select(f=>new NavigationFrame(f.Id,f.Name,f.At,f.BusinessDate,f.OriginCloseId,f.Close))],read.navigation.CurveNames,read.navigation.Curves,read.navigation.ReferenceDisplay){FrameCatalog=session.FrameCatalog,Live=session.Live?.Summary(),CurveConfigurations=read.configurations,RestoreKey=session.Knowledge.Applied?.Context==session.MarketContext?session.Knowledge.Applied?.RestoreKey:null});
})));
api.MapGet("/reference/catalog",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    var requestedOffset=c.Request.Query["offset"].ToString();
    if(!int.TryParse(c.Request.Query.ContainsKey("offset")?requestedOffset:"0",System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out var offset))
        throw new MarketDataException("reference_page",$"Choose an offset from 0 to {PreviewDatabase.ReferenceMaximumOffset}.");
    var table=c.Request.Query["table"].ToString();
    var snapshot=ContextVersions.SnapshotId(c.Request.Query);
    Guid? definitionRevisionId=null;
    if(c.Request.Query.TryGetValue("definitionRevisionId",out var requestedDefinition))
    {
        if(table!="futures_option_products"||snapshot is null||requestedDefinition.Count!=1||
            !Guid.TryParse(requestedDefinition[0],out var exact)||exact==Guid.Empty)
            throw new MarketDataException("reference_page","Choose a snapshot and an exact definition for option products.");
        definitionRevisionId=exact;
    }
    var page=await marketReads.References(connection,Pin(session,snapshot),table,offset,c.Request.Query["search"].ToString(),
        c.Request.Query.TryGetValue("code",out var code)?code.ToString():null,ct,definitionRevisionId);
    var referenceDisplay=await ReferenceEditors.Display(connection,page.ReferenceRevision,ct);
    return Results.Ok(new {page.Table,page.Rows,page.References,page.Offset,page.PageSize,page.MaximumOffset,page.HasMore,page.Search,page.ReferenceRevision,page.DefinitionRevisionId,referenceDisplay,
        tables=PreviewDatabase.ReferenceTables,referenceScope="selected_table",context=session.MarketContext,profile=session.Profile});
},c.RequestAborted))));
api.MapGet("/reference/search",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    var found=await marketReads.SearchReferences(connection,Pin(session),c.Request.Query["q"].ToString(),ct);
    return Results.Ok(new {found.Query,found.Hits,found.Truncated,context=session.MarketContext,profile=session.Profile});
},c.RequestAborted))));
api.MapGet("/reference/futures-product",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    var read=await marketReads.FuturesProduct(connection,Pin(session),c.Request.Query["code"].ToString(),ct);
    var referenceDisplay=await ReferenceEditors.Display(connection,read.ReferenceRevision,ct);
    return Results.Ok(new {read.Product,read.Series,read.Milestones,read.ExecutionPolicies,read.References,read.ReferenceRevision,read.DefinitionRevisionId,referenceDisplay,context=session.MarketContext,profile=session.Profile});
},c.RequestAborted))));
api.MapGet("/reference/editor",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    await Pin(session).Bind(connection,database.Manifest.CatalogId,ct);
    var code=c.Request.Query.TryGetValue("code",out var selected)?selected.ToString():null;
    return Results.Ok(await ReferenceEditors.Read(connection,database,c.Request.Query["table"].ToString(),ct,code));
},c.RequestAborted))));
Task<IResult> ReferenceMutation(HttpContext c,string permission,Func<NpgsqlConnection,CancellationToken,Task<JsonElement>> work)=>Locked(c,session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before committing reference display changes.");
    return database.WithIdentity<IResult>(Profile(session),permission,async(connection,ct)=>
    {
        c.Items["reference_attempted"]=true;
        return Results.Ok(await work(connection,ct));
    },c.RequestAborted);
});
var crudeSetupEditor=new CrudeSetupEditor(database);
api.MapGet("/crude-setup/catalog",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity<IResult>(Profile(session),"ReadMarketData",async(connection,ct)=>
{
 var view=ContextVersions.SnapshotId(c.Request.Query) is {} snapshotId?await PartyView(connection,session,snapshotId,ct):null;
 if(view is null&&session.MarketContext is not null)throw new MarketDataException("historical_read_only","Select a dated view to inspect crude setup.");
 return Results.Ok(await crudeSetupEditor.CatalogRead(connection,ct,view,ContextVersions.OptionalId(c.Request.Query,"proposalId"),c.Features.Get<AssistantInvocation>() is not null));
},c.RequestAborted))));
api.MapPost("/crude-setup/save",(HttpContext c,CrudeSetupSaveRequest body)=>ReferenceMutation(c,"WriteMarketDefinitions",(connection,ct)=>crudeSetupEditor.Save(connection,body,ct)));
api.MapPost("/crude-setup/validate",(HttpContext c,CrudeSetupVersionRequest body)=>Locked(c,session=>database.WithIdentity<IResult>(Profile(session),"ReadMarketData",async(connection,ct)=>
{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data to validate crude delivery setup.");
 return Results.Ok(await crudeSetupEditor.Validate(connection,body.SetupId,ct));
},c.RequestAborted)));
api.MapPost("/crude-setup/publish",(HttpContext c,CrudeSetupPublishRequest body)=>ReferenceMutation(c,"SealMarketDefinitions",async(connection,ct)=>
{
 _=await crudeSetupEditor.Validate(connection,body.SetupId,ct);
 return await crudeSetupEditor.Publish(connection,body,ct);
}));
api.MapPost("/crude-setup/preview",(HttpContext c,CrudeSetupPreviewRequest body)=>Locked(c,session=>database.WithIdentity<IResult>(Profile(session),"ReadMarketData",async(connection,ct)=>
{
 if(body.KnowledgeMode is "context" or "retained")
 {
  var view=await PartyView(connection,session,body.SnapshotId,ct);
  if(body.KnowledgeMode=="retained"&&(session.MarketContext is null||body.ContextId!=session.MarketContext.ContextId)||body.KnowledgeMode=="context"&&body.ContextId is not null)throw PinnedContext.Missing();
  await view.Bind(connection,database.Manifest.CatalogId,ct);
  if(!(await CrudeSetupEditor.CurrentSetups(connection,database.Manifest.CatalogId,ct)).Contains(body.SetupId))
   throw new MarketDataException("crude_setup_context","Choose the published delivery binding resolved by the header context.");
  return Results.Ok(await crudeSetupEditor.Preview(connection,view.CalculationFrame,body,ct));
 }
 var pin=Pin(session,body.SnapshotId);
 if(body.KnowledgeMode!="current"||pin.Context is not null||body.ContextId is not null)throw new MarketDataException("crude_setup_input","Choose current working knowledge for delivery-mark preview.");
 var frame=pin.RequireFrame();
 var context=(await knowledgeStore.Capture(connection,new(frame.Time.Date.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),null,"current"),ct)).Context;
 await PinnedContext.BindExact(connection,database.Manifest.CatalogId,context,ct);
 return Results.Ok(await crudeSetupEditor.Preview(connection,frame with{ContextId=context.ContextId},body,ct));
},c.RequestAborted)));
var gasSetupEditor=new GasSetupEditor(database);
api.MapGet("/gas-setup/catalog",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity<IResult>(Profile(session),"ReadMarketData",async(connection,ct)=>
{
 var view=ContextVersions.SnapshotId(c.Request.Query) is {} snapshotId?await PartyView(connection,session,snapshotId,ct):null;
 if(view is null&&session.MarketContext is not null)throw new MarketDataException("historical_read_only","Select a dated view to inspect gas setup.");
 return Results.Ok(await gasSetupEditor.Catalog(connection,ct,view,ContextVersions.OptionalId(c.Request.Query,"proposalId"),c.Features.Get<AssistantInvocation>() is not null));
},c.RequestAborted))));
api.MapPost("/gas-setup/save",(HttpContext c,GasSetupSaveRequest body)=>ReferenceMutation(c,"WriteMarketDefinitions",(connection,ct)=>gasSetupEditor.Save(connection,body,ct)));
api.MapPost("/gas-setup/validate",(HttpContext c,GasSetupValidateRequest body)=>ReferenceMutation(c,"ReadMarketData",(connection,ct)=>gasSetupEditor.Validate(connection,body,ct)));
api.MapPost("/gas-setup/publish",(HttpContext c,GasSetupPublishRequest body)=>ReferenceMutation(c,"SealMarketDefinitions",(connection,ct)=>gasSetupEditor.Publish(connection,body,ct)));
// The route holds Write; the apply function enforces Seal in the same transaction.
api.MapPost("/reference/commit",(HttpContext c,ReferenceCommitRequest body)=>ReferenceMutation(c,"WriteReferenceData",(connection,ct)=>ReferenceEditors.Commit(connection,database,body,ct)));
api.MapGet("/market/contract-display",()=>Results.Ok(new{version=CurveDisplay.Version,choices=new[]{
    new{value=CurveDisplay.All,label="All recorded contracts · show status"},
    new{value=CurveDisplay.Live,label="Live contracts at selected close"}}}));
api.MapPost("/market/input-curve-shapes",(HttpContext c,CurveInspectionsRequest body)=>Locked(c,async session=>
{
    var pin=Pin(session,body.SnapshotId);
    var curves=await database.WithIdentity(Profile(session),"ReadMarketData",(connection,ct)=>marketReads.InspectCurveShapes(connection,pin,body.Curves,ct,body.ContractDisplay),c.RequestAborted);
    return pin.Context is {} context?Results.Ok(new{curves,context}):Results.Ok(new{curves});
}));
api.MapGet("/analytics/overlays",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",(_,_)=>
    Task.FromResult(Results.Ok(new {policy=OpenEtrm.Shared.Analytics.Overlays.Policy,overlays=OpenEtrm.Shared.Analytics.Overlays.Catalog})),c.RequestAborted))));
api.MapPost("/corrections",(HttpContext c,CorrectionRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to current input editing before changing observations.");
    var frame=Frame(session,body.SnapshotId);
    // The correction replaces the pin in whichever bundle of this snapshot holds it (D172).
    var held=frame.ObservationIds.Contains(body.ObservationId)?[]:(database.Manifest.InputMarkets??[])
        .Select(m=>(m.RevisionId,Pins:database.InputMarketPins(frame,m)??[])).Where(m=>m.Pins.Contains(body.ObservationId)).ToArray();
    if (held.Length>1) throw new MarketDataException("ambiguous_reference","A required exact reference is missing or ambiguous.");
    if (!frame.ObservationIds.Contains(body.ObservationId)&&held.Length==0) throw new MarketDataException("snapshot_input","Only an explicitly selected observation can be corrected here.");
    if (session.Frames.Count>=128&&!(c.Features.Get<AssistantInvocation>()?.ResultIdentity is {} replay&&session.Frames.ContainsKey(replay))) throw new MarketDataException("preview_limit","Restart the disposable preview to create more saved selections.");
    if (string.IsNullOrWhiteSpace(body.Value)) throw new MarketDataException("missing_value","Enter an exact decimal value.");
    c.Items["correction_attempted"]=true;
    var id=await database.WithIdentity(Profile(session),"ImportObservations",async(connection,ct)=>{
        var selected=held.Length==1?held[0].RevisionId:(await MarketInputSelection.RootSource(database,connection,frame,ct)).DefinitionRevisionId;
        return await database.Correct(connection,selected,body.ObservationId,body.Value,ct);
    },c.RequestAborted);
    var updated=held.Length==0?frame.SelectRevision(body.ObservationId,id,"Edited inputs")
        :frame.SelectSwapMarketRevision(held[0].RevisionId,held[0].Pins,body.ObservationId,id,"Edited inputs");
    if(c.Features.Get<AssistantInvocation>()?.ResultIdentity is {} identity)updated=updated with{Id=identity};
    if(c.Features.Get<AssistantInvocation>() is not null)session.Frames.TryAdd(updated.Id,updated);else session.Frames.Add(updated.Id,updated);
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
        var data=await database.Read(connection,ct,observationIds:[..frame.ObservationIds.Append(body.ObservationId).Distinct()]);
        if(!data.HasObservation(body.ObservationId))
            foreach(var market in database.Manifest.InputMarkets??[])
            {
                if(database.InputMarketPins(frame,market) is not {} pins||!await database.InputMarketAvailable(connection,market,ct))continue;
                var bundle=await database.Read(connection,ct,market.RevisionId,observationIds:[..pins.Append(body.ObservationId).Distinct()]);
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
async Task BindDeskRead(NpgsqlConnection connection,PreviewSession session,Guid snapshot,CancellationToken ct)
{
    var frame=Frame(session,snapshot);
    if(session.MarketContext is not null){
        var view=await PartyView(connection,session,frame.Id,ct);
        await view.Bind(connection,database.Manifest.CatalogId,ct);
        await TradingContext.Bind(connection,database.Manifest.CatalogId,frame,view.Context,ct);
    }
}
api.MapPost("/desk/directory",(HttpContext c,DeskReadRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTradeParties",async(connection,ct)=>{
        await DeskAdmission.Capture(database,connection,"ReadTradeParties",ct);
        await BindDeskRead(connection,session,body.SnapshotId,ct);
        return Results.Ok(await LifecycleNative.Read(connection,"SELECT desk.directory($1)::text",ct,database.Manifest.CatalogId));
    },c.RequestAborted)));
api.MapPost("/desk/document",(HttpContext c,DeskReadRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadAgreementDocuments",async(connection,ct)=>{
        await DeskAdmission.Capture(database,connection,"ReadAgreementDocuments",ct);
        await BindDeskRead(connection,session,body.SnapshotId,ct);
        await using var q=ScheduleStore.Command(connection,"SELECT filename,content FROM desk.document($1,$2)",database.Manifest.CatalogId,body.Id??Guid.Empty);
        await using var row=await q.ExecuteReaderAsync(ct);
        c.Response.Headers.CacheControl="no-store";
        return await row.ReadAsync(ct)?Results.Ok(new {filename=row.GetString(0),pdf=row.GetFieldValue<byte[]>(1)}):Results.NotFound();
    },c.RequestAborted)));
foreach(var kind in new[]{"agreement","credit","confirmation"}){
    var permission=BilateralDesk.Permission(kind);
    api.MapPost("/desk/"+kind,(HttpContext c,DeskCommand body)=>Locked(c,session=>{
        if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before recording a desk decision.");
        if(body.Record.ValueKind!=JsonValueKind.Object||!body.Record.TryGetProperty("kind",out var type)||type.ValueKind!=JsonValueKind.String||type.GetString()!=kind)
            throw new MarketDataException("desk_terms","The record must identify the selected operation.");
        return database.WithIdentity(Profile(session),permission,async(connection,ct)=>{
            await DeskAdmission.Capture(database,connection,permission,ct);
            return Results.Ok(await LifecycleNative.Read(connection,"SELECT desk.record($1,$2,$3::jsonb)::text",ct,database.Manifest.CatalogId,body.OperationId,body.Record.GetRawText()));
        },c.RequestAborted);
    }));
}
api.MapPost("/desk/eligibility",(HttpContext c,DeskEligibility body)=>Locked(c,session=>{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Select working data to check a proposed booking.");
    return database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await LifecycleNative.Read(connection,
        "SELECT desk.eligibility($1,$2,$3,$4,statement_timestamp())::text",ct,database.Manifest.CatalogId,body.LegalEntityId,body.CounterpartyId,body.Product)),c.RequestAborted);
}));
api.MapPost("/desk/choose",(HttpContext c,DeskChoice body)=>Locked(c,session=>{
    var profile=Profile(session);
    if(body.ExpectedProfile is {} expected&&profile!=expected)
        throw new MarketDataException("desk_profile_changed","The account changed. Open the booking again.");
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Select working data to choose a booking agreement.");
    return database.WithIdentity(profile,"ReadTrades",async(connection,ct)=>{
        var permission=await PreviewDatabase.HasPermission(connection,"BookTrade",ct)?"BookTrade":"AmendTrade";
        if(!await PreviewDatabase.HasPermission(connection,permission,ct))throw new PreviewDeniedException();
        await DeskAdmission.Capture(database,connection,permission,ct);
        return Results.Ok(await LifecycleNative.Read(connection,"SELECT desk.choose($1,$2,$3)::text",ct,database.Manifest.CatalogId,body.OperationId,body.AgreementId));
    },c.RequestAborted);
}));
api.MapPost("/parties/catalog",(HttpContext c,PartyReadRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTradeParties",async(connection,ct)=>
        Results.Ok(await partyReads.Catalog(connection,await PartyView(connection,session,body.SnapshotId,ct),body.Kind,ct)),c.RequestAborted)));
api.MapPost("/parties/save",(HttpContext c,PartySaveRequest body)=>Locked(c,session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before editing party records.");
    c.Items["party_attempted"]=true;
    return database.WithIdentity(Profile(session),"WriteTradeParties",async(connection,ct)=>Results.Ok(await PartyEditors.Save(connection,database.Manifest.CatalogId,body,ct)),c.RequestAborted);
}));
api.MapGet("/settings/operating-policies",async Task<IResult>(HttpContext c)=>await Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>Results.Ok(await OperatingPolicies.Directory(connection,ct)),c.RequestAborted)));
api.MapPost("/settings/operating-policies",(HttpContext c,SaveOperatingPolicyRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),OperatingPolicies.PermissionFor(body.Kind),
    async(connection,ct)=>Results.Ok(await OperatingPolicies.Save(connection,body,ct)),c.RequestAborted,OperatingPolicies.AdmissionFor(body.Kind))));
api.MapGet("/settings/preferences",async Task<IResult>(HttpContext c)=>await Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>Results.Ok(await UserPreferences.Read(connection,turnScope,ct)),c.RequestAborted)));
api.MapPost("/settings/preferences",(HttpContext c,SaveUserPreferencesRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>Results.Ok(await UserPreferences.Save(connection,turnScope,body,ct)),c.RequestAborted)));

api.MapGet("/table-views/blotter",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await TableViews.Read(connection,ct)),c.RequestAborted))));
api.MapPost("/table-views/blotter",(HttpContext c,SaveTableView body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await TableViews.Save(connection,body,ct)),c.RequestAborted)));
api.MapPost("/table-views/blotter/delete",(HttpContext c,DeleteTableView body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>{await TableViews.Delete(connection,body,ct);return Results.Ok(new {deleted=true});},c.RequestAborted)));
api.MapGet("/dashboard/layout",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>Results.Ok(await DashboardLayouts.Read(connection,ct)),c.RequestAborted))));
api.MapPost("/dashboard/layout",(HttpContext c,SaveDashboardLayout body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>Results.Ok(await DashboardLayouts.Save(connection,body,ct)),c.RequestAborted)));
var futuresStore=new FuturesStore(database);
var lngStore=new PhysicalLngStore(database);
// Trading pages name their knowledge: current knowledge, captured now at the working frame's date;
// a context captured earlier, re-read over a working frame; or the selected historical view's own.
Task<TradingView> TradeView(NpgsqlConnection connection,PreviewSession session,FuturesReadRequest body,InputFrame frame,CancellationToken ct)
    =>TradingReadSelection.Capture(session,body,frame).Resolve(connection,knowledgeStore,body,ct);
// How trading reads publish their results: camel-case members, exact decimals as exact text.
var tradingJson=new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,Converters={new ExactDecimalJsonConverter()}};
api.MapPost("/physical/power-trades/get",(HttpContext c,StructuredPowerTradeReadRequest body)=>
{
    if(body.Selected)
    {
        var selected=body.Selection();
        AsyncReadVisibilityProof? exactVisibility=null;
        return TradingRead(c,body.Context!,
            (connection,view,ct)=>OperatingSideReadProjection.Run(()=>tradeReads.ReadBound(connection,view,async()=>await operatingTrades.ReadSelected(connection,view,selected,ct),ct),proof=>{exactVisibility=proof;return Task.CompletedTask;}),
            expectedConvention:TradingBlotter.Convention,sourceAdmission:(connection,ct)=>OperatingSideReadAdmission.Capture(connection,database,ct),
            publicationCheck:(connection,_,ct)=>(exactVisibility??throw new PreviewDeniedException()).Require(connection,ct));
    }
    return PhysicalPowerOperation(c,body.SnapshotId,"ReadTrades",false,async(connection,_,frame,_,_,ct)=>
        Results.Ok(await operatingTrades.ReadExact(connection,frame,body.TradeId,ct)));
});

Task<object> ReadGasMarket(NpgsqlConnection connection,TradingView view,Guid? trade,Func<PreviewData,InputFrame,Task<object>> read,CancellationToken ct)=>
    tradeReads.ReadBound(connection,view,async()=>{
        var selected=await PhysicalMarketSelection.Gas.Read(database,connection,view.CalculationFrame,ct,trade);
        return await read(selected.Data,selected.Frame);
    },ct);

// Trading pages without a read operation of their own run their store's read on the shared trading read path.
Task<IResult> FuturesRead(HttpContext c,FuturesReadRequest body,Func<NpgsqlConnection,PreviewData,InputFrame,CancellationToken,Task<object>> read,bool scenario=false,Guid? headTrade=null,bool amendmentPreview=false,string? expectedConvention=null,bool dealRisk=false,Guid? gasTrade=null,Guid? crudeTrade=null,Guid? crudeSetup=null,bool gasReferenceChoices=false,Guid? lngTrade=null)
{
    CatalogueSource? gasChoiceSource=null;
    return TradingRead(c,body,(connection,view,ct)=>gasReferenceChoices&&view.Working&&expectedConvention==PhysicalGasStore.Convention
        ?tradeReads.ReadBound(connection,view,async()=>{
            var source=(await new CatalogueStore(database.Manifest.CatalogId).ReadBound(connection,ct,view.CalculationFrame.Time.Date)).RequireRole("gas");
            gasChoiceSource=source;
            var data=await database.Read(connection,ct,ppaBundle:source,observationIds:[],observationScope:ObservationReadScope.ExactPins);
            return await read(connection,data,view.CalculationFrame,ct);
        },ct)
        :expectedConvention==PhysicalLngStore.Convention
        ?tradeReads.ReadBound(connection,view,async()=>{
            var selected=await PhysicalMarketSelection.Lng.Read(database,connection,view.CalculationFrame,ct,lngTrade);
            return await read(connection,selected.Data,selected.Frame,ct);
        },ct)
        :expectedConvention==PhysicalCrudeStore.Convention
        ?tradeReads.ReadBound(connection,view,async()=>{
            var selected=await PhysicalMarketSelection.Crude.Read(database,connection,view.CalculationFrame,ct,crudeTrade,crudeSetup);
            return await read(connection,selected.Data,selected.Frame,ct);
        },ct)
        :expectedConvention==PhysicalGasStore.Convention
        ?ReadGasMarket(connection,view,gasTrade,(data,frame)=>read(connection,data,frame,ct),ct)
        :tradeReads.Read(connection,view,data=>read(connection,data,view.CalculationFrame,ct),ct),scenario,headTrade,amendmentPreview,expectedConvention,dealRisk,
        publicationCheck:expectedConvention==PhysicalLngStore.Convention?(connection,view,ct)=>lngTrade is {} selectedTrade?lngStore.RecheckScope(connection,selectedTrade,ct):lngStore.RecheckReferences(connection,ct):gasReferenceChoices?async(connection,view,ct)=>{
            if(!view.Working)return;
            var source=gasChoiceSource??throw new MarketDataException("market_inputs","The gas choices did not retain their exact source.");
            await MarketBundle.Require(connection,source.Apply(database.Manifest),ct);
        }:null);
}
// The page keeps request policy: conventions, risk and amendment permissions, and the booking and amendment flags.
Task<IResult> TradingRead(HttpContext c,FuturesReadRequest body,Func<NpgsqlConnection,TradingView,CancellationToken,Task<object>> read,bool scenario=false,Guid? headTrade=null,bool amendmentPreview=false,string? expectedConvention=null,bool dealRisk=false,bool combinedWorkspace=false,bool concurrent=false,
    Func<NpgsqlConnection,TradingView,CancellationToken,Task>? recheck=null,
    Func<NpgsqlConnection,TradingView,CancellationToken,Task>? publicationCheck=null,
    Func<NpgsqlConnection,CancellationToken,Task>? sourceAdmission=null,
    Func<NpgsqlConnection,TradingReadSelection,CancellationToken,Task>? initialAdmission=null)
{
    TradingView? completedView=null;
    async Task<IResult> Execute(string profile,Func<TradingReadSelection> capture)
    {
        var completed=await ExecuteTrading(profile,capture,body,read,c.RequestAborted,scenario,headTrade,amendmentPreview,expectedConvention,dealRisk,combinedWorkspace,publicationCheck,sourceAdmission:sourceAdmission,initialAdmission:initialAdmission);
        completedView=completed.View;return Results.Ok(completed.Result);
    }
    if(!concurrent)return Locked(c,session=>Execute(Profile(session),()=>Capture(session,body)));
    if(!combinedWorkspace||scenario||dealRisk||amendmentPreview||headTrade is not null||recheck is null)
        throw new InvalidOperationException("Only the read-only combined workspace can release the session gate.");
    return Concurrent(c,body,async(session,capture)=>(await Execute(Profile(session),capture),completedView!),recheck);
}
Task<(object Result,TradingView View)> ExecuteTrading(string profile,Func<TradingReadSelection> capture,FuturesReadRequest body,
    Func<NpgsqlConnection,TradingView,CancellationToken,Task<object>> read,CancellationToken cancellation,
    bool scenario=false,Guid? headTrade=null,bool amendmentPreview=false,string? expectedConvention=null,bool dealRisk=false,bool combinedWorkspace=false,
    Func<NpgsqlConnection,TradingView,CancellationToken,Task>? publicationCheck=null,string? hostedDigest=null,
    Func<NpgsqlConnection,CancellationToken,Task>? sourceAdmission=null,
    Func<NpgsqlConnection,TradingReadSelection,CancellationToken,Task>? initialAdmission=null)=>
    TradingTransaction(profile,capture,body,async(connection,selection,ct)=>
    {
        // What refuses the read, and the head a ticket compares, come before the knowledge the read is of: a refused
        // read captures nothing, and the head is read under the same gate hold as that capture, as it always was.
        RequireConvention(body,expectedConvention);
        if(initialAdmission is not null)await initialAdmission(connection,selection,ct);
        if(scenario && !await PreviewDatabase.HasPermission(connection,"RunScenario",ct)||dealRisk && !await PreviewDatabase.HasPermission(connection,"RunDealRisk",ct))throw new PreviewDeniedException();
        if(amendmentPreview&&!selection.Working)
            throw new MarketDataException("historical_read_only","Amendments require working data.");
        if(amendmentPreview&&!await PreviewDatabase.HasPermission(connection,"AmendTrade",ct))throw new PreviewDeniedException();
        if(!selection.Working||headTrade is not {} trade)return null;
        await using var head=ScheduleStore.Command(connection,expectedConvention==SwapStore.Convention?"SELECT security.swap_current_head($1,$2)":"SELECT security.futures_current_head($1,$2)",database.Manifest.CatalogId,trade);
        return await head.ExecuteScalarAsync(ct) is int revision?revision:null;
    },async(connection,selection,currentHead,resolve,ct)=>
    {
        var working=selection.Working;
        var view=await resolve();
        var value=await read(connection,view,ct);
        if(scenario && !await PreviewDatabase.HasPermission(connection,"RunScenario",ct)||dealRisk && !await PreviewDatabase.HasPermission(connection,"RunDealRisk",ct))throw new PreviewDeniedException();
        var response=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(value,tradingJson))!.AsObject();
        foreach(var json in combinedWorkspace?new[]{response["catalog"]!.AsObject(),response["blotter"]!.AsObject()}:new[]{response})
        {
        await Rights(connection,json,view,selection,json["books"] is not null,ct);
        if(headTrade is not null)
        {
            var selectedRevision=json["state"]?["revision"]?.GetValue<int>()??json["expectedRevision"]?.GetValue<int>();
            var lifecycleEligible=json["lifecycleEligible"]?.GetValue<bool>()??true;
            var canAmend=working&&currentHead==selectedRevision&&lifecycleEligible&&await PreviewDatabase.HasPermission(connection,"AmendTrade",ct);
            if(amendmentPreview&&!canAmend)throw new MarketDataException(expectedConvention==SwapStore.Convention?"swap_revision_conflict":"futures_revision_conflict","Reload the latest eligible trade revision before amending.");
            json["canAmend"]=JsonSerializer.SerializeToNode(canAmend);
            var stated=json["lifecycleReason"]?.GetValue<string>();json.Remove("lifecycleReason");
            json["amendUnavailableReason"]=JsonSerializer.SerializeToNode(!working?$"{selection.ReadOnlyLabel} is read-only. Return to working data to amend."
                :currentHead!=selectedRevision?"This is an earlier trade revision. Open the latest revision at its working As of date."
                :!lifecycleEligible?stated??(expectedConvention==SwapStore.Convention?"This swap cannot be amended in this state. Once started, only a scheduled swap with index-settled sides in period, daily-rate or contract-total quantities can be amended."
                    :"This retained state is outside the supported live, unprocessed futures lifecycle. Settlement must be handled separately.")
                :!canAmend?"Your current role does not have Amend Trade permission.":null);
        }
        }
        // Rights and the financial result are materialized. Recheck independently
        // expiring read and operation grants together at the publication boundary.
        if(publicationCheck is not null)await publicationCheck(connection,view,ct);
        await using var publication=ScheduleStore.Command(connection,
            "SELECT security.trading_access($1,'ReadTrades') AND (NOT $2 OR security.has_permission('RunScenario')) AND (NOT $3 OR security.has_permission('RunDealRisk'))",
            database.Manifest.CatalogId,scenario,dealRisk);
        if(!Equals(await publication.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        return ((object)response,view);
    },cancellation,hostedDigest,sourceAdmission);
// A trading read in its identity transaction. A working read first admits itself and captures current knowledge in
// a transaction of its own: the capture certifies under the catalog's publication gate, and holding that gate while
// the read values would make every other working read, in any session, wait behind it (D420). The read's transaction
// then reads at that context. `admit` refuses what it must before anything is captured and returns the trade head a
// ticket read compares. A retained or historical view takes no gate and reads in the one transaction.
// Neither transaction's result is an outcome an assistant invocation may record or replay: no trading read needs
// an approval receipt. A read that declares a source admission keeps one transaction: the admission is captured before
// anything can wait and is required again at the commit that publishes the read, so the two cannot be split.
async Task<T> TradingTransaction<T>(string profile,Func<TradingReadSelection> capture,FuturesReadRequest body,
    Func<NpgsqlConnection,TradingReadSelection,CancellationToken,Task<int?>> admit,
    Func<NpgsqlConnection,TradingReadSelection,int?,Func<Task<TradingView>>,CancellationToken,Task<T>> read,CancellationToken cancellation,string? hostedDigest=null,
    Func<NpgsqlConnection,CancellationToken,Task>? sourceAdmission=null)
{
    if(sourceAdmission is not null)return await database.WithIdentity(profile,"ReadTrades",async(connection,ct)=>
    {
        await sourceAdmission(connection,ct);
        var admitted=capture();var head=await admit(connection,admitted,ct);
        return await read(connection,admitted,head,()=>admitted.Resolve(connection,knowledgeStore,body,ct),ct);
    },cancellation,hostedDigest:hostedDigest);
    TradingReadSelection? selection=null;
    var first=await database.WithIdentity(profile,"ReadTrades",async(connection,ct)=>
    {
        selection=capture();var head=await admit(connection,selection,ct);
        if(selection.Working)return (Head:head,Knowledge:await selection.CaptureKnowledge(connection,knowledgeStore,ct),Value:default(T));
        return (Head:head,Knowledge:(ScheduleContext?)null,Value:await read(connection,selection,head,()=>selection.Resolve(connection,knowledgeStore,body,ct),ct));
    },cancellation,hostedDigest:hostedDigest);
    if(first.Knowledge is not {} knowledge)return first.Value!;
    return await database.WithIdentity(profile,"ReadTrades",(connection,ct)=>
        read(connection,selection!,first.Head,()=>selection!.Captured(connection,knowledgeStore,knowledge,ct),ct),cancellation,hostedDigest:hostedDigest);
}
TradingReadSelection Capture(PreviewSession session,FuturesReadRequest body)=>TradingReadSelection.Capture(session,body,
    session.LivePin(body.SnapshotId)?.RequireFrame()??Frame(session,body.SnapshotId));
void RequireConvention(FuturesReadRequest body,string? expected)
{
    if(expected==PhysicalLngStore.Convention){if(body.Convention!=expected)throw new MarketDataException("lng_context","Select the explicit supported LNG cargo valuation convention.");}
    else if(expected==PhysicalCrudeStore.Convention){if(body.Convention!=expected)throw new MarketDataException("crude_context","Select the explicit supported physical crude valuation convention.");}
    else if(expected is SwapStore.Convention or PhysicalGasStore.Convention or TradingBlotter.Convention or RecForwardStore.Convention or HeatRateCallStore.Convention or GasStorageStore.Convention){if(body.Convention!=expected)throw new MarketDataException("valuation_convention","Select the read's explicit supported valuation convention.");}
    else if(expected==FuturesOptionStore.Convention)FuturesOptionStore.RequireConvention(body.Convention);
    else FuturesStore.Convention(body.Convention);
}
// The context and As of a trading page read, and the reader's rights under its selection; with the booking rights when it offers booking.
async Task Rights(NpgsqlConnection connection,System.Text.Json.Nodes.JsonObject json,TradingView view,TradingReadSelection selection,bool booking,CancellationToken ct)
{
    var working=selection.Working;
    json["context"]=JsonSerializer.SerializeToNode(view.Context,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});json["asOf"]=JsonSerializer.SerializeToNode(selection.Frame.At);
    json["canRunDealRisk"]=JsonSerializer.SerializeToNode(await PreviewDatabase.HasPermission(connection,"RunDealRisk",ct));
    var canBook=working&&await PreviewDatabase.HasPermission(connection,"BookTrade",ct);
    if(json["canBook"] is System.Text.Json.Nodes.JsonValue offered&&offered.TryGetValue<bool>(out var eligible))canBook&=eligible;
    json["canBook"]=JsonSerializer.SerializeToNode(canBook);
    if(!booking)return;
    json["canConfigureGasServices"]=JsonSerializer.SerializeToNode(working&&await PreviewDatabase.HasPermission(connection,"ReadGasOperations",ct)&&await PreviewDatabase.HasPermission(connection,"ConfigureGasServices",ct));
    json["canRecordGasOperations"]=JsonSerializer.SerializeToNode(working&&await PreviewDatabase.HasPermission(connection,"ReadGasOperations",ct)&&await PreviewDatabase.HasPermission(connection,"RecordGasOperations",ct)&&await PreviewDatabase.HasPermission(connection,"AmendTrade",ct));
    json["canAmendTrades"]=JsonSerializer.SerializeToNode(working&&await PreviewDatabase.HasPermission(connection,"AmendTrade",ct));
    json["bookingUnavailableReason"]=JsonSerializer.SerializeToNode(!working
        ?$"{selection.ReadOnlyLabel} is read-only. Return to working data, then select an As of date."
        :!await PreviewDatabase.HasPermission(connection,"BookTrade",ct)?"Your current role does not have Book Trade permission.":null);
}
// The reader may still read trades and the market data that values them.
async Task RequireReadAccess(NpgsqlConnection connection,CancellationToken ct)
{
    await using var access=ScheduleStore.Command(connection,
        "SELECT security.trading_access($1,'ReadTrades') AND security.market_access($1,'ReadMarketData')",database.Manifest.CatalogId);
    if(!Equals(await access.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
}
// A read-only trading read outside the session gate: the selection is captured under the gate, which is released while the
// read runs and taken again to authorize its result before the caller publishes it.
async Task<T> Concurrent<T>(HttpContext c,FuturesReadRequest body,Func<PreviewSession,Func<TradingReadSelection>,Task<(T Result,TradingView View)>> read,
    Func<NpgsqlConnection,TradingView,CancellationToken,Task> recheck)
{
    var session=Session(c);TradingReadSelection? selection=null;
    await session.Gate.WaitAsync(c.RequestAborted);
    var held=true;(T Result,TradingView View) completed;
    try
    {
        completed=await read(session,()=>
        {
            // Authenticate before validating the selected frame, as a gated
            // trading read does. Release only after immutable capture.
            selection=Capture(session,body);session.Gate.Release();held=false;
            return selection;
        });
    }
    finally {if(held)session.Gate.Release();}
    await session.Gate.WaitAsync(c.RequestAborted);
    try
    {
        selection!.RequireSession(session);
        // The gate wait follows the valuation transaction. Authorize again
        // before returning; no DB epoch is held while waiting for this gate.
        return await database.WithIdentity(selection.Profile,"ReadTrades",async(connection,ct)=>
        {
            await RequireReadAccess(connection,ct);
            await recheck(connection,completed.View,ct);
            await MarketBundle.RequireReferenceVisibility(connection,database.Manifest,ct);
            selection.RequireSession(session);
            return completed.Result;
        },c.RequestAborted);
    }
    finally {session.Gate.Release();}
}
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
var recTrades=new RecForwardStore(database);
Task<IResult> RecTradeRead(HttpContext c,FuturesReadRequest body,
    Func<NpgsqlConnection,PreviewData,InputFrame,ScheduleContext,bool,CancellationToken,Task<object>> work)=>TradingRead(c,
    body??throw new MarketDataException("rec_forward","Select the shared financial context."),async(connection,view,ct)=>
    {
        using var visibility=new AsyncReadVisibility();
        var result=await tradeReads.Read(connection,view,data=>work(connection,data,view.CalculationFrame,view.Context,view.Working,ct),ct,
            referenceScope:ReferenceReadScope.AllVisible,observationScope:ObservationReadScope.ExactPins);
        await visibility.Seal().Require(connection,ct);return result;
    },expectedConvention:RecForwardStore.Convention);
api.MapPost("/rec/trades/catalog",(HttpContext c,RecForwardCatalogRequest body)=>RecTradeRead(c,body.Context,
    (connection,data,frame,context,current,ct)=>RecForwardCatalog.Read(connection,database,data,frame,context.KnownAt!,current,ct)));
api.MapPost("/rec/trades/preview",(HttpContext c,RecForwardPreviewRequest body)=>RecTradeRead(c,body.Context,
    async(connection,data,frame,context,_,ct)=>await recTrades.Preview(connection,data,frame,SchedulePreview.ParseAt(context.KnownAt),body,ct)));
api.MapPost("/rec/trades/get",(HttpContext c,RecForwardReadRequest body)=>RecTradeRead(c,body.Context,
    async(connection,_,frame,context,_,ct)=>(await recTrades.ReadExact(connection,frame,body.TradeId,body.Revision,ct)) with{RetainedSource=RetainedSideSource.From(frame,context)}));
api.MapPost("/rec/trades/valuation",(HttpContext c,RecForwardReadRequest body)=>RecTradeRead(c,body.Context,
    async(connection,_,frame,_,_,ct)=>await recTrades.Value(connection,frame,body.TradeId,body.Revision,ct)));
api.MapPost("/rec/trades/status",(HttpContext c,RecForwardStatusRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
    {
        if(Pin(session).Context is not null)throw new MarketDataException("historical_read_only","Recover certificate trade outcomes from current working data.");
        return Results.Ok(await recTrades.Status(connection,body,ct));
    },c.RequestAborted)));
api.MapPost("/rec/trades/book",(HttpContext c,RecForwardBookRequest body)=>Locked(c,async session=>
{
    c.Items["rec_trade_attempted"]=true;
    if(Pin(session).Context is not null)throw new MarketDataException("historical_read_only","Return to working data before booking a certificate trade.");
    var profile=Profile(session);var retained=new RetainedScheduleStore(database);
    var selected=await database.WithIdentity(profile,"BookTrade",async(connection,ct)=>
    {
        var receipt=await recTrades.RequestStatus(connection,body,ct);
        if(receipt is not null)return (Receipt:receipt,Prepared:(RecForwardPrepared?)null,Frame:(InputFrame?)null);
        if(body.Context is null||body.Context.KnowledgeMode!="current")throw new MarketDataException("historical_read_only","Book from an explicitly selected working financial snapshot.");
        var pin=Pin(session,body.Context.SnapshotId);
        if(pin.Context is not null)throw new MarketDataException("historical_read_only","Book from the selected working financial snapshot.");
        var view=await TradingView.Capture(connection,new KnowledgeResolver(database.Manifest.CatalogId),pin.RequireFrame(),ct);
        await view.Bind(connection,database.Manifest.CatalogId,ct);
        var frame=view.CalculationFrame;await TradingContext.Bind(connection,database.Manifest.CatalogId,frame,view.Context,ct);
        using var visibility=new AsyncReadVisibility();
        var data=await MarketInputSelection.Root(database,connection,frame,ct,referenceScope:ReferenceReadScope.AllVisible,includeCalendarDays:true);
        var prepared=await recTrades.PrepareBook(connection,data,frame,SchedulePreview.ParseAt(view.Context.KnownAt),body,ct);
        await visibility.Seal().Require(connection,ct);
        return (Receipt:(RecForwardBookingReceipt?)null,Prepared:(RecForwardPrepared?)prepared,Frame:(InputFrame?)frame);
    },c.RequestAborted);
    if(selected.Receipt is not null)return Results.Ok(selected.Receipt);
    var prepared=selected.Prepared!;
    await retained.Stage(profile,body.OperationId,body,"BookTrade",null,null,prepared.Retained.Generate(c.RequestAborted),c.RequestAborted);
    return await database.WithIdentity(profile,"BookTrade",async(connection,ct)=>
    {
        await retained.Lock(connection,body.OperationId,ct);
        var receipt=await recTrades.Save(connection,selected.Frame!,body,prepared,ct);
        if(receipt.Changed&&await retained.Publish(connection,body.OperationId,ct)!=receipt.InstrumentId)
            throw new MarketDataException("rec_forward_schedule","Publish the exact independently retained certificate Side schedules.");
        return Results.Ok(receipt);
    },c.RequestAborted);
}));

var heatRateCalls=new HeatRateCallStore(database,touStore);
// Booking and marking both act on the working snapshot the caller selected; a historical view is read-only.
// Terms are resolved under that snapshot's bound context; the write then runs in its own unbound transaction.
async Task<T> HeatRateCallWrite<TPrepared,T>(HttpContext c,PreviewSession session,FuturesReadRequest context,string permission,
    Func<NpgsqlConnection,PreviewData,InputFrame,CancellationToken,Task<TPrepared>> prepare,Func<NpgsqlConnection,InputFrame,TPrepared,CancellationToken,Task<T>> write)
{
    if(Pin(session).Context is not null||context is null||context.KnowledgeMode!="current")
        throw new MarketDataException("historical_read_only","Return to working data before booking or recording.");
    var profile=Profile(session);
    var selected=await database.WithIdentity(profile,permission,async(connection,ct)=>
    {
        var pin=Pin(session,context.SnapshotId);
        if(pin.Context is not null)throw new MarketDataException("historical_read_only","Use the selected working financial snapshot.");
        var view=await TradingView.Capture(connection,new KnowledgeResolver(database.Manifest.CatalogId),pin.RequireFrame(),ct);
        await view.Bind(connection,database.Manifest.CatalogId,ct);
        var frame=view.CalculationFrame;await TradingContext.Bind(connection,database.Manifest.CatalogId,frame,view.Context,ct);
        using var visibility=new AsyncReadVisibility();
        var data=await MarketInputSelection.Root(database,connection,frame,ct,referenceScope:ReferenceReadScope.AllVisible,includeCalendarDays:true);
        var prepared=await prepare(connection,data,frame,ct);
        await visibility.Seal().Require(connection,ct);return (Prepared:prepared,Frame:frame);
    },c.RequestAborted);
    return await database.WithIdentity(profile,permission,(connection,ct)=>write(connection,selected.Frame,selected.Prepared,ct),c.RequestAborted);
}
api.MapPost("/heat-rate-calls/book",(HttpContext c,HeatRateCallBookRequest body)=>Locked(c,async session=>Results.Ok(
    await HeatRateCallWrite(c,session,body.Context,"BookTrade",
        async(connection,data,frame,ct)=>await heatRateCalls.Replay(connection,body,ct) is {} receipt?(Receipt:receipt,Prepared:(HeatRateCallPrepared?)null):
            (Receipt:(HeatRateCallReceipt?)null,Prepared:await heatRateCalls.Prepare(connection,data,frame,body,ct)),
        async(connection,frame,prepared,ct)=>prepared.Receipt??await heatRateCalls.Save(connection,frame,body,prepared.Prepared!,ct)))));
api.MapPost("/heat-rate-calls/status",(HttpContext c,HeatRateCallStatusRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
    {
        if(Pin(session).Context is not null)throw new MarketDataException("historical_read_only","Recover booking outcomes from current working data.");
        return Results.Ok(await heatRateCalls.Status(connection,body,ct));
    },c.RequestAborted)));
// A mark takes effect at the selected working snapshot's instant. Marking needs no trade access, so it captures no trading view.
api.MapPost("/spread-marks/record",(HttpContext c,SpreadMarksRecordRequest body)=>Locked(c,async session=>
{
    if(Pin(session).Context is not null||body.Context is null)throw new MarketDataException("historical_read_only","Return to working data before marking.");
    var frame=Pin(session,body.Context.SnapshotId).RequireFrame();
    return Results.Ok(await database.WithIdentity(Profile(session),null,(connection,ct)=>heatRateCalls.RecordMarks(connection,frame,body,ct),c.RequestAborted));
}));
var curveMarks=new CurveMarkStore(database.Manifest.CatalogId);
Task<IResult> RecordCurveMarks(HttpContext c,FuturesReadRequest context,Func<NpgsqlConnection,InputFrame,CancellationToken,Task<JsonElement>> write)=>
    Locked(c,async session=>{
        if(context is null||Pin(session).Context is not null||context.KnowledgeMode!="current"||context.ContextId is not null)
            throw new MarketDataException("historical_read_only","Return to working data before marking.");
        if(context.Convention!=PhysicalGasStore.Convention)throw new MarketDataException("curve_mark_input","Select the gas valuation context.");
        var frame=Pin(session,context.SnapshotId).RequireFrame();
        return Results.Ok(await database.WithIdentity(Profile(session),null,(connection,ct)=>write(connection,frame,ct),c.RequestAborted));
    });
api.MapPost("/volatility-marks/record",(HttpContext c,VolatilityMarksRecordRequest body)=>RecordCurveMarks(c,body.Context,
    (connection,frame,ct)=>curveMarks.RecordVolatility(connection,frame,body,ct)));
api.MapPost("/correlation-marks/record",(HttpContext c,CorrelationMarksRecordRequest body)=>RecordCurveMarks(c,body.Context,
    (connection,frame,ct)=>curveMarks.RecordCorrelation(connection,frame,body,ct)));
api.MapPost("/curve-marks/read",(HttpContext c,CurveMarksReadRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("curve_mark_input","Select a financial context."),
    (connection,view,ct)=>tradeReads.ReadBound(connection,view,async()=>await curveMarks.Read(connection,body,ct),ct),
    expectedConvention:PhysicalGasStore.Convention));
Task<IResult> HeatRateCallRead(HttpContext c,FuturesReadRequest context,Func<NpgsqlConnection,TradingView,CancellationToken,Task<object>> work)=>TradingRead(c,
    context??throw new MarketDataException("heat_rate_call_terms","Select the shared financial context."),async(connection,view,ct)=>
    {
        using var visibility=new AsyncReadVisibility();
        var result=await tradeReads.ReadBound(connection,view,()=>work(connection,view,ct),ct);
        await visibility.Seal().Require(connection,ct);return result;
    },expectedConvention:HeatRateCallStore.Convention);
api.MapPost("/spread-marks/read",(HttpContext c,SpreadMarksReadRequest body)=>HeatRateCallRead(c,body.Context,
    async(connection,_,ct)=>await heatRateCalls.ReadMarks(connection,body,ct)));
api.MapPost("/heat-rate-calls/catalog",(HttpContext c,FuturesReadRequest body)=>HeatRateCallRead(c,body,async(connection,view,ct)=>
{
    var frame=view.CalculationFrame;
    return await heatRateCalls.Choices(connection,await MarketInputSelection.Root(database,connection,frame,ct,referenceScope:ReferenceReadScope.AllVisible),frame,ct);
}));
api.MapPost("/heat-rate-calls/get",(HttpContext c,HeatRateCallReadRequest body)=>HeatRateCallRead(c,body.Context,
    async(connection,view,ct)=>await heatRateCalls.Read(connection,view.CalculationFrame,body.TradeId,ct)));

// Gas storage contracts (D432, D433): booking, the plan in force, a proposed plan and the valued position.
var gasStorage=new GasStorageStore(database);
Task<IResult> GasStorageRead(HttpContext c,FuturesReadRequest context,Func<NpgsqlConnection,PreviewData,InputFrame,CancellationToken,Task<object>> work)=>TradingRead(c,
    context??throw new MarketDataException("gas_storage_terms","Select the shared financial context."),async(connection,view,ct)=>
    {
        using var visibility=new AsyncReadVisibility();var frame=view.CalculationFrame;
        var result=await tradeReads.ReadBound(connection,view,async()=>
            await work(connection,await MarketInputSelection.Root(database,connection,frame,ct,referenceScope:ReferenceReadScope.AllVisible),frame,ct),ct);
        await visibility.Seal().Require(connection,ct);return result;
    },expectedConvention:GasStorageStore.Convention);
api.MapPost("/gas-storage/catalog",(HttpContext c,FuturesReadRequest body)=>GasStorageRead(c,body,gasStorage.Choices));
api.MapPost("/gas-storage/get",(HttpContext c,GasStorageReadRequest body)=>GasStorageRead(c,body.Context,
    (connection,data,frame,ct)=>gasStorage.Read(connection,data,frame,body.TradeId,ct)));
api.MapPost("/gas-storage/propose",(HttpContext c,GasStorageProposeRequest body)=>GasStorageRead(c,body.Context,
    (connection,data,frame,ct)=>gasStorage.Propose(connection,data,frame,body,ct)));
api.MapPost("/gas-storage/book",(HttpContext c,GasStorageBookRequest body)=>Locked(c,async session=>Results.Ok(
    await HeatRateCallWrite(c,session,body.Context,"BookTrade",
        async(connection,data,frame,ct)=>await gasStorage.Replay(connection,body,ct) is {} receipt?(Receipt:receipt,Prepared:(GasStoragePrepared?)null):
            (Receipt:(GasStorageReceipt?)null,Prepared:gasStorage.Prepare(data,frame,body)),
        async(connection,frame,prepared,ct)=>prepared.Receipt??await gasStorage.Save(connection,frame,body,prepared.Prepared!,ct)))));
api.MapPost("/gas-storage/plan",(HttpContext c,GasStoragePlanRequest body)=>Locked(c,async session=>Results.Ok(
    await HeatRateCallWrite(c,session,body.Context,"BookTrade",
        (connection,data,frame,ct)=>gasStorage.PreparePlan(connection,data,body,ct),
        async(connection,frame,replayed,ct)=>replayed??await gasStorage.SavePlan(connection,frame,body,ct)))));
api.MapPost("/gas-storage/status",(HttpContext c,GasStorageStatusRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
    {
        if(Pin(session).Context is not null)throw new MarketDataException("historical_read_only","Recover request outcomes from current working data.");
        return Results.Ok(await gasStorage.Status(connection,body,ct));
    },c.RequestAborted)));

api.MapPost("/futures/catalog",(HttpContext c,FuturesReadRequest body)=>FuturesRead(c,body,futuresStore.Catalog));
api.MapPost("/futures/amend-preview",(HttpContext c,FuturesAmendRequest body)=>FuturesRead(c,new(body.SnapshotId,body.Convention,body.KnowledgeMode),
    async(connection,data,frame,ct)=>await futuresStore.PreviewAmendment(connection,data,frame,body,ct),headTrade:body.TradeId,amendmentPreview:true));
api.MapPost("/futures/amend",(HttpContext c,FuturesAmendRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before amending.");
    c.Items["futures_attempted"]=true;
    return await database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>
    {
        if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct)||!await PreviewDatabase.HasPermission(connection,"ReadMarketData",ct))throw new PreviewDeniedException();
        return Results.Ok(await futuresStore.Amend(connection,await MarketInputSelection.ReadTerms(database,connection,body.DefinitionRevisionId,ct),Frame(session,body.SnapshotId),body,ct));
    },c.RequestAborted);
}));
api.MapPost("/futures/blotter",(HttpContext c,FuturesBlotterRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("blotter_view","Supply the selected context."),(connection,data,frame,ct)=>futuresStore.Blotter(connection,data,frame,body.View,ct)));
api.MapPost("/futures/holdings",(HttpContext c,FuturesReadRequest body)=>FuturesRead(c,body,futuresStore.Holdings));
var swapStore=new SwapStore(database);
api.MapPost("/lng/references",(HttpContext c,FuturesReadRequest body)=>TradingRead(c,body,
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>view.Working?lngStore.References(connection,ct):
  throw new MarketDataException("historical_read_only","Use current knowledge to choose LNG trade references."),ct),
 expectedConvention:PhysicalLngStore.Convention,publicationCheck:(connection,view,ct)=>lngStore.RecheckReferences(connection,ct)));
api.MapPost("/lng/resolve",(HttpContext c,LngResolveRequest body)=>TradingRead(c,new(body.SnapshotId,body.Convention,body.KnowledgeMode),
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>view.Working?lngStore.ResolveReference(connection,view.CalculationFrame,body,ct):
  throw new MarketDataException("historical_read_only","Use current knowledge to resolve a new LNG cargo."),ct),
 expectedConvention:PhysicalLngStore.Convention,publicationCheck:(connection,view,ct)=>lngStore.RecheckReferences(connection,ct)));
api.MapPost("/lng/preview",(HttpContext c,PhysicalLngWriteRequest body)=>FuturesRead(c,new(body.SnapshotId,body.Convention,body.KnowledgeMode),
 (connection,data,frame,ct)=>lngStore.Preview(connection,data,frame,body,ct),expectedConvention:PhysicalLngStore.Convention));
api.MapPost("/lng/directory",(HttpContext c,FuturesReadRequest body)=>TradingRead(c,body,
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>lngStore.Directory(connection,ct),ct),
 expectedConvention:PhysicalLngStore.Convention,publicationCheck:(connection,view,ct)=>lngStore.RecheckScope(connection,null,ct)));
api.MapPost("/lng/editor",(HttpContext c,PhysicalLngEditorRequest body)=>FuturesRead(c,body.Context,
 (connection,data,frame,ct)=>lngStore.Editor(connection,data,frame,body.TradeId,ct),expectedConvention:PhysicalLngStore.Convention,lngTrade:body.TradeId));
api.MapPost("/lng/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",
 async(connection,ct)=>Results.Ok(await lngStore.Status(connection,body.OperationId,ct)),c.RequestAborted)));
api.MapPost("/lng/book",(HttpContext c,PhysicalLngWriteRequest body)=>Locked(c,async session=>
{
 if(session.MarketContext is not null||body.KnowledgeMode!="current")throw new MarketDataException("historical_read_only","Return to working data to book.");
 var profile=Profile(session);var frame=Frame(session,body.SnapshotId);var retained=new RetainedScheduleStore(database);
 var terms=await database.WithIdentity(profile,"BookTrade",async(connection,ct)=>{
  var status=await lngStore.Status(connection,body.OperationId,ct);
  return status.TryGetProperty("tradeId",out _)?null:await lngStore.Prepare(connection,frame,body,ct);
 },c.RequestAborted);
 if(terms is not null)await retained.Stage(profile,body.OperationId,body,"BookTrade",null,null,terms.Generate(c.RequestAborted),c.RequestAborted);
 return await database.WithIdentity(profile,"BookTrade",async(connection,ct)=>{
  if(terms is not null)await retained.Lock(connection,body.OperationId,ct);
  var result=await lngStore.Save(connection,frame,body,ct);
  if(terms is not null)await retained.Publish(connection,body.OperationId,ct);
  return Results.Ok(result);
 },c.RequestAborted);
}));

var crudeStore=new PhysicalCrudeStore(database);
api.MapPost("/crude/references",(HttpContext c,FuturesReadRequest body)=>TradingRead(c,body,
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>
  view.Working?crudeStore.References(connection,ct):throw new MarketDataException("historical_read_only","Use current knowledge to choose physical trade references."),ct),
 expectedConvention:PhysicalCrudeStore.Convention,publicationCheck:(connection,view,ct)=>crudeStore.RecheckReferences(connection,ct)));
api.MapPost("/crude/resolve",(HttpContext c,CrudeResolveRequest body)=>TradingRead(c,new(body.SnapshotId,body.Convention,body.KnowledgeMode),
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>
  view.Working?crudeStore.ResolveReference(connection,view.CalculationFrame,body,ct):throw new MarketDataException("historical_read_only","Use current knowledge to resolve a new physical trade."),ct),
 expectedConvention:PhysicalCrudeStore.Convention));
api.MapPost("/crude/catalog",(HttpContext c,FuturesReadRequest body)=>FuturesRead(c,body,
 (connection,data,frame,ct)=>crudeStore.CatalogRead(connection,data,frame,ct),expectedConvention:PhysicalCrudeStore.Convention));
api.MapPost("/crude/preview",(HttpContext c,PhysicalCrudeWriteRequest body)=>FuturesRead(c,new(body.SnapshotId,body.Convention,body.KnowledgeMode),
 (connection,data,frame,ct)=>crudeStore.Preview(connection,data,frame,body,ct),expectedConvention:PhysicalCrudeStore.Convention,crudeSetup:body.CrudeSetupId));
api.MapPost("/crude/directory",(HttpContext c,FuturesReadRequest body)=>TradingRead(c,body,
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>crudeStore.Directory(connection,ct),ct),expectedConvention:PhysicalCrudeStore.Convention));
api.MapPost("/crude/editor",(HttpContext c,PhysicalCrudeEditorRequest body)=>FuturesRead(c,body.Context,
 (connection,data,frame,ct)=>crudeStore.Editor(connection,data,frame,body.TradeId,ct),expectedConvention:PhysicalCrudeStore.Convention,crudeTrade:body.TradeId));
api.MapPost("/crude/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await crudeStore.Status(connection,body.OperationId,ct)),c.RequestAborted)));
api.MapPost("/crude/book",(HttpContext c,PhysicalCrudeWriteRequest body)=>Locked(c,async session=>
{
 if(session.MarketContext is not null||body.KnowledgeMode!="current")throw new MarketDataException("historical_read_only","Return to working data to book.");
 var profile=Profile(session);var frame=Frame(session,body.SnapshotId);var retained=new RetainedScheduleStore(database);
 var terms=await database.WithIdentity(profile,"BookTrade",async(connection,ct)=>{
  var status=await crudeStore.Status(connection,body.OperationId,ct);
  return status.TryGetProperty("tradeId",out _)?null:await crudeStore.Prepare(connection,frame,body,ct);
 },c.RequestAborted);
 if(terms is not null)await retained.Stage(profile,body.OperationId,body,"BookTrade",null,null,terms.Generate(c.RequestAborted),c.RequestAborted);
 return await database.WithIdentity(profile,"BookTrade",async(connection,ct)=>{
  if(terms is not null)await retained.Lock(connection,body.OperationId,ct);
  var result=await crudeStore.Save(connection,frame,body,ct);
  if(terms is not null)await retained.Publish(connection,body.OperationId,ct);
  return Results.Ok(result);
 },c.RequestAborted);
}));

var crudeLifecycle=new CrudeLifecycleStore(database);
api.MapPost("/crude/operations/read",(HttpContext c,PhysicalCrudeEditorRequest body)=>TradingRead(c,body.Context,
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>crudeLifecycle.Read(connection,view.CalculationFrame,body.TradeId,view.Working,ct),ct),expectedConvention:PhysicalCrudeStore.Convention));
api.MapPost("/crude/operations/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>Results.Ok(await crudeLifecycle.Status(connection,body.OperationId,ct)),c.RequestAborted)));
api.MapPost("/crude/operations/invoice/preview",(HttpContext c,JsonElement body)=>TradingRead(c,CrudeLifecycleStore.Context(body,"invoice"),
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>crudeLifecycle.Preview(connection,view.CalculationFrame,body,ct),ct),expectedConvention:PhysicalCrudeStore.Convention));
foreach(var operation in new[]{("receipt","receipt"),("allocation","allocation"),("dispatch","dispatch"),("invoice/issue","invoice"),("payment","payment"),("payment/reverse","payment_reversal"),("invoice/void","invoice_void")})
{
 var kind=operation.Item2;
 api.MapPost("/crude/operations/"+operation.Item1,(HttpContext c,JsonElement body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>{
  if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before recording operational facts.");
  var context=CrudeLifecycleStore.Context(body,kind);
  return Results.Ok(await crudeLifecycle.Save(connection,Frame(session,context.SnapshotId),body,kind,ct));
 },c.RequestAborted)));
}

var gasStore=new PhysicalGasStore(database);
api.MapPost("/gas/catalog",(HttpContext c,FuturesReadRequest body)=>FuturesRead(c,body,
 (connection,data,frame,ct)=>gasStore.CatalogRead(connection,data,frame,ct),expectedConvention:PhysicalGasStore.Convention));
var gasBilling=new GasCashBillingStore(database);
var gasBillingOperating=new GasBillingOperatingStore(database);
api.MapPost("/gas/billing/worklist",(HttpContext c,GasBillingWorklistRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await gasBillingOperating.Worklist(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/gas/billing/operating-preview",(HttpContext c,GasBillingOperatingPreviewRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await gasBillingOperating.Preview(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/gas/billing/enrollment-choices",(HttpContext c,GasBillingOperatingContext body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await GasBillingEnrollmentJobs.Choices(connection,database,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/gas/billing/enrollment-preview",(HttpContext c,GasBillingEnrollmentPreview body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await GasBillingEnrollmentJobs.Preview(connection,database,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/gas/billing/enroll",(HttpContext c,GasBillingEnrollmentRequest body)=>Locked(c,session=>
{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Enroll gas processing in the current workspace.");
 return database.WithIdentity(Profile(session),"RunJobs",async(connection,ct)=>Results.Ok((await GasBillingEnrollmentJobs.Save(connection,database,body,ct)).Outcome),c.RequestAborted);
}));
api.MapPost("/gas/billing/read",(HttpContext c,GasBillingReadRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("gas_billing_context","Supply the selected billing context."),
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>gasBilling.Read(connection,view.CalculationFrame,body,ct),ct),expectedConvention:PhysicalGasStore.Convention));
api.MapPost("/gas/billing/preview",(HttpContext c,GasBillingPreviewRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("gas_billing_context","Supply the selected billing context."),
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>gasBilling.Preview(connection,view.CalculationFrame,body,ct),ct),expectedConvention:PhysicalGasStore.Convention));
api.MapPost("/gas/billing/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>Results.Ok(await gasBilling.Status(connection,body.OperationId,ct)),c.RequestAborted)));
api.MapPost("/gas/billing/determine",(HttpContext c,GasBillingDetermineRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>{
 if(!TradingReadSelection.IsWorking(session,GasCashBillingStore.RequirePreview(body).Context))throw new MarketDataException("historical_read_only","Return to working data before determining billing.");
 return Results.Ok(await gasBilling.Determine(connection,Frame(session,body.Preview.Context.SnapshotId),body,ct));
},c.RequestAborted)));
var gasBills=new GasBillStore(database);
api.MapPost("/gas/bills/read",(HttpContext c,GasBillReadRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("gas_bill_context","Supply the selected bill context."),
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>gasBills.Read(connection,view.CalculationFrame,body,ct),ct),expectedConvention:PhysicalGasStore.Convention));
api.MapPost("/gas/bills/preview",(HttpContext c,GasBillPreviewRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("gas_bill_context","Supply the selected bill context."),
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>gasBills.Preview(connection,view.CalculationFrame,body,ct),ct),expectedConvention:PhysicalGasStore.Convention));
api.MapPost("/gas/bills/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>Results.Ok(await gasBills.Status(connection,body.OperationId,ct)),c.RequestAborted)));
api.MapPost("/gas/bills/issue",(HttpContext c,GasBillIssueRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>{
 if(body.Preview?.Context is not {} selected||!TradingReadSelection.IsWorking(session,selected))throw new MarketDataException("historical_read_only","Return to working data before issuing a bill.");
 return Results.Ok(await gasBills.Issue(connection,Frame(session,selected.SnapshotId),body,ct));
},c.RequestAborted)));
var gasOperations=new GasDeliveryOperationsStore(database);
api.MapPost("/gas/operations/read",(HttpContext c,GasOperationsReadRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("gas_operations_context","Supply the header context."),
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>gasOperations.Read(connection,view.CalculationFrame,body,ct),ct),expectedConvention:PhysicalGasStore.Convention));
api.MapPost("/gas/operations/preview",(HttpContext c,GasOperationPreviewRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("gas_operations_context","Supply the header context."),
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>gasOperations.Preview(connection,view.CalculationFrame,body,ct),ct),expectedConvention:PhysicalGasStore.Convention));
api.MapPost("/gas/operations/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadGasOperations",async(connection,ct)=>Results.Ok(await gasOperations.Status(connection,body.OperationId,ct)),c.RequestAborted)));
foreach(var configureGasService in new[]{true,false}){
 var configure=configureGasService;
 api.MapPost(configure?"/gas/operations/configure":"/gas/operations/record",(HttpContext c,GasOperationCommand body)=>Locked(c,session=>database.WithIdentity(Profile(session),configure?"ConfigureGasServices":"RecordGasOperations",async(connection,ct)=>{
  if(body.Preview?.Context is not {} selected||!TradingReadSelection.IsWorking(session,selected))throw new MarketDataException("historical_read_only","Return to working data before recording gas operations.");
  return Results.Ok(await gasOperations.Record(connection,Frame(session,selected.SnapshotId),body,configure,ct));
 },c.RequestAborted)));
}
var gasDaily=new GasDailyQuantities(database);
var deliveryWorkbench=new DeliveryWorkbenchStore(database);
foreach(var deliveryProfile in new[]{"gas-transport","crude-receipt"})
{
 var profile=deliveryProfile;
 var readPermission=profile=="gas-transport"?"ReadGasOperations":"ReadTrades";
 var writePermission=profile=="gas-transport"?"RecordGasOperations":"AmendTrade";
 api.MapPost("/delivery/workbench/"+profile+"/directory",(HttpContext c,FacilityDirectoryRequest body)=>Locked(c,session=>
  database.WithIdentity(Profile(session),readPermission,async(connection,ct)=>Results.Ok(await deliveryWorkbench.Directory(connection,body,session.MarketContext,profile,ct)),c.RequestAborted)));
 api.MapPost("/delivery/workbench/"+profile+"/read",(HttpContext c,DeliveryWorkbenchReadRequest body)=>Locked(c,session=>
  database.WithIdentity(Profile(session),readPermission,async(connection,ct)=>Results.Ok(await deliveryWorkbench.Read(connection,body,session.MarketContext,profile,ct)),c.RequestAborted)));
 api.MapPost("/delivery/workbench/"+profile+"/preview",(HttpContext c,DeliveryWorkbenchWriteRequest body)=>Locked(c,session=>
  database.WithIdentity(Profile(session),writePermission,async(connection,ct)=>Results.Ok(await deliveryWorkbench.Preview(connection,body,session.MarketContext,profile,ct)),c.RequestAborted)));
 api.MapPost("/delivery/workbench/"+profile+"/record",(HttpContext c,DeliveryWorkbenchWriteRequest body)=>Locked(c,session=>
  database.WithIdentity(Profile(session),writePermission,async(connection,ct)=>Results.Ok(await deliveryWorkbench.Record(connection,body,session.MarketContext,profile,ct)),c.RequestAborted)));
 api.MapPost("/delivery/workbench/"+profile+"/status",(HttpContext c,FacilityStatusRequest body)=>Locked(c,session=>
  database.WithIdentity(Profile(session),readPermission,async(connection,ct)=>Results.Ok(await deliveryWorkbench.Status(connection,body,profile,ct)),c.RequestAborted)));
}
var deliveryNominations=new DeliveryNominationBatchStore(database);
api.MapPost("/delivery/workbench/gas-transport/nominations/preview",(HttpContext c,DeliveryNominationBatchPreviewRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadGasOperations",async(connection,ct)=>
 {
  if(body.Input?.Context is not {} context||!TradingReadSelection.IsWorking(session,context))throw new MarketDataException("historical_read_only","Return to working data before reviewing nominations.");
  return Results.Ok(await deliveryNominations.Preview(connection,Frame(session,context.SnapshotId),body,ct));
 },c.RequestAborted)));
api.MapPost("/delivery/workbench/gas-transport/nominations/record",(HttpContext c,DeliveryNominationBatchRecordRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"RecordGasOperations",async(connection,ct)=>
 {
  if(body.Input?.Context is not {} context||!TradingReadSelection.IsWorking(session,context))throw new MarketDataException("historical_read_only","Return to working data before recording nominations.");
  return Results.Ok(await deliveryNominations.Record(connection,Frame(session,context.SnapshotId),body,ct));
 },c.RequestAborted)));
api.MapPost("/delivery/workbench/gas-transport/nominations/status",(HttpContext c,FacilityStatusRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadGasOperations",async(connection,ct)=>Results.Ok(await deliveryNominations.Status(connection,body,ct)),c.RequestAborted)));
var facilityQuantities=new FacilityQuantityStore(database);
api.MapPost("/desk/facilities",(HttpContext c,FacilityDirectoryRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>{
    var business=new OperationalBusinessContext(database);
    var scope=await business.Resolve(connection,body.Context,session.MarketContext,"desk_facility",ct);
    if(!scope.Working)await business.BindResolved(connection,scope,ct);
    var definition=database.Manifest.RevisionId;
    var profiles=await LifecycleNative.Read(connection,"SELECT desk.profiles($1,$2)::text",ct,database.Manifest.CatalogId,definition);
    var setup=scope.Working&&await PreviewDatabase.HasPermission(connection,"ConfigureFacilityLimits",ct)?
        await LifecycleNative.Read(connection,"SELECT desk.profile_setup($1,$2)::text",ct,database.Manifest.CatalogId,definition):(JsonElement?)null;
    return Results.Ok(new{definitionRevisionId=definition,profiles,setup,scope.Working,scope.At});
 },c.RequestAborted)));
api.MapPost("/desk/facility-activity",(HttpContext c,FacilityTradeWindow body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>{
    var business=new OperationalBusinessContext(database);
    var scope=await business.Resolve(connection,body.Context,session.MarketContext,"desk_facility",ct);
    if(!scope.Working)await business.BindResolved(connection,scope,ct);
    return Results.Ok(await LifecycleNative.Read(connection,"SELECT desk.facility_activity($1,$2,$3,$4,$5)::text",ct,database.Manifest.CatalogId,body.ProfileId,body.From,body.To,scope.At));
 },c.RequestAborted)));
api.MapPost("/desk/facility-profile",(HttpContext c,DeskCommand body)=>Locked(c,session=>{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before configuring a facility.");
 return database.WithIdentity(Profile(session),"ConfigureFacilityLimits",async(connection,ct)=>{
    await DeskAdmission.Capture(database,connection,"ConfigureFacilityLimits",ct);
    return Results.Ok(await LifecycleNative.Read(connection,"SELECT desk.save_profile($1,$2,$3::jsonb)::text",ct,database.Manifest.CatalogId,body.OperationId,body.Record.GetRawText()));
 },c.RequestAborted);
}));
api.MapPost("/desk/attribute",(HttpContext c,FacilityAttribution body)=>Locked(c,session=>{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before associating a trade with a facility.");
 return database.WithIdentity(Profile(session),"BookTrade",async(connection,ct)=>{
    await DeskAdmission.Capture(database,connection,"BookTrade",ct);
    return Results.Ok(await LifecycleNative.Read(connection,"SELECT desk.attribute($1,$2,$3)::text",ct,database.Manifest.CatalogId,body.TradeId,body.ProfileId));
 },c.RequestAborted);
}));
var storageFacilities=new StorageFacilityStore(database);
var transportRoutes=new GasTransportRouteStore(database);
api.MapPost("/gas/transport/routes/directory",(HttpContext c,FacilityDirectoryRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadGasOperations",async(connection,ct)=>Results.Ok(await transportRoutes.Directory(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/gas/transport/routes/read",(HttpContext c,GasTransportRouteReadRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadGasOperations",async(connection,ct)=>Results.Ok(await transportRoutes.Read(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/gas/transport/routes/status",(HttpContext c,FacilityStatusRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadGasOperations",async(connection,ct)=>Results.Ok(await transportRoutes.Status(connection,body,ct)),c.RequestAborted)));
api.MapPost("/gas/transport/routes/preview",(HttpContext c,GasTransportRouteWriteRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),GasTransportRouteStore.Permission(body.Command),async(connection,ct)=>Results.Ok(await transportRoutes.Preview(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/gas/transport/routes/record",(HttpContext c,GasTransportRouteWriteRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),GasTransportRouteStore.Permission(body.Command),async(connection,ct)=>Results.Ok(await transportRoutes.Record(connection,body,session.MarketContext,ct)),c.RequestAborted)));
var transportCapacity=new GasTransportCapacityStore(database);
var pipelineOutlook=new GasPipelineOutlookStore(database);
api.MapPost("/gas/pipeline-outlook",(HttpContext c,GasPipelineOutlookRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadGasOperations",async(connection,ct)=>Results.Ok(await pipelineOutlook.Read(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/gas/transport/capacity/directory",(HttpContext c,FacilityDirectoryRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadGasOperations",async(connection,ct)=>Results.Ok(await transportCapacity.Directory(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/gas/transport/capacity/read",(HttpContext c,GasTransportCapacityReadRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadGasOperations",async(connection,ct)=>Results.Ok(await transportCapacity.Read(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/gas/transport/capacity/status",(HttpContext c,FacilityStatusRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadGasOperations",async(connection,ct)=>Results.Ok(await transportCapacity.Status(connection,body,ct)),c.RequestAborted)));
api.MapPost("/gas/transport/capacity/preview",(HttpContext c,GasTransportCapacityWriteRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),GasTransportCapacityStore.Permission(body.Command),async(connection,ct)=>Results.Ok(await transportCapacity.Preview(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/gas/transport/capacity/record",(HttpContext c,GasTransportCapacityWriteRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),GasTransportCapacityStore.Permission(body.Command),async(connection,ct)=>Results.Ok(await transportCapacity.Record(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/facilities/operations/directory",(HttpContext c,FacilityDirectoryRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadFacilityQuantities",async(connection,ct)=>Results.Ok(await storageFacilities.Directory(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/facilities/operations/read",(HttpContext c,FacilityOperationsReadRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadFacilityQuantities",async(connection,ct)=>Results.Ok(await storageFacilities.Read(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/facilities/operations/status",(HttpContext c,FacilityStatusRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadFacilityQuantities",async(connection,ct)=>Results.Ok(await storageFacilities.Status(connection,body,ct)),c.RequestAborted)));
api.MapPost("/facilities/operations/preview",(HttpContext c,FacilityOperationsWriteRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),StorageFacilityStore.Permission(body.Command),async(connection,ct)=>Results.Ok(await storageFacilities.Preview(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/facilities/operations/record",(HttpContext c,FacilityOperationsWriteRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),StorageFacilityStore.Permission(body.Command),async(connection,ct)=>Results.Ok(await storageFacilities.Record(connection,body,session.MarketContext,ct)),c.RequestAborted)));

api.MapPost("/facilities/quantities/preview",(HttpContext c,FacilityPreviewRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"RecordFacilityQuantities",async(connection,ct)=>Results.Ok(await facilityQuantities.Preview(connection,body,session.MarketContext,ct,c.Features.Get<AssistantInvocation>()?.CapturedWorkingRead==true)),c.RequestAborted)));
api.MapPost("/facilities/quantities/directory",(HttpContext c,FacilityDirectoryRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadFacilityQuantities",async(connection,ct)=>Results.Ok(await facilityQuantities.Directory(connection,body,session.MarketContext,ct,c.Features.Get<AssistantInvocation>()?.CapturedWorkingRead==true)),c.RequestAborted)));
api.MapPost("/facilities/quantities/read",(HttpContext c,FacilityReadRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadFacilityQuantities",async(connection,ct)=>Results.Ok(await facilityQuantities.Read(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/facilities/quantities/status",(HttpContext c,FacilityStatusRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadFacilityQuantities",async(connection,ct)=>Results.Ok(await facilityQuantities.Status(connection,body,ct)),c.RequestAborted)));
api.MapPost("/facilities/quantities/record",(HttpContext c,FacilityWriteRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),body.Command.ValueKind==JsonValueKind.Object&&body.Command.TryGetProperty("kind",out var kind)&&kind.ValueKind==JsonValueKind.String&&kind.GetString()=="limit"?"ConfigureFacilityLimits":"RecordFacilityQuantities",
 async(connection,ct)=>Results.Ok(await facilityQuantities.Save(connection,body,session.MarketContext,ct)),c.RequestAborted)));
api.MapPost("/gas/quantities/read",(HttpContext c,GasDailyReadRequest body)=>TradingRead(c,body.Context,
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>gasDaily.Read(connection,view.CalculationFrame,body,ct),ct),expectedConvention:PhysicalGasStore.Convention));
api.MapPost("/gas/quantities/history",(HttpContext c,PhysicalGasEditorRequest body)=>TradingRead(c,body.Context,
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>gasDaily.History(connection,body.TradeId,ct),ct),expectedConvention:PhysicalGasStore.Convention));
api.MapPost("/gas/quantities/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>Results.Ok(await gasDaily.Status(connection,body.OperationId,ct)),c.RequestAborted)));
api.MapPost("/gas/quantities/record",(HttpContext c,GasDailyWriteRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>{
 if(await gasDaily.RecordRefusal(connection,TradingReadSelection.IsWorking(session,body.Context),ct) is {} refused)throw refused;
 return Results.Ok(await gasDaily.Save(connection,Frame(session,body.Context.SnapshotId),body,ct));
},c.RequestAborted)));

api.MapPost("/gas/swing/read",(HttpContext c,JsonElement request)=>
{
 var body=GasSwingStore.Request<GasSwingReadRequest>(request);
 return TradingRead(c,body.Context,(connection,view,ct)=>tradeReads.ReadBound(connection,view,
  async()=>await GasSwingStore.Read(connection,database,body.TradeId,body.InstrumentId,ct),ct),
  expectedConvention:PhysicalGasStore.Convention,publicationCheck:(connection,_,ct)=>
   GasSwingStore.RequireScope(connection,database.Manifest.CatalogId,body.TradeId,body.InstrumentId,ct));
});
api.MapPost("/gas/swing/prepare",(HttpContext c,JsonElement request)=>
{
 var body=GasSwingStore.Request<GasSwingPrepareRequest>(request);
 GasSwingAction.WriteAuthority? authority=null;
 return TradingRead(c,body.RetainedContext(),(connection,view,ct)=>ReadGasMarket(connection,view,body.TradeId,async(data,frame)=>{
  if(!TradingReadSelection.IsWorking(Session(c),body.Context))throw new MarketDataException("historical_read_only","Return to working data to prepare a swing election.");
  await GasSwingAction.RequireAuthority(connection,authority!,body.TradeId,body.InstrumentId,ct);
  var selected=TradePositionSelection.From(new(body.Context,body.TradeId,body.InstrumentId,body.ExpectedRevision,GasPositions.Type));
  var position=(await GasPositions.Read(connection,database.Manifest,[],[],ct,selected)).Single();
  var held=await new GasCompositionStore(database).Load(connection,data,frame,position,ct);
  if(held.SwingRights.IsEmpty)throw new MarketDataException("swing_election_conflict","Refresh the selected swing trade revision.");
  var action=await GasSwingAction.Read(connection,database,held,frame,body.ExpectedRevision,null,authority,ct);
  return new{command=action.Build(body)};
 },ct),expectedConvention:PhysicalGasStore.Convention,publicationCheck:(connection,_,ct)=>
  GasSwingAction.RequireAuthority(connection,authority!,body.TradeId,body.InstrumentId,ct),initialAdmission:async(connection,_,ct)=>{
  var initial=await GasSwingAction.CaptureDeadline(connection,database.Manifest.CatalogId,ct)??throw new PreviewDeniedException();
  authority=await initial.Admit(connection,ct);
 });
});
api.MapPost("/gas/swing/record",(HttpContext c,JsonElement request)=>
{
 var body=GasSwingStore.Request<GasSwingRecordRequest>(request);
 return Locked(c,session=>
 {
  if(!TradingReadSelection.IsWorking(session,body.Context))throw new MarketDataException("historical_read_only","Return to working data before recording swing elections.");
  RequireConvention(body.Context,PhysicalGasStore.Convention);
  var frame=Frame(session,body.Context.SnapshotId);
  return database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>Results.Ok(await GasSwingStore.Record(connection,database,body,frame,ct)),c.RequestAborted);
 });
});

api.MapPost("/gas/exercise/read",(HttpContext c,GasExerciseReadRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("exercise_election","Select a financial context."),
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,async()=>await GasExerciseStore.Read(connection,database.Manifest.CatalogId,body.TradeId,body.InstrumentId,ct),ct),
 expectedConvention:PhysicalGasStore.Convention));
api.MapPost("/gas/exercise/prepare",(HttpContext c,GasExercisePrepareRequest body)=>TradingRead(c,body.RetainedContext(),
 (connection,view,ct)=>tradeReads.ReadBound(connection,view,async()=>{
  if(!TradingReadSelection.IsWorking(Session(c),body.Context))throw new MarketDataException("historical_read_only","Return to working data to prepare an exercise election.");
  var periods=await GasExerciseAction.ReadPeriods(connection,database.Manifest.CatalogId,body.TradeId,body.InstrumentId,body.ExpectedRevision,ct);
  return new{command=GasExerciseAction.Build(body,periods)};
 },ct),expectedConvention:PhysicalGasStore.Convention,publicationCheck:(connection,_,ct)=>
  GasExerciseAction.RequireAuthority(connection,database.Manifest.CatalogId,body.TradeId,body.InstrumentId,ct)));
api.MapPost("/gas/exercise/record",(HttpContext c,GasExerciseRecordRequest body)=>Locked(c,session=>
{
 if(!TradingReadSelection.IsWorking(session,body.Context))throw new MarketDataException("historical_read_only","Return to working data before recording exercise elections.");
 RequireConvention(body.Context,PhysicalGasStore.Convention);
 var frame=Frame(session,body.Context.SnapshotId);
 return database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>Results.Ok(await GasExerciseStore.Record(connection,database.Manifest.CatalogId,body,frame,ct)),c.RequestAborted);
}));

var gasComposition=new GasCompositionStore(database);
var gasRequestJson=app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
api.MapPost("/gas/composition/preview",(HttpContext c,JsonElement request)=>
{
 var body=GasCompositionRequestJson.From(request,gasRequestJson);
 return FuturesRead(c,new(body.SnapshotId,body.Convention,body.KnowledgeMode,body.PreviewContextId),
  (connection,data,frame,ct)=>gasComposition.Preview(connection,data,frame,body,ct),expectedConvention:PhysicalGasStore.Convention,gasTrade:body.PreviewTradeId);
});
api.MapPost("/gas/composition/editor",(HttpContext c,PhysicalGasEditorRequest body)=>FuturesRead(c,body.Context,
 (connection,data,frame,ct)=>gasComposition.Editor(connection,data,frame,body.TradeId,ct),expectedConvention:PhysicalGasStore.Convention,gasTrade:body.TradeId));
// A gas booking's outcome by its operation id, for the person who sent it; the composition and ticket bookings share it.
Task<IResult> GasBookingStatus(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await gasStore.Status(connection,body.OperationId,ct)),c.RequestAborted));
api.MapPost("/gas/composition/status",GasBookingStatus);
api.MapPost("/gas/composition/book",(HttpContext c,JsonElement request)=>
{
 var body=GasCompositionRequestJson.From(request,gasRequestJson);
 return Locked(c,session=>{
  GasCompositionStore.RequireNewTrade(body);
  return BookGasComposition(c,session,body);
 });
});
// Booking takes working data only: no selected historical context, current knowledge and no captured context.
void RequireGasBooking(PreviewSession session,string? knowledgeMode,Guid? contextId=null)
{
 if(session.MarketContext is not null||knowledgeMode!="current"||contextId is not null)throw new MarketDataException("historical_read_only","Return to working data to book or amend.");
}
// Books one composition under the session's gate.
async Task<IResult> BookGasComposition(HttpContext c,PreviewSession session,GasCompositionRequest body)
{
 RequireGasBooking(session,body.KnowledgeMode);
 return Results.Ok(await gasComposition.Book(Profile(session),Frame(session,body.SnapshotId),body,false,c.RequestAborted,session.Ends));
}

// The deal ticket (docs/deal-ticket.md) runs the gas operations it delegates to, with their permissions and context handling.
var gasTicket=new GasTicketStore(database);
var parkLoanTicket=new GasParkLoanTicketStore(database);
var parkCustodyTicket=new GasParkCustodyTicketStore(database);
var transportTicket=new GasTransportFinancialTicketStore(database);
var transportFinancial=new GasTransportFinancialStore(database);
Task<IResult> FinancialTicketRead(HttpContext c,FuturesReadRequest request,Func<NpgsqlConnection,InputFrame,CancellationToken,Task<object>> read,bool requireCapacity=false)=>
 TradingRead(c,request,(connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>read(connection,view.CalculationFrame,ct),ct),expectedConvention:PhysicalGasStore.Convention,
 sourceAdmission:(connection,ct)=>GasTransportFinancialEntry.Capture(connection,database,requireCapacity,ct));
FuturesReadRequest TicketContext(FuturesReadRequest? context)=>context??throw new MarketDataException("ticket_input","Supply the ticket's read context.");
GasCompositionRequest TicketRequest(TicketDraft draft,FuturesReadRequest context,Guid operation,Guid definition)=>
 GasTicket.Request(draft,context.SnapshotId,context.KnowledgeMode??throw new MarketDataException("ticket_input","Supply the ticket's knowledge mode."),operation,definition);
var gasLocations=new GasLocationBookingStore(database,gasComposition);
api.MapPost("/tickets/gas-locations/book",(HttpContext c,GasLocationBooking body)=>Locked(c,async session=>{
 var context=TicketContext(body.Context);RequireGasBooking(session,context.KnowledgeMode,context.ContextId);RequireConvention(context,PhysicalGasStore.Convention);
 return Results.Ok(await gasLocations.Book(Profile(session),Frame(session,context.SnapshotId),body,c.RequestAborted));
}));
api.MapPost("/tickets/gas-locations/status",(HttpContext c,PhysicalPowerStatusRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok<object?>((object?)await gasLocations.Read(connection,body.RequestId,null,ct)??new{found=false}),c.RequestAborted)));
api.MapPost("/tickets/gas-locations/start",(HttpContext c,GasLocationStart body)=>Locked(c,session=>{
 var context=TicketContext(body.Context);RequireGasBooking(session,context.KnowledgeMode,context.ContextId);RequireConvention(context,PhysicalGasStore.Convention);
 _=Frame(session,context.SnapshotId);
 return database.WithIdentity(Profile(session),"ReadTrades",(_,_)=>Task.FromResult(Results.Ok(GasLocationEntry.Start(body.Agreements,body.PurchasePrice,body.SalePrice))),c.RequestAborted);
}));
api.MapPost("/tickets/description",(HttpContext c,TicketDescriptionRequest body)=>body.Product==GasTransportFinancialTicket.Product?
 FinancialTicketRead(c,TicketContext(body.Context),(_,_,_)=>Task.FromResult<object>(GasTransportFinancialTicket.Describe(body.Draft))):FuturesRead(c,TicketContext(body.Context),
 async (connection,data,frame,ct)=>GasTicketStore.Description(body.Product,body.Draft) with{SideCalendars=await SideCalendarPolicies.Catalog(connection,ct)},expectedConvention:PhysicalGasStore.Convention,gasReferenceChoices:true));
api.MapPost("/tickets/choices",(HttpContext c,TicketFieldRequest body)=>TicketDraft.Require(body.Draft).Product==GasTransportFinancialTicket.Product?
 FinancialTicketRead(c,TicketContext(body.Context),(connection,frame,ct)=>transportTicket.Choices(connection,frame,body,ct),requireCapacity:body.Field is "entitlement" or "capacity_version"):FuturesRead(c,TicketContext(body.Context),
 (connection,data,frame,ct)=>GasParkCustodyTicket.IsSelected(body.Draft)?parkCustodyTicket.Choices(connection,data,frame,body,ct):TicketDraft.Require(body.Draft).Product==GasParkLoanTicket.Product?parkLoanTicket.Choices(connection,data,frame,body,ct):gasTicket.Choices(connection,data,frame,body,ct),expectedConvention:PhysicalGasStore.Convention,gasReferenceChoices:true));
api.MapPost("/tickets/lookup",(HttpContext c,TicketFieldRequest body)=>TicketDraft.Require(body.Draft).Product==GasTransportFinancialTicket.Product?
 FinancialTicketRead(c,TicketContext(body.Context),(connection,frame,ct)=>transportTicket.Lookup(connection,frame,body,ct)):FuturesRead(c,TicketContext(body.Context),
 (connection,data,frame,ct)=>GasParkCustodyTicket.IsSelected(body.Draft)?parkCustodyTicket.Lookup(connection,data,frame,body,ct):TicketDraft.Require(body.Draft).Product==GasParkLoanTicket.Product?parkLoanTicket.Lookup(connection,data,frame,body,ct):gasTicket.Lookup(connection,data,frame,body,ct),expectedConvention:PhysicalGasStore.Convention,
 gasReferenceChoices:TicketDraft.Require(body.Draft).Product is GasParkLoanTicket.Product or GasParkCustodyTicket.Product||!GasTicket.RequiresMarketInputs(body.Draft,body.Side,body.Field)));
api.MapPost("/tickets/preview",(HttpContext c,TicketPreviewRequest body)=>TicketDraft.Require(body.Draft).Product==GasTransportFinancialTicket.Product?Locked(c,async session=>{
 var context=TicketContext(body.Context);RequireGasBooking(session,context.KnowledgeMode,context.ContextId);RequireConvention(context,PhysicalGasStore.Convention);
 return Results.Ok(await transportTicket.Preview(Profile(session),Frame(session,context.SnapshotId),body,c.RequestAborted));
}):FuturesRead(c,TicketContext(body.Context),
 (connection,data,frame,ct)=>GasParkCustodyTicket.IsSelected(body.Draft)?parkCustodyTicket.Preview(connection,data,frame,body,ct):TicketDraft.Require(body.Draft).Product==GasParkLoanTicket.Product?parkLoanTicket.Preview(connection,data,frame,body,ct):gasComposition.TicketPreview(connection,data,frame,TicketRequest(body.Draft,body.Context,Guid.Empty,data.DefinitionRevision.RevisionId),ct),expectedConvention:PhysicalGasStore.Convention));
// A booked trade offers day entries by the record operation's own rule, on the read's working or historical selection.
api.MapPost("/tickets/trade",(HttpContext c,TicketTradeRequest body)=>
{
 var context=TicketContext(body.Context);
 if(body.Product==GasTransportFinancialTicket.Product)
  return FinancialTicketRead(c,context,(connection,frame,ct)=>transportTicket.Trade(connection,frame,body,ct));
 var park=body.Product is GasParkLoanTicket.Product or GasParkCustodyTicket.Product;
 GasExerciseAction? exercise=null;GasSwingAction? swing=null;GasSwingAction.WriteAuthority? swingAuthority=null;
 return TradingRead(c,context,(connection,view,ct)=>ReadGasMarket(connection,view,park?null:body.TradeId,async(data,frame)=>{
  if(body.Product==GasParkCustodyTicket.Product)return await parkCustodyTicket.Trade(connection,data,frame,body,ct);
  if(body.Product==GasParkLoanTicket.Product)return await parkLoanTicket.Trade(connection,data,frame,body,ct);
  _=GasTicketStore.Description(body.Product??GasTicket.Description.Product);
  var result=await gasTicket.Trade(connection,data,frame,body,view.Working,swingAuthority,ct);
  exercise=result.Exercise;swing=result.Swing;
  return result.Ticket;
 },ct),expectedConvention:PhysicalGasStore.Convention,publicationCheck:async(connection,_,ct)=>{
  if(exercise is {Allowed:true})await GasExerciseAction.RequireAuthority(connection,database.Manifest.CatalogId,exercise.TradeId,exercise.InstrumentId,ct);
  if(swing?.Admission is {} admission)await GasSwingAction.RequireAuthority(connection,admission,swing.TradeId,swing.InstrumentId,ct);
 },initialAdmission:async(connection,selection,ct)=>{
  // Capture the potential swing action before the working knowledge capture or
  // any bound trade read waits. Only a ticket that publishes it consumes the cap.
  if(!park&&selection.Working)swingAuthority=await GasSwingAction.CaptureDeadline(connection,database.Manifest.CatalogId,ct);
 });
});
api.MapPost("/tickets/status",(HttpContext c,TicketStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
{
 if(body.Product==GasTransportFinancialTicket.Product)
 {
  if(body.Profile!=GasTransportFinancialProfile.Version)throw GasTransportFinancialStore.Source();
  var status=await transportTicket.Status(connection,body.OperationId,ct);
  return Results.Ok(status.GetProperty("found").GetBoolean()?status.GetProperty("outcome"):status);
 }
 if(body.UsesCustody())
 {
  var status=await parkCustodyTicket.Status(connection,body.OperationId,ct);
  return Results.Ok(status.GetProperty("found").GetBoolean()?status.GetProperty("outcome"):status);
 }
 if(body.Product==GasParkLoanTicket.Product)
 {
  var status=await parkLoanTicket.Status(connection,body.OperationId,ct);
  return Results.Ok(status.GetProperty("found").GetBoolean()?status.GetProperty("outcome"):status);
 }
 if(body.Product is not(null or "physical_gas"))throw new MarketDataException("ticket_input","Select a supported ticket product.");
 return Results.Ok(await gasStore.Status(connection,body.OperationId,ct));
},c.RequestAborted)));
api.MapPost("/tickets/amend-preview",(HttpContext c,TicketAmendRequest body)=>FuturesRead(c,TicketContext(body.Context),
 (connection,data,frame,ct)=>gasComposition.TicketPreview(connection,data,frame,GasAmendments.Terms(body,Guid.Empty),ct),
 expectedConvention:PhysicalGasStore.Convention,gasTrade:body.TradeId,amendmentPreview:true));
api.MapPost("/tickets/amend",(HttpContext c,TicketAmendRequest body)=>Locked(c,async session=>{
 var context=TicketContext(body.Context);RequireGasBooking(session,context.KnowledgeMode,context.ContextId);RequireConvention(context,PhysicalGasStore.Convention);
 if(body.OperationId is not {} operation||operation==Guid.Empty)throw new MarketDataException("gas_amendment_input","Supply the amendment request identity.");
 var terms=await gasTicket.RetriedRequest(Profile(session),body.Draft,operation,true,(draft,retained)=>GasAmendments.Terms(body with{Draft=draft},operation,retained),c.RequestAborted);
 return await BookGasComposition(c,session,terms);
}));
// Booking maps the draft against the definition revision its booking read resolves for the selected frame.
api.MapPost("/tickets/book",(HttpContext c,TicketBookRequest body)=>Locked(c,async session=>{
 var context=TicketContext(body.Context);RequireGasBooking(session,context.KnowledgeMode,context.ContextId);RequireConvention(context,PhysicalGasStore.Convention);
 if(TicketDraft.Require(body.Draft).Product==GasTransportFinancialTicket.Product)return await BookTransport(c,session,body);
 if(GasParkCustodyTicket.IsSelected(body.Draft))return await BookParkCustody(c,session,body);
 if(body.Draft.Product==GasParkLoanTicket.Product)return await BookParkLoan(c,session,body);
 var terms=await gasTicket.RetriedRequest(Profile(session),body.Draft,body.OperationId,false,
  (draft,retained)=>GasTicket.Request(draft,context.SnapshotId,context.KnowledgeMode!,body.OperationId,body.DefinitionRevisionId,retained),c.RequestAborted);
 return await BookGasComposition(c,session,terms);
}));

async Task<IResult> BookParkLoan(HttpContext c,PreviewSession session,TicketBookRequest body)=>
 Results.Ok(await parkLoanTicket.Book(Profile(session),Frame(session,body.Context.SnapshotId),body,c.RequestAborted));

async Task<IResult> BookParkCustody(HttpContext c,PreviewSession session,TicketBookRequest body)=>
 Results.Ok(await parkCustodyTicket.Book(Profile(session),Frame(session,body.Context.SnapshotId),body,c.RequestAborted));

async Task<IResult> BookTransport(HttpContext c,PreviewSession session,TicketBookRequest body)=>
 Results.Ok(await transportTicket.Book(Profile(session),Frame(session,body.Context.SnapshotId),body,c.RequestAborted));

api.MapPost("/gas/transport/financial/preview",(HttpContext c,GasTransportFinancialWriteRequest body)=>Locked(c,async session=>{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before reviewing a financial change.");
 return await database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>Results.Ok(await transportFinancial.Preview(connection,body,ct)),c.RequestAborted);
}));
api.MapPost("/gas/transport/financial/record",(HttpContext c,GasTransportFinancialWriteRequest body)=>Locked(c,async session=>{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before recording a financial change.");
 return await database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>Results.Ok(GasTransportFinancialStore.Receipt(body,await transportFinancial.Record(connection,body,ct))),c.RequestAborted);
}));
api.MapPost("/gas/transport/financial/status",(HttpContext c,FacilityStatusRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await transportFinancial.Status(connection,body,ct)),c.RequestAborted)));
api.MapPost("/gas/transport/financial/setup",(HttpContext c,LifecycleDiscoveryRequest body)=>Locked(c,session=>
 database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await GasTransportFinancialSetup.Read(connection,database,body,ct)),c.RequestAborted)));

api.MapPost("/gas/park-custody/evidence",(HttpContext c,GasParkCustodyEvidenceRequest body)=>Locked(c,async session=>
{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before recording custody observations.");
 var result=await database.WithIdentity(Profile(session),"RecordGasOperations",(connection,ct)=>GasParkCustodyEvidenceStore.Record(connection,database,body,ct),c.RequestAborted);
 return Results.Ok(result.Outcome);
}));
api.MapPost("/gas/park-custody/setup",(HttpContext c,LifecycleDiscoveryRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadGasOperations",
 async(connection,ct)=>Results.Ok(await GasParkCustodySetup.Read(connection,database,body,ct)),c.RequestAborted)));
api.MapPost("/gas/park-custody/evidence/status",(HttpContext c,GasParkCustodyEvidenceRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"RecordGasOperations",
 async(connection,ct)=>Results.Ok(await GasParkCustodyEvidenceStore.Status(connection,database,body,ct)),c.RequestAborted)));

api.MapPost("/physical/operations/directory",(HttpContext c,PhysicalDirectoryRequest body)=>TradingRead(c,
 body.Context??throw new MarketDataException("position_facts","Supply the selected header context."),
 (connection,view,ct)=>tradeReads.PhysicalPositions(connection,view,body.BookIds,body.PortfolioIds,body.Desk,body.Offset,body.Limit,ct),expectedConvention:TradingBlotter.Convention));
// The foreground reuse check and the update waiter share the exact authorization/selection contract.
Task<string> BlotterVersion(PreviewSession session,FuturesReadRequest body,CancellationToken cancellation)=>
 database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
{
 RequireConvention(body,TradingBlotter.Convention);
 var selection=Capture(session,body);
 await RequireReadAccess(connection,ct);
 if(!selection.Working)
 {
  var view=await selection.Resolve(connection,knowledgeStore,body,ct);
  await view.Bind(connection,database.Manifest.CatalogId,ct);
 }
 await MarketBundle.RequireReferenceVisibility(connection,database.Manifest,ct);
 var version=await BlotterFreshness.Read(connection,database.Manifest,body,selection,ct);
 await RequireReadAccess(connection,ct);selection.RequireSession(session);
 return version;
},cancellation);
api.MapPost("/trading/changes",async Task<IResult>(HttpContext c,BlotterChangesRequest body)=>
{
 if(body.Context is null)throw new MarketDataException("blotter_context","Supply the exact displayed selection.");
 var session=Session(c);var profile=Profile(session);var ct=c.RequestAborted;
 if(!await session.BlotterWait.WaitAsync(0,ct))throw new MarketDataException("blotter_wait_active","Only one Blotter update wait is allowed per session.");
 try
 {
  async Task<string> Read()
  {
   await session.Gate.WaitAsync(ct);
   try
   {
    if(!ReferenceEquals(session,Session(c))||session.Profile!=profile)throw new PreviewDeniedException();
    return await BlotterVersion(session,body.Context,ct);
   }
   finally{session.Gate.Release();}
  }
  return Results.Ok(new{version=await BlotterFreshness.Wait(changes,body.Version,Read,ct)});
 }
 finally{session.BlotterWait.Release();}
});
api.MapPost("/trading/gas",(HttpContext c,TradePositionReadRequest body)=>TradingRead(c,body.Context,
    (connection,view,ct)=>tradeReads.GasTrade(connection,view,body,ct),expectedConvention:TradingBlotter.Convention));
api.MapPost("/trading/side-schedules",(HttpContext c,SideScheduleReadRequest body)=>TradingRead(c,body.Position?.Context??throw new MarketDataException("side_schedule_inspection","Select the exact position context."),
    (connection,view,ct)=>tradeReads.SideSchedule(connection,view,body,ct),expectedConvention:TradingBlotter.Convention));
api.MapPost("/trading/crude",(HttpContext c,TradePositionReadRequest body)=>TradingRead(c,body.Context,
    (connection,view,ct)=>tradeReads.CrudeTrade(connection,view,body,ct),expectedConvention:TradingBlotter.Convention));
api.MapPost("/trading/lng",(HttpContext c,TradePositionReadRequest body)=>TradingRead(c,body.Context,
    (connection,view,ct)=>tradeReads.LngTrade(connection,view,body,ct),expectedConvention:TradingBlotter.Convention));
api.MapPost("/trading/position",(HttpContext c,TradePositionReadRequest body)=>TradingRead(c,
 body.Context??throw new MarketDataException("position_context","Supply the selected context."),
 (connection,view,ct)=>tradeReads.Position(connection,view,body,ct),expectedConvention:TradingBlotter.Convention));
api.MapPost("/trading/close-out-draft",(HttpContext c,CloseOutDraftRequest body)=>TradingRead(c,
 body.Context??throw new MarketDataException("close_out","Supply the selected context."),
 (connection,view,ct)=>tradeReads.CloseOutDraft(connection,view,body,ct),expectedConvention:TradingBlotter.Convention));
api.MapPost("/trading/position-adjustment-draft",(HttpContext c,PositionAdjustmentDraftRequest body)=>TradingRead(c,
 body.Context??throw new MarketDataException("close_out","Supply the selected context."),
 (connection,view,ct)=>tradeReads.PositionAdjustmentDraft(connection,view,body,ct),expectedConvention:TradingBlotter.Convention));
// The workspace's Blotter valuation as NDJSON frames (D268):
// refused as trading/workspace refuses before start; then start, the rows of each completed unit, and complete once the
// result passes the final recheck under the session gate. The read runs outside the gate and authorizes every frame.
api.MapPost("/trading/workspace/stream",async(HttpContext c,TradingWorkspaceStreamRequest body)=>
{
 if(body.BookingCatalog is not(null or "directory" or "priced_contracts"))throw new MarketDataException("blotter_view","Choose an available booking catalog projection.");
 var context=body.Context??throw new MarketDataException("blotter_view","Supply the selected context.");
 var writer=new BlotterStreamWriter(c.Response.Body,tradingJson);
 c.Items[typeof(BlotterStreamWriter)]=writer;
 var positions=0;
 var result=await Concurrent(c,context,(session,capture)=>TradingTransaction(Profile(session),capture,context,
  (_,_,_)=>{RequireConvention(context,TradingBlotter.Convention);return Task.FromResult<int?>(null);},async(connection,selection,_,resolve,ct)=>
 {
  // The reader's authority on the run's own connection and epoch, checked before every frame: start and each rows frame.
  async Task Authorize(CancellationToken ct)
  {
   await RequireReadAccess(connection,ct);
   await database.RequireSignedIn(connection,ct);
   selection.RequireSession(session);
  }
  var view=await resolve();
  var value=await tradeReads.WorkspaceRun(connection,view,body.View,body.Summaries.IsDefault?[]:body.Summaries,body.BookingCatalog=="directory",async run=>
  {
   positions=run.Positions;
   var start=new System.Text.Json.Nodes.JsonObject{["positions"]=positions};
   await Rights(connection,start,view,selection,true,ct);
   // Refused here, nothing has started: the refusal is an ordinary HTTP error.
   await Authorize(ct);
   c.Response.StatusCode=StatusCodes.Status200OK;
   c.Response.ContentType=BlotterStreamWriter.ContentType;
   c.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
   await c.Response.StartAsync(ct);
   await writer.Start(start,ct);
   var rows=await writer.Rows(run.Units(ct),positions,body.View.Mode=="trades",Authorize,ct);
   // The rows have been sent; complete carries the rest of the report.
   return TradingBlotter.WithoutRows(run.Finish(rows));
  },ct);
  var response=JsonSerializer.SerializeToNode(value,tradingJson)!.AsObject();
  response["blotter"]!.AsObject().Remove("holdings");
  foreach(var json in new[]{response["catalog"]!.AsObject(),response["blotter"]!.AsObject()})await Rights(connection,json,view,selection,json["books"] is not null,ct);
  return (response,view);
 },c.RequestAborted),
  async(connection,view,ct)=>{await tradeReads.Positions(connection,view,body.View.BookIds,body.View.PortfolioIds,0,1,ct);});
 var complete=new System.Text.Json.Nodes.JsonObject{["valued"]=positions,["positions"]=positions};
 foreach(var name in new[]{"blotter","summaries","catalog"}){var node=result[name];result.Remove(name);complete[name]=node;}
 await writer.Complete(complete,c.RequestAborted);
 return Results.Empty;
});
api.MapPost("/swaps/catalog",(HttpContext c,FuturesReadRequest body)=>FuturesRead(c,body,swapStore.Catalog,expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/settlement/preview",(HttpContext c,SwapSettlementPreviewRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("settlement_context","Supply the selected certified context."),
 (connection,data,frame,ct)=>swapStore.SettlementPreview(connection,data,frame,body,ct),expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/settlement/inspect",(HttpContext c,SwapSettlementReadRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("settlement_context","Supply the selected certified context."),
 (connection,data,frame,ct)=>swapStore.InspectSettlement(connection,data,frame,body,ct),headTrade:body.TradeId,expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/settlement/pnl",(HttpContext c,SwapPnlRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
{
 if(body.Opening is null||body.Closing is null||body.Opening.Convention!=SwapStore.Convention||body.Closing.Convention!=SwapStore.Convention)
  throw new MarketDataException("settlement_context","Supply both explicit swap contexts.");
 var openingFrame=Frame(session,body.Opening.SnapshotId);var closingFrame=Frame(session,body.Closing.SnapshotId);
 if(openingFrame.At>closingFrame.At)throw new MarketDataException("settlement_bridge","The closing instant must not precede the opening instant.");
 var opening=await TradeView(connection,session,body.Opening,openingFrame,ct);var closing=await TradeView(connection,session,body.Closing,closingFrame,ct);
 return Results.Ok(await new SwapSettlementOperations(database).Reconcile(connection,opening,closing,body,ct));
},c.RequestAborted)));
api.MapPost("/swaps/settlement/determine",(HttpContext c,SwapSettlementDetermineRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>
{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before determining cash.");
 if(body.Preview?.Context is not {} context||context.Convention!=SwapStore.Convention)throw new MarketDataException("settlement_context","Supply the supported swap calculation context.");
 c.Items["swap_attempted"]=true;
 await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102)"))await gate.ExecuteNonQueryAsync(ct);
 return Results.Ok(await swapStore.DetermineSettlement(connection,Frame(session,context.SnapshotId),body,ct));
},c.RequestAborted)));
api.MapPost("/swaps/settlement/net-preview",(HttpContext c,SwapNetPreviewRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("settlement_context","Supply the selected certified context."),
 (connection,data,frame,ct)=>swapStore.PreviewSwapNet(connection,data,frame,body,ct),expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/settlement/net",(HttpContext c,SwapNetRecordRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>
{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before recording net settlement.");
 if(body.Preview?.Context is not {} context)throw new MarketDataException("settlement_context","Supply the selected recording context.");
 c.Items["swap_attempted"]=true;
 await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102)"))await gate.ExecuteNonQueryAsync(ct);
 return Results.Ok(await swapStore.RecordSwapNet(connection,Frame(session,context.SnapshotId),body,ct));
},c.RequestAborted)));
api.MapPost("/swaps/settlement/payment",(HttpContext c,SwapCashRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>
{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before recording cash.");
 if(body.Context is null)throw new MarketDataException("settlement_context","Supply the selected recording context.");
 c.Items["swap_attempted"]=true;
 await using(var gate=ScheduleStore.Command(connection,"SELECT pg_advisory_xact_lock(730849102)"))await gate.ExecuteNonQueryAsync(ct);
 return Results.Ok(await swapStore.RecordSwapCash(connection,Frame(session,body.Context.SnapshotId),body,ct));
},c.RequestAborted)));
api.MapPost("/swaps/settlement/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
{
 if(body.OperationId==Guid.Empty)throw new MarketDataException("settlement_input","Supply the retained operation identity.");
 return Results.Ok(await SwapSettlementStatus.Read(connection,database,body.OperationId,ct));
},c.RequestAborted)));
api.MapPost("/swaps/contract-observation-preview",(HttpContext c,SwapContractObservationPreviewRequest body)=>FuturesRead(c,
 body.Context??throw new MarketDataException("swap_context","Supply the selected observation context."),
 (connection,data,frame,ct)=>swapStore.ContractObservationPreview(connection,data,frame,body,ct),expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/observation-options",(HttpContext c,SwapObservationOptionsRequest body)=>FuturesRead(c,
 body.Context??throw new MarketDataException("swap_context","Supply the selected observation context."),
 (connection,data,frame,ct)=>swapStore.ObservationOptions(connection,data,frame,body,ct),expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/quantity-schedule",(HttpContext c,SwapEditorRequest body)=>TradingRead(c,body.Context,
 (connection,view,ct)=>tradeReads.Read(connection,view,data=>swapStore.QuantitySchedule(connection,view.CalculationFrame,body.TradeId,body.Revision,ct),ct),headTrade:body.TradeId,expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/scheduled-periods",(HttpContext c,SwapScheduledPeriodsRequest body)=>FuturesRead(c,body.Context,
 (connection,data,frame,ct)=>Task.FromResult(SwapStore.ScheduledPeriods(body)),expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/scheduled-price",(HttpContext c,SwapScheduledPriceRequest body)=>FuturesRead(c,new(body.Terms.SnapshotId,body.Terms.Convention,body.Terms.KnowledgeMode),
 (connection,data,frame,ct)=>swapStore.ScheduledDraftPrice(connection,data,frame,body,ct),headTrade:body.Terms.TradeId,amendmentPreview:body.Terms.TradeId is not null,expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/preview",(HttpContext c,SwapWriteRequest body)=>FuturesRead(c,new(body.SnapshotId,body.Convention,body.KnowledgeMode),
 (connection,data,frame,ct)=>swapStore.Preview(connection,data,frame,body,ct),headTrade:body.TradeId,amendmentPreview:body.TradeId is not null,expectedConvention:SwapStore.Convention));
Task<IResult> SaveSwap(HttpContext c,SwapWriteRequest body,bool amend)=>Locked(c,async session=>
{
 if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data to book or amend.");
 if(amend!=(body.TradeId is not null))throw new MarketDataException("swap_input","Choose booking or latest-head amendment explicitly.");
 c.Items["swap_attempted"]=true;
 var profile=Profile(session);var frame=Frame(session,body.SnapshotId);
 return Results.Ok(await new SwapBookingOperations(database).Save(profile,frame,body,amend,c.RequestAborted));
});
api.MapPost("/swaps/book",(HttpContext c,SwapWriteRequest body)=>SaveSwap(c,body,false));
api.MapPost("/swaps/amend",(HttpContext c,SwapWriteRequest body)=>SaveSwap(c,body,true));
api.MapPost("/swaps/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>Results.Ok(await swapStore.Status(connection,body.OperationId,ct)),c.RequestAborted)));
api.MapPost("/futures/price",(HttpContext c,FuturesDraftRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("draft_context","Choose the draft market context."),(connection,data,frame,ct)=>futuresStore.PriceDraft(connection,data,frame,body,ct)));
api.MapPost("/futures/draft-risk",(HttpContext c,FuturesDraftRiskRequest body)=>FuturesRead(c,body.Draft?.Context??throw new MarketDataException("draft_context","Choose the draft and market context."),(connection,data,frame,ct)=>futuresStore.RiskDraft(connection,data,frame,body,ct),dealRisk:true));
api.MapPost("/swaps/price",(HttpContext c,SwapDraftRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("draft_context","Choose the draft market context."),(connection,data,frame,ct)=>swapStore.PriceDraft(connection,data,frame,body,ct),expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/draft-risk",(HttpContext c,SwapDraftRiskRequest body)=>FuturesRead(c,body.Draft?.Context??throw new MarketDataException("draft_context","Choose the draft and market context."),(connection,data,frame,ct)=>swapStore.RiskDraft(connection,data,frame,body,ct),expectedConvention:SwapStore.Convention,dealRisk:true));
var portfolioRisk=new PortfolioRiskPreview(database);
var pnlReports=new TradingPnlReport(database);
var pnlArtifacts=new PnlReportArtifacts(database,app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(ApplicationDataProtection.PnlPurpose));
app.Lifetime.ApplicationStopped.Register(pnlArtifacts.Dispose);
api.MapGet("/portfolio/pnl/policies",()=>Results.Ok(TradingPnlReport.Policies));
// The run captures the knowledge cutoff of both dates in a transaction of its own, whose commit releases the publication
// gate (D420), and values in a second. What the request and its two frames refuse comes before the capture, so a run
// refused there stores no context. Each bind of the second transaction reads the stored contexts again.
api.MapPost("/portfolio/pnl/run",(HttpContext c,TradingPnlRequest body)=>Locked(c,async session=>
{
    var profile=Profile(session);
    var dated=await database.WithIdentity(profile,"RunScenario",async(connection,ct)=>
    {
        if(body.Opening is not {KnowledgeMode:"at",ContextId:null,Convention:TradingBlotter.Convention}||body.Closing is not {KnowledgeMode:"at",ContextId:null,Convention:TradingBlotter.Convention}||body.KnownAt is null)
            throw new MarketDataException("pnl_context","Supply both explicit trading frames and one exact shared knowledge cutoff.");
        var owner=await PnlReportArtifacts.Owner(connection,ct);
        var opening=Frame(session,body.Opening.SnapshotId);var closing=Frame(session,body.Closing.SnapshotId);
        TradingPnlReport.Admit(body,opening,closing);
        async Task<TradingView> PnlView(InputFrame frame)=>TradingView.Reread(frame with{ContextId=null},
            (await knowledgeStore.Capture(connection,new(frame.BusinessDate.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),body.KnownAt,"at"),ct)).Context);
        return (Owner:owner,Opening:await PnlView(opening),Closing:await PnlView(closing));
    },c.RequestAborted);
    return await database.WithIdentity(profile,"RunScenario",async(connection,ct)=>
    {
        if(await PnlReportArtifacts.Owner(connection,ct)!=dated.Owner)throw new PreviewDeniedException();
        var calculation=(PnlCalculated)await pnlReports.Run(connection,dated.Opening,dated.Closing,body,ct);
        if(await PnlReportArtifacts.Owner(connection,ct)!=dated.Owner)throw new PreviewDeniedException();
        var capture=pnlArtifacts.Seal(dated.Owner,calculation,DateTimeOffset.UtcNow);
        return Results.Ok(new{report=calculation.Report,artifact=capture.Artifact,saveRefusal=capture.Refusal});
    },c.RequestAborted);
}));
api.MapPost("/portfolio/pnl/save",(HttpContext c,SavePnlReportRequest body)=>Locked(c,async session=>
{
    var result=await database.WithIdentity(Profile(session),"RunScenario",async(connection,ct)=>Results.Ok(await pnlArtifacts.Save(connection,body,ct)),c.RequestAborted);
    pnlArtifacts.Release(body.Artifact); // Release CPU memo only after the durable transaction commits.
    return result;
}));
api.MapGet("/portfolio/pnl/saved",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"RunScenario",async(connection,ct)=>
    Results.Ok(await pnlArtifacts.List(connection,ct)),c.RequestAborted))));
api.MapGet("/portfolio/pnl/saved/{id:guid}",(HttpContext c,Guid id)=>Locked(c,session=>database.WithIdentity(Profile(session),"RunScenario",async(connection,ct)=>
    Results.Ok(await pnlArtifacts.Read(connection,id,ct)),c.RequestAborted)));
Task<IResult> PortfolioRead(HttpContext c,FuturesReadRequest body,Func<NpgsqlConnection,PreviewData,InputFrame,CancellationToken,Task<object>> read)=>
    TradingRead(c,body,(connection,view,ct)=>tradeReads.Read(connection,view,
        data=>read(connection,data,view.CalculationFrame,ct),ct,
        referenceScope:ReferenceReadScope.TradeValuation,observationScope:ObservationReadScope.ExactPins),
        scenario:true,expectedConvention:TradingBlotter.Convention);
async Task<IResult> QueuePortfolioRisk(HttpContext c,SubmitPortfolioRiskJob body)
{
    var prior=await WithSession(c,session=>database.WithIdentity(Profile(session),"RunScenario",
        (connection,ct)=>RiskJobs.Replay(connection,database.Manifest.CatalogId,body,ct),c.RequestAborted));
    if(prior is not null)return Results.Ok(prior);
    return await PortfolioRead(c,body.Request?.Context??throw new MarketDataException("risk_job_context","Supply the exact input selection."),
        (connection,data,frame,ct)=>
        {c.Items["risk_job_attempted"]=true;return RiskJobs.Submit(connection,database.Manifest,portfolioRisk,data,frame,body,ct);});
}
api.MapPost("/portfolio/risk/setup",(HttpContext c,PortfolioRiskCatalogRequest body)=>TradingRead(c,
    body.Context??throw new MarketDataException("risk_context","Select the shared financial context."),
    (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>portfolioRisk.Setup(connection,view.CalculationFrame,body,ct),ct),
    scenario:true,expectedConvention:TradingBlotter.Convention));
api.MapPost("/physical/power-trades/hedges",(HttpContext c,StructuredPowerHedgeRequest body)=>PortfolioRead(c,
    body.Context??throw new MarketDataException("risk_context","Select the shared financial context."),
    async(connection,data,frame,ct)=>
    {
        if(!await PreviewDatabase.HasPermission(connection,"ReadPhysical",ct))throw new PreviewDeniedException();
        var result=await new StructuredPowerHedgeService(database).Calculate(connection,data,frame,body,ct);
        if(!await PreviewDatabase.HasPermission(connection,"ReadPhysical",ct))throw new PreviewDeniedException();
        return result;
    }));
api.MapPost("/portfolio/risk/economic/sources",(HttpContext c,PortfolioEconomicSourceRequest body)=>PortfolioRead(c,
    body.Scope?.Context??throw new MarketDataException("risk_economic_scope","Select the exact economic opening scope."),
    (connection,data,frame,ct)=>portfolioRisk.EconomicSources(connection,data,frame,body,ct)));
api.MapPost("/portfolio/risk/economic/cash-funding",(HttpContext c,PortfolioEconomicCashFundingRequest body)=>PortfolioRead(c,
    body.Scope?.Context??throw new MarketDataException("risk_economic_scope","Select the exact economic opening scope."),
    (connection,data,frame,ct)=>portfolioRisk.EconomicCashFunding(connection,data,frame,body,ct)));
api.MapPost("/portfolio/risk/economic/setup",(HttpContext c,PortfolioRiskCatalogRequest body)=>PortfolioRead(c,
    body.Context??throw new MarketDataException("risk_economic_scope","Select the exact economic opening scope."),
    (connection,data,frame,ct)=>portfolioRisk.EconomicSetup(connection,data,frame,body,ct)));
api.MapPost("/portfolio/risk/economic/history",(HttpContext c,PortfolioEconomicHistoryRequest body)=>PortfolioRead(c,
    body.Source?.Scope?.Context??throw new MarketDataException("risk_economic_scope","Select the exact economic opening scope."),
    (connection,data,frame,ct)=>portfolioRisk.EconomicHistory(connection,data,frame,body,ct)));
api.MapPost("/portfolio/risk/parametric",(HttpContext c,PortfolioParametricRequest body)=>
{
    var request=PortfolioRiskPreview.StatisticalRequest(body);
    return PortfolioRead(c,request.Context,(connection,data,frame,ct)=>portfolioRisk.Calculate(connection,data,frame,request,ct));
});
api.MapPost("/portfolio/risk/monte-carlo",(HttpContext c,PortfolioMonteCarloRequest body)=>
{
    var request=PortfolioRiskPreview.MonteCarloRequest(body);
    return PortfolioRead(c,request.Context,(connection,data,frame,ct)=>portfolioRisk.Calculate(connection,data,frame,request,ct));
});
api.MapPost("/portfolio/risk/monte-carlo/jobs",(HttpContext c,SubmitMonteCarloRiskJob body)=>
{
    var request=PortfolioRiskPreview.MonteCarloRequest(body.Request);
    return QueuePortfolioRisk(c,new(body.RequestId,request));
});
api.MapPost("/portfolio/risk/historical",(HttpContext c,PortfolioHistoricalRequest body)=>
{
    var request=PortfolioRiskPreview.HistoricalRequest(body);
    return PortfolioRead(c,request.Context,(connection,data,frame,ct)=>portfolioRisk.Calculate(connection,data,frame,request,ct));
});
api.MapPost("/portfolio/risk/historical/summary",(HttpContext c,PortfolioHistoricalRequest body)=>
{
    var request=PortfolioRiskPreview.HistoricalRequest(body);
    return PortfolioRead(c,request.Context,(connection,data,frame,ct)=>portfolioRisk.HistoricalSummary(connection,data,frame,body,ct));
});
api.MapPost("/portfolio/risk/historical/sample",(HttpContext c,PortfolioHistoricalSampleRequest body)=>
{
    var request=PortfolioRiskPreview.HistoricalRequest(body?.Request!);
    return PortfolioRead(c,request.Context,(connection,data,frame,ct)=>portfolioRisk.HistoricalSample(connection,data,frame,body!,ct));
});
api.MapPost("/portfolio/risk/historical/jobs",(HttpContext c,SubmitHistoricalRiskJob body)=>
{
    var request=PortfolioRiskPreview.HistoricalRequest(body.Request);
    return QueuePortfolioRisk(c,new(body.RequestId,request));
});
api.MapPost("/portfolio/risk/statistical/setup",(HttpContext c,PortfolioRiskCatalogRequest body)=>PortfolioRead(c,
    body.Context??throw new MarketDataException("risk_context","Select the shared financial context."),
    (connection,data,frame,ct)=>portfolioRisk.StatisticalSetup(connection,data,frame,body,ct)));
api.MapPost("/portfolio/risk/parametric/summary",(HttpContext c,PortfolioParametricRequest body)=>
{
    var request=PortfolioRiskPreview.StatisticalRequest(body);
    return PortfolioRead(c,request.Context,(connection,data,frame,ct)=>portfolioRisk.ParametricSummary(connection,data,frame,body,ct));
});
api.MapPost("/portfolio/risk/parametric/jobs",(HttpContext c,SubmitParametricRiskJob body)=>
{
    var request=PortfolioRiskPreview.StatisticalRequest(body.Request);
    return QueuePortfolioRisk(c,new(body.RequestId,request));
});
api.MapPost("/portfolio/risk/jobs",(HttpContext c,SubmitPortfolioRiskJob body)=>QueuePortfolioRisk(c,body));
api.MapPost("/portfolio/risk/jobs/{id:guid}/cancel",(HttpContext c,Guid id)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"RunScenario",async(connection,ct)=>Results.Ok(await RiskJobs.Read(connection,database.Manifest.CatalogId,id,ct,cancel:true,expectedConvention:PortfolioRiskPreview.Convention)),c.RequestAborted)));
api.MapGet("/risk/definitions",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),"RunScenario",async(connection,ct)=>Results.Ok(await RiskRunDefinitions.List(connection,database.Manifest.CatalogId,ct)),c.RequestAborted))));
api.MapPost("/risk/definitions",(HttpContext c,SaveRiskRunDefinition body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"RunScenario",async(connection,ct)=>Results.Ok(await RiskRunDefinitions.Save(connection,database.Manifest.CatalogId,body,ct)),c.RequestAborted)));
api.MapPost("/risk/definitions/{id:guid}/run",(HttpContext c,Guid id,RunRiskDefinition body)=>PortfolioRead(c,
    body.Context??throw new MarketDataException("risk_job_context","Supply the definition version and exact retained context."),
    async(connection,data,frame,ct)=>
    {
        c.Items["risk_job_attempted"]=true;
        return (object)await RiskRunDefinitions.Run(connection,database.Manifest,portfolioRisk,data,frame,id,body,ct);
    }));
api.MapPost("/risk/definitions/{id:guid}/latest",(HttpContext c,Guid id,LatestRiskRun body)=>TradingRead(c,
    body.Context??throw new MarketDataException("risk_context","Select the shared financial context."),
    (connection,view,ct)=>tradeReads.LatestRiskRun(connection,view,id,body.RunId,ct),scenario:true,expectedConvention:TradingBlotter.Convention));
api.MapPost("/futures/book",(HttpContext c,FuturesBookRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before booking or amending.");
    c.Items["futures_attempted"]=true;
    return await database.WithIdentity(Profile(session),"BookTrade",async(connection,ct)=>
    {
        if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct))throw new PreviewDeniedException();
        return Results.Ok(await futuresStore.Book(connection,await MarketInputSelection.ReadTerms(database,connection,body.DefinitionRevisionId,ct),Frame(session,body.SnapshotId),body,ct));
    },c.RequestAborted);
}));
api.MapPost("/futures/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
    Results.Ok(await futuresStore.Status(connection,body.OperationId,ct)),c.RequestAborted)));
// Actual currency cash recording is a shared membership operation, independently of the parent product.
api.MapPost("/cash-payments/record",(HttpContext c,RecordCashPaymentRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before recording a payment.");
    return await database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>
        Results.Ok(await CashPaymentStore.Record(connection,database.Manifest.CatalogId,Frame(session,body.SnapshotId),body,ct)),c.RequestAborted);
}));
api.MapPost("/cash-payments/status",(HttpContext c,RecordCashPaymentRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"AmendTrade",
    async(connection,ct)=>Results.Ok(await CashPaymentStore.Status(connection,database,body,ct)),c.RequestAborted)));
// Listed futures options: the bookable series at a frame, and booking with premium paid up front under the futures booking authority.
var optionStore=new FuturesOptionStore(database);
api.MapPost("/options/catalog",(HttpContext c,FuturesReadRequest body)=>FuturesRead(c,body,optionStore.Catalog,expectedConvention:FuturesOptionStore.Convention));
api.MapPost("/options/listings",(HttpContext c,OptionListingsRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("option_context","Supply the selected context."),
    (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>OptionListings.Read(connection,database,view,body,ct),ct),expectedConvention:FuturesOptionStore.Convention));
// Option bookings and lifecycle events, written by a human under the booking authority; no agent tool writes them.
Task<IResult> OptionWrite(HttpContext c,Guid snapshot,Func<NpgsqlConnection,InputFrame,CancellationToken,Task<JsonElement>> write)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before booking or recording option events.");
    c.Items["futures_attempted"]=true;
    return await database.WithIdentity(Profile(session),"BookTrade",async(connection,ct)=>
    {
        if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct))throw new PreviewDeniedException();
        return Results.Ok(await write(connection,Frame(session,snapshot),ct));
    },c.RequestAborted);
});
api.MapPost("/options/book",(HttpContext c,FuturesOptionBookRequest body)=>OptionWrite(c,body.SnapshotId,(connection,frame,ct)=>optionStore.Book(connection,frame,body,ct)));
api.MapPost("/options/status",(HttpContext c,FuturesOptionStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
    Results.Ok(await optionStore.Status(connection,body.OperationId,ct)),c.RequestAborted)));
api.MapPost("/options/exercise",(HttpContext c,FuturesOptionEventRequest body)=>OptionWrite(c,body.SnapshotId,(connection,frame,ct)=>optionStore.Close(connection,frame,body,"exercise",ct)));
api.MapPost("/options/assignment",(HttpContext c,FuturesOptionEventRequest body)=>OptionWrite(c,body.SnapshotId,(connection,frame,ct)=>optionStore.Close(connection,frame,body,"assignment",ct)));
api.MapPost("/options/instruction",(HttpContext c,FuturesOptionInstructionRequest body)=>OptionWrite(c,body.SnapshotId,(connection,frame,ct)=>optionStore.Instruct(connection,frame,body,ct)));
api.MapPost("/options/expiry",(HttpContext c,FuturesOptionExpiryRequest body)=>OptionWrite(c,body.SnapshotId,(connection,frame,ct)=>optionStore.Expire(connection,frame,body,ct)));
api.MapPost("/options/holding",(HttpContext c,FuturesOptionHoldingRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("option_context","Supply the selected context."),
    (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>optionStore.Holding(connection,body.TradeId,ct),ct),expectedConvention:FuturesOptionStore.Convention));
// Booked option positions with their series' stored analytics, for those who read trades and market data. It prices nothing.
api.MapPost("/options/positions",(HttpContext c,OptionPositionsRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("option_context","Supply the selected context."),
    (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>OptionPositionAnalytics.Read(connection,database,view,body,ct),ct),expectedConvention:FuturesOptionStore.Convention));
// A strategy of option legs on one expiry: what it may be built from, read from the product's stored surface, and its
// analysis, which calibrates the legs as valuation does and values them under the shared scenario layers. Both are reads
// for those who read trades and market data, and book and store nothing; the analysis is also an asynchronous read.
api.MapPost("/options/strategy/setup",(HttpContext c,OptionStrategySetupRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("option_context","Supply the selected context."),
    (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>OptionStrategyRead.Setup(connection,database,view,body,ct),ct),expectedConvention:FuturesOptionStore.Convention));
// Volatility surfaces: a fit is a job on the retained risk queue, admitted over working data by those who may publish market data
// and read trades; the read returns the stored revision fitted at the selected close and known at the selected context, and the
// library lists the stored surfaces with the close each belongs to. No agent tool fits.
api.MapPost("/vol-surfaces/fit",(HttpContext c,VolSurfaceFitRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data to fit a volatility surface.");
    c.Items["risk_job_attempted"]=true;
    return await database.WithIdentity(Profile(session),"PublishMarketData",async(connection,ct)=>
    {
        if(!await PreviewDatabase.HasPermission(connection,"ReadTrades",ct))throw new PreviewDeniedException();
        var view=await TradingView.Capture(connection,knowledgeStore,Frame(session,body.SnapshotId),ct);
        return Results.Ok(await tradeReads.ReadBound(connection,view,()=>VolSurfaceStore.Submit(connection,database,view,body,ct),ct));
    },c.RequestAborted);
}));
api.MapPost("/vol-surfaces/read",(HttpContext c,VolSurfaceReadRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("vol_surface_context","Supply the selected context."),
    (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>VolSurfaceStore.Read(connection,database,view,body,ct),ct),expectedConvention:FuturesOptionStore.Convention));
api.MapPost("/vol-surfaces/axes",(HttpContext c,VolSurfaceAxesRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("vol_surface_context","Supply the selected context."),
    (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>VolSurfaceStore.Axes(connection,database,view,body,ct),ct),expectedConvention:FuturesOptionStore.Convention));
api.MapPost("/vol-surfaces/library",(HttpContext c,VolSurfaceLibraryRequest body)=>TradingRead(c,body.Context??throw new MarketDataException("vol_surface_context","Supply the selected context."),
    (connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>VolSurfaceStore.Library(connection,database,view,body,ct),ct),expectedConvention:FuturesOptionStore.Convention));
api.MapGet("/jobs",(HttpContext c,int? page,int? collectionPage)=>Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>Results.Ok(await CollectionJobs.Directory(connection,ct,page??0,collectionPage??0)),c.RequestAborted)));
api.MapPost("/jobs/collection/update",(HttpContext c,CollectionJobUpdate body)=>Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>{c.Items["automation_attempted"]=true;return Results.Ok(await CollectionJobs.Update(connection,body,ct));},c.RequestAborted)));
api.MapPost("/infrastructure/pool/scale",(HttpContext c,PoolScaleRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>{c.Items["automation_attempted"]=true;return Results.Ok(await JobOperations.ScalePool(connection,body,ct));},c.RequestAborted)));
api.MapGet("/infrastructure/pool",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),null,
    async(connection,ct)=>Results.Ok(await JobOperations.PoolStatus(connection,ct)),c.RequestAborted))));
api.MapGet("/infrastructure/logs/{kind}",(HttpContext c,string kind)=>Locked(c,session=>database.WithIdentity(Profile(session),"ViewWorkerDiagnostics",
    async(connection,ct)=>Results.Ok(await JobOperations.PoolLogs(connection,kind,ct)),c.RequestAborted)));
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
// Settings › AI administers the page assistant (docs/ai-assistant.md §9, §11).
var (modelProviders,modelCatalog)=(new ModelProviders(custody,database.Manifest,checkEndpoint),new ModelCatalog());
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
api.MapGet("/settings/ai/page-prompts",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ManageAssistantPrompts",async(connection,ct)=>Results.Ok(await AssistantPagePrompts.Directory(connection,ct)),c.RequestAborted))));
api.MapPost("/settings/ai/page-prompts/save",(HttpContext c,SavePagePromptRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ManageAssistantPrompts",async(connection,ct)=>Results.Ok(await AssistantPagePrompts.Save(connection,body,ct)),c.RequestAborted)));
api.MapPost("/settings/ai/agents/create",(HttpContext c,CreateAgentRequest body)=>AgentCommand(c,(connection,ct)=>AgentAdministration.Create(connection,body,ct),control:true));
api.MapPost("/settings/ai/agents/save",(HttpContext c,SaveAgentRequest body)=>AgentCommand(c,(connection,ct)=>AgentAdministration.Save(connection,body,ct)));
api.MapPost("/settings/ai/agents/save-operating-role",(HttpContext c,SaveAgentOperatingRoleRequest body)=>AgentCommand(c,(connection,ct)=>AgentAdministration.SaveOperatingRole(connection,body,ct)));
api.MapPost("/settings/ai/agents/publish",(HttpContext c,AgentCommandRequest body)=>AgentCommand(c,(connection,ct)=>AgentAdministration.Publish(connection,body,ct),control:true));
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
// Turns (§7.2, §7.4, §8). An ask retains the selection its page shows: current knowledge only while no historical context
// is selected, a retained context only when it is the selected one, and a frame only of the session's current market
// context. Requested financial reads capture the frame's date; facility questions name an independent operating date.
var agentTurns=new AgentTurns(database,turnScope,agentWorker,turnWorker,new AgentTools(database,marketReads,scheduleReads,partyReads,tradeReads,app.Logger){Workspace=assistantWorkspace});
(string Mode,Guid? Retained,InputFrame? Frame) Knowledge(PreviewSession session,AskKnowledge? knowledge,JsonElement report)
{
    var page=report.GetProperty("page").GetString()!;
    var businessDate=PageReports.BusinessDate(report);
    var selected=session.MarketContext?.ContextId;
    var (mode,retained)=knowledge switch
    {
        {Mode:"current",ContextId:null} when selected is null=>("current",(Guid?)null),
        {Mode:"retained",ContextId:{} id} when id==selected=>("retained",id),
        _=>throw new MarketDataException("context_required","Ask over the knowledge the page shows: current knowledge, or the selected historical view.")
    };
    var frame=knowledge.FrameId is {} chosen?Frame(session,chosen):null;
    if(frame is not null&&frame.ContextId!=retained)throw PinnedContext.Missing();
    if(page=="facility_quantities")
    {
        if(frame is not null||businessDate is null||mode=="retained"&&session.MarketContext!.EffectiveOn!=businessDate.Value.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture))throw PinnedContext.Missing();
        return(mode,retained,null);
    }
    return mode=="current"&&frame is null&&PageReports.Operational(page)?("operational",null,null):(mode,retained,frame);
}
api.MapPost("/assistant/turns/ask",(HttpContext c,AskRequest body)=>Locked(c,session=>
{
    PageReports.Require(body.PageReport);
    var (mode,retained,frame)=Knowledge(session,body.Knowledge,body.PageReport);
    c.Items["turn_attempted"]=true;
    return agentTurns.Ask(session,Profile(session),body,mode,retained,frame,c.RequestAborted,id=>assistantWorkspace.Register(id,session,c.User));
}));
api.MapPost("/assistant/actions/review",(HttpContext c,CurveProposalRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),AssistantOperations.Permission,async(connection,ct)=>
        Results.Ok(assistantWorkspace.Review(await assistantWorkspace.Recorded(connection,body.TurnId,body.Step,ct),body.TurnId,body.Step)),c.RequestAborted)));
// Only a separate authenticated, CSRF-protected human request reaches this endpoint. The body identifies a retained
// proposal, never accepts a replacement operation/payload, and the original business handler reauthorizes the save.
api.MapPost("/assistant/actions/authorize",(HttpContext c,CurveProposalRequest body)=>Locked(c,async session=>
{
    var recorded=await database.WithIdentity(Profile(session),AssistantOperations.Permission,
        (connection,ct)=>assistantWorkspace.Recorded(connection,body.TurnId,body.Step,ct),c.RequestAborted);
    return Results.Ok(await assistantWorkspace.Approve(c,session,recorded,body.TurnId,body.Step,c.RequestAborted));
}));
api.MapPost("/assistant/turns/stop",(HttpContext c,StopRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),null,async(connection,ct)=>Results.Ok(await agentTurns.Stop(connection,body,ct)),c.RequestAborted)));
api.MapPost("/assistant/curve-proposal",(HttpContext c,CurveProposalRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),null,async(connection,ct)=>Results.Ok(await agentTurns.CurveProposal(connection,body,ct)),c.RequestAborted)));
api.MapPost("/assistant/curve-draft",(HttpContext c,CurveDraftRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>Results.Ok(await agentTurns.CurveDraft(connection,body,ct)),c.RequestAborted)));
api.MapGet("/assistant/conversations",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),null,async(connection,ct)=>Results.Ok(await agentTurns.Conversations(connection,ct)),c.RequestAborted))));
api.MapGet("/assistant/conversations/{id:guid}",(HttpContext c,Guid id,long after)=>Locked(c,session=>
    database.WithIdentity(Profile(session),null,async(connection,ct)=>Results.Ok(await agentTurns.Conversation(connection,id,after,ct)),c.RequestAborted)));
// The live event stream (§8) takes no session semaphore, so the session's commands, Stop among them, never wait for it.
var turnStream=new TurnStream(database,turnScope,agentWorker,turnStreams,
    app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions,
    TimeProvider.System,app.Lifetime.ApplicationStopping,app.Logger);
api.MapGet("/assistant/conversations/{id:guid}/events",(HttpContext c,Guid id,long after)=>turnStream.Serve(c,()=>Session(c),id,after));
api.MapGet("/assistant/turns/unfinished",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),null,async(connection,ct)=>Results.Ok(await agentTurns.Unfinished(connection,ct)),c.RequestAborted))));
api.MapPost("/assistant/conversations/read",(HttpContext c,MarkReadRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),null,async(connection,ct)=>Results.Ok(await agentTurns.MarkRead(connection,body,ct)),c.RequestAborted)));
// A question owns its original inputs. Reopening its view reconstructs those
// coordinates under current owner/source authority, without an id-only lookup.
api.MapPost("/assistant/turns/{id:guid}/view",(HttpContext c,Guid id,TurnViewRequest body)=>Locked(c,session=>
    SelectContext(c,session,body.Selection,(connection,ct)=>agentTurns.ViewSource(connection,id,ct))));
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
    return Results.Ok(await providerTests.Run(database,Profile(session),body,c.RequestAborted,(CancellationToken)c.Items["client_aborted"]!));
}));
// The one definition of each registered read: it captures an immutable selection under the gate, and the plan it
// returns reads in its own identity transaction with no gate held, on a channel worker or in an ordinary request.
ReadPlan PrepareRead(PreviewSession session,ReadInput input)=>WorkspaceReadContext.Capture(Plan(session,input),session.Knowledge.Applied);
ReadPlan Plan(PreviewSession session,ReadInput input)
{
    var profile=Profile(session);var digest=hosted is null?null:session.Digest;
    Task<T> AsReader<T>(string permission,Func<NpgsqlConnection,CancellationToken,Task<T>> read,CancellationToken ct)=>
        database.WithIdentity(profile,permission,read,ct,hostedDigest:digest);
    if(input.Body is BuildRequest curve)
    {
        var pin=Pin(session,curve.SnapshotId);var workspace=CaptureWorkspaceProjection(session,pin);
        return new(input,ct=>AsReader("ReadMarketData",async(connection,t)=>
        {
            var read=await marketReads.Evaluate(connection,pin,curve.Curve,t,curve.IncludeWorkspace,curve.ContractDisplay,curve.SourceScope==AsyncReadProjections.CurveLineage,
                curve.DefinitionScope==AsyncReadContract.CurveDependencies);
            var result=AsyncReadProjections.Curve(read,pin,curve,curve.IncludeWorkspace?workspace(read.Workspace??throw new InvalidOperationException("The requested workspace projection is missing."),true):null);
            var source=read.InputSelection.Source;var observations=read.InputSelection.ObservationIds;
            return new ReadOutcome(result,async(publication,cancellation)=>
            {
                await pin.Bind(publication,database.Manifest.CatalogId,cancellation);
                await MarketBundle.Require(publication,source.Apply(database.Manifest),cancellation);
                await MarketBundle.RequireObservationPins(publication,source.Apply(database.Manifest),observations,cancellation);
            });
        },ct));
    }
    if(input.Body is HistoryRequest history)
    {
        if(!session.HasDiscoveredFrames)throw new AsyncReadException(409,"async_workspace_not_ready","Load workspace navigation before requesting asynchronous history.");
        var policy=HistoryRevisionPolicy.Require(history.RevisionPolicy);var pin=Pin(session,history.SnapshotId);
        var offered=session.MarketContext is null?session.InitialFrames.Select(id=>Frame(session,id)).Where(frame=>frame.PpaBundle is null).Concat(session.VisibleFrames.Where(frame=>frame.OriginCloseId is not null)):session.VisibleFrames;
        ImmutableArray<InputFrame> frames=[..pin.Frame?.Close is {} pinned?offered.Where(frame=>frame.Close is null||frame.Close.Session==pinned.Session):offered];
        var aliases=session.Live is null?null:session.CaptureContextAliases(pin.Context!);
        ImmutableArray<Guid>? anchors=session.Live is null?null:[..session.InitialFrames.SelectMany(id=>session.Frames[id].ObservationIds).Distinct()];
        return new(input,ct=>AsReader("ReadMarketData",async(connection,t)=>
        {
            var points=await marketReads.History(connection,pin,frames,history.Curve,history.ContractId,history.SeriesId,policy,t,aliases,anchors);
            return new ReadOutcome(AsyncReadProjections.History(points,pin,history,policy),
                (publication,cancellation)=>pin.Bind(publication,database.Manifest.CatalogId,cancellation));
        },ct));
    }
    if(input.Body is CurveShapesRequest shapes)
    {
        var pin=Pin(session,shapes.SnapshotId);
        return new(input,ct=>AsReader("ReadMarketData",async(connection,t)=>
        {
            var curves=await marketReads.CurveShapes(connection,pin,shapes.Curves,t,shapes.ContractDisplay);
            return new ReadOutcome(pin.Context is {} context?new{curves,context}:(object)new{curves},
                (publication,cancellation)=>pin.Bind(publication,database.Manifest.CatalogId,cancellation));
        },ct));
    }
    if(input.Context is {} context)
    {
        var selection=Capture(session,context);
        if(input.Operation=="trading.freshness.v1")return new(input,ct=>AsReader("ReadTrades",async(connection,t)=>
        {
            RequireConvention(context,TradingBlotter.Convention);await RequireReadAccess(connection,t);
            TradingView? retained=null;
            if(!selection.Working){retained=await selection.Resolve(connection,knowledgeStore,context,t);await retained.Bind(connection,database.Manifest.CatalogId,t);}
            await MarketBundle.RequireReferenceVisibility(connection,database.Manifest,t);
            var version=await BlotterFreshness.Read(connection,database.Manifest,context,selection,t);
            await RequireReadAccess(connection,t);
            return new ReadOutcome(new{version},async(publication,cancellation)=>
            {
                if(retained is not null)await retained.Bind(publication,database.Manifest.CatalogId,cancellation);
                await RequireReadAccess(publication,cancellation);
            });
        },ct));
        var scenario=input.Permissions.Contains("RunScenario");
        var combined=input.Body is TradingWorkspaceRequest;
        var convention=input.Body is FuturesEditorRequest?null:input.Body is SwapEditorRequest?SwapStore.Convention:
            input.Body is OptionStrategyAnalyzeRequest?FuturesOptionStore.Convention:TradingBlotter.Convention;
        Guid? head=input.Body switch{FuturesEditorRequest body=>body.TradeId,SwapEditorRequest body=>body.TradeId,_=>null};
        Func<NpgsqlConnection,TradingView,CancellationToken,Task<object>> operation=input.Body switch
        {
            FuturesReadRequest=>tradeReads.Directory,
            TradingPositionsRequest body=>(connection,view,ct)=>tradeReads.Positions(connection,view,body.BookIds,body.PortfolioIds,body.Offset,body.Limit,ct),
            TradingWorkspaceRequest body=>(connection,view,ct)=>body.BookingCatalog is null or "directory" or "priced_contracts"
                ?tradeReads.Workspace(connection,view,body.View,body.Summaries.IsDefault?[]:body.Summaries,body.BookingCatalog=="directory",ct)
                :throw new MarketDataException("blotter_view","Choose an available booking catalog projection."),
            FuturesBlotterRequest body=>(connection,view,ct)=>tradeReads.Blotter(connection,view,body.View,ct),
            FuturesEditorRequest body=>(connection,view,ct)=>tradeReads.FuturesTrade(connection,view,body.TradeId,body.Revision,ct),
            SwapEditorRequest body=>(connection,view,ct)=>tradeReads.SwapTrade(connection,view,body.TradeId,body.Revision,ct),
            RiskScopeRequest body=>(connection,view,ct)=>tradeReads.ReadBound(connection,view,()=>RiskTradeScope.Directory(connection,database.Manifest.CatalogId,view.CalculationFrame,body,ct),ct),
            OptionStrategyAnalyzeRequest body=>(connection,view,ct)=>OptionStrategyRead.Read(tradeReads,connection,database,view,body,ct),
            _=>(connection,view,ct)=>tradeReads.Read(connection,view,data=>AsyncPortfolioRead(input,connection,data,view.CalculationFrame,ct),ct,
                referenceScope:ReferenceReadScope.TradeValuation,observationScope:ObservationReadScope.ExactPins)
        };
        return new(input,async ct=>
        {
            var completed=await ExecuteTrading(profile,()=>selection,context,operation,ct,
                scenario:scenario,headTrade:head,expectedConvention:convention,combinedWorkspace:combined,hostedDigest:digest);
            return new ReadOutcome(completed.Result,async(publication,cancellation)=>
            {
                await completed.View.Bind(publication,database.Manifest.CatalogId,cancellation);
                await TradingContext.Bind(publication,database.Manifest.CatalogId,completed.View.CalculationFrame,completed.View.Context,cancellation);
                await RequireReadAccess(publication,cancellation);
            });
        });
    }
    // Retained job status remains an ordinary authorized read. Re-read only its
    // visibility at publication; no job is submitted, cancelled or restarted.
    async Task<object> JobRead(NpgsqlConnection connection,CancellationToken ct)=>input.Body switch
    {
        AsyncReadEmpty=>await RiskJobs.List(connection,database.Manifest.CatalogId,PortfolioRiskPreview.Convention,ct),
        AsyncReadJob job=>await RiskJobs.Read(connection,database.Manifest.CatalogId,job.Id,ct,expectedConvention:PortfolioRiskPreview.Convention),
        AsyncReadArchive archive=>await RiskJobs.StatisticalArchive(connection,database.Manifest.CatalogId,archive.Limit,
            archive.Before is null?null:SchedulePreview.ParseAt(archive.Before),archive.Id,archive.Method,ct),
        _=>throw AsyncReadContract.Invalid()
    };
    return new(input,ct=>AsReader("RunScenario",async(connection,t)=>
    {
        var value=await JobRead(connection,t);var ids=AsyncReadJobs.Ids(value);
        return new ReadOutcome(value,async(publication,cancellation)=>
        {
            foreach(var id in ids)await RiskJobs.Read(publication,database.Manifest.CatalogId,id,cancellation,
                expectedConvention:PortfolioRiskPreview.Convention,expose:input.Body is AsyncReadJob);
        });
    },ct));
}
Task<object> AsyncPortfolioRead(ReadInput input,NpgsqlConnection connection,PreviewData data,InputFrame frame,CancellationToken ct)=>input.Body switch
{
    PortfolioRiskCatalogRequest body=>portfolioRisk.Catalog(connection,data,frame,body,ct),
    PortfolioRiskRequest body=>portfolioRisk.Calculate(connection,data,frame,body,ct),
    PortfolioHistoryRequest body=>portfolioRisk.History(connection,data,frame,body,ct),
    _=>throw AsyncReadContract.Invalid()
};
// The ordinary route of a registered read (docs/async-reads.md). The session gate covers its capture and the
// authorization before its answer, never its database work. That authorization requires the exact sources and books
// the read touched, as the response channel's does. A delegated assistant invocation stays inside the gate, where
// its own guards apply and nothing is authorized a second time. `prepare` is session preparation the capture needs,
// done in the capture's own gate hold.
async Task<IResult> ServeRead(HttpContext c,string operation,object body,Func<PreviewSession,CancellationToken,Task>? prepare=null)
{
    var input=AsyncReadContract.Input(operation,body,exactSelection:false);
    async Task<ReadPlan> Capture(PreviewSession session)
    {
        if(prepare is not null)await prepare(session,c.RequestAborted);
        return PrepareRead(session,input);
    }
    if(c.Features.Get<AssistantInvocation>() is not null)
        return await Locked(c,async session=>Results.Ok((await (await Capture(session)).Execute(c.RequestAborted)).Value));
    var session=Session(c);var digest=hosted is null?null:session.Digest;
    return Results.Ok(await UngatedRead.Run(session,async captured=>AsyncReadVisibility.Observed(await Capture(captured)),
        (profile,plan,outcome,ct)=>ReadAuthority.Require(database,profile,digest,plan.Input.Permissions,ct,outcome.Recheck),c.RequestAborted));
}
// Each route keeps its own refusal of a missing selection; PrepareRead is the one definition of the read itself.
T Selected<T>(T body,FuturesReadRequest? context,string code,string message)=>context is null?throw new MarketDataException(code,message):body;
api.MapPost("/evaluate",(HttpContext c,BuildRequest body)=>ServeRead(c,"curve.evaluate.v1",body));
// The same pin, identity and permission as /evaluate, for the contract prices of several curves in one read; a selected context is stated as there.
api.MapPost("/market/curve-shapes",(HttpContext c,CurveShapesRequest body)=>ServeRead(c,"curve.shapes.v1",body));
// Frame discovery assigns session state, so this route does it under the gate that captures the read: a live update
// between the two cannot leave the capture unprepared. The channel refuses an unprepared session instead.
api.MapPost("/history",(HttpContext c,HistoryRequest body)=>ServeRead(c,"curve.history.v1",body,async(session,ct)=>
{
    HistoryRevisionPolicy.Require(body.RevisionPolicy);
    if(!session.HasDiscoveredFrames)await RefreshFrames(session,ct);
}));
api.MapPost("/trading/freshness",(HttpContext c,FuturesReadRequest body)=>ServeRead(c,"trading.freshness.v1",body));
api.MapPost("/trading/directory",(HttpContext c,FuturesReadRequest body)=>ServeRead(c,"trading.directory.v1",body));
api.MapPost("/trading/positions",(HttpContext c,TradingPositionsRequest body)=>ServeRead(c,"trading.positions.v1",
    Selected(body,body.Context,"position_facts","Supply the selected context.")));
// The progressive form keeps its own gate release and recheck; the ordinary form is the registered read.
api.MapPost("/trading/workspace",(HttpContext c,TradingWorkspaceRequest body)=>
{
 if(body.BookingCatalog is not(null or "directory" or "priced_contracts"))throw new MarketDataException("blotter_view","Choose an available booking catalog projection.");
 if(body.Context is null)throw new MarketDataException("blotter_view","Supply the selected context.");
 if(!body.Progressive)return ServeRead(c,"trading.workspace.v1",body);
 return TradingRead(c,body.Context,
  (connection,view,ct)=>tradeReads.Workspace(connection,view,body.View,body.Summaries.IsDefault?[]:body.Summaries,body.BookingCatalog=="directory",ct),
  expectedConvention:TradingBlotter.Convention,combinedWorkspace:true,concurrent:true,
  recheck:async(connection,view,ct)=>{await tradeReads.Positions(connection,view,body.View.BookIds,body.View.PortfolioIds,0,1,ct);});
});
api.MapPost("/trading/blotter",(HttpContext c,FuturesBlotterRequest body)=>ServeRead(c,"trading.blotter.v1",
    Selected(body,body.Context,"blotter_view","Supply the selected context.")));
api.MapPost("/futures/editor",(HttpContext c,FuturesEditorRequest body)=>ServeRead(c,"futures.editor.v1",
    Selected(body,body.Context,"futures_trade","Supply the selected context.")));
api.MapPost("/swaps/editor",(HttpContext c,SwapEditorRequest body)=>ServeRead(c,"swaps.editor.v1",
    Selected(body,body.Context,"swap_context","Supply the selected context.")));
api.MapPost("/options/strategy/analyze",(HttpContext c,OptionStrategyAnalyzeRequest body)=>ServeRead(c,"options.strategy.analyze.v1",
    Selected(body,body.Context,"option_context","Supply the selected context.")));
api.MapPost("/portfolio/risk/scope",(HttpContext c,RiskScopeRequest body)=>ServeRead(c,"portfolio.risk.scope.v1",
    Selected(body,body.Context,"risk_context","Select the shared financial context.")));
api.MapPost("/portfolio/risk/catalog",(HttpContext c,PortfolioRiskCatalogRequest body)=>ServeRead(c,"portfolio.risk.catalog.v1",
    Selected(body,body.Context,"risk_context","Select the shared financial context.")));
api.MapPost("/portfolio/risk",(HttpContext c,PortfolioRiskRequest body)=>ServeRead(c,"portfolio.risk.calculate.v1",
    Selected(body,body.Context,"risk_context","Select the shared financial context.")));
api.MapPost("/portfolio/risk/history",(HttpContext c,PortfolioHistoryRequest body)=>ServeRead(c,"portfolio.risk.history.v1",
    Selected(body,body.Scope?.Context,"risk_context","Select the retained risk scope and context.")));
api.MapGet("/portfolio/risk/jobs",(Func<HttpContext,Task<IResult>>)(c=>ServeRead(c,"portfolio.risk.jobs.list.v1",new AsyncReadEmpty())));
api.MapGet("/portfolio/risk/jobs/{id:guid}",(HttpContext c,Guid id)=>ServeRead(c,"portfolio.risk.jobs.read.v1",new AsyncReadJob(id)));
Task<IResult> StatisticalArchive(HttpContext c,string method)
{
    if(!int.TryParse(c.Request.Query["limit"],out var limit)||limit is <1 or >100)throw new MarketDataException("risk_archive_input","Choose a report page size from 1 to 100.");
    var before=c.Request.Query["before"].ToString();var cursor=c.Request.Query["id"].ToString();
    if((before.Length==0)!=(cursor.Length==0)||cursor.Length>0&&!Guid.TryParse(cursor,out _))throw new MarketDataException("risk_archive_input","Use both fields of the retained archive cursor.");
    return ServeRead(c,"portfolio.risk.archive.v1",new AsyncReadArchive(method,limit,before.Length==0?null:before,cursor.Length==0?null:Guid.Parse(cursor)));
}
api.MapGet("/portfolio/risk/parametric/archive",(Func<HttpContext,Task<IResult>>)(c=>StatisticalArchive(c,"parametric")));
api.MapGet("/portfolio/risk/monte-carlo/archive",(Func<HttpContext,Task<IResult>>)(c=>StatisticalArchive(c,"monte_carlo")));
api.MapGet("/portfolio/risk/historical/archive",(Func<HttpContext,Task<IResult>>)(c=>StatisticalArchive(c,"historical")));
api.MapGet("/portfolio/risk/statistical/archive",(Func<HttpContext,Task<IResult>>)(c=>StatisticalArchive(c,"statistical")));
var asyncReads=new AsyncReads(database,app.Lifetime.ApplicationStopping,hosted is not null,app.Logger,TimeSpan.FromSeconds(processLimits.RequestSeconds));
asyncReadServices.Add(asyncReads);asyncReads.Map(api,Session,PrepareRead);
}
var files=new PhysicalFileProvider(webRoot);
app.UseDefaultFiles(new DefaultFilesOptions {FileProvider=files});
app.UseStaticFiles(new StaticFileOptions {FileProvider=files});
app.Lifetime.ApplicationStarted.Register(()=>Console.WriteLine($"PREVIEW_READY http://127.0.0.1:{port}"));
try{await app.RunAsync();}
finally{foreach(var asyncReads in asyncReadServices)await asyncReads.DisposeAsync();}

[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminCreate(Guid OperationId,string? Login,string? Name,string? Kind,string[]? Roles);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record AdminUpdate(Guid OperationId,Guid Id,int ExpectedVersion,string? Name,bool? Enabled,string[]? Roles);
public sealed record ProfileRequest(string Profile);
// A request whose session is missing, unknown or past its end.
public sealed class PreviewSessionEnded : Exception;
public sealed record LocalTimeRequest(string? LocalTime,string? TimeZone);
public sealed record BuildRequest(Guid SnapshotId,string Curve,bool IncludeSources=false,bool IncludeWorkspace=false,CurveDisplaySelection? ContractDisplay=null)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceScope{get;init;}
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DefinitionScope{get;init;}
}
public sealed record HistoryRequest(Guid SnapshotId,string Curve,Guid? ContractId=null,Guid? SeriesId=null,string? RevisionPolicy=null,ImmutableArray<OpenEtrm.Shared.Analytics.OverlayRequest>? Overlays=null);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveShapesRequest(Guid SnapshotId,ImmutableArray<string> Curves,CurveDisplaySelection? ContractDisplay=null);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveInspectionsRequest([property:System.Text.Json.Serialization.JsonRequired]Guid SnapshotId,
    [property:System.Text.Json.Serialization.JsonRequired]ImmutableArray<CurveInspection> Curves,CurveDisplaySelection? ContractDisplay=null);
public sealed record CorrectionRequest(Guid SnapshotId,Guid ObservationId,string Value);
public sealed record SelectionRequest(Guid SnapshotId,Guid ObservationId);
public sealed partial class PreviewSession
{
    public static readonly TimeSpan Lifetime=TimeSpan.FromMinutes(30);
    public DateTimeOffset Created {get;}=DateTimeOffset.UtcNow;
    // Within its lifetime and, in hosted mode, no later than the sign-in session it belongs to (docs/ai-assistant.md §11).
    public DateTimeOffset Ends {get;}
    // Names the session to the database, which binds its turns to it (docs/ai-assistant.md §7.2, §11): its sign-in
    // session's digest in hosted mode, otherwise random, so it reveals nothing of the session's cookie.
    public string Digest {get;}
    public SemaphoreSlim Gate {get;}
    // Ordinary reads that execute outside the gate share these slots; a further read waits for one.
    public const int MaximumReads=2;
    internal SemaphoreSlim Reads {get;}
    internal SemaphoreSlim BlotterWait {get;}
    private string? profile;
    internal PreviewSession Account {get;}
    internal WorkspaceReadContext Knowledge {get;}=new();
    public string? Profile {get=>Account.profile;set=>Account.profile=value;}
    private ScheduleContext? marketContext;
    private long viewGeneration;
    internal long ViewGeneration=>Interlocked.Read(ref viewGeneration);
    internal void InvalidateReads()=>Interlocked.Increment(ref viewGeneration);
    public ScheduleContext? MarketContext {get=>marketContext;set{marketContext=value;InvalidateReads();}}
    internal void ResetView()
    {
        StopLive();
        ForgetDiscoveredFrames(resetAliasIdentities:true);
        MarketContext=null;Knowledge.Reset();
    }
    public Dictionary<string,string> CustomProfiles {get;}
    public Dictionary<Guid,InputFrame> Frames {get;}=[];
    public PreviewShortcutLibrary Shortcuts {get;}=new();
    public DateWorkspace Dates {get;}=new();
    public Guid[] InitialFrames {get;}
    // The built-in inputs recorded as closes; the session offers them as discovered closes.
    public ImmutableHashSet<Guid> InitialCloses {get;}
    // The page's built-in inputs: its remaining built-in working frames (or their aliases under a context) and its built-in closes.
    public Guid[] InitialFrameIds=>[..(MarketContext is null?InitialFrames:ContextAliases(MarketContext).Select(f=>f.Id))
        .Concat(VisibleFrames.Where(f=>f.OriginCloseId is {} close&&InitialCloses.Contains(close)).Select(f=>f.Id))];
    public PreviewSession(PreviewManifest manifest,HostedSignIn? signIn=null,PreviewSession? account=null)
    {
        Account=account??this;
        Gate=account?.Gate??new(1,1);
        Reads=account?.Reads??new(MaximumReads,MaximumReads);
        BlotterWait=account?.BlotterWait??new(1,1);
        CustomProfiles=account?.CustomProfiles??[];
        Ends=account?.Ends??signIn?.Ends??Created+Lifetime;
        Digest=account?.Digest??signIn?.Digest??Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        InitialCloses=[..manifest.Frames.Select(f=>f.CloseId).OfType<Guid>()];
        foreach(var f in manifest.Frames.Where(f=>f.CloseId is null)) {
            if(f.CurveContracts is null)throw new MarketDataException("missing_curve_view","Every seed frame requires explicit curve contract selections.");
            var frame=new InputFrame(Guid.NewGuid(),"Initial synthetic inputs",f.At,OpenEtrm.Shared.Temporal.BusinessTime.UtcSnapshot(f.At).Date,f.ObservationIds.ToImmutableArray(),
                f.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray()),FrozenObservationIds:(f.FrozenObservationIds??[]).ToImmutableArray()){PpaBundle=f.PpaBundle,Source=f.PpaBundle??new(manifest.RevisionId,manifest.ReferenceCatalogId,manifest.ReferenceRevisionId)};Frames.Add(frame.Id,frame);
        }
        InitialFrames=Frames.Values.Where(f=>f.PpaBundle is null).Select(f=>f.Id).ToArray();
    }
}
