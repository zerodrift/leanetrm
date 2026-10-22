using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;

namespace OpenEtrm.MarketData;

public sealed partial class CurveModel
{
    public ConnectedCurveExport ExportConnected(CurveBuild build,bool freezeHistorical,ResolvedCalculationContext context,
        CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        if(build is null||context is null||!ReferenceEquals(build.Model,this)||build.NumericalPolicy!=NumericalPolicy||
            !build.Scenario.IsBase||build.Snapshot.ValuationAt>context.ValuationAt||build.Snapshot.MarketCutoff>context.MarketAsOf||
            freezeHistorical!=(build.Snapshot.ValuationAt<context.ValuationAt)||
            !freezeHistorical&&build.Snapshot.MarketCutoff!=context.MarketAsOf)
            throw new MarketDataException("connected_curve_source","Export an unshifted exact source build with explicit historical freezing under the same resolved context.");
        var points=new Dictionary<(CurveRequest,InputUse),CurvePointKey>();
        var declarations=new List<ConnectedCurvePoint>();
        foreach(var evaluation in build.Evaluations)
        {
            cancellation.ThrowIfCancellationRequested();
            var request=evaluation.Result.Request;var curve=curves[request.Curve];var contract=contracts[request.Contract];
            var sample=new ConnectedSourceSample(build.Snapshot.Id,build.Snapshot.ValuationAt,build.Snapshot.MarketCutoff,
                build.Snapshot.SelectionPolicy,evaluation.Use,freezeHistorical);
            var key=new CurvePointKey(request.Curve,ConnectedIdentity.Id(new{Version="connected_curve_build_v1",request,evaluation.Use,Sample=sample}));
            ConnectedCurveExpression expression;
            if(curve.Rule==CurveRule.Direct)
            {
                var original=new InputKey(priceSeries[(request.Contract,curve.Quote)],evaluation.Use);
                var selected=build.SelectedInputs[original];
                var input=freezeHistorical?original with{Use=InputUse.FrozenFact}:original;
                expression=new ConnectedSource(request,input,selected.Observation,Quote(curve),series[original.Series]){Sample=sample};
            }
            else
            {
                CurvePointReference Dependency(InputSlot slot)
                {
                    var input=evaluation.Inputs.Single(i=>i.Slot==slot);
                    return CurvePointReference.Exact(points[(input.Source,input.Use)]);
                }
                ConnectedNumericInput? numeric=null;
                if(evaluation.MultiplierObservation is {} observation)
                {
                    var input=new InputKey(curve.Multiplier!.Series!.Value,InputUse.Variable);
                    var selected=build.SelectedInputs[input];
                    if(selected.Observation.Key!=observation)throw new MarketDataException("connected_curve_source","The selected multiplier observation differs from the completed source build.");
                    numeric=new(input,series[input.Series],selected.Observation);
                }
                expression=new ConnectedBuiltCurve(curve.Rule,Dependency(InputSlot.Base),Dependency(InputSlot.Spread),
                    evaluation.Multiplier,numeric,Quote(curve),sample,NumericalPolicy);
            }
            if(!points.TryAdd((request,evaluation.Use),key))
                throw new MarketDataException("connected_curve_source","A completed source build repeats an exact evaluation.");
            declarations.Add(new(key,contract.DeliveryFrom,contract.DeliveryTo,evaluation.Result.Price.Unit,expression,contract));
        }
        return new(declarations.GroupBy(p=>p.Key.Curve).OrderBy(g=>g.Key.Code,StringComparer.Ordinal)
            .Select(g=>new ConnectedCurveDefinition(g.Key,g.OrderBy(p=>p.Key.PointId).ToImmutableArray())).ToImmutableArray(),points.ToImmutableDictionary());
    }
}
