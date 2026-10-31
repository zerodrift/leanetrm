using System.Runtime.CompilerServices;
using OpenEtrm.Api;

namespace OpenEtrm.Api.Checks;

internal static class PreviewFailureDiagnosticChecks
{
    internal static void Run()
    {
        Exception failure;
        try{Raise("private-argument-value");throw new Exception("Expected synthetic failure.");}
        catch(InvalidOperationException error){failure=error;}
        var codes=PreviewFailureDiagnostics.Codes(failure).ToArray();
        if(!codes.Contains("OpenEtrm.Api.Checks.PreviewFailureDiagnosticChecks.Raise")||codes.Length>8||
            codes.Any(code=>code.Contains("private")||code.Contains('/')||code.Contains('\\')||code.Contains('(')))
            throw new Exception("Failure diagnostics must retain bounded method identifiers without messages, arguments or source paths.");
        var wrapped=new Exception("private-wrapper-value",failure);
        if(!PreviewFailureDiagnostics.Codes(wrapped).Contains("OpenEtrm.Api.Checks.PreviewFailureDiagnosticChecks.Raise"))
            throw new Exception("Nested failures must retain their project code location without their message.");
        Console.WriteLine("PASS preview diagnostics: bounded project method identifiers without messages, arguments or source paths");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Raise(string value)=>throw new InvalidOperationException(value);
}
