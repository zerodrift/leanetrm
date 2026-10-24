using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Npgsql;

namespace OpenEtrm.Api;

public sealed record HostedIdentity(Guid PrincipalId, string Issuer, string Subject, string Login, string Name, DateTimeOffset Ends);
// The sign-in session an application session belongs to: the digest that names it to the database, and when it ends
// (docs/ai-assistant.md §11).
public sealed record HostedSignIn(string Digest, DateTimeOffset Ends);

public sealed class HostedAuthentication : IAsyncDisposable
{
    private const string Scheme = "LocalAccount";
    private readonly NpgsqlDataSource source;
    private readonly HostedConfiguration settings;
    // Unknown accounts perform the same supported password verification work.
    private readonly string dummyHash = HostedAccess.HashPassword(HostedAccess.NewToken());
    private readonly SemaphoreSlim hashWork = new(4, 4);
    public HostedAuthentication(HostedConfiguration settings)
    {
        this.settings = settings;
        source = NpgsqlDataSource.Create(HostedConfiguration.Connection(settings.AuthenticatorConnectionFile).ConnectionString);
    }
    public static void Configure(WebApplicationBuilder builder, HostedConfiguration settings)
    {
        builder.Services.AddDataProtection().SetApplicationName("OpenEtrm:" + settings.InstallationId)
            .PersistKeysToFileSystem(Directory.CreateDirectory(Path.Combine(settings.StateDirectory, "keys")))
            .ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(settings.ProtectionCertificateFile,null));
        builder.Services.AddHttpContextAccessor();
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1; o.KnownProxies.Clear(); o.KnownIPNetworks.Clear();
            o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(settings.TrustedProxyNetwork));
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("local-auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
        });
        builder.Services.AddAuthentication(Scheme).AddCookie(Scheme, o =>
        {
            o.Cookie.Name = "__Host-openetrm_account"; o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Strict; o.Cookie.SecurePolicy = CookieSecurePolicy.Always; o.Cookie.Path = "/";
            o.ExpireTimeSpan = TimeSpan.FromMinutes(30); o.SlidingExpiration = false;
            o.Events.OnValidatePrincipal = async c =>
            {
                var authentication = c.HttpContext.RequestServices.GetRequiredService<HostedAuthentication>();
                var token = c.Principal?.FindFirstValue("session");
                var identity = token is null ? null : await authentication.Identity(token, c.HttpContext.RequestAborted);
                if (identity is null || c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier) != identity.Subject)
                { c.RejectPrincipal(); await c.HttpContext.SignOutAsync(Scheme); }
                else c.HttpContext.Items[typeof(HostedIdentity)] = identity;
            };
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        });
    }
    public static HostedIdentity Current(HttpContext context) => context.Items[typeof(HostedIdentity)] as HostedIdentity
        ?? throw new PreviewDeniedException();
    public static string SessionDigest(HttpContext context) => HostedAccess.TokenDigest(context.User.FindFirstValue("session")??throw new PreviewDeniedException());
    public static HostedSignIn SignIn(HttpContext context) => new(SessionDigest(context), Current(context).Ends);
    public async Task<HostedIdentity?> Identity(string token, CancellationToken ct)
    {
        string digest;
        try { digest = HostedAccess.TokenDigest(token); } catch (ArgumentException) { return null; }
        await using var command = source.CreateCommand("SELECT * FROM security.hosted_session_info($1)");
        command.Parameters.AddWithValue(digest);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        if (reader.GetString(1) != settings.IdentityIssuer || reader.GetGuid(5) != Tenant || reader.GetGuid(6) != Workspace) return null;
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetFieldValue<DateTimeOffset>(7));
    }
    public Guid Tenant { private get; set; }
    public Guid Workspace { private get; set; }
    // Told of each sign-out once its session is revoked, with the session's digest and subject, so the session's streams
    // end at once (docs/ai-assistant.md §11).
    public Action<string, string>? SignedOut { private get; set; }
    public void Map(WebApplication app)
    {
        var auth = app.MapGroup("/auth");
        auth.MapGet("", (HttpContext c, IAntiforgery antiforgery) =>
        {
            var csrf = HtmlEncoder.Default.Encode(antiforgery.GetAndStoreTokens(c).RequestToken!);
            return Results.Content(Page(csrf), "text/html; charset=utf-8");
        });
        auth.MapGet("/script.js", () => Results.Content("""
            const token=location.hash.startsWith('#invite=')?location.hash.slice(8):'';
            if(token){document.getElementById('invitation').value=token;document.getElementById('claim').hidden=false;document.getElementById('signin').hidden=true;history.replaceState(null,'','/auth');}
            if(location.search==='?claimed=1')document.getElementById('message').textContent='Password set. Sign in with your chosen login.';
            if(location.search==='?failed=1')document.getElementById('message').textContent='Sign-in or invitation could not be verified. Check your details or request a new invitation.';
            """, "text/javascript"));
        auth.MapGet("/style.css",()=>Results.Content("""
            *{box-sizing:border-box}body{margin:0;background:#f4f6f1;color:#20382e;font:16px/1.5 system-ui,sans-serif;min-height:100vh;display:grid;place-items:center;padding:24px}main{width:100%;max-width:440px;background:white;border:1px solid #dbe4d7;border-radius:14px;padding:36px;box-shadow:0 12px 40px #20382e0c}h1{font-size:27px;margin:0 0 12px;letter-spacing:-.7px}#message{font-size:14px;color:#637367;margin-bottom:24px}label{display:grid;gap:7px;font-size:14px;font-weight:600}input{width:100%;padding:11px 12px;border:1px solid #cbd7c6;border-radius:6px;font:inherit}input:focus{outline:2px solid #8faa75;outline-offset:2px}button{width:100%;border:0;border-radius:6px;background:#365d3b;color:white;padding:12px;font:600 15px system-ui;cursor:pointer;margin-top:10px}[hidden]{display:none!important}
            ""","text/css"));
        auth.MapPost("/claim", async (HttpContext c) =>
        {
            if(c.User.Identity?.IsAuthenticated==true)return Results.StatusCode(409);
            var form = await c.Request.ReadFormAsync(c.RequestAborted);
            string login, digest, hash;
            try
            {
                login = HostedAccess.Login(form["login"].ToString());
                digest = HostedAccess.TokenDigest(form["invitation"].ToString());
                var password = form["password"].ToString(); HostedAccess.Password(password);
                await hashWork.WaitAsync(c.RequestAborted);
                try { hash = HostedAccess.HashPassword(password); } finally { hashWork.Release(); }
            }
            catch (ArgumentException) { return Results.Redirect("/auth?failed=1"); }
            await using var command = source.CreateCommand("SELECT security.claim_hosted_invitation($1,$2,$3,$4)");
            foreach (var value in new[] { digest, login, form["name"].ToString(), hash }) command.Parameters.AddWithValue(value);
            return Results.Redirect(await command.ExecuteScalarAsync(c.RequestAborted) is Guid ? "/auth?claimed=1" : "/auth?failed=1");
        }).RequireRateLimiting("local-auth");
        auth.MapPost("/login", async (HttpContext c) =>
        {
            if(c.User.Identity?.IsAuthenticated==true)return Results.Redirect("/");
            var form = await c.Request.ReadFormAsync(c.RequestAborted);
            string? login;
            try { login = HostedAccess.Login(form["login"].ToString()); }
            catch (ArgumentException) { login = null; }
            Guid id = Guid.Empty; long version = 0; string hash = dummyHash;
            await using (var lookup = source.CreateCommand("SELECT * FROM security.hosted_login_lookup($1)"))
            {
                lookup.Parameters.AddWithValue(login??"");
                await using var reader = await lookup.ExecuteReaderAsync(c.RequestAborted);
                if (await reader.ReadAsync(c.RequestAborted)) { id = reader.GetGuid(0); hash = reader.GetString(1); version = reader.GetInt64(2); }
            }
            PasswordVerificationResult verification;string replacement;
            await hashWork.WaitAsync(c.RequestAborted);
            try { verification=HostedAccess.CheckPassword(hash,form["password"].ToString());replacement=verification==PasswordVerificationResult.SuccessRehashNeeded?HostedAccess.HashPassword(form["password"].ToString()):hash; } finally { hashWork.Release(); }
            var token = HostedAccess.NewToken();
            await using var finish = source.CreateCommand("SELECT security.finish_hosted_login($1,$2,$3,$4,$5,$6)");
            finish.Parameters.AddWithValue(id); finish.Parameters.AddWithValue(version); finish.Parameters.AddWithValue(hash);
            finish.Parameters.AddWithValue(verification!=PasswordVerificationResult.Failed && id != Guid.Empty); finish.Parameters.AddWithValue(HostedAccess.TokenDigest(token));finish.Parameters.AddWithValue(replacement);
            if (await finish.ExecuteScalarAsync(c.RequestAborted) is not Guid || await Identity(token, c.RequestAborted) is not {} identity)
                return Results.Redirect("/auth?failed=1");
            var claims = new ClaimsIdentity([new(ClaimTypes.NameIdentifier, identity.Subject), new("session", token)], Scheme);
            c.Response.Cookies.Delete("__Host-openetrm_workspace",new CookieOptions{Secure=true,Path="/"});
            await c.SignInAsync(Scheme, new ClaimsPrincipal(claims), new AuthenticationProperties { IsPersistent = false });
            return Results.Redirect("/");
        }).RequireRateLimiting("local-auth");
        app.MapPost("/api/logout", async (HttpContext c) =>
        {
            var token = c.User.FindFirstValue("session") ?? throw new PreviewDeniedException();
            var digest = HostedAccess.TokenDigest(token);
            await using var command = source.CreateCommand("SELECT security.revoke_hosted_session($1)");
            command.Parameters.AddWithValue(digest); await command.ExecuteNonQueryAsync(c.RequestAborted);
            SignedOut?.Invoke(digest, Current(c).Subject);
            await c.SignOutAsync(Scheme); c.Response.Cookies.Delete("__Host-openetrm_workspace",new CookieOptions{Secure=true,Path="/"}); return Results.Ok(new{signedOut=true});
        }).WithMetadata(new PreviewScope("baseline"));
    }
    private static string Page(string csrf) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>LeanETRM · Sign in</title><link rel="stylesheet" href="/auth/style.css"></head>
        <body><main><h1>LeanETRM</h1><p id="message">Private workspace. Sign in with your invited account.</p>
        <form id="signin" method="post" action="/auth/login"><input type="hidden" name="__RequestVerificationToken" value="{{csrf}}"><p><label>Login <input name="login" autocomplete="username" required maxlength="48"></label></p><p><label>Password <input name="password" type="password" autocomplete="current-password" required maxlength="1024"></label></p><button>Sign in</button></form>
        <form id="claim" method="post" action="/auth/claim" hidden><input type="hidden" name="__RequestVerificationToken" value="{{csrf}}"><input id="invitation" type="hidden" name="invitation"><p><label>Login <input name="login" autocomplete="username" pattern="[a-z][a-z0-9_]{0,47}" required maxlength="48"></label></p><p><label>Display name <input name="name" autocomplete="name" required maxlength="80"></label></p><p><label>Choose password <input name="password" type="password" autocomplete="new-password" required minlength="15" maxlength="1024"></label></p><button>Set password</button></form>
        </main><script src="/auth/script.js"></script></body></html>
        """;
    public async ValueTask DisposeAsync() { await source.DisposeAsync(); hashWork.Dispose(); }
}
