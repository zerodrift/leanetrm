using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData;

public sealed partial class CurveModel
{
    public ImmutableArray<QuoteFactor> QuoteFactors(MarketSnapshot snapshot)
    {
        if(snapshot.Definitions!=revision || snapshot.Selections.IsDefault)Fail("snapshot_pin","Use the exact retained snapshot definitions and selections.");
        var result=ImmutableArray.CreateBuilder<QuoteFactor>();
        foreach(var selected in snapshot.Selections.Where(s=>s.Input.Use==InputUse.Variable))
        {
            if(!series.TryGetValue(selected.Input.Series,out var definition) || definition.Quote is not {} quoteKey ||
                definition.Contract is not {} contract || snapshot.ValuationAt>=contracts[contract].Expiry)continue;
            var quote=quotes[quoteKey];var obs=selected.Observation;
            var direct=curves.Values.Where(c=>c.Rule==CurveRule.Direct && c.Quote==quoteKey && c.Constituents.Contains(contract) &&
                c.DirectSource==obs.Source && c.DirectRole==obs.Role).Select(c=>c.Key).OrderBy(c=>c.Code,StringComparer.Ordinal).ToImmutableArray();
            if(!direct.IsEmpty && obs.State==PublicationState.Final)result.Add(new(selected.Input,quoteKey,PriceUnit.FromQuote(quote),contract,obs.Key,obs.Source,obs.Role,obs.Value,direct,quote.TradingTick));
        }
        return result.OrderBy(f=>contracts[f.Contract].DeliveryFrom).ThenBy(f=>f.Input.Series.Id).ToImmutableArray();
    }
    public CurveScenarioSet BuildScenarios(MarketSnapshot snapshot,ImmutableArray<CurveRequest> requests,
        ImmutableArray<QuoteBump> bumps,CancellationToken cancellation=default)
    {
        if(bumps.IsDefaultOrEmpty || bumps.Length>16 || bumps.Select(b=>b.Input).Distinct().Count()!=bumps.Length)
            Fail("risk_bump","Select between one and sixteen distinct explicit quote-input bumps.");
        cancellation.ThrowIfCancellationRequested();
        var @base=Build(snapshot,requests,null,[],cancellation);
        var factors=ImmutableArray.CreateBuilder<FactorScenarios>();
        foreach(var bump in bumps)
        {
            cancellation.ThrowIfCancellationRequested();
            if(bump.ConventionId!=QuoteBump.Convention || !Positive(bump.Size))
                Fail("risk_bump","Choose the versioned absolute symmetric convention and a positive exact bump.");
            if(bump.Input.Use!=InputUse.Variable || !series.TryGetValue(bump.Input.Series,out var definition) ||
                definition.Quote!=bump.Quote || !quotes.TryGetValue(bump.Quote,out var quote) || PriceUnit.FromQuote(quote)!=bump.Unit)
                throw new MarketDataException("risk_factor","A shock requires the exact variable price series, quote and price unit.");
            var selected=snapshot.Selections.SingleOrDefault(s=>s.Input==bump.Input)
                ??throw new MarketDataException("risk_factor","The exact selected variable observation is absent.");
            var matching=curves.Values.Where(c=>c.Rule==CurveRule.Direct && c.Quote==bump.Quote && c.Constituents.Contains(definition.Contract!.Value));
            if(!matching.Any(c=>c.DirectSource==selected.Observation.Source && c.DirectRole==selected.Observation.Role) ||
                selected.Observation.State!=PublicationState.Final || snapshot.ValuationAt>=contracts[definition.Contract!.Value].Expiry)
                Fail("risk_factor","Only an explicitly selected live direct quote is shockable; frozen facts and derived output shocks are unsupported.");
            // Synthetic scenarios obey the quote domain, not the published settlement tick.
            Domain(selected.Observation.Value+bump.Size,quote.Minimum,quote.Maximum);
            Domain(selected.Observation.Value-bump.Size,quote.Minimum,quote.Maximum);
            var affected=new HashSet<(CurveRequest,InputUse)>();
            foreach(var node in @base.Evaluations)
            {
                cancellation.ThrowIfCancellationRequested();
                var curve=curves[node.Result.Request.Curve];
                var direct=curve.Rule==CurveRule.Direct && node.Use==InputUse.Variable &&
                    priceSeries[(node.Result.Request.Contract,curve.Quote)]==bump.Input.Series;
                if(direct || node.Inputs.Any(i=>affected.Contains((i.Source,i.Use))))
                    affected.Add((node.Result.Request,node.Use));
            }
            var reusable=@base.Evaluations.Where(e=>!affected.Contains((e.Result.Request,e.Use))).ToImmutableArray();
            var up=Build(snapshot,requests,new(bump,true),reusable,cancellation);
            cancellation.ThrowIfCancellationRequested();
            var down=Build(snapshot,requests,new(bump,false),reusable,cancellation);
            factors.Add(new(bump,up,down));
        }
        return new(@base,factors.ToImmutable());
    }
}
