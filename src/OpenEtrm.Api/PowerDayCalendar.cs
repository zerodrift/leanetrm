using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

public sealed record PowerDayCalendarRequest(Guid ReferenceCatalogId,Guid ReferenceRevisionId,DateOnly From,DateOnly Through,ClockRule[] Clocks);
public static class PowerDayCalendar
{
    // Operator-only preparation. Collector credentials cannot write or replace these rows.
    public static string Render(PowerDayCalendarRequest request)
    {
        if(request.ReferenceCatalogId==Guid.Empty||request.ReferenceRevisionId==Guid.Empty||request.Through<request.From||request.Through.DayNumber-request.From.DayNumber>1461||request.Clocks.Length is <1 or >2||request.Clocks.Select(c=>c.Code).Distinct().Count()!=request.Clocks.Length)
            throw new MarketDataException("collection_calendar","Supply exact references, distinct market clocks and an explicit window of at most four years.");
        var sql=new StringBuilder("BEGIN;\n");
        foreach(var rule in request.Clocks)
        {
            var clock=PowerClock.FromRule(rule,TimeZoneDatabase.Installed());
            if(rule.Kind!="iana"||rule.IanaZone is not("America/New_York" or "America/Chicago"))throw new MarketDataException("collection_calendar","Only the qualified PJM and ERCOT market clocks are supported.");
            for(var day=request.From;day<=request.Through;day=day.AddDays(1))
            {
                var hours=PowerHours.Compile(day,day.AddDays(1),clock,TimeOfUse.All(60),[],60);
                sql.Append(CultureInfo.InvariantCulture,$"INSERT INTO power_data.operating_days VALUES('{request.ReferenceCatalogId}','{request.ReferenceRevisionId}','{rule.Code.Replace("'","''")}','{day:yyyy-MM-dd}','{hours[0].Start:O}','{hours[^1].End:O}','{clock.Fingerprint}');\n");
            }
        }
        return sql.Append("COMMIT;\n").ToString();
    }
    public static void Run(string input,string output)
    {
        var request=JsonSerializer.Deserialize<PowerDayCalendarRequest>(File.ReadAllText(input),new JsonSerializerOptions(JsonSerializerDefaults.Web))??throw new InvalidOperationException("Calendar request required.");
        using var file=new FileStream(output,FileMode.CreateNew,FileAccess.Write);
        using var writer=new StreamWriter(file);writer.Write(Render(request));
    }
}
