using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.MarketData;

public sealed partial class CurveModel
{
    // Compile exact domain relationships and selected observations. No numerical
    // evaluation occurs here; ordinary curves and recipes share the graph engine.
    public ConnectedCurveExport Compile(MarketSnapshot snapshot,ImmutableArray<CurveRequest> requests,
        bool freezeHistorical,ResolvedCalculationContext context,CancellationToken cancellation=default)
        =>CompileValidated(snapshot,requests,freezeHistorical,context,ValidateSnapshot(snapshot,cancellation),cancellation);

    private ConnectedCurveExport CompileValidated(MarketSnapshot snapshot,ImmutableArray<CurveRequest> requests,
        bool freezeHistorical,ResolvedCalculationContext context,ImmutableDictionary<InputKey,SelectedObservation> inputs,CancellationToken cancellation)
    {
        if(context is null||snapshot.ValuationAt>context.ValuationAt||snapshot.MarketCutoff>context.MarketAsOf||
            freezeHistorical!=(snapshot.ValuationAt<context.ValuationAt)||!freezeHistorical&&snapshot.MarketCutoff!=context.MarketAsOf)
            throw new MarketDataException("connected_curve_source","Compile an exact source snapshot with explicit historical freezing under the selected resolved context.");
        if(requests.IsDefault||requests.Distinct().Count()!=requests.Length)
            Fail("request","Supply distinct explicit output requests.");
        var points=new Dictionary<(CurveRequest Request,InputUse Use),CurvePointKey>();
        var declarations=new List<ConnectedCurvePoint>();
        var pending=new Queue<(CurveRequest Request,InputUse Use)>();
        CurvePointKey Point(CurveRequest request,InputUse use)
        {
            if(use==InputUse.Variable && curves.TryGetValue(request.Curve,out var selected) &&
                selected.ConstituentExpiry==ConstituentExpiryRule.FreezeFinalSettlement && contracts.TryGetValue(request.Contract,out var term) && snapshot.ValuationAt>=term.Expiry)
            {
                var frozen=Point(request,InputUse.FrozenFact);
                points.TryAdd((request,InputUse.Variable),frozen);
                return frozen;
            }
            if(points.TryGetValue((request,use),out var existing))return existing;
            var sample=new ConnectedSourceSample(snapshot.Id,snapshot.ValuationAt,snapshot.MarketCutoff,snapshot.SelectionPolicy,use,freezeHistorical);
            var key=new CurvePointKey(request.Curve,ConnectedIdentity.Id(new{Version="connected_curve_build_v1",request,Use=use,Sample=sample}));
            points.Add((request,use),key);pending.Enqueue((request,use));return key;
        }
        foreach(var request in requests)Point(request,InputUse.Variable);
        while(pending.TryDequeue(out var item))
        {
            cancellation.ThrowIfCancellationRequested();var (request,use)=item;
            if(!curves.TryGetValue(request.Curve,out var curve)||!curveMembers[curve.Key].Contains(request.Contract))
                throw new MarketDataException("request_member","Requested curve constituent is absent.");
            var contract=contracts[request.Contract];
            if(use==InputUse.Variable&&snapshot.ValuationAt>=contract.Expiry)
                throw new MarketDataException("source_expired","A live constituent expired; an explicit supported transition is required.");
            var sample=new ConnectedSourceSample(snapshot.Id,snapshot.ValuationAt,snapshot.MarketCutoff,snapshot.SelectionPolicy,use,freezeHistorical);
            var quote=Quote(curve);ConnectedCurveExpression expression;
            if(curve.Rule==CurveRule.Direct)
            {
                var key=new InputKey(priceSeries[(request.Contract,curve.Quote)],use);
                if(!inputs.TryGetValue(key,out var selection))
                    throw new MarketDataException("missing_observation","Exact required variable/frozen input is absent.");
                var observation=selection.Observation;
                var expectedRole=use==InputUse.FrozenFact?ObservationRole.FinalContractSettlement:curve.DirectRole;
                if(observation.Source!=curve.DirectSource||observation.Role!=expectedRole||observation.State!=PublicationState.Final)
                    throw new MarketDataException("selection_mismatch","Selected quote, source, role or finality is incompatible.");
                if(use==InputUse.FrozenFact&&(contract.Kind!=ContractKind.Future&&curve.ConstituentExpiry!=ConstituentExpiryRule.FreezeFinalSettlement||observation.ObservedAt<contract.Expiry))
                    throw new MarketDataException("frozen_fact","Final future settlement must be observed at or after its explicit expiry.");
                expression=new ConnectedSource(request,freezeHistorical?key with{Use=InputUse.FrozenFact}:key,
                    observation,quote,series[key.Series]){Sample=sample};
            }
            else
            {
                if(use!=InputUse.Variable)throw new MarketDataException("unsupported_expiry","Composite final settlements are not implemented.");
                CurvePointReference Dependency(InputSlot slot)
                {
                    var input=curve.Inputs.Single(i=>i.Slot==slot);var sourceKey=mappingSources[(request.Curve,slot,request.Contract)];
                    var source=contracts[sourceKey];
                    var sourceUse=snapshot.ValuationAt<source.Expiry?InputUse.Variable:
                        input.ExpiryRule==SourceExpiryRule.FreezeFinalFuture?InputUse.FrozenFact:
                        throw new MarketDataException("source_expired","Source expired and its policy requires a live value.");
                    return CurvePointReference.Exact(Point(new(input.Source,sourceKey),sourceUse));
                }
                NumericValue? multiplier=null;ConnectedNumericInput? numeric=null;
                if(curve.Multiplier is {} binding)
                {
                    ExactDecimal factor;
                    if(binding.Series is {} numericSeries)
                    {
                        var input=new InputKey(numericSeries,InputUse.Variable);
                        if(!inputs.TryGetValue(input,out var selected))
                            throw new MarketDataException("missing_observation","Explicit multiplier series observation is absent.");
                        var observation=selected.Observation;
                        if(observation.Source!=binding.Source||observation.Role!=binding.Role||observation.State!=PublicationState.Final)
                            throw new MarketDataException("selection_mismatch","Multiplier source, role or finality does not match.");
                        factor=observation.Value;numeric=new(input,series[input.Series],observation);
                    }
                    else factor=binding.Constant!.Value;
                    multiplier=new(factor,NumericSpecification(binding));
                }
                expression=new ConnectedBuiltCurve(curve.Rule,Dependency(InputSlot.Base),Dependency(InputSlot.Spread),multiplier,numeric,quote,sample,NumericalPolicy);
            }
            declarations.Add(new(points[item],contract.DeliveryFrom,contract.DeliveryTo,PriceUnit.FromQuote(quote),expression,contract));
        }
        return new(declarations.GroupBy(p=>p.Key.Curve).OrderBy(g=>g.Key.Code,StringComparer.Ordinal)
            .Select(g=>new ConnectedCurveDefinition(g.Key,g.OrderBy(p=>p.Key.PointId).ToImmutableArray())).ToImmutableArray(),points.ToImmutableDictionary());
    }
}
