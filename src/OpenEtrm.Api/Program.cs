using OpenEtrm.Shared.Temporal;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

if (!args.Contains("--local-preview")) throw new InvalidOperationException("This host supports only an explicitly selected local synthetic preview.");
var root=Path.GetFullPath(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT") ?? throw new InvalidOperationException("Use tools/run_preview.py."));
var runId=Guid.Parse(File.ReadAllText(Path.Combine(root,".openetrm-preview")));
var port=int.Parse(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_PORT") ?? throw new InvalidOperationException("Explicit preview port is required."));
var webRoot=Path.GetFullPath(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_WEB") ?? throw new InvalidOperationException("Explicit built web root is required."));
var builder=WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(o=>o.SingleLine=true); builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.ConfigureKestrel(o=> { o.Listen(IPAddress.Loopback,port); o.Limits.MaxRequestBodySize=32*1024; });
builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
builder.Services.AddSingleton<IAntiforgeryAdditionalDataProvider,PreviewAntiforgeryScope>();
builder.Services.AddAntiforgery(o=> { o.HeaderName="X-Preview-CSRF"; o.Cookie.Name="openetrm_preview_csrf"; o.Cookie.HttpOnly=true; o.Cookie.SameSite=SameSiteMode.Strict; o.Cookie.SecurePolicy=CookieSecurePolicy.None; });
builder.Services.ConfigureHttpJsonOptions(o=>{o.SerializerOptions.PropertyNamingPolicy=JsonNamingPolicy.CamelCase;o.SerializerOptions.Converters.Add(new ExactDecimalJsonConverter());});
var app=builder.Build();
await using var database=new PreviewDatabase(root);

var profiles=new[] { new {id="viewer",name="Viewer",description="Read only"},new {id="editor",name="Market Data Editor",description="Read and correct synthetic prices"},new {id="approver",name="Market Data Approver",description="Read and publish schedule definitions"},new {id="admin",name="Access Administrator",description="Manage workspace identities and roles"},new {id="trader",name="Trader",description="Book and inspect synthetic futures holdings"},new {id="risk",name="Risk Analyst",description="Inspect holdings and run shared market scenarios"} };

app.Use(async (context,next)=>
{
    var origin=$"http://127.0.0.1:{port}";
    var apiPath=context.Request.Path.StartsWithSegments("/examples/exchange/api",out var suffix)?new PathString("/api"+suffix):context.Request.Path;
    if (context.Connection.RemoteIpAddress is not {} remote || !IPAddress.IsLoopback(remote) ||
        context.Request.Host.Host!="127.0.0.1" || context.Request.Host.Port!=port ||
        context.Request.Headers.TryGetValue("Origin",out var supplied) && supplied!=origin)
    { context.Response.StatusCode=403; return; }
    context.Response.Headers["Content-Security-Policy"]="default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers["X-Content-Type-Options"]="nosniff"; context.Response.Headers["Referrer-Policy"]="no-referrer";
    if (apiPath.StartsWithSegments("/api")) context.Response.Headers.CacheControl="no-store";
    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(15));
    context.RequestAborted=deadline.Token;
    try
    {
        if (context.Request.Method is not ("GET" or "HEAD"))
        {
            if (!context.Request.Headers.TryGetValue("Origin",out var requestOrigin) || requestOrigin!=origin) throw new PreviewDeniedException();
            await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
        }
        await next(context);
    }
    catch (Exception e) when (!context.Response.HasStarted)
    {
        var (status,code,message)=e switch
        {
            PreviewDeniedException => (403,"access_denied","The selected test identity cannot perform this operation."),
            AntiforgeryValidationException => (400,"request_token","Reload the preview to obtain a valid request token."),
            MarketDataException m when m.Code is "shortcut_conflict" or "tou_conflict" or "table_view_conflict" or "futures_revision_conflict" or "swap_revision_conflict" => (409,m.Code,m.Message),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="party_conflict" => (409,p.MessageText,"This party or request changed. Reload the latest revision or retry the original request."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "party_input" or "party_limit" => (422,p.MessageText,"Supply a stable code, name and an effective time at or after the preceding version. Party codes cannot change."),
            PostgresException p when p.SqlState is "23505" or "40001" && apiPath.StartsWithSegments("/api/tou-products") => (409,"tou_conflict","This TOU product changed or its code already exists. Reload before editing."),
            MarketDataException m => (422,m.Code,m.Message),
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
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "last_administrator" or "self_access_change" or "admin_input" or "admin_limit" => (422,p.MessageText,"The administrative change was rejected. Keep a current administrator and supply explicit account details and roles."),
            PostgresException p when p.SqlState=="23505" && apiPath.StartsWithSegments("/api/admin") => (409,"admin_conflict","That account or request already exists. Reload administration."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText=="amount_format_conflict" => (409,"amount_format_conflict","This deal display changed. Reload its precision controls before saving."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "amount_format" or "futures_lot" => (422,p.MessageText,"Supply exact supported contract lot terms or deal display precision."),
            PostgresException p when p.SqlState=="23505" => (409,"revision_conflict","This observation has already been revised. Inspect its history and explicitly select the intended revision."),
            PostgresException p when p.SqlState=="P0001" && p.MessageText is "knowledge_coverage" or "knowledge_clock" or "knowledge_tracking" or "knowledge_retention" or "knowledge_limit" or "knowledge_timeline" => (422,p.MessageText,"The requested knowledge cutoff cannot be certified. Check retained coverage, clock health and commit tracking."),
            PostgresException p when p.SqlState=="42501" => (403,"access_denied","Database permissions rejected this operation."),
            PostgresException p when p.SqlState is "23514" or "55000" => (422,"definition_rejected","The value or operation violates an explicit database specification."),
            PostgresException when context.Items.ContainsKey("administration_attempted") => (503,"administration_outcome_unknown","The administrative outcome cannot be confirmed. Retain the original request and inspect the audit before retrying."),
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
            OperationCanceledException => (503,"request_cancelled","The request was cancelled or exceeded its time limit."),
            BadHttpRequestException or JsonException => (400,"request_format","The request is incomplete or malformed."),
            _ => (500,"preview_error","The preview could not complete the operation.")
        };
        if(e is PostgresException pg)app.Logger.LogWarning("Database operation rejected: {SqlState}",pg.SqlState);
        if (status==500) app.Logger.LogError("Preview operation failed: {ErrorType}",e.GetType().Name);
        context.Response.StatusCode=status;
        await context.Response.WriteAsJsonAsync(new {code,message},CancellationToken.None);
    }
});

await using var exchangeDatabase=File.Exists(Path.Combine(root,"exchange-manifest.json"))?new PreviewDatabase(root,"exchange-manifest.json"):null;
MapPreview(app.MapGroup("/api").WithMetadata(new PreviewScope("baseline")),database,"openetrm_preview");
if(exchangeDatabase is not null)MapPreview(app.MapGroup("/examples/exchange/api").WithMetadata(new PreviewScope("exchange")),exchangeDatabase,"openetrm_exchange_preview");
app.MapGet("/api/examples",()=>new[]{new {id="baseline",name="Original synthetic example",url="/"},new {id="exchange",name="CME & ICE futures · synthetic prices",url="/?example=exchange"}}.Where(e=>e.id=="baseline"||exchangeDatabase is not null));
void MapPreview(RouteGroupBuilder api,PreviewDatabase database,string cookieName)
{
var sessions=new ConcurrentDictionary<string,PreviewSession>();
api.MapGet("/health",()=>new {mode="local-synthetic-preview",runId});
api.MapGet("/bootstrap",async (HttpContext context,IAntiforgery antiforgery)=>
{
    foreach(var item in sessions.Where(x=>DateTimeOffset.UtcNow-x.Value.Created>TimeSpan.FromMinutes(30)).ToArray()) sessions.TryRemove(item.Key,out _);
    PreviewSession session;
    if (context.Request.Cookies.TryGetValue(cookieName,out var cookie)&&sessions.TryGetValue(cookie,out var existing)) session=existing;
    else
    {
        if (sessions.Count>=64) throw new PreviewDeniedException();
        var id=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); session=new PreviewSession(database.Manifest);
        sessions[id]=session;
        context.Response.Cookies.Append(cookieName,id,new CookieOptions {HttpOnly=true,SameSite=SameSiteMode.Strict,Secure=false,MaxAge=TimeSpan.FromMinutes(30),Path="/"});
    }
    await session.Gate.WaitAsync(context.RequestAborted);
    try { return new {mode="local-synthetic-preview",runId,notice="Disposable synthetic workspace. Test identities are simulations, not production sign-in.",profiles,
        profile=session.Profile,customProfiles=session.CustomProfiles.Select(p=>new{id=p.Key,name=p.Value}).ToArray(),csrfToken=antiforgery.GetAndStoreTokens(context).RequestToken,frames=session.Frames.Values.Where(f=>f.ContextId==session.MarketContext?.ContextId).OrderBy(f=>f.At).ToArray(),
        catalogId=database.Manifest.CatalogId,tenantId=database.Manifest.TenantId,workspaceId=database.Manifest.WorkspaceId}; }
    finally { session.Gate.Release(); }
});
PreviewSession Session(HttpContext context)
{
    if (!context.Request.Cookies.TryGetValue(cookieName,out var id)||!sessions.TryGetValue(id,out var session)||DateTimeOffset.UtcNow-session.Created>TimeSpan.FromMinutes(30)) throw new PreviewDeniedException();
    return session;
}
async Task<IResult> Locked(HttpContext context,Func<PreviewSession,Task<IResult>> work)
{
    var session=Session(context); await session.Gate.WaitAsync(context.RequestAborted);
    try {return await work(session);} finally {session.Gate.Release();}
}
string Profile(PreviewSession session)=>session.Profile ?? throw new PreviewDeniedException();
api.MapPost("/time/inspect",(HttpContext c,LocalTimeRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",(_,_)=>
    Task.FromResult<IResult>(Results.Ok(new{candidates=UserTime.Inspect(body.LocalTime,body.TimeZone)})),c.RequestAborted)));
InputFrame Frame(PreviewSession session,Guid id)=>session.Frames.TryGetValue(id,out var frame) && frame.ContextId==session.MarketContext?.ContextId?frame:throw new MarketDataException("snapshot_missing","Select a saved input snapshot from this preview session.");
api.MapPost("/profile",(HttpContext c,ProfileRequest body)=>Locked(c,session=>
{
    if (!profiles.Any(p=>p.id==body.Profile) && !session.CustomProfiles.ContainsKey(body.Profile)) throw new PreviewDeniedException();
    session.Profile=body.Profile; session.MarketContext=null; return Task.FromResult(Results.Ok(new {profile=session.Profile}));
}));
PreviewSequenceCoverage ScheduleSource(string? code) => (database.Manifest.Schedules ?? []).SingleOrDefault(s=>s.Curve==code)
    ?? throw new MarketDataException("sequence_source","Choose an explicitly configured contract schedule.");
Task<IResult> ScheduleOperation(HttpContext c,Func<PreviewSession,PreviewData,IResult> work) => Locked(c,async session=>
    await database.WithIdentity(Profile(session),"ReadMarketData",async (connection,ct)=>work(session,await MarketData(session,connection,ct)),c.RequestAborted));
api.MapPost("/units/convert",(HttpContext c,UnitConvertRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    var data=await MarketData(session,connection,ct);
    return Results.Ok(UnitOperations.Convert(await database.ReadUnitCatalog(connection,data,ct),body));
},c.RequestAborted)));
api.MapPost("/amounts/display",(HttpContext c,System.Collections.Immutable.ImmutableArray<DisplayNumberRequest> body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),null,(_,_)=>Task.FromResult<IResult>(Results.Ok(UnitOperations.Display(body))),c.RequestAborted)));
var touStore=new TouProductStore(database.Manifest.CatalogId,database.Manifest.ReferenceCatalogId,database.Manifest.ReferenceRevisionId);
// TOU products are current definitions; a historical view never substitutes them.
Task<IResult> PowerOperation(HttpContext c,string permission,Func<PreviewSession,NpgsqlConnection,CancellationToken,Task<IResult>> work)=>Locked(c,session=>
{
    if(session.MarketContext is not null)
        throw new MarketDataException("power_historical","TOU products and power hours require current inputs; historical TOU publication is not implemented.");
    return database.WithIdentity(Profile(session),permission,(connection,ct)=>work(session,connection,ct),c.RequestAborted);
});
async Task<PowerPreview.ProductSet> PublishedProducts(NpgsqlConnection connection,CancellationToken ct)=>
    PowerPreview.Products(TouProductStore.Published(await touStore.Read(connection,ct)),await touStore.Clocks(connection,ct));
// Stored display names for every curve in this catalog, for screens that show curve codes from any bundle.
api.MapGet("/curve-names",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
{
    if(session.MarketContext is {} selected)await MarketContextStore.Bind(connection,database.Manifest.CatalogId,selected,ct);
    await using var command=ScheduleStore.Command(connection,"SELECT code,name FROM market_data.curves WHERE catalog_id=$1 UNION SELECT code,name FROM market_data.rate_curves WHERE catalog_id=$1 ORDER BY 1,2",database.Manifest.CatalogId);
    await using var reader=await command.ExecuteReaderAsync(ct);var rows=new List<object>();
    while(await reader.ReadAsync(ct))rows.Add(new {code=reader.GetString(0),name=reader.GetString(1)});
    return Results.Ok(rows);
},c.RequestAborted))));
api.MapPost("/power/catalog",(HttpContext c,PowerCatalogRequest body)=>PowerOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(PowerPreview.Catalog(await database.Read(connection,ct),Frame(session,body.SnapshotId),await PublishedProducts(connection,ct)))));
api.MapPost("/power/preview",(HttpContext c,PowerPreviewRequest body)=>PowerOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(PowerPreview.Calculate(await database.Read(connection,ct),Frame(session,body.SnapshotId),body,await PublishedProducts(connection,ct)))));
api.MapGet("/tou-products",(Func<HttpContext,Task<IResult>>)(c=>PowerOperation(c,"ReadMarketData",async(session,connection,ct)=>
{
    var canEdit=await HasPermission(connection,"WriteMarketDefinitions",ct);var canPublish=await HasPermission(connection,"SealMarketDefinitions",ct);
    // Row security returns drafts to authors only; readers receive published versions.
    return Results.Ok(new {versions=await touStore.Read(connection,ct),clocks=await touStore.Clocks(connection,ct),canEdit,canPublish});
})));
api.MapPost("/tou-products/save",(HttpContext c,TouProductSaveRequest body)=>PowerOperation(c,"WriteMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await touStore.Save(connection,body,ct))));
api.MapPost("/tou-products/publish",(HttpContext c,TouProductPublishRequest body)=>PowerOperation(c,"SealMarketDefinitions",async(session,connection,ct)=>
    Results.Ok(await touStore.Publish(connection,body,ct))));
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
Task<IResult> DateOperation(HttpContext c,string permission,Func<PreviewSession,NpgsqlConnection,CancellationToken,Task<IResult>> work)=>Locked(c,session=>
    database.WithIdentity(Profile(session),permission,async(connection,ct)=>
    {
        if(session.MarketContext is {} selected)await MarketContextStore.Bind(connection,database.Manifest.CatalogId,selected,ct);
        return await work(session,connection,ct);
    },c.RequestAborted));
async Task<bool> HasPermission(NpgsqlConnection connection,string permission,CancellationToken ct)
{
    await using var command=ScheduleStore.Command(connection,"SELECT security.has_permission($1)",permission);
    return Equals(await command.ExecuteScalarAsync(ct),true);
}
api.MapGet("/date-schedules",(Func<HttpContext,Task<IResult>>)(c=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
{
    var knownThrough=session.MarketContext?.KnownThrough??await scheduleStore.Capture(connection,ct);
    var canEdit=session.MarketContext is null && await HasPermission(connection,"WriteMarketDefinitions",ct);
    var canPublish=session.MarketContext is null && await HasPermission(connection,"SealMarketDefinitions",ct);
    // Row security returns drafts to authors only; readers receive published versions.
    var versions=session.MarketContext is {} selected?await scheduleStore.ReadAt(connection,selected,ct):await scheduleStore.Read(connection,ct);
    return Results.Ok(new {knownThrough,versions,canEdit,canPublish,choices=DateWorkspace.Choices,
        contractDefinitions=session.ContractDefinitions.Where(p=>p.Key.Owner==Profile(session)).Select(p=>p.Value).ToArray(),
        calendars=(await scheduleStore.Calendars(connection,ct)).Select(c=>new {c.Reference,c.CoverageFrom,c.CoverageTo,c.WeekendDays,
            closedDates=c.Days.Where(d=>!d.Open).Select(d=>d.Date),openDates=c.Days.Where(d=>d.Open).Select(d=>d.Date)})});
})));
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
    if(body.Code is null || !System.Text.RegularExpressions.Regex.IsMatch(body.Code,@"\A[a-z][a-z0-9_]{0,31}\z") ||
        string.IsNullOrWhiteSpace(body.Name) || body.Name.Length>80 || body.Contracts is null || body.Contracts.Length is <1 or >120 ||
        body.Contracts.Any(c=>c is null) || body.Contracts.Select(c=>c.Label).Distinct().Count()!=body.Contracts.Length)
        throw new TemporalException("contract_dates","Name the definition and supply uniquely named contracts.");
    var view=await ScheduleView(connection,body.Context,ct);
    var contracts=body.Contracts.Select(d=>DateWorkspace.Contract(new(d.Label,d.Start,d.End,d.LastTrading,d.CutoffUtc),e=>PublishedSchedules.Resolve(view.Versions,e,view.Context))).ToImmutableArray();
    for(var n=1;n<contracts.Length;n++)if(contracts[n].DeliveryFrom<contracts[n-1].DeliveryTo || contracts[n].Expiry<=contracts[n-1].Expiry)
        throw new TemporalException("contract_dates","Contract periods must be ordered without overlap and have increasing last-trading instants.");
    return new(body.Code,body.Name,view.Context,contracts.Select(c=>new DatedContractDefinition(c.Label,c.DeliveryFrom,c.DeliveryTo,c.Expiry,c.DeliveryStart,c.DeliveryEnd,c.LastTrading,c.CutoffUtc)).ToImmutableArray());
}
api.MapPost("/date-schedules/contracts/preview",(HttpContext c,PublishedContractRequest body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
    Results.Ok(await BindContracts(connection,body,ct))));
api.MapPost("/date-schedules/contracts/save",(HttpContext c,PublishedContractRequest body)=>DateOperation(c,"ReadMarketData",async(session,connection,ct)=>
{
    var result=await BindContracts(connection,body,ct);var key=(Profile(session),result.Code);
    if(session.ContractDefinitions.TryGetValue(key,out var old) && JsonSerializer.Serialize(old)!=JsonSerializer.Serialize(result))
        throw new TemporalException("date_definition_conflict","This candidate name has different terms. Choose a new code.");
    if(!session.ContractDefinitions.ContainsKey(key) && session.ContractDefinitions.Keys.Count(k=>k.Owner==Profile(session))>=16)
        throw new TemporalException("preview_limit","At most 16 candidate definitions can be kept per persona.");
    session.ContractDefinitions[key]=result;return Results.Ok(result);
}));
async Task<PreviewData> MarketData(PreviewSession session,NpgsqlConnection connection,CancellationToken ct)
{
    if(session.MarketContext is {} selected)await MarketContextStore.Bind(connection,database.Manifest.CatalogId,selected,ct);
    return await database.Read(connection,ct);
}
api.MapPost("/market/context",(HttpContext c,MarketContextRequest body)=>Locked(c,async session=>
{
    if(body.Selection!="latest_correction")throw new MarketDataException("selection_policy","Choose the explicit latest-known correction policy.");
    var snapshot=await database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    {
        var captured=await knowledgeStore.Capture(connection,new(body.EffectiveOn,body.KnownAt,body.Mode),ct);
        await MarketContextStore.Bind(connection,database.Manifest.CatalogId,captured.Context,ct);
        var data=await database.Read(connection,ct);
        var end=DateWorkspace.Date(captured.Context.EffectiveOn).AddDays(1).ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc);
        var selected=session.InitialFrames.Select(id=>session.Frames[id]).Where(f=>f.At.UtcDateTime<end).ToArray();
        var frames=selected.Select(f=>session.Frames.Values.FirstOrDefault(existing=>existing.ContextId==captured.Context.ContextId&&existing.At==f.At)
            ??MarketContextStore.Select(f,data,captured.Context)).ToArray();
        if(session.Frames.Count+frames.Count(f=>!session.Frames.ContainsKey(f.Id))>128)throw new MarketDataException("preview_limit","This preview has reached its retained input limit.");
        return (captured,frames);
    },c.RequestAborted);
    foreach(var frame in snapshot.frames)session.Frames.TryAdd(frame.Id,frame);
    session.MarketContext=snapshot.captured.Context;
    return Results.Ok(snapshot.captured);
}));
api.MapPost("/market/current",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,async session=>
{
    await database.WithIdentity(Profile(session),"ReadMarketData",(_,_)=>Task.FromResult(true),c.RequestAborted);
    session.MarketContext=null;return Results.Ok(new {mode="explicit_input_pins"});
})));
api.MapGet("/workspace",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,async session=>
{
    var read=await database.WithIdentity(Profile(session),"ReadMarketData",async(connection,ct)=>
    {
        var data=await MarketData(session,connection,ct);
        UnitCatalogData? units;
        try { units=await database.ReadUnitCatalog(connection,data,ct); }
        catch(MarketDataException e) when(e.Code=="unit_catalog") { units=null; }
        return (Data:data,Units:units);
    },c.RequestAborted);
    var data=read.Data;
    return Results.Ok(new {unitCatalog=read.Units,unitCatalogError=read.Units is null?"unit_catalog":null,context=session.MarketContext,profile=session.Profile,example=database.Manifest.Example,futuresConvention=database.Manifest.FuturesConvention,definitions=data.Definitions,references=data.References,observations=data.Observations,
        frames=session.Frames.Values.Where(f=>f.ContextId==session.MarketContext?.ContextId).OrderBy(f=>f.At).ToArray(),initialFrameIds=session.MarketContext is null?session.InitialFrames:session.Frames.Values.Where(f=>f.ContextId==session.MarketContext.ContextId).Select(f=>f.Id).ToArray()});
})));
api.MapPost("/evaluate",(HttpContext c,BuildRequest body)=>Locked(c,async session=>
{
    var frame=Frame(session,body.SnapshotId);
    if(session.MarketContext is {} selected && DateOnly.FromDateTime(frame.At.UtcDateTime)!=DateWorkspace.Date(selected.EffectiveOn))
        throw new MarketDataException("market_date","Choose inputs for the selected context date. No earlier market date is substituted.");
    var result=await database.WithIdentity(Profile(session),"ReadMarketData",async (connection,ct)=>(await MarketData(session,connection,ct)).Result(frame,body.Curve),c.RequestAborted);
    if(session.MarketContext is {} view)
    {
        var json=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(result,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase}))!.AsObject();
        json["context"]=JsonSerializer.SerializeToNode(view,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});return Results.Ok(json);
    }
    return Results.Ok(result);
}));
api.MapPost("/history",(HttpContext c,HistoryRequest body)=>Locked(c,async session=>
{
    var current=Frame(session,body.SnapshotId);
    var result=await database.WithIdentity(Profile(session),"ReadMarketData",async (connection,ct)=>
    {
        var data=await MarketData(session,connection,ct);
        var frames=(session.MarketContext is null?session.InitialFrames.Select(id=>Frame(session,id)):session.Frames.Values.Where(f=>f.ContextId==session.MarketContext.ContextId)).Select(f=>f.At==current.At?current:f).OrderBy(f=>f.At).ToArray();
        return frames.Select(f=>
        {
            try {
                var value=data.HistoryValue(f,body.Curve,body.ContractId,body.SeriesId);
                return new HistorySample(f.At,value.Value,f.Id,value.Inputs,null);
            }
            catch(MarketDataException error) when(error.Code is "missing_observation" or "source_expired")
            { return new HistorySample(f.At,null,f.Id,[],error.Code); }
        }).ToArray();
    },c.RequestAborted);
    return Results.Ok(new {mode="rebuilt_from_explicit_seed_pins_with_selected_frame",points=result});
}));
api.MapPost("/corrections",(HttpContext c,CorrectionRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to current input editing before changing observations.");
    var frame=Frame(session,body.SnapshotId);
    if (!frame.ObservationIds.Contains(body.ObservationId)) throw new MarketDataException("snapshot_input","Only an explicitly selected observation can be corrected here.");
    if (session.Frames.Count>=128) throw new MarketDataException("preview_limit","Restart the disposable preview to create more saved selections.");
    if (string.IsNullOrWhiteSpace(body.Value)) throw new MarketDataException("missing_value","Enter an exact decimal value.");
    c.Items["correction_attempted"]=true;
    var id=await database.WithIdentity(Profile(session),"ImportObservations",(connection,ct)=>database.Correct(connection,body.ObservationId,body.Value,ct),c.RequestAborted);
    var updated=frame with {Id=Guid.NewGuid(),Name="Edited inputs",ObservationIds=frame.ObservationIds.Replace(body.ObservationId,id)};
    session.Frames.Add(updated.Id,updated);
    return Results.Ok(new {frame=updated,observationId=id});
}));
api.MapPost("/select-observation",(HttpContext c,SelectionRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to current input editing before changing selected revisions.");
    var frame=Frame(session,body.SnapshotId);
    var updated=await database.WithIdentity(Profile(session),"ReadMarketData",async (connection,ct)=>
    {
        var data=await database.Read(connection,ct); var chosen=data.Observation(body.ObservationId);
        var previous=frame.ObservationIds.Where(id=>
        {
            var o=data.Observation(id);
            return o.Id("series_id")==chosen.Id("series_id")&&o.Text("source_event_id")==chosen.Text("source_event_id")&&o.Text("source_code")==chosen.Text("source_code")&&o.Text("role")==chosen.Text("role")&&o.Instant("observed_at")==chosen.Instant("observed_at");
        }).ToArray();
        if (previous.Length!=1) throw new MarketDataException("selection_mismatch","Select a revision of the same exact source fact and time.");
        return frame with {Id=Guid.NewGuid(),Name="Explicit revision selection",ObservationIds=frame.ObservationIds.Replace(previous[0],body.ObservationId)};
    },c.RequestAborted);
    if (session.Frames.Count>=128) throw new MarketDataException("preview_limit","Restart the disposable preview to create more saved selections.");
    session.Frames.Add(updated.Id,updated); return Results.Ok(new {frame=updated});
}));
api.MapPost("/parties/catalog",(HttpContext c,PartyReadRequest body)=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ReadTradeParties",async(connection,ct)=>
    {
        var frame=Frame(session,body.SnapshotId);
        var selected=session.MarketContext ?? (await knowledgeStore.Capture(connection,new(frame.At.UtcDateTime.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),null,"current"),ct)).Context;
        await MarketContextStore.Bind(connection,database.Manifest.CatalogId,selected,ct);
        return Results.Ok(new{versions=await PartyEditors.Read(connection,database.Manifest.CatalogId,body.Kind,frame.At,ct),context=selected,
            canEdit=session.MarketContext is null && await HasPermission(connection,"WriteTradeParties",ct)});
    },c.RequestAborted)));
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
Task<IResult> FuturesRead(HttpContext c,FuturesReadRequest body,Func<NpgsqlConnection,PreviewData,InputFrame,CancellationToken,Task<object>> read,bool scenario=false,Guid? headTrade=null,bool amendmentPreview=false,string? expectedConvention=null,bool dealRisk=false)=>
    Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
    {
        if(expectedConvention is SwapStore.Convention or TradingBlotter.Convention){if(body.Convention!=expectedConvention)throw new MarketDataException("swap_context","Select the explicit supported swap valuation convention.");}
        else FuturesStore.Convention(body.Convention);
        if(scenario && !await HasPermission(connection,"RunScenario",ct)||dealRisk && !await HasPermission(connection,"RunDealRisk",ct))throw new PreviewDeniedException();
        var frame=Frame(session,body.SnapshotId);
        var working=session.MarketContext is null&&body.KnowledgeMode=="current"&&body.ContextId is null;
        if(amendmentPreview&&!working)
            throw new MarketDataException("historical_read_only","Amendments require working data.");
        if(amendmentPreview&&!await HasPermission(connection,"AmendTrade",ct))throw new PreviewDeniedException();
        int? currentHead=null;
        if(working&&headTrade is {} trade)
        {
            await using var head=ScheduleStore.Command(connection,expectedConvention==SwapStore.Convention?"SELECT security.swap_current_head($1,$2)":"SELECT security.futures_current_head($1,$2)",database.Manifest.CatalogId,trade);
            currentHead=await head.ExecuteScalarAsync(ct) is int revision?revision:null;
        }
        ScheduleContext selected;
        if(body.KnowledgeMode=="current" && body.ContextId is null && session.MarketContext is null)
            selected=(await knowledgeStore.Capture(connection,new(frame.At.UtcDateTime.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),null,"current"),ct)).Context;
        else if(body.KnowledgeMode=="retained" && body.ContextId is {} id)
        {
            selected=(await knowledgeStore.Read(connection,id,ct)).Context;
            if(session.MarketContext is {} view && view.ContextId!=selected.ContextId)
                throw new MarketDataException("futures_context","Trading must use the shared selected historical context.");
        }
        else throw new MarketDataException("futures_context","Choose current knowledge explicitly or supply the exact retained context.");
        await MarketContextStore.Bind(connection,database.Manifest.CatalogId,selected,ct);
        await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_trading_context($1,$2)",database.Manifest.CatalogId,frame.At))await bind.ExecuteNonQueryAsync(ct);
        var data=await database.Read(connection,ct);
        var value=await read(connection,data,frame,ct);
        if(scenario && !await HasPermission(connection,"RunScenario",ct)||dealRisk && !await HasPermission(connection,"RunDealRisk",ct))throw new PreviewDeniedException();
        await using(var check=ScheduleStore.Command(connection,"SELECT security.trading_access($1,'ReadTrades')",database.Manifest.CatalogId))
            if(!Equals(await check.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
        var json=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(value,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,Converters={new ExactDecimalJsonConverter()}}))!.AsObject();
        json["context"]=JsonSerializer.SerializeToNode(selected,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase});json["asOf"]=JsonSerializer.SerializeToNode(frame.At);
        json["canRunDealRisk"]=JsonSerializer.SerializeToNode(await HasPermission(connection,"RunDealRisk",ct));
        json["canBook"]=JsonSerializer.SerializeToNode(session.MarketContext is null&&body.KnowledgeMode=="current"&&await HasPermission(connection,"BookTrade",ct));
        if(json["books"] is not null)
        {
            json["canAmendTrades"]=JsonSerializer.SerializeToNode(working&&await HasPermission(connection,"AmendTrade",ct));
            json["bookingUnavailableReason"]=JsonSerializer.SerializeToNode(!working
                ?"Historical view is read-only. Return to working data, then select an As of date."
                :!await HasPermission(connection,"BookTrade",ct)?"Your current role does not have Book Trade permission.":null);
        }
        if(headTrade is not null)
        {
            var selectedRevision=json["state"]?["revision"]?.GetValue<int>()??json["expectedRevision"]?.GetValue<int>();
            var lifecycleEligible=json["lifecycleEligible"]?.GetValue<bool>()??true;
            var canAmend=working&&currentHead==selectedRevision&&lifecycleEligible&&await HasPermission(connection,"AmendTrade",ct);
            if(amendmentPreview&&!canAmend)throw new MarketDataException(expectedConvention==SwapStore.Convention?"swap_revision_conflict":"futures_revision_conflict","Reload the latest eligible trade revision before amending.");
            json["canAmend"]=JsonSerializer.SerializeToNode(canAmend);
            json["amendUnavailableReason"]=JsonSerializer.SerializeToNode(!working?"Historical view is read-only. Return to working data to amend."
                :currentHead!=selectedRevision?"This is an earlier trade revision. Open the latest revision at its working As of date."
                :!lifecycleEligible?"This retained state is outside the supported live, unprocessed futures lifecycle. Settlement must be handled separately."
                :!canAmend?"Your current role does not have Amend Trade permission.":null);
        }
        return Results.Ok(json);
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
api.MapPost("/futures/editor",(HttpContext c,FuturesEditorRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("futures_trade","Supply the selected context."),
    (connection,data,frame,ct)=>futuresStore.Editor(connection,data,frame,body,ct),headTrade:body.TradeId));
api.MapPost("/futures/amend-preview",(HttpContext c,FuturesAmendRequest body)=>FuturesRead(c,new(body.SnapshotId,body.Convention,body.KnowledgeMode),
    (connection,data,frame,ct)=>futuresStore.PreviewAmendment(connection,data,frame,body,ct),headTrade:body.TradeId,amendmentPreview:true));
api.MapPost("/futures/amend",(HttpContext c,FuturesAmendRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before amending.");
    c.Items["futures_attempted"]=true;
    return await database.WithIdentity(Profile(session),"AmendTrade",async(connection,ct)=>
    {
        if(!await HasPermission(connection,"ReadTrades",ct)||!await HasPermission(connection,"ReadMarketData",ct))throw new PreviewDeniedException();
        return Results.Ok(await futuresStore.Amend(connection,await database.Read(connection,ct),Frame(session,body.SnapshotId),body,ct));
    },c.RequestAborted);
}));
api.MapPost("/futures/blotter",(HttpContext c,FuturesBlotterRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("blotter_view","Supply the selected context."),(connection,data,frame,ct)=>futuresStore.Blotter(connection,data,frame,body.View,ct)));
api.MapPost("/futures/holdings",(HttpContext c,FuturesReadRequest body)=>FuturesRead(c,body,futuresStore.Holdings));
var swapStore=new SwapStore(database);
var tradingBlotter=new TradingBlotter(database.Manifest,futuresStore,swapStore);
api.MapPost("/trading/blotter",(HttpContext c,FuturesBlotterRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("blotter_view","Supply the selected context."),
 (connection,data,frame,ct)=>tradingBlotter.Read(connection,data,frame,body.View,ct),expectedConvention:TradingBlotter.Convention));
api.MapPost("/swaps/catalog",(HttpContext c,FuturesReadRequest body)=>FuturesRead(c,body,swapStore.Catalog,expectedConvention:SwapStore.Convention));
api.MapPost("/swaps/editor",(HttpContext c,SwapEditorRequest body)=>FuturesRead(c,body.Context??throw new MarketDataException("swap_context","Supply the selected context."),
 (connection,data,frame,ct)=>swapStore.Editor(connection,data,frame,body,ct),headTrade:body.TradeId,expectedConvention:SwapStore.Convention));
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
api.MapPost("/futures/risk/catalog",(HttpContext c,FuturesReadRequest body)=>FuturesRead(c,body,futuresRisk.Catalog,scenario:true));
api.MapPost("/futures/risk",(HttpContext c,FuturesRiskRequest body)=>FuturesRead(c,
    body.Context??throw new MarketDataException("risk_context","Select the shared financial context."),
    (connection,data,frame,ct)=>futuresRisk.Calculate(connection,data,frame,body,ct),scenario:true));
api.MapPost("/futures/book",(HttpContext c,FuturesBookRequest body)=>Locked(c,async session=>
{
    if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before booking or amending.");
    c.Items["futures_attempted"]=true;
    return await database.WithIdentity(Profile(session),"BookTrade",async(connection,ct)=>
    {
        if(!await HasPermission(connection,"ReadTrades",ct))throw new PreviewDeniedException();
        return Results.Ok(await futuresStore.Book(connection,await database.Read(connection,ct),Frame(session,body.SnapshotId),body,ct));
    },c.RequestAborted);
}));
api.MapPost("/futures/status",(HttpContext c,FuturesStatusRequest body)=>Locked(c,session=>database.WithIdentity(Profile(session),"ReadTrades",async(connection,ct)=>
    Results.Ok(await futuresStore.Status(connection,body.OperationId,ct)),c.RequestAborted)));
api.MapGet("/access",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),null,async(connection,ct)=>
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.current_permissions()");
        return Results.Ok(new {permissions=(string[])(await command.ExecuteScalarAsync(ct))!});
    },c.RequestAborted))));
// The principal kinds security.principals accepts; each maps to one login kind.
(string Code,string Name)[] IdentityKinds=[("human","Human"),("service","Service"),("agent","Agent")];
api.MapGet("/admin",(Func<HttpContext,Task<IResult>>)(c=>Locked(c,session=>
    database.WithIdentity(Profile(session),"ManageAccess",async(connection,ct)=>
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.administration_directory()::text");
        var directory=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>((string)(await command.ExecuteScalarAsync(ct))!)!;
        directory["kinds"]=JsonSerializer.SerializeToElement(IdentityKinds.Select(k=>new {code=k.Code,name=k.Name}));
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
    },c.RequestAborted,administration:true);
    if(result.GetProperty("kind").GetString()=="human")session.CustomProfiles[result.GetProperty("login").GetString()!]=result.GetProperty("name").GetString()!;
    return Results.Ok(result);
});
api.MapPost("/admin/create",(HttpContext c,AdminCreate body)=>AdminMutation(c,body,"create"));
api.MapPost("/admin/update",(HttpContext c,AdminUpdate body)=>AdminMutation(c,body,"update"));
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
public sealed record LocalTimeRequest(string? LocalTime,string? TimeZone);
public sealed record BuildRequest(Guid SnapshotId,string Curve);
public sealed record HistoryRequest(Guid SnapshotId,string Curve,Guid? ContractId=null,Guid? SeriesId=null);
public sealed record HistorySample(DateTimeOffset At,string? Value,Guid SnapshotId,Guid[] ObservationIds,string? UnavailableReason);
public sealed record CorrectionRequest(Guid SnapshotId,Guid ObservationId,string Value);
public sealed record SelectionRequest(Guid SnapshotId,Guid ObservationId);
public sealed class PreviewSession
{
    public DateTimeOffset Created {get;}=DateTimeOffset.UtcNow;
    public SemaphoreSlim Gate {get;}=new(1,1);
    public string? Profile {get;set;}
    public ScheduleContext? MarketContext {get;set;}
    public Dictionary<string,string> CustomProfiles {get;}=[];
    public Dictionary<Guid,InputFrame> Frames {get;}=[];
    public PreviewShortcutLibrary Shortcuts {get;}=new();
    public DateWorkspace Dates {get;}=new();
    public Dictionary<(string Owner,string Code),BoundContractDefinition> ContractDefinitions {get;}=[];
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
