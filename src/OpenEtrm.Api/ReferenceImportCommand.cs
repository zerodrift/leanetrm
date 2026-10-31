using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReferenceImportConfiguration(string AuthenticatorConnectionFile,string OperatorConnectionFile,
    string Issuer,string Subject,Guid TenantId,Guid WorkspaceId,Guid CatalogId,Guid OperationId,ReferenceImportOptions Mapping);

/// <summary>Private operator entry point. Replays retained bytes before opening a trusted executor connection.</summary>
public static class ReferenceImportCommand
{
    public static async Task<int> Run(string[] args)
    {
        try
        {
            var inspect=args.Length>0&&args[0]=="inspect";if(inspect)args=args[1..];
            if(args is not [var configuration,var capture])throw new MarketDataException("reference_import_command","Choose --import-reference CONFIG CAPTURE with explicit private files.");
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));var ct=deadline.Token;
            var settings=Read<ReferenceImportConfiguration>(configuration,1024*1024);
            if(settings.TenantId==Guid.Empty||settings.WorkspaceId==Guid.Empty||settings.CatalogId==Guid.Empty||settings.OperationId==Guid.Empty||
                !Uri.TryCreate(settings.Issuer,UriKind.Absolute,out var issuer)||issuer.Scheme!="https"||string.IsNullOrWhiteSpace(settings.Subject)||settings.Subject.Length>160||settings.Mapping is null)
                throw new MarketDataException("reference_import_command","Explicit identity, catalog, operation and mappings are required.");
            var plan=await ReferenceImportProtocol.Prepare(Read<ReferenceCapturePackage>(capture,48*1024*1024),settings.Mapping,ct);
            if(inspect){Console.WriteLine(JsonSerializer.Serialize(new{state="inactive",planned=true,provider=plan.Provider,products=plan.Products.Length,series=plan.Series.Length,documents=plan.Documents.Length}));return 0;}
            Private(settings.AuthenticatorConnectionFile);Private(settings.OperatorConnectionFile);
            var auth=HostedConfiguration.Connection(settings.AuthenticatorConnectionFile);var executor=HostedConfiguration.Connection(settings.OperatorConnectionFile);
            if(auth.Username==executor.Username||auth.Host!=executor.Host||auth.Port!=executor.Port||auth.Database!=executor.Database)throw new InvalidOperationException();
            auth.CommandTimeout=30;executor.CommandTimeout=30;
            await using var authenticator=new NpgsqlConnection(auth.ConnectionString);await authenticator.OpenAsync(ct);
            await using var connection=new NpgsqlConnection(executor.ConnectionString);await connection.OpenAsync(ct);
            await Boundary(authenticator,false,ct);await Boundary(connection,true,ct);
            Guid token;
            await using(var issue=ScheduleStore.Command(authenticator,"SELECT security.issue_context($1,$2,'service_credential',$3,$4,NULL,$5::name,120)",settings.Issuer,settings.Subject,settings.TenantId,settings.WorkspaceId,executor.Username!))
                token=(Guid)(await issue.ExecuteScalarAsync(ct)??throw new InvalidOperationException());
            await using var transaction=await connection.BeginTransactionAsync(ct);
            await using(var bind=ScheduleStore.Command(connection,"SELECT security.use_context($1)",token))await bind.ExecuteNonQueryAsync(ct);
            string receipt;
            await using(var import=ScheduleStore.Command(connection,"SELECT security.import_reference_capture($1,$2,$3::jsonb)::text",settings.CatalogId,settings.OperationId,plan.Payload()))
                receipt=(string)(await import.ExecuteScalarAsync(ct)??throw new InvalidOperationException());
            await transaction.CommitAsync(ct);
            Console.WriteLine(receipt);return 0;
        }
        catch(MarketDataException e){Console.Error.WriteLine(JsonSerializer.Serialize(new{code=e.Code,error=e.Message}));return 1;}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NpgsqlException or OperationCanceledException)
        {Console.Error.WriteLine("{\"code\":\"reference_import_failed\",\"error\":\"Private import was not confirmed. Retain the same operation and inputs to reconcile safely; no source or credential details are logged.\"}");return 1;}
    }
    internal static Task Boundary(NpgsqlConnection connection,bool executor,CancellationToken ct) =>
        DatabaseLoginBoundary.Require(connection, executor ? DatabaseLoginProfile.ReferenceImporter : DatabaseLoginProfile.ImportAuthenticator, ct);
    static T Read<T>(string path,int maximum)
    {
        Private(path);if(new FileInfo(path).Length>maximum)throw new IOException();
        using var document=JsonDocument.Parse(File.ReadAllBytes(path));CmeReferenceCapture.RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<T>(ReferenceImportProtocol.Json)??throw new JsonException();
    }
    static void Private(string path)
    {
        if(OperatingSystem.IsWindows()||!Path.IsPathFullyQualified(path)||new FileInfo(path).LinkTarget is not null||
           (File.GetUnixFileMode(path)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute))!=0)
            throw new IOException();
    }
}
