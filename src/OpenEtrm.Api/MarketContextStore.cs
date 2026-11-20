using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public sealed record MarketContextRequest(string? EffectiveOn,string? KnownAt,string? Mode,string? Selection,Guid? SnapshotId=null,string? FrameSelectionPolicy=null,string? RestoreKey=null);
// Selection is deliberately separate from certification. A certified timestamp
// does not authorize choosing a source, filling a gap or rolling a contract.
public static class MarketContextStore
{
    public static async Task Bind(NpgsqlConnection connection,Guid catalog,ScheduleContext context,CancellationToken ct)
    {
        var exact=await new KnowledgeResolver(catalog).Retained(connection,context,ct);
        await exact.ReadCut!.Bind(connection,catalog,exact,ct);
    }

    public static InputFrame Select(InputFrame seed,PreviewData data,ScheduleContext context)
    {
        return data.SelectPinned(seed) with {Id=Guid.NewGuid(),Name="Certified market inputs",ContextId=context.ContextId,ResolvesLatestCorrection=true};
    }
}
