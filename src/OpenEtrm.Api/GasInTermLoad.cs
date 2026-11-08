using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasInTermLoadConfiguration([property:JsonRequired] string ManifestFile,[property:JsonRequired] string AuthenticatorConnectionFile,
    [property:JsonRequired] string LoaderConnectionFile);
// The ticket's composition request, whose snapshot names a recorded close, and the administrator who loads it.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasInTermLoadRequest([property:JsonRequired] string Administrator,[property:JsonRequired] GasCompositionRequest Composition);

/// <summary>Private operator entry point (D360): a named administrator loads a gas composition already in its delivery
/// term through the ticket's booking path, on the dedicated loader login. There is no HTTP route.</summary>
public static class GasInTermLoad
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    // The ticket booking's authority and the reads it values with, each refused under its own code.
    static readonly (string Permission,string Code)[] Authority=[("BookTrade","gas_in_term_book_authority"),("ReadTrades","gas_in_term_trade_read_authority"),
        ("ReadMarketData","gas_in_term_market_read_authority")];
    public static async Task<int> Run(string[] args)
    {
        try
        {
            if(args is not [var configuration,var requestFile])throw new MarketDataException("gas_in_term_command","Choose --admin-load-gas-in-term CONFIG REQUEST with explicit private files.");
            using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(5));var ct=deadline.Token;
            var settings=Read<GasInTermLoadConfiguration>(configuration,64*1024);var request=Read<GasInTermLoadRequest>(requestFile,4*1024*1024);
            if(string.IsNullOrWhiteSpace(request.Administrator)||request.Administrator.Length>48)throw new MarketDataException("gas_in_term_command","Name the administrator who loads the composition.");
            var body=request.Composition;GasCompositionStore.RequireNewTrade(body);
            if(body.KnowledgeMode!="current")throw new MarketDataException("historical_read_only","Load with current knowledge from a recorded close.");
            var manifest=JsonSerializer.Deserialize<PreviewManifest>(File.ReadAllText(settings.ManifestFile),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new JsonException();
            if(manifest.Mode is not("local-synthetic-preview" or "hosted-pilot"))throw new InvalidOperationException();
            ReferenceImportCommand.Private(settings.AuthenticatorConnectionFile);ReferenceImportCommand.Private(settings.LoaderConnectionFile);
            var auth=HostedConfiguration.Connection(settings.AuthenticatorConnectionFile);var loader=HostedConfiguration.Connection(settings.LoaderConnectionFile);
            if(auth.Username==loader.Username||auth.Host!=loader.Host||auth.Port!=loader.Port||auth.Database!=loader.Database)throw new InvalidOperationException();
            await using var database=new PreviewDatabase(manifest,auth,loader);await database.RequireLoaderBoundary(ct);
            var frame=await database.WithIdentity(request.Administrator,null,async(connection,ct)=>
            {
                foreach(var(permission,code) in Authority)
                    if(!await PreviewDatabase.HasPermission(connection,permission,ct))throw new MarketDataException(code,$"The named administrator does not hold {permission}, which the in-term load requires.");
                try{return await MarketCloses.Frame(connection,manifest.CatalogId,body.SnapshotId,null,ct);}
                catch(PreviewDeniedException){throw new MarketDataException("gas_in_term_close","Name a recorded close as the composition's snapshot.");}
            },ct);
            var result=await new GasCompositionStore(database).Book(request.Administrator,frame,body,true,ct);
            Console.WriteLine(result.GetRawText());return 0;
        }
        catch(MarketDataException e){return Refuse(e.Code,e.Message);}
        catch(PreviewDeniedException){return Refuse("gas_in_term_authority","The named administrator does not hold the booking authority the load requires.");}
        catch(PostgresException e) when(e.SqlState=="42501"&&e.MessageText=="Unbound or disabled identity")
        {return Refuse("gas_in_term_identity","The named administrator is not an enabled identity of this workspace's issuer.");}
        catch(PostgresException e) when(e.SqlState=="P0001"&&System.Text.RegularExpressions.Regex.IsMatch(e.MessageText,"^[a-z][a-z0-9_]{0,63}$"))
        {return Refuse(e.MessageText,"The database refused the in-term load.");}
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NpgsqlException or OperationCanceledException)
        {return Refuse("gas_in_term_failed","The in-term load was not confirmed. Retry the same request to receive its first outcome; no credential details are logged.");}
    }
    static int Refuse(string code,string error){Console.Error.WriteLine(JsonSerializer.Serialize(new{code,error}));return 1;}
    static T Read<T>(string path,int maximum)
    {
        ReferenceImportCommand.Private(path);if(new FileInfo(path).Length>maximum)throw new IOException();
        using var document=JsonDocument.Parse(File.ReadAllBytes(path));CmeReferenceCapture.RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Deserialize<T>(Json)??throw new JsonException();
    }
}
