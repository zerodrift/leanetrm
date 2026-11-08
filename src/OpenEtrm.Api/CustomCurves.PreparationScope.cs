using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

public static partial class CustomCurves
{
    // Owned by one authorized read. Only ordinary base markets enter this scope;
    // instrument-specific fixing admission still runs on every use.
    internal sealed class PreparationScope
    {
        private sealed record Entry(NpgsqlConnection Connection,PreviewDatabase Database,PreviewData Data,
            MarketSnapshot Snapshot,ScheduleContext Context,ImmutableHashSet<CurveRequest> Dependencies,PreparedMarket Market);
        private readonly List<Entry> entries=[];

        internal PreparedMarket? Find(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,
            MarketSnapshot snapshot,ScheduleContext context,ImmutableArray<CurveRequest> dependencies)
        {
            var found=entries.FirstOrDefault(entry=>ReferenceEquals(entry.Connection,connection)&&ReferenceEquals(entry.Database,database)&&
                ReferenceEquals(entry.Data,data)&&entry.Context==context&&entry.Dependencies.Count==dependencies.Length&&
                entry.Dependencies.SetEquals(dependencies)&&entry.Snapshot.HasSameInputs(snapshot));
            return found is null?null:found.Market with{Preparation=new(0,0)};
        }

        internal void Add(NpgsqlConnection connection,PreviewDatabase database,PreviewData data,MarketSnapshot snapshot,
            ScheduleContext context,ImmutableArray<CurveRequest> dependencies,PreparedMarket market)=>
            entries.Add(new(connection,database,data,snapshot,context,dependencies.ToImmutableHashSet(),market));
    }

    // The saved custom curve versions one authorized read sees, read once on its connection. What a read sees depends only on
    // the request context its transaction has bound, so the read's valuations share this one read rather than repeating it.
    internal sealed class SavedVersions(PreviewDatabase database)
    {
        private (NpgsqlConnection Connection,ImmutableArray<CustomCurveVersion> Versions)? read;

        internal async Task<ImmutableArray<CustomCurveVersion>> Read(NpgsqlConnection connection,CancellationToken ct)
        {
            if(read is {} prior&&ReferenceEquals(prior.Connection,connection))return prior.Versions;
            var versions=await new CustomCurvesStore(database).Read(connection,ct);
            read=(connection,versions);
            return versions;
        }
    }
}
