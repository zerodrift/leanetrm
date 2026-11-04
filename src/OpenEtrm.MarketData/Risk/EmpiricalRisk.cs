using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Risk;

internal sealed record EmpiricalRiskResult(string HistoryId,int Samples,ImmutableArray<HistoricalPartition> Partitions);

// Shared exact sample reducer. Method adapters own policy and admission; the legacy
// Historical* value records keep their existing public/JSON compatibility.
internal static class EmpiricalRisk
{
    internal static EmpiricalRiskResult Calculate(string historyId,ImmutableArray<string> expectedSamples,string confidence,
        ImmutableArray<HistoricalExposure> exposures,ImmutableArray<HistoricalObservation> observations,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(historyId is null||historyId.Length!=64||!historyId.All(char.IsAsciiHexDigitLower)||expectedSamples.IsDefault||
            expectedSamples.Length is <2 or >MonteCarloRisk.MaximumPaths||expectedSamples.Any(id=>!ValidId(id))||
            expectedSamples.Distinct(StringComparer.Ordinal).Count()!=expectedSamples.Length)
            throw Error("risk_historical_samples","Supply the exact captured history and all distinct common sample identities.");
        if(exposures.IsDefaultOrEmpty||exposures.Length>HistoricalRisk.MaximumExposures||exposures.Any(e=>e is null||!ValidId(e.Id)||
            e.BookId==Guid.Empty||e.TradeId==Guid.Empty||e.BindingId==Guid.Empty||!ValidId(e.Measure))||
            exposures.Select(e=>e.Id).Distinct(StringComparer.Ordinal).Count()!=exposures.Length||
            exposures.Select(e=>(e.BookId,e.TradeId,e.BindingId,e.Measure,e.Currency)).Distinct().Count()!=exposures.Length)
            throw Error("risk_historical_exposures","Supply the complete distinct native position/measure/currency exposure set.");
        if((1L+expectedSamples.Length)*exposures.Length>HistoricalRisk.MaximumValues)
            throw Error("risk_scenario_capacity","The complete historical base/sample matrix exceeds 250000 native values.");
        var ordered=exposures.OrderBy(e=>e.Id,StringComparer.Ordinal).ToArray();
        foreach(var exposure in ordered)Money.RequireKey(exposure.Currency);
        var indices=ordered.Select((e,i)=>(e.Id,i)).ToDictionary(x=>x.Id,x=>x.i,StringComparer.Ordinal);
        var bases=ordered.Select(e=>Number(e.BaseValue)).ToArray();
        if(observations.IsDefault||observations.Length!=expectedSamples.Length||observations.Any(row=>row is null||!ValidId(row.Id)||
            row.Values.IsDefault||row.Values.Length!=ordered.Length||row.Values.Any(v=>v is null))||
            observations.Select(row=>row.Id).Distinct(StringComparer.Ordinal).Count()!=expectedSamples.Length||
            !observations.Select(row=>row.Id).Order(StringComparer.Ordinal).SequenceEqual(expectedSamples.Order(StringComparer.Ordinal)))
            throw Error("risk_historical_samples","Every captured sample must occur exactly once with every native exposure.");
        var byId=observations.ToDictionary(row=>row.Id,StringComparer.Ordinal);
        var ids=expectedSamples.Order(StringComparer.Ordinal).ToArray();var losses=new SignedRatio[ids.Length][];
        for(var row=0;row<ids.Length;row++)
        {
            cancellation.ThrowIfCancellationRequested();var values=byId[ids[row]].Values;
            if(values.Any(v=>v.ExposureId is null||!indices.ContainsKey(v.ExposureId))||values.Select(v=>v.ExposureId).Distinct(StringComparer.Ordinal).Count()!=ordered.Length)
                throw Error("risk_historical_samples","A historical observation omitted, duplicated or substituted a native exposure.");
            losses[row]=new SignedRatio[ordered.Length];
            foreach(var value in values){var column=indices[value.ExposureId];losses[row][column]=Bound(bases[column]-Number(value.Value));}
        }
        var alpha=Number(confidence);var count=new SignedRatio(ids.Length,1);
        var rankNumerator=alpha.Numerator*ids.Length;var rank=(int)((rankNumerator+alpha.Denominator-1)/alpha.Denominator);
        var tailMass=Bound(count*(new SignedRatio(1,1)-alpha));
        var partitions=ImmutableArray.CreateBuilder<HistoricalPartition>();
        foreach(var group in ordered.Select((e,i)=>(Exposure:e,Index:i)).GroupBy(x=>(x.Exposure.Measure,x.Exposure.Currency))
            .OrderBy(g=>g.Key.Measure,StringComparer.Ordinal).ThenBy(g=>g.Key.Currency.Revision.CatalogId)
            .ThenBy(g=>g.Key.Currency.Revision.RevisionId).ThenBy(g=>g.Key.Currency.Code,StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();var columns=group.ToArray();
            var total=losses.Select(row=>Sum(columns.Select(c=>row[c.Index]))).ToArray();
            var sorted=total.Order().ToArray();var quantile=sorted[rank-1];
            var above=total.Count(loss=>loss.CompareTo(quantile)>0);var ties=total.Count(loss=>loss==quantile);
            var remaining=Bound(tailMass-new SignedRatio(above,1));
            if(remaining.Numerator.Sign<0||remaining.CompareTo(new(ties,1))>0)
                throw Error("risk_historical_numeric","The exact empirical tail mass does not match its quantile boundary.");
            var varWeights=total.Select(loss=>loss==quantile?new SignedRatio(1,ties):SignedRatio.Zero).ToArray();
            var esWeights=total.Select(loss=>Divide(loss.CompareTo(quantile)>0?new(1,1):loss==quantile?remaining/ties:SignedRatio.Zero,tailMass)).ToArray();
            var es=Sum(total.Select((loss,i)=>Bound(loss*esWeights[i])));
            HistoricalContribution Contribution(Guid id,int[] selected)
            {
                cancellation.ThrowIfCancellationRequested();
                var sampleLoss=losses.Select(row=>Sum(selected.Select(i=>row[i]))).ToArray();
                return new(id,Text(Sum(selected.Select(i=>bases[i]))),Text(Sum(sampleLoss.Select((loss,i)=>Bound(loss*varWeights[i])))),
                    Text(Sum(sampleLoss.Select((loss,i)=>Bound(loss*esWeights[i])))));
            }
            var books=columns.GroupBy(c=>c.Exposure.BookId).OrderBy(g=>g.Key).Select(g=>Contribution(g.Key,[..g.Select(c=>c.Index)])).ToImmutableArray();
            var trades=columns.GroupBy(c=>c.Exposure.TradeId).OrderBy(g=>g.Key).Select(g=>Contribution(g.Key,[..g.Select(c=>c.Index)])).ToImmutableArray();
            var basis=Sum(columns.Select(c=>bases[c.Index]));
            partitions.Add(new(group.Key.Measure,group.Key.Currency,Text(basis),Text(Sum(total)/ids.Length),Text(quantile),Text(es),Text(tailMass),ties,
                books,trades,[..ids.Select((id,i)=>new HistoricalLoss(id,Text(Bound(basis-total[i])),Text(total[i]),Text(varWeights[i]),Text(esWeights[i])))],
                Histogram(total,cancellation)));
        }
        cancellation.ThrowIfCancellationRequested();
        return new(historyId,ids.Length,partitions.ToImmutable());
    }
    private static ImmutableArray<HistoricalHistogramBin> Histogram(SignedRatio[] values,CancellationToken cancellation)
    {
        var low=values.Min();var high=values.Max();var range=Bound(high-low);
        if(range.Numerator.IsZero)return[new(Text(low),Text(high),values.Length,"1")];
        var buckets=Math.Min(32,values.Length);var counts=new int[buckets];
        foreach(var value in values)
        {
            cancellation.ThrowIfCancellationRequested();var position=Bound(Divide(Bound(value-low),range)*new SignedRatio(buckets,1));
            var index=Math.Min(buckets-1,(int)(position.Numerator/position.Denominator));counts[index]++;
        }
        return [..counts.Select((count,i)=>new HistoricalHistogramBin(Text(Bound(low+range*new SignedRatio(i,buckets))),
            Text(Bound(low+range*new SignedRatio(i+1,buckets))),count,Text(new(count,values.Length))))];
    }
    private static bool ValidId(string? value)=>!string.IsNullOrWhiteSpace(value)&&value.Length<=128;
    private static SignedRatio Number(string value)
    {
        try{var(n,d)=ExactNumberText.Parse(value??"");return Bound(new(n,d));}
        catch(MarketDataException error){throw new MarketDataException("risk_historical_numeric","Historical values require bounded exact decimal or rational amounts.",error);}
    }
    private static SignedRatio Bound(SignedRatio value)
    {
        if(value.Numerator.GetBitLength()>16384||value.Denominator.GetBitLength()>16384)
            throw Error("risk_historical_numeric","Exact historical arithmetic exceeds the 16384-bit intermediate bound.");
        return value;
    }
    private static SignedRatio Sum(IEnumerable<SignedRatio> values)=>values.Aggregate(SignedRatio.Zero,(sum,value)=>Bound(sum+value));
    private static SignedRatio Divide(SignedRatio value,SignedRatio divisor)=>Bound(new(value.Numerator*divisor.Denominator,value.Denominator*divisor.Numerator));
    private static string Text(SignedRatio value)=>ExactNumberText.Format(value);
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
