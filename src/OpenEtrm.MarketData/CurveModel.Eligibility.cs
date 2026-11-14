using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData;

public sealed record ExcludedCurveOutput(CurveRequest Request,EligibilityState State);
public sealed record CurveOutputSelection(DateTimeOffset At,string Policy,ImmutableArray<CurveRequest> Selected,
    ImmutableArray<ExcludedCurveOutput> Excluded);

public sealed partial class CurveModel
{
    public const string LiveOutputsPolicy="live_contract_outputs_v1";

    // Selecting a NEW output scope is distinct from executing exact retained
    // requests. This never changes definitions, observations or dependencies.
    public CurveOutputSelection SelectLiveOutputs(DateTimeOffset at,ImmutableArray<CurveRequest> candidates,string policy)
    {
        if(policy!=LiveOutputsPolicy)
            throw new MarketDataException("curve_output_policy","Select the explicit supported live-output selection policy.");
        if(!TradingInstant.IsSupported(at)||candidates.IsDefault||candidates.Distinct().Count()!=candidates.Length)
            throw new MarketDataException("curve_output_scope","Supply exact ordered candidate requests and a supported UTC instant.");
        var selected=ImmutableArray.CreateBuilder<CurveRequest>();var excluded=ImmutableArray.CreateBuilder<ExcludedCurveOutput>();
        foreach(var request in candidates)
        {
            var curve=AssetDefinition(request.Curve);var contract=ContractDefinition(request.Contract);
            if(!curveMembers[curve.Key].Contains(contract.Key))
                throw new MarketDataException("curve_output_scope","Every candidate must be an exact declared member of its curve.");
            var window=new ContractOperationWindow(contract.Key,ContractOperation.LiveCurveOutput,null,contract.Expiry,
                ContractOperationWindow.MarkingCutoffVersion);
            var state=ContractEligibility.At(window,at);
            if(state==EligibilityState.Eligible)selected.Add(request);else excluded.Add(new(request,state));
        }
        return new(at,policy,selected.ToImmutable(),excluded.ToImmutable());
    }
}
