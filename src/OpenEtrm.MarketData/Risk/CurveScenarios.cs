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
            var direct=curves.Values.Where(c=>c.Rule==CurveRule.Direct && c.Quote==quoteKey && curveMembers[c.Key].Contains(contract) &&
                c.DirectSource==obs.Source && c.DirectRole==obs.Role).Select(c=>c.Key).OrderBy(c=>c.Code,StringComparer.Ordinal).ToImmutableArray();
            if(!direct.IsEmpty && obs.State==PublicationState.Final)result.Add(new(selected.Input,quoteKey,PriceUnit.FromQuote(quote),contract,obs.Key,obs.Source,obs.Role,obs.Value,direct,quote.TradingTick));
        }
        return result.OrderBy(f=>contracts[f.Contract].DeliveryFrom).ThenBy(f=>f.Input.Series.Id).ToImmutableArray();
    }
    public CurveScenarioSet BuildScenarios(MarketSnapshot snapshot,ImmutableArray<CurveRequest> requests,
        ImmutableArray<QuoteBump> bumps,CancellationToken cancellation=default)
    {
        if(bumps.IsDefaultOrEmpty || bumps.Length>16 || bumps.Any(b=>b is null) || bumps.Select(b=>b.Input).Distinct().Count()!=bumps.Length)
            Fail("risk_bump","Select between one and sixteen distinct explicit quote-input bumps.");
        cancellation.ThrowIfCancellationRequested();
        var @base=Build(snapshot,requests,null,[],cancellation);
        return new(@base,Scenarios(@base,bumps,cancellation).ToImmutableArray());
    }
    // Stream one shared factor pair at a time: a broad risk vector must not retain
    // hundreds of whole market graphs or rebuild its base for every batch.
    public IEnumerable<FactorScenarios> Scenarios(CurveBuild @base,ImmutableArray<QuoteBump> bumps,CancellationToken cancellation=default)
    {
        RequireScenarioParent(@base);
        if(bumps.IsDefaultOrEmpty||bumps.Length>4096||bumps.Any(b=>b is null)||bumps.Select(b=>b.Input).Distinct().Count()!=bumps.Length)
            Fail("risk_bump","Select one to 4096 distinct explicit quote-input bumps.");
        foreach(var bump in bumps)
        {
            cancellation.ThrowIfCancellationRequested();
            if(bump.ConventionId!=QuoteBump.Convention || !Positive(bump.Size))
                Fail("risk_bump","Choose the versioned absolute symmetric convention and a positive exact bump.");
            var shift=new QuoteShift(bump.Input,bump.Quote,bump.Unit,bump.Size,QuoteShift.Convention);
            var up=WithShifts(@base,[shift],cancellation,new(bump,true));
            var down=WithShifts(@base,[shift with{Size=default(ExactDecimal)-bump.Size}],cancellation,new(bump,false));
            yield return new(bump,up,down);
        }
    }
    public CurveBuild WithShifts(CurveBuild parent,ImmutableArray<QuoteShift> shifts,CancellationToken cancellation=default)
        =>WithShifts(parent,shifts,cancellation,null);

    private CurveBuild WithShifts(CurveBuild parent,ImmutableArray<QuoteShift> shifts,CancellationToken cancellation,QuoteShock? shock)
    {
        cancellation.ThrowIfCancellationRequested();RequireScenarioParent(parent);
        if(parent.Scenario.Depth>=64||shifts.IsDefaultOrEmpty||shifts.Length>4096||shifts.Any(s=>s is null)||
            shifts.Select(s=>s.Input).Distinct().Count()!=shifts.Length||
            shifts.Any(s=>s.ConventionId!=QuoteShift.Convention||s.Size==default))
            Fail("scenario_shift","Select one to 4096 distinct nonzero signed parent-additive quote shifts, the explicit rebuild convention and at most 64 nested layers.");
        foreach(var shift in shifts)
        {
            cancellation.ThrowIfCancellationRequested();
            if(shift.Input.Use!=InputUse.Variable||!series.TryGetValue(shift.Input.Series,out var definition)||
                definition.Quote!=shift.Quote||!quotes.TryGetValue(shift.Quote,out var quote)||PriceUnit.FromQuote(quote)!=shift.Unit)
                throw new MarketDataException("risk_factor","A shock requires the exact variable price series, quote and price unit.");
            if(!parent.SelectedInputs.TryGetValue(shift.Input,out var selected))
                throw new MarketDataException("risk_factor","The exact selected variable observation is absent.");
            var matching=curves.Values.Where(c=>c.Rule==CurveRule.Direct&&c.Quote==shift.Quote&&curveMembers[c.Key].Contains(definition.Contract!.Value));
            if(!matching.Any(c=>c.DirectSource==selected.Observation.Source&&c.DirectRole==selected.Observation.Role)||
                selected.Observation.State!=PublicationState.Final||parent.Snapshot.ValuationAt>=contracts[definition.Contract!.Value].Expiry)
                Fail("risk_factor","Only an explicitly selected live direct quote is shockable; frozen facts and derived output shocks are unsupported.");
            // Scenarios obey the quote domain without rewriting settlement facts or their ticks.
            Domain(selected.Observation.Value+parent.Scenario.Offset(shift.Input)+shift.Size,quote.Minimum,quote.Maximum);
        }
        var scenario=parent.Scenario.Child(shifts);
        var changed=shifts.Select(s=>s.Input).ToHashSet();var affected=new HashSet<(CurveRequest,InputUse)>();
        foreach(var node in parent.Evaluations)
        {
            cancellation.ThrowIfCancellationRequested();var curve=curves[node.Result.Request.Curve];
            var direct=curve.Rule==CurveRule.Direct&&changed.Contains(new(priceSeries[(node.Result.Request.Contract,curve.Quote)],node.Use));
            if(direct||node.Inputs.Any(i=>affected.Contains((i.Source,i.Use))))affected.Add((node.Result.Request,node.Use));
        }
        var reusable=parent.Evaluations.Where(e=>!affected.Contains((e.Result.Request,e.Use))).ToImmutableArray();
        return Build(parent.Snapshot,parent.Values.Select(v=>v.Request).ToImmutableArray(),shock,reusable,cancellation,scenario);
    }
    private void RequireScenarioParent(CurveBuild parent)
    {
        if(parent is null||!ReferenceEquals(parent.Model,this)||parent.NumericalPolicy!=NumericalPolicy)
            Fail("snapshot_pin","Scenario reuse requires this model's exact immutable parent build and numerical policy.");
    }
}
