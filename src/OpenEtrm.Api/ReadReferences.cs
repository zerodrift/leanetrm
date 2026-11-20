using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.Api;

// References belong to one signed-in workspace. They carry their exact certified
// coordinates, so concurrent reads never compete for a 'latest context' cache.
internal sealed class ReadReferences
{
    readonly IDataProtector protector=new EphemeralDataProtectionProvider().CreateProtector("OpenEtrm.ReadContext.v1");
    internal const string Header="X-OpenETRM-Read-References";
    internal sealed class Scope
    {
        internal ConcurrentDictionary<Guid,KnowledgeSnapshot> Sources {get;}=new();
        internal ConcurrentDictionary<Guid,string> References {get;}=new();
        internal Dictionary<Guid,string> Incoming {get;}=[];
        internal void Publish(Guid id,string proof)
        {
            lock(References)
            {
                if(References.Count>=8&&!References.ContainsKey(id))
                    throw new TemporalException("read_reference_limit","This operation exceeds its eight read-reference transport slots.");
                References.TryAdd(id,proof);
            }
        }
        internal void Receive(string metadata)
        {
            try
            {
                if(metadata.Length>16384)throw new JsonException();
                using var value=JsonDocument.Parse(metadata);
                if(value.RootElement.ValueKind!=JsonValueKind.Object)throw new JsonException();
                foreach(var field in value.RootElement.EnumerateObject())
                    if(Incoming.Count>=8||!Guid.TryParseExact(field.Name,"D",out var id)||field.Value.ValueKind!=JsonValueKind.String||
                        !Incoming.TryAdd(id,field.Value.GetString()!))throw new JsonException();
            }
            catch(JsonException){throw new TemporalException("read_reference","Supply distinct bounded read references as transport metadata.");}
        }
    }
    internal static readonly AsyncLocal<Scope?> Operation=new();
    sealed record Reference(Guid Catalog,ScheduleContext Context,DateTimeOffset CoverageFrom,DateTimeOffset CertifiedThrough,
        CalendarReadCut Cut,DateTimeOffset ExpiresAt);

    internal KnowledgeSnapshot Issue(Guid catalog,KnowledgeSnapshot source,DateTimeOffset expiresAt)
    {
        if(source.Context.ReadCut is not {} cut)return source;
        var payload=new Reference(catalog,source.Context,source.CoverageFrom,source.CertifiedThrough,cut,expiresAt);
        Operation.Value?.Publish(source.Context.ContextId!.Value,protector.Protect(JsonSerializer.Serialize(payload)));
        Remember(source);
        return source;
    }

    internal KnowledgeSnapshot Open(Guid catalog,Guid id,string reference)
    {
        try
        {
            if(reference.Length>8192)throw new CryptographicException();
            var source=JsonSerializer.Deserialize<Reference>(protector.Unprotect(reference))??throw new CryptographicException();
            if(source.Catalog!=catalog||source.Context.ContextId!=id||DateTimeOffset.UtcNow>=source.ExpiresAt)
                throw new CryptographicException();
            var result=new KnowledgeSnapshot(source.Context with{ReadCut=source.Cut},
                source.CoverageFrom,source.CertifiedThrough);
            Remember(result);
            Operation.Value?.Publish(id,reference);
            return result;
        }
        catch(Exception error) when(error is CryptographicException or JsonException)
        {throw new TemporalException("read_reference","This read reference has expired or belongs to another workspace. Refresh the view.");}
    }

    internal static void Expose(Guid catalog,KnowledgeSnapshot source)
    {
        if(WorkspaceReadContext.Page.Value is {} page)page.Knowledge.References.Issue(catalog,source,page.Ends);
    }

    internal static void Remember(KnowledgeSnapshot source)
    {
        if(source.Context.ContextId is {} id)Operation.Value?.Sources.TryAdd(id,source);
    }
}
