using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.Api;

// One adapter for retained database rows and their exact JSON projections.
// Clock validation and operating-day behavior remain in the domain implementation.
public static class ReferenceClockJson
{
    public static ClockRule Read(JsonElement row) => new(row.Text("code"),row.Text("kind"),
        row.OptionalText("fixed_offset_seconds") is {} seconds?int.Parse(seconds,CultureInfo.InvariantCulture):null,
        row.OptionalText("iana_zone"),row.OptionalText("tzdb_version"));
}
