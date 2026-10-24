using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace OpenEtrm.MarketData;

// Exact report transport; decimal calculations still obey ExactDecimal bounds.
internal static class ExactNumberText
{
 internal static (BigInteger Numerator,BigInteger Denominator) Parse(string value)
 {
  var parts=value.Split('/');
  if(parts.Length==1)return(ExactDecimal.Parse(value).ScaledValue,ExactDecimal.WorkingScale);
  if(parts.Length!=2||parts.Any(p=>p.Length>1234)||!Regex.IsMatch(value,@"^-?\d+/[1-9]\d*$"))
   throw new MarketDataException("numeric_format","Supply a bounded exact decimal or rational amount.");
  var n=BigInteger.Parse(parts[0],CultureInfo.InvariantCulture);var d=BigInteger.Parse(parts[1],CultureInfo.InvariantCulture);
  if(n.GetBitLength()>4096||d.GetBitLength()>4096)throw new MarketDataException("numeric_range","Exact fractions exceed the supported bound.");
  return(n,d);
 }
}
