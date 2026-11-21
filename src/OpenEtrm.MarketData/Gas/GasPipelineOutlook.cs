using System.Globalization;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Gas;

public sealed record GasOutlookPath(Guid ReferenceCatalogId,Guid ReferenceRevisionId,string Pipeline,string Zone,string Point,string Product,string QuantityCode,string Unit,string Direction);
public sealed record GasOutlookEvidence(string Status,string? Quantity);
public sealed record GasOutlookOccurrence(GasOutlookPath Path,DateOnly Day,Guid TradeId,int Revision,string Side,string ContractNumerator,string ContractDenominator,
    GasOutlookEvidence? Nominated,GasOutlookEvidence? Confirmed,GasOutlookEvidence? Scheduled,GasOutlookEvidence? Allocated)
{
    public GasOutlookEvidence? Submitted {get;init;}
}
public sealed record GasOutlookStage(int Reported,int Provisional,int Withdrawn,string? ReportedQuantity,string? Quantity);
public sealed record GasOutlookRow(GasOutlookPath Path,DateOnly Period,int Occurrences,string Contracted,GasOutlookStage Nominated,GasOutlookStage Confirmed,GasOutlookStage Scheduled,GasOutlookStage Allocated,string? RequestedLessScheduled,GasOutlookStage Submitted,string? SubmittedLessScheduled);

public static class GasPipelineOutlook
{
    public static GasOutlookRow[] Summarize(IEnumerable<GasOutlookOccurrence> occurrences,string period,bool byPipeline=false)
    {
        if(period is not ("day" or "month"))throw new MarketDataException("gas_outlook_period","Select a gas day or calendar month.");
        var groups=occurrences.GroupBy(r=>(Path:byPipeline?r.Path with{Zone="",Point=""}:r.Path,Period:period=="month"?new DateOnly(r.Day.Year,r.Day.Month,1):r.Day));
        return [..groups.OrderBy(g=>g.Key.Period).ThenBy(g=>g.Key.Path.Pipeline,StringComparer.Ordinal).ThenBy(g=>g.Key.Path.Zone,StringComparer.Ordinal)
            .ThenBy(g=>g.Key.Path.Point,StringComparer.Ordinal).ThenBy(g=>g.Key.Path.Product,StringComparer.Ordinal).ThenBy(g=>g.Key.Path.Direction,StringComparer.Ordinal)
            .ThenBy(g=>g.Key.Path.Unit,StringComparer.Ordinal).ThenBy(g=>g.Key.Path.ReferenceCatalogId).ThenBy(g=>g.Key.Path.ReferenceRevisionId).Select(g=>
        {
            var rows=g.ToArray();
            var nominated=Stage(rows.Select(r=>r.Nominated));var scheduled=Stage(rows.Select(r=>r.Scheduled));
            var submitted=Stage(rows.Select(r=>r.Submitted),external:true);
            string? Gap(GasOutlookStage stage,Func<GasOutlookOccurrence,GasOutlookEvidence?> select)=>
                stage.Reported==rows.Length&&scheduled.Reported==rows.Length?ExactNumberText.Format(SignedRatio.Sum(rows.Select(r=>Number(select(r)!.Quantity!)-Number(r.Scheduled!.Quantity!)))):null;
            var contracted=SignedRatio.Sum(rows.Select(r=>new SignedRatio(Integer(r.ContractNumerator),Integer(r.ContractDenominator))));
            return new GasOutlookRow(g.Key.Path,g.Key.Period,rows.Length,ExactNumberText.Format(contracted),nominated,Stage(rows.Select(r=>r.Confirmed)),scheduled,
                Stage(rows.Select(r=>r.Allocated)),Gap(nominated,r=>r.Nominated),submitted,Gap(submitted,r=>r.Submitted));
        })];
    }
    static GasOutlookStage Stage(IEnumerable<GasOutlookEvidence?> evidence,bool external=false)
    {
        var rows=evidence.ToArray();
        var reported=rows.OfType<GasOutlookEvidence>().Where(e=>external?e.Status=="submitted":e.Status is "final" or "provisional").ToArray();
        if(rows.Any(e=>e is not null&&((external?e.Status!="submitted":e.Status is not ("final" or "provisional" or "withdrawn"))||(e.Status=="withdrawn")!=(e.Quantity is null))))
            throw new MarketDataException("gas_outlook_source","A daily quantity requires a valid native status and quantity.");
        var subtotal=reported.Length==0?null:ExactNumberText.Format(SignedRatio.Sum(reported.Select(r=>Number(r.Quantity!))));
        return new(reported.Length,reported.Count(r=>r.Status=="provisional"),rows.Count(r=>r?.Status=="withdrawn"),subtotal,reported.Length==rows.Length?subtotal:null);
    }
    static BigInteger Integer(string value)=>BigInteger.Parse(value,CultureInfo.InvariantCulture);
    static SignedRatio Number(string value)=>SignedRatio.TryParse(value,out var n)?n:throw new MarketDataException("gas_outlook_source","Retain the exact native quantity.");
}
