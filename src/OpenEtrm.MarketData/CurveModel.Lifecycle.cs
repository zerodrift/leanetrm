using System.Collections.Immutable;

namespace OpenEtrm.MarketData;

public enum LifecycleInputExpiryTreatment { DeclaredCurvePolicy=1, FreezeSelectedNativeMark }

public sealed partial class CurveModel
{
    // These are the selected contract's explicit input-expiry relationships.
    // Holding filters and the presence of an unrelated futures trade play no role.
    public ImmutableDictionary<CurveRequest,DateTimeOffset> LifecycleFinals(ImmutableDictionary<CurveRequest,DateTimeOffset> uses,
        DateTimeOffset opening,ImmutableDictionary<CurveRequest,DateTimeOffset> selected,LifecycleInputExpiryTreatment treatment)
    {
        if(!Enum.IsDefined(treatment))throw new MarketDataException("projection_input_expiry","Select an exact supported input-expiry projection treatment.");
        var finals=ImmutableDictionary.CreateBuilder<CurveRequest,DateTimeOffset>();
        var visited=new HashSet<(CurveRequest,bool,DateTimeOffset,bool)>();
        void Visit(CurveRequest request,bool frozenByEdge,DateTimeOffset through,bool root)
        {
            if(!visited.Add((request,frozenByEdge,through,root)))return;
            var curve=AssetDefinition(request.Curve);var contract=ContractDefinition(request.Contract);
            if(!curveMembers[curve.Key].Contains(request.Contract))throw new MarketDataException("request_member","The lifecycle market must retain an exact curve constituent.");
            if(curve.Rule==CurveRule.Direct)
            {
                if(contract.Expiry<=opening||contract.Expiry>through)return;
                var finalAt=selected.GetValueOrDefault(request,contract.Expiry);var at=contract.Expiry;
                var configured=frozenByEdge||curve.ConstituentExpiry==ConstituentExpiryRule.FreezeFinalSettlement;
                if(contract.Kind!=ContractKind.Future||finalAt<at||!configured&&(!root||!selected.ContainsKey(request))||
                    finalAt>at&&!configured&&treatment!=LifecycleInputExpiryTreatment.FreezeSelectedNativeMark)
                    throw new MarketDataException("projection_input_expiry","An expiring input requires its saved curve transition; a later selected final also requires explicit native-expiry mark carry.");
                if(finals.TryGetValue(request,out var existing)&&existing!=at)
                    throw new MarketDataException("projection_input_expiry","A native input has conflicting selected final-settlement instants.");
                finals[request]=at;return;
            }
            foreach(var input in curve.Inputs)
                Visit(new(input.Source,mappingSources[(request.Curve,input.Slot,request.Contract)]),input.ExpiryRule==SourceExpiryRule.FreezeFinalFuture,through,false);
        }
        foreach(var (request,through) in uses)Visit(request,false,through,true);
        return finals.ToImmutable();
    }
}
