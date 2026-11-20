using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.Shared.Temporal;

internal static class ReadReferenceChecks
{
    internal static void Run()
    {
        var before=ReadReferences.Operation.Value;
        try
        {
            var catalog=Guid.NewGuid();var owner=new ReadReferences();
            var at=DateTimeOffset.Parse("2026-01-01T00:00:00Z");
            var cut=new CalendarReadCut(catalog,at,3,"123456",1);
            var source=new KnowledgeSnapshot(cut.On(new(2020,12,18)),at,at);
            var id=source.Context.ContextId!.Value;
            cut.RequireCoordinates(catalog,source.Context);
            CoordinatesRefused(()=>cut.RequireCoordinates(Guid.NewGuid(),source.Context));
            CoordinatesRefused(()=>cut.RequireCoordinates(catalog,source.Context with{KnownAt=at.AddTicks(10).ToString("O")}));
            CoordinatesRefused(()=>cut.RequireCoordinates(catalog,source.Context with{KnownThrough=4}));
            CoordinatesRefused(()=>cut.RequireCoordinates(catalog,source.Context with{ContextId=Guid.NewGuid()}));
            CoordinatesRefused(()=>cut.RequireCoordinates(catalog,source.Context with{EffectiveOn="2020-12-19"}));
            var publicContext=JsonSerializer.Deserialize<ScheduleContext>(JsonSerializer.Serialize(source.Context));
            if(!source.Context.HasSameCoordinates(publicContext)||source.Context.HasSameCoordinates(publicContext! with{ContextId=Guid.NewGuid()}))
                throw new Exception("Public coordinate comparison must admit the exact JSON projection and reject changed coordinates.");
            var bytes=JsonSerializer.Serialize(source);
            ReadReferences.Operation.Value=new();
            var first=owner.Issue(catalog,source,DateTimeOffset.UtcNow.AddMinutes(1));
            var token=ReadReferences.Operation.Value.References[id];
            ReadReferences.Operation.Value=new();
            var second=owner.Issue(catalog,source,DateTimeOffset.UtcNow.AddMinutes(1));
            if(first.Context!=second.Context||first.Context.GetHashCode()!=second.Context.GetHashCode()||JsonSerializer.Serialize(second)!=bytes)
                throw new Exception("Read transport must not alter financial context equality or JSON.");
            if(owner.Open(catalog,id,token).Context!=source.Context)throw new Exception("A reference lost its exact source.");
            Refused(()=>new ReadReferences().Open(catalog,id,token));
            Refused(()=>owner.Open(Guid.NewGuid(),id,token));
            Refused(()=>owner.Open(catalog,Guid.NewGuid(),token));
            Refused(()=>owner.Open(catalog,id,token+"corrupt"));
            ReadReferences.Operation.Value=new();
            owner.Issue(catalog,source,DateTimeOffset.UtcNow.AddSeconds(-1));
            Refused(()=>owner.Open(catalog,id,ReadReferences.Operation.Value.References[id]));
            Refused(()=>new ReadReferences.Scope().Receive("{\""+id+"\":\"a\",\""+id+"\":\"b\"}"));
            Console.WriteLine("PASS operation read references: immutable financial identity, exact ownership, expiry, tamper and duplicate rejection");
        }
        finally{ReadReferences.Operation.Value=before;}
    }
    static void CoordinatesRefused(Action action)
    {
        try{action();}catch(TemporalException e)when(e.Code=="temporal_context"){return;}
        throw new Exception("A mismatched read coordinate was accepted.");
    }
    static void Refused(Action action)
    {
        try{action();}catch(TemporalException e)when(e.Code=="read_reference"){return;}
        throw new Exception("An invalid read reference was accepted.");
    }
}
