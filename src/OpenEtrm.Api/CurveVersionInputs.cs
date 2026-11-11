using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Retained graphs read their exact versioned inputs. New graphs use the active catalogue.
internal static class CurveVersionInputs
{
    static MarketDataException Invalid()=>new("curve_inputs","The exact curve version inputs are unavailable or inconsistent.");
    internal static Guid Exact(Guid revision)=>revision!=Guid.Empty?revision:throw Invalid();
    internal static async Task<Guid> Resolve(PreviewDatabase database,Npgsql.NpgsqlConnection connection,Guid? saved,CancellationToken ct)=>
        saved is {} exact?Exact(exact):(await new CatalogueStore(database.Manifest.CatalogId).ReadBound(connection,ct)).RequireRole("primary").DefinitionRevisionId;
    internal static async Task<Guid> NewGraph(PreviewDatabase database,Npgsql.NpgsqlConnection connection,
        OpenEtrm.MarketData.CustomCurves.RecipeDefinition definition,DateOnly? effectiveOn,CancellationToken ct)
    {
        var catalogue=await new CatalogueStore(database.Manifest.CatalogId).ReadBound(connection,ct,effectiveOn);
        var revisions=definition.Nodes.Where(n=>n.Kind=="source"&&n.Parameters.ContainsKey("curve"))
            .Select(n=>catalogue.RequireCurve(n.Parameters["curve"]).DefinitionRevisionId).ToHashSet();
        var parents=definition.Nodes.Select(n=>n.PublishedSource).Where(p=>p is not null).ToArray();
        if(parents.Length>0)
        {
            var versions=await new CustomCurvesStore(database).Read(connection,ct);
            foreach(var parent in parents)
            {
                var saved=versions.SingleOrDefault(v=>v.Id==parent!.CurveId&&v.Version==parent.Version)??throw Invalid();
                if(saved.MarketRevisionId is {} revision)revisions.Add(Exact(revision));
            }
        }
        if(revisions.Count>1)throw new MarketDataException("curve_inputs","This graph profile requires one exact native source revision; cross-source graph composition is not admitted.");
        return revisions.Count==1?revisions.Single():catalogue.RequireRole("primary").DefinitionRevisionId;
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
