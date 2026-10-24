using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record MarketContextRequest(string? EffectiveOn,string? KnownAt,string? Mode,string? Selection);
// A retained context selected again by its id, as captured, with the same explicit correction policy.
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record RetainedContextRequest(Guid ContextId,string? Selection);

// Selection is deliberately separate from certification. A certified timestamp
// does not authorize choosing a source, filling a gap or rolling a contract.
public static class MarketContextStore
{
    public static async Task Bind(NpgsqlConnection connection,Guid catalog,ScheduleContext context,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT security.use_market_context($1,$2)",catalog,context.ContextId!);
        await command.ExecuteNonQueryAsync(ct);
    }

    public static InputFrame Select(InputFrame seed,PreviewData data,ScheduleContext context)
    {
        return data.SelectPinned(seed) with {Id=Guid.NewGuid(),Name="Certified market inputs",ContextId=context.ContextId,ResolvesLatestCorrection=true};
    }
}
