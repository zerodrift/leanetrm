using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Retained graphs read their exact versioned inputs. New graphs use the active catalogue.
internal static class CurveVersionInputs
{
    static MarketDataException Invalid()=>new("curve_inputs","The exact curve version inputs are unavailable or inconsistent.");
    internal static Guid Revision(PreviewDatabase database,Guid? saved)
    {
        var revision=saved??database.Manifest.RevisionId;
        if(revision==Guid.Empty||revision!=database.Manifest.RevisionId)throw Invalid();
        return revision;
    }
    internal static InputFrame Frame(PreviewDatabase database,InputFrame frame,Guid revision)
    {
        _=Revision(database,revision);
        return frame;
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
