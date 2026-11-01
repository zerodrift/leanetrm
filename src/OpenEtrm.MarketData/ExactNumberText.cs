using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using OpenEtrm.Shared.Numerics;

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
 // The text Parse reads: a plain decimal when the value is exact at the working scale and within its range, otherwise the reduced "n/d".
 internal static string Format(SignedRatio value)
 {
  try{if(value.TryScaleToInteger(ExactDecimal.WorkingScale,out var scaled))return ExactDecimal.FromScaled(scaled).ToString();}
  catch(MarketDataException e)when(e.Code=="numeric_range"){}
  return value.Numerator.ToString(CultureInfo.InvariantCulture)+"/"+value.Denominator.ToString(CultureInfo.InvariantCulture);
 }
}
