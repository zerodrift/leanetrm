using System.Diagnostics;

namespace OpenEtrm.Api;

internal static class PreviewFailureDiagnostics
{
    // Reflection identifiers only: never exception messages, arguments, file
    // names, line numbers or a rendered stack trace from the private runtime.
    internal static IEnumerable<string> Codes(Exception error)
    {
        var seen=new HashSet<string>(StringComparer.Ordinal);
        Exception? current=error;
        for(var depth=0;current is not null&&depth<8;current=current.InnerException,depth++)
        foreach(var frame in new StackTrace(current,false).GetFrames())
        {
            var method=frame.GetMethod();var type=method?.DeclaringType;
            if(type is null||type.Namespace is not {} space||
                !new[]{"OpenEtrm.Api","OpenEtrm.MarketData","OpenEtrm.Rates","OpenEtrm.Shared"}
                    .Any(prefix=>space==prefix||space.StartsWith(prefix+".",StringComparison.Ordinal)))continue;
            if(type.IsGenericType)type=type.GetGenericTypeDefinition();
            var code=type.FullName+"."+method!.Name;
            if(code.Length>240||!seen.Add(code))continue;
            yield return code;
            if(seen.Count==8)yield break;
        }
    }
}
