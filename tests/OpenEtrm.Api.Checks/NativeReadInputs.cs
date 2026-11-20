using System.Globalization;
using System.Text.Json;
using OpenEtrm.Api;

// The synthetic native client transports explicit input coordinates between
// its Python producer and managed check process. This never changes database
// authority or binding: KnowledgeResolver verifies/registers each value anew.
internal static class NativeReadInputs
{
    internal static IDisposable? Open(string[] arguments)
    {
        if(arguments is not [_,var fixture]||!File.Exists(fixture)||
            Environment.GetEnvironmentVariable("OPENETRM_NATIVE_CHECK_INPUTS") is not {} path||!File.Exists(path))return null;
        var selected=File.ReadAllText(fixture);
        using var document=JsonDocument.Parse(File.ReadAllText(path));
        var scope=new ReadReferences.Scope();
        foreach(var source in document.RootElement.EnumerateObject())
        {
            if(!selected.Contains(source.Name,StringComparison.OrdinalIgnoreCase))continue;
            var row=source.Value;
            var catalog=row.GetProperty("catalog_id").GetGuid();
            var cut=new CalendarReadCut(catalog,row.GetProperty("known_at").GetDateTimeOffset(),row.GetProperty("schedule_prefix").GetInt32(),
                row.GetProperty("system_id").GetString()!,row.GetProperty("timeline").GetInt32());
            var context=cut.On(DateOnly.ParseExact(row.GetProperty("effective_on").GetString()!,"yyyy-MM-dd",CultureInfo.InvariantCulture));
            var id=Guid.Parse(source.Name);
            if(context.ContextId!=id)throw new Exception("Synthetic read inputs disagree with their correlation.");
            scope.Sources.TryAdd(id,new(context,row.GetProperty("coverage_from").GetDateTimeOffset(),row.GetProperty("certified_through").GetDateTimeOffset()));
        }
        var previous=ReadReferences.Operation.Value;
        ReadReferences.Operation.Value=scope;
        return new Restore(previous);
    }
    sealed class Restore(ReadReferences.Scope? previous):IDisposable
    {public void Dispose()=>ReadReferences.Operation.Value=previous;}
}
