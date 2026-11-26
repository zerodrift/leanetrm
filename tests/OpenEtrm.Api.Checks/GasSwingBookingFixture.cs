using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;

// Generate and publish retained programs using the same managed staging and native commands as booking.
// The preview runner owns these disposable databases, including its synthetic roles and close frames.
internal static class GasSwingBookingFixture
{
    internal static async Task Book(string root,string input)
    {
        var runtime = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_RUNTIME"));
        var authenticator = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_AUTHENTICATOR"));
        if(authenticator.Database!=runtime.Database || authenticator.Host!=root || authenticator.Port!=runtime.Port ||
            authenticator.Username!="preview_authenticator" || runtime.Username!="preview_runtime" || runtime.Database is not ("postgres" or "preview_swing_0402" or "preview_swing_fresh") || runtime.Host!=root || !File.Exists(Path.Combine(root,".openetrm-preview")))
            throw new InvalidOperationException("Require an owned gas swing fixture database of this preview.");
        authenticator.Timezone = runtime.Timezone = "UTC";
        var manifest = JsonSerializer.Deserialize<PreviewManifest>(await File.ReadAllTextAsync(Path.Combine(root,"manifest.json")),JsonSerializerOptions.Web)!;
        await using var database = new PreviewDatabase(manifest,authenticator,runtime);
        var body=JsonSerializer.Deserialize<GasCompositionRequest>(await File.ReadAllTextAsync(input),JsonSerializerOptions.Web)!;
        if(runtime.Database!="postgres" && body.SwingRights is {Length:>0})throw new InvalidOperationException("Use ordinary published terms for predecessor setup.");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var frame=await database.WithIdentity("trader",null,(connection,ct)=>
            MarketCloses.Frame(connection,database.Manifest.CatalogId,body.SnapshotId,null,ct),timeout.Token);
        var receipt=await new GasCompositionStore(database).Book("trader",frame,body,false,timeout.Token);
        Console.WriteLine(receipt.GetRawText());
    }
}
