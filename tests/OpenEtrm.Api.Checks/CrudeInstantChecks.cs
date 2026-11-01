using System.Globalization;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class CrudeInstantChecks
{
 internal static void Run()
 {
  DateTimeOffset Read(string value)=>CrudeLifecycleStore.Time(JsonSerializer.SerializeToElement(new{instant=value}),"instant");
  var expected=new DateTimeOffset(2040,2,29,12,34,56,TimeSpan.Zero).AddTicks(1234560);
  var zone=Environment.GetEnvironmentVariable("TZ");var culture=CultureInfo.CurrentCulture;
  try
  {
   foreach(var localZone in new[]{"America/Chicago","Asia/Tokyo"})
   {
    Environment.SetEnvironmentVariable("TZ",localZone);TimeZoneInfo.ClearCachedData();CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("ar-SA");
    foreach(var text in new[]{"2040-02-29T12:34:56.123456Z","2040-02-29T12:34:56.123456+00:00","2040-02-29T12:34:56.1234560Z"})
     if(Read(text)!=expected||Read(text).Ticks!=expected.Ticks||Read(text).Offset!=TimeSpan.Zero)throw new Exception("A retained UTC microsecond changed under the host time zone or culture.");
    foreach(var text in new[]{"2040-02-29T12:34:56","2040-02-29","2040-02-29T12:34:56+01:00","2040-02-29T12:34:56-00:00","2040-02-29T12:34:56.1234561Z","2040-02-29T12:34:56.12345601Z","9999-12-31T00:00:00Z"})
    {
     try{Read(text);throw new Exception("Unsupported retained instant accepted: "+text);}
     catch(MarketDataException e)when(e.Code=="crude_lifecycle_input"){}
    }
   }
  }
  finally{Environment.SetEnvironmentVariable("TZ",zone);TimeZoneInfo.ClearCachedData();CultureInfo.CurrentCulture=culture;}
  if(SchedulePreview.ParseAt("2040-02-29T12:34:56.1234561Z").Ticks!=expected.Ticks+1||SchedulePreview.ParseAt("9999-12-31T00:00:00Z").Year!=9999)
   throw new Exception("General schedule parsing must retain its existing precision and range policy.");
  Console.WriteLine("PASS crude UTC instants: explicit offsets, host-zone independence, lossless microseconds and bounded trading horizon; general schedule parsing unchanged");
 }
}
