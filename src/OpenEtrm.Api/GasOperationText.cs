using System.Buffers;
using System.Text;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// PostgreSQL length counts Unicode scalars. Gas operation business references are
// opaque identities: trim ASCII spaces only, never general Unicode whitespace.
internal static class GasOperationText
{
    internal static string Key(string? value,string code)
    {
        var text=value?.Trim(' ')??throw Invalid(code);
        if(Length(text,code) is <1 or >128||text.Any(char.IsControl))throw Invalid(code);
        return text;
    }

    internal static string Evidence(string? value,string code)
    {
        var text=value?.Trim(' ')??throw Invalid(code);
        if(Length(text,code) is <1 or >2000||text.Contains('\0'))throw Invalid(code);
        return text;
    }

    static int Length(string text,string code)
    {
        var remaining=text.AsSpan();var count=0;
        while(!remaining.IsEmpty)
        {
            if(Rune.DecodeFromUtf16(remaining,out _,out var consumed)!=OperationStatus.Done)throw Invalid(code);
            count++;remaining=remaining[consumed..];
        }
        return count;
    }

    static MarketDataException Invalid(string code)=>new(code,"Supply canonical business references within 128 characters and evidence within 2000 characters. Business references cannot contain control characters; evidence cannot contain NUL.");
}
