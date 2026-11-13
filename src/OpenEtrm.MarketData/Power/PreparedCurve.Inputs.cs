using System.Collections.Immutable;

namespace OpenEtrm.MarketData.Power;

public sealed partial class PreparedCurve
{
    public static ImmutableArray<CurveRequest> MissingInputs(CurveModel model,MarketSnapshot snapshot,
        IEnumerable<PreparedCurve> consumers,ImmutableArray<CurveRequest> requests,IReadOnlySet<CurveRequest> selected,
        CancellationToken cancellation=default)
    {
        var prepared=consumers.ToImmutableArray();
        if(requests.IsDefault||requests.Distinct().Count()!=requests.Length||selected is null||
            prepared.Any(p=>p is null||p.Key.Revision!=snapshot.Definitions||p.ValuationAt!=snapshot.ValuationAt))
            throw new MarketDataException("interval_price_source","Admit exact prepared inputs under their selected calculation context.");
        var omitted=requests.Where(request=>!selected.Contains(request)).ToImmutableArray();
        var admitted=new Dictionary<CurveRequest,PreparedCurve[]>();
        foreach(var request in omitted)
        {
            cancellation.ThrowIfCancellationRequested();
            var owners=prepared.Where(p=>p.Periods.Values.Any(m=>m.Dependencies.Contains(request))).ToArray();
            if(owners.Length>0&&owners.All(p=>p.DeclaresFrozenAnchor(model,request)))admitted.Add(request,owners);
        }
        // One validation of the pinned input set for this preparation; no
        // cross-request or cross-scenario cache is introduced.
        var finals=model.FrozenAnchors(snapshot,[..admitted.Keys],cancellation);
        foreach(var (request,owners) in admitted)
            foreach(var owner in owners)owner.RequireFrozenAnchorKnowledge(request,finals[request]);
        return [..omitted.Where(request=>!admitted.ContainsKey(request))];
    }

    private bool DeclaresFrozenAnchor(CurveModel model,CurveRequest request)
    {
        if(Curve is not ShapedIntervalCurve)return false;
        var source=model.AssetDefinition(request.Curve);var contract=model.ContractDefinition(request.Contract);
        if(source.ConstituentExpiry!=ConstituentExpiryRule.FreezeFinalSettlement||contract.Expiry>ValuationAt)return false;
        return Periods.Values.Any(p=>p.Shape?.Dependencies.Contains(request)==true);
    }

    private void RequireFrozenAnchorKnowledge(CurveRequest request,MarketObservation final)
    {
        foreach(var period in Periods.Values.Where(p=>p.Shape?.Dependencies.Contains(request)==true))
            foreach(var interval in period.Intervals.Where(i=>period.Shape!.SelectedAnchors?.GetValueOrDefault(i.Start)==request))
                RequireFrozenKnowledge([final.Key],interval);
    }

    internal void RequireFrozenKnowledge(IReadOnlyCollection<ObservationKey> lineage,DeliveryInterval interval)
    {
        var knowledge=((ShapedIntervalCurve)Curve).FrozenKnowledge;
        var cutoff=interval.End>ValuationAt&&ValuationAt<KnownAt?ValuationAt:KnownAt;
        if(lineage.Count==0||lineage.Any(id=>!knowledge.TryGetValue(id,out var learned)||
            learned.Offset!=TimeSpan.Zero||learned>cutoff))
            throw new MarketDataException("frozen_fact","The exact frozen anchor needs certified knowledge eligible at the request cutoff and, for live intervals, at valuation.");
    }
}
