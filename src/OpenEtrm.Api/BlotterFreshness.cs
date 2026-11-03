using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace OpenEtrm.Api;

// A conservative equality token, never a read authorization. Its input head is sampled before a valuation starts;
// a publication during that valuation therefore forces a new read at the next validation rather than being missed.
internal static class BlotterFreshness
{
    // A server restart/release cannot validate values made by another running implementation.
    static readonly Guid instance=Guid.NewGuid();
    internal static async Task<string> Read(NpgsqlConnection connection,PreviewManifest manifest,FuturesReadRequest request,
        TradingReadSelection selection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.blotter_head($1)",manifest.CatalogId);
        var head=(string)(await command.ExecuteScalarAsync(ct)??throw new PreviewDeniedException());
        var inputs=JsonSerializer.SerializeToUtf8Bytes(new{instance,head,manifest,request,selection.Profile,selection.Frame,selection.SelectedContext,selection.Live});
        return Convert.ToHexStringLower(SHA256.HashData(inputs));
    }
}
