using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace OpenEtrm.Api;

public sealed class PageSessionRegistry(int capacity)
{
    readonly ConcurrentDictionary<string,PreviewSession> sessions=new();
    readonly object admission=new();
    sealed class Entry(PreviewSession session)
    {
        internal PreviewSession Session {get;}=session;
        internal HashSet<Guid> Documents {get;}=[];
        internal DateTimeOffset? Detached {get;set;}
    }
    readonly Dictionary<(string Cookie,Guid Tab),Entry> views=[];
    // Each sign-in reserves its first workspace. Extra workspaces, including
    // the compatibility client, draw from the same configured process budget.
    int Slots=>sessions.Count+views.GroupBy(v=>v.Key.Cookie).Sum(g=>g.Sum(v=>Math.Max(1,v.Value.Documents.Count))-1);

    public PreviewSession View(string cookie,PreviewSession account,Guid? tab,Func<PreviewSession>? create=null,Guid? document=null,Guid? previousDocument=null)
    {
        lock(admission)
        {
            if(!sessions.TryGetValue(cookie,out var active)||active!=account)throw new PreviewSessionEnded();
            var key=(cookie,tab??Guid.Empty);
            if(!views.TryGetValue(key,out var entry))
            {
                if(tab is not null&&create is null)throw new PreviewSessionEnded();
                if(views.Keys.Any(k=>k.Cookie==cookie))MakeRoom(cookie);
                entry=new(tab is null?account:create!());
                views.Add(key,entry);
            }
            if(document is {} id&&!entry.Documents.Contains(id))
            {
                if(previousDocument is {} previous && entry.Documents.Remove(previous))
                {
                    if(entry.Documents.Count==0)entry.Session.InvalidateReads();
                }
                else if(entry.Documents.Count!=0)MakeRoom();
                entry.Documents.Add(id);entry.Detached=null;
            }
            return entry.Session;
        }
    }
    void MakeRoom(string? incomingViewOwner=null)
    {
        if(Slots<capacity)return;
        // A sign-in can reuse its reserved first slot. Only surplus views
        // can yield capacity to a different sign-in or an additional document.
        var parked=views.Where(v=>v.Value.Detached.HasValue &&
            (v.Key.Cookie==incomingViewOwner || views.Keys.Any(k=>k.Cookie==v.Key.Cookie && k!=v.Key)))
            .OrderBy(v=>v.Value.Detached).FirstOrDefault();
        if(parked.Value is null)throw new PreviewDeniedException();
        views.Remove(parked.Key);
    }
    internal void Detach(string cookie,Guid tab,Guid document,TimeSpan grace)
    {
        lock(admission)
        {
            if(!views.TryGetValue((cookie,tab),out var view)||!view.Documents.Remove(document)||view.Documents.Count!=0)return;
            var detached=DateTimeOffset.UtcNow;view.Detached=detached;
            view.Session.InvalidateReads();
            _=Expire();
            async Task Expire()
            {
                await Task.Delay(grace);
                lock(admission)
                    if(views.TryGetValue((cookie,tab),out var current)&&current==view&&current.Detached==detached)
                        views.Remove((cookie,tab));
            }
        }
    }
    internal void ResetViews(PreviewSession account)
    {
        lock(admission)
            foreach(var view in views.Values.Select(e=>e.Session).Where(v=>v.Account==account).Append(account).Distinct())
                view.ResetView();
    }
    private void Remove(string cookie)
    {
        if(sessions.TryRemove(cookie,out var account))account.InvalidateReads();
        foreach(var key in views.Keys.Where(k=>k.Cookie==cookie).ToArray())
        {
            views[key].Session.InvalidateReads();
            views.Remove(key);
        }
    }
    public string[] ReclaimCandidates()
    {
        lock(admission)return Slots>=capacity?sessions.Values.Select(x=>x.Digest).Distinct().ToArray():[];
    }
    public (string Id,PreviewSession Session,bool Created) GetOrCreate(string? id,Func<PreviewSession,bool> matches,Func<PreviewSession> create)
    {
        lock(admission)
        {
            foreach(var item in sessions.Where(x=>DateTimeOffset.UtcNow>x.Value.Ends).ToArray())Remove(item.Key);
            if(id is not null&&sessions.TryGetValue(id,out var existing)&&matches(existing))return(id,existing,false);
            MakeRoom();
            id=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var session=create();sessions[id]=session;
            return(id,session,true);
        }
    }
    public bool TryGetValue(string id,[System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out PreviewSession? session)=>sessions.TryGetValue(id,out session);
    public void Retire(string digest)
    {
        lock(admission)
            foreach(var item in sessions.Where(x=>x.Value.Digest==digest).ToArray())Remove(item.Key);
    }
}
