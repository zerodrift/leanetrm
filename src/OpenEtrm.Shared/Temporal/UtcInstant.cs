using System.Globalization;

namespace OpenEtrm.Shared.Temporal;

// Wire syntax only. Consumers retain their precision, horizon and error policy.
public static class UtcInstant
{
 public static bool TryParse(string? value,out DateTimeOffset instant)
 {
  instant=default;
  return value is not null && value.Contains('T') && DateTimeOffset.TryParseExact(value,
   ["yyyy-MM-dd'T'HH:mm:ss'Z'","yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'","yyyy-MM-dd'T'HH:mm:sszzz","yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
   CultureInfo.InvariantCulture,DateTimeStyles.AssumeUniversal,out instant) && instant.Offset==TimeSpan.Zero;
 }
}
