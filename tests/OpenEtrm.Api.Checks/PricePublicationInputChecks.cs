using System.Text.Json;
using OpenEtrm.Api;

internal static class PricePublicationInputChecks
{
 public static void Run()
 {
  var options=new JsonSerializerOptions(JsonSerializerDefaults.Web);
  options.Converters.Add(new PublicationInstantConverter());
  DateTimeOffset Read(string text)=>JsonSerializer.Deserialize<DateTimeOffset>(JsonSerializer.Serialize(text),options);
  foreach(var text in new[]{"2021-01-02T12:05:00.Z","2021-01-02T12:05:00.0000000Z","2021-01-02T12:05:00+00:00","2021-01-02T12:05:00"})
  {
   try{Read(text);throw new Exception("Unsupported publication issue instant accepted: "+text);}
   catch(JsonException){}
  }
  var whole=new DateTimeOffset(2021,1,2,12,5,0,TimeSpan.Zero);
  if(Read("2021-01-02T12:05:00Z")!=whole||Read("2021-01-02T12:05:00.123456Z")!=whole.AddTicks(1234560)||
   Read("2021-01-02T12:05:00.1Z")!=whole.AddMilliseconds(100))
   throw new Exception("The publication issue instant lost exact UTC microseconds.");
  Console.WriteLine("PASS publication issue instants require explicit UTC Z and a nonempty optional fraction of at most six digits");
 }
}
