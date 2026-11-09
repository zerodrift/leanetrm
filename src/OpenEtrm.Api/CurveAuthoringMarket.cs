using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Authoring can select a separately admitted sample bundle without changing the
// primary pricing revision used by existing holdings. Each selection stays exact.
public sealed record PreviewCurveMarket(string Name,Guid RevisionId,PreviewSeedFrame[] Frames);
public sealed record CurveMarketChoice(Guid RevisionId,string Name);
internal static class CurveAuthoringMarket
{
    static MarketDataException Invalid()=>new("curve_market","Select one explicitly admitted curve market with exact inputs at this close.");
    internal static Guid Revision(PreviewDatabase database,Guid? requested)
    {
        var revision=requested??database.Manifest.RevisionId;
        var markets=database.Manifest.CurveMarkets??[];
        if(revision==Guid.Empty||markets.Any(m=>m.RevisionId==Guid.Empty||m.RevisionId==database.Manifest.RevisionId||string.IsNullOrWhiteSpace(m.Name)||m.Name.Length>120)||
            markets.Select(m=>m.RevisionId).Distinct().Count()!=markets.Length||
            revision!=database.Manifest.RevisionId&&!markets.Any(m=>m.RevisionId==revision))throw Invalid();
        return revision;
    }
    internal static InputFrame Frame(PreviewDatabase database,InputFrame frame,Guid revision)
    {
        _=Revision(database,revision);
        if(revision==database.Manifest.RevisionId)return frame;
        var choices=database.Manifest.CurveMarkets!.Single(m=>m.RevisionId==revision).Frames.Where(f=>f.At==frame.At).ToArray();
        if(choices.Length!=1)throw Invalid();
        var source=choices[0];
        if(source.ObservationIds is null||source.CurveContracts is null||source.ObservationIds.Distinct().Count()!=source.ObservationIds.Length)throw Invalid();
        return frame with{ObservationIds=[..source.ObservationIds],
            FrozenObservationIds=source.FrozenObservationIds is {} frozen?[..frozen]:default,
            CurveContracts=source.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray()),
            OriginCloseId=null,SwapMarketSelections=null,PpaBundle=null};
    }
    internal static async Task<ImmutableArray<CurveMarketChoice>> Choices(NpgsqlConnection connection,PreviewDatabase database,CancellationToken ct)
    {
        var choices=new[]{new CurveMarketChoice(database.Manifest.RevisionId,"Main market")}
            .Concat((database.Manifest.CurveMarkets??[]).Select(m=>new CurveMarketChoice(m.RevisionId,m.Name))).ToArray();
        foreach(var choice in choices)_=Revision(database,choice.RevisionId);
        var visible=ImmutableArray.CreateBuilder<CurveMarketChoice>();
        foreach(var choice in choices)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT 1 FROM market_data.revisions WHERE catalog_id=$1 AND id=$2 AND state='sealed'",database.Manifest.CatalogId,choice.RevisionId);
            if(await command.ExecuteScalarAsync(ct) is not null)visible.Add(choice);
        }
        return visible.ToImmutable();
    }
    internal static Guid? Query(HttpContext context)
    {
        if(!context.Request.Query.TryGetValue("marketRevisionId",out var raw))return null;
        return raw.Count==1&&Guid.TryParseExact(raw[0],"D",out var id)&&id!=Guid.Empty?id:throw Invalid();
    }
    internal static Guid? SavedRevision(CustomCurveVersion saved,IEnumerable<CustomCurveVersion> versions)
    {
        var all=versions.ToDictionary(v=>(v.Id,v.Version));var revisions=new HashSet<Guid>();var seen=new HashSet<(Guid,int)>();var visiting=new HashSet<(Guid,int)>();
        void Visit(CustomCurveVersion current,int depth)
        {
            if(depth>32)throw Invalid();
            var key=(current.Id,current.Version);
            if(seen.Contains(key))return;
            if(!visiting.Add(key))throw Invalid();
            foreach(var source in current.Sources.Values)revisions.Add(source.RevisionId);
            foreach(var source in current.Definition.Nodes.Select(n=>n.PublishedSource).Where(p=>p is not null))
            {
                if(!all.TryGetValue((source!.CurveId,source.Version),out var parent))throw Invalid();
                Visit(parent,depth+1);
            }
            visiting.Remove(key);seen.Add(key);
        }
        Visit(saved,0);
        if(revisions.Count>1)throw Invalid();
        return revisions.Count==0?null:revisions.Single();
    }
}
