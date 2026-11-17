using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Physical;

namespace OpenEtrm.Api;

public sealed partial class StorageFacilityStore
{
    internal static object[] ReadTables(JsonElement facts,StorageFacilityAssessment? measured)
    {
        var tables=new List<object>();
        var ports=facts.GetProperty("versionBundles").EnumerateArray().SelectMany(bundle=>bundle.GetProperty("ports").EnumerateArray()).ToDictionary(p=>p.Id("id"));
        string Point(JsonElement row)
        {
            if(!ports.TryGetValue(row.Id("port_id"),out var port))throw new MarketDataException("storage_facility_source","A physical activity is missing its exact retained pipeline port.");
            return port.Text("pipeline_code")+" / "+port.Text("point_code");
        }
        object Ratchets(string label,JsonElement bands)=>new{label,columns=new[]{"Lower inventory","Upper inventory","Maximum injection / hour","Maximum withdrawal / hour"},
            rows=bands.EnumerateArray().Select(row=>new[]{row.Text("lower"),row.Text("upper"),row.Text("injectionPerHour"),row.Text("withdrawalPerHour")}).ToArray()};
        tables.Add(Ratchets("Current-definition ratchets · revision "+facts.GetProperty("version").GetProperty("revision").GetRawText(),facts.GetProperty("ratchets")));
        if(measured is not null)
        {
            var bundles=facts.GetProperty("versionBundles").EnumerateArray().Where(b=>b.GetProperty("version").Id("id")==measured.RulesVersion).ToArray();
            if(bundles.Length!=1)throw new MarketDataException("storage_facility_source","Retain the measured window's exact physical rule version.");
            var selected=bundles[0];
            tables.Add(Ratchets("Measured-window ratchets · revision "+selected.GetProperty("version").GetProperty("revision").GetRawText(),selected.GetProperty("ratchets")));
            tables.Add(new{label="Measured inventory window",columns=new[]{"From","Through","Closing physical quantity","Rule revision"},
                rows=new[]{new[]{measured.From.ToString("O"),measured.Through.ToString("O"),measured.ClosingLevel?.ToExactString()??"Unavailable",selected.GetProperty("version").GetProperty("revision").GetRawText()}},instantColumns=new[]{0,1}});
        }
        tables.Add(new{label="Dated opening evidence",columns=new[]{"Business reference","Revision","Observed at","Physical quantity","Evidence"},
            rows=facts.GetProperty("openings").EnumerateArray().Select(row=>new[]{row.Text("business_key"),row.GetProperty("revision").GetRawText(),row.Text("observed_at"),row.Text("quantity"),row.Text("evidence")}).ToArray(),instantColumns=new[]{2}});
        tables.Add(new{label="Recorded measured activity",columns=new[]{"Business reference","Revision","Pipeline point","Direction","From","Through","Quantity","Source"},
            rows=facts.GetProperty("activities").EnumerateArray().Select(row=>new[]{row.Text("business_key"),row.GetProperty("revision").GetRawText(),Point(row),
                row.Text("direction"),row.Text("from_at"),row.Text("through_at"),row.Text("quantity"),row.OptionalId("source_revision_id") is {} source?"Linked reported flow "+source:"Independent measured evidence"}).ToArray(),instantColumns=new[]{4,5}});
        tables.Add(new{label="Chosen plan activity",columns=new[]{"Pipeline point","Direction","From","Through","Planned quantity"},
            rows=facts.GetProperty("planActivities").EnumerateArray().Select(row=>new[]{Point(row),row.Text("direction"),row.Text("from_at"),row.Text("through_at"),row.Text("quantity")}).ToArray(),instantColumns=new[]{2,3}});
        tables.Add(new{label="Measured reporting coverage",columns=new[]{"Business reference","Revision","From","Through","Statement","Activities"},
            rows=facts.GetProperty("coverages").EnumerateArray().Select(row=>new[]{row.Text("business_key"),row.GetProperty("revision").GetRawText(),row.Text("from_at"),row.Text("through_at"),
                row.Text("statement")==StorageFacilityCoverage.CompleteActual?"Complete observed window":"Incomplete observed window",row.GetProperty("activityRevisionIds").GetArrayLength().ToString(System.Globalization.CultureInfo.InvariantCulture)}).ToArray(),instantColumns=new[]{2,3}});
        var plan=facts.GetProperty("planAssessment");
        if(plan.ValueKind==JsonValueKind.Object)
        {
            string Ratio(JsonElement quantity)=>quantity.Text("d")=="1"?quantity.Text("n"):quantity.Text("n")+" / "+quantity.Text("d");
            tables.Add(new{label="Retained chosen-plan assessment",columns=new[]{"From","Through","Original closing quantity","Source state"},
                rows=new[]{new[]{plan.Text("from"),plan.Text("through"),Ratio(plan.GetProperty("closing")),facts.GetProperty("requiresReassessment").GetBoolean()?"Reassessment required; original result retained":"Original accepted assessment"}},instantColumns=new[]{0,1}});
        }
        tables.AddRange(AssessmentTables(measured));return tables.ToArray();
    }
}
