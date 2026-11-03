using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace OpenEtrm.Api;

public sealed class PageSessionRegistry(int capacity)
{
    readonly ConcurrentDictionary<string,PreviewSession> sessions=new();
    readonly object admission=new();
    public string[] ReclaimCandidates()
    {
        lock(admission)return sessions.Count>=capacity?sessions.Values.Select(x=>x.Digest).Distinct().ToArray():[];
    }
    public (string Id,PreviewSession Session,bool Created) GetOrCreate(string? id,Func<PreviewSession,bool> matches,Func<PreviewSession> create)
    {
        lock(admission)
        {
            foreach(var item in sessions.Where(x=>DateTimeOffset.UtcNow>x.Value.Ends).ToArray())sessions.TryRemove(item.Key,out _);
            if(id is not null&&sessions.TryGetValue(id,out var existing)&&matches(existing))return(id,existing,false);
            if(sessions.Count>=capacity)throw new PreviewDeniedException();
            id=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var session=create();sessions[id]=session;
            return(id,session,true);
        }
    }
    public bool TryGetValue(string id,[System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PreviewSession? session)=>sessions.TryGetValue(id,out session);
    public void Retire(string digest)
    {
        lock(admission)
            foreach(var item in sessions.Where(x=>x.Value.Digest==digest).ToArray())sessions.TryRemove(item.Key,out _);
    }
}
