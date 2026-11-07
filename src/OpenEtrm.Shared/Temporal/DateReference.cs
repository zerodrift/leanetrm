using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenEtrm.Shared.Temporal;

// A data reference, never an executable scripting language. Array indexes are zero-based.
public sealed partial record DateReference(string Symbol, int? Index, int? BusinessDays)
{
    [GeneratedRegex(@"\A(oe\.[a-z][a-z0-9_]{0,31})(?:\[([0-9]{1,5})\])?(?:\s*([+-])\s*([1-9][0-9]{0,4})bd)?\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Syntax();
    public static DateReference Parse(string? expression)
    {
        if(expression is null || expression.Length>100)throw Invalid();
        var match=Syntax().Match(expression.Trim());
        if(!match.Success)throw Invalid();
        int? index=match.Groups[2].Success?int.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture):null;
        int? offset=match.Groups[4].Success?int.Parse(match.Groups[4].Value,CultureInfo.InvariantCulture)*(match.Groups[3].Value=="-"?-1:1):null;
        if(index>99999 || offset is >10000 or <-10000)throw Invalid();
        return new(match.Groups[1].Value,index,offset);
    }
    private static TemporalException Invalid()=>new("date_expression", "Use oe.name or oe.name[index], optionally followed by +Nbd or -Nbd with a positive count.");
}
