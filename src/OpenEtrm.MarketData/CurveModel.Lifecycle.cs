using System.Collections.Immutable;

namespace OpenEtrm.MarketData;

public sealed partial class CurveModel
{
    // These are the selected contract's explicit input-expiry relationships.
    // Holding filters and the presence of an unrelated futures trade play no role.
    internal ImmutableDictionary<CurveRequest,DateTimeOffset> LifecycleFinals(ImmutableDictionary<CurveRequest,DateTimeOffset> uses,
        DateTimeOffset opening,DateTimeOffset horizon,ImmutableDictionary<CurveRequest,DateTimeOffset> selected)
    {
        var finals=ImmutableDictionary.CreateBuilder<CurveRequest,DateTimeOffset>();
        var visited=new HashSet<(CurveRequest,bool,DateTimeOffset)>();
        void Visit(CurveRequest request,bool frozenByEdge,DateTimeOffset through)
        {
            if(!visited.Add((request,frozenByEdge,through)))return;
            var curve=AssetDefinition(request.Curve);var contract=ContractDefinition(request.Contract);
            if(!curveMembers[curve.Key].Contains(request.Contract))throw new MarketDataException("request_member","The lifecycle market must retain an exact curve constituent.");
            if(curve.Rule==CurveRule.Direct)
            {
                if(contract.Expiry<=opening||contract.Expiry>through)return;
                var at=selected.GetValueOrDefault(request,contract.Expiry);
                if(contract.Kind!=ContractKind.Future||!frozenByEdge&&curve.ConstituentExpiry!=ConstituentExpiryRule.FreezeFinalSettlement&&!selected.ContainsKey(request))
                    throw new MarketDataException("projection_input_expiry","An expiring live input requires an explicit supported final-settlement transition in its selected curve or event policy.");
                if(finals.TryGetValue(request,out var existing)&&existing!=at)
                    throw new MarketDataException("projection_input_expiry","A native input has conflicting selected final-settlement instants.");
                finals[request]=at;return;
            }
            foreach(var input in curve.Inputs)
                Visit(new(input.Source,mappingSources[(request.Curve,input.Slot,request.Contract)]),input.ExpiryRule==SourceExpiryRule.FreezeFinalFuture,through);
        }
        foreach(var (request,through) in uses)Visit(request,false,through);
        return finals.ToImmutable();
    }
}
