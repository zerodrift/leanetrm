using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CurveDisplaySelection(string Version,string Contracts);
public sealed record CurveContractDisplay(Guid ContractId,string DeliveryFrom,string DeliveryTo,string Expiry,bool Expired,string Status,string? UnavailableReason)
{
    public required ContractOperationWindow Window{get;init;}
    public required EligibilityState Eligibility{get;init;}
}
public sealed record CurveDisplayInfo(CurveDisplaySelection Selection,int CapturedCount,int EligibleCount,ImmutableArray<CurveContractDisplay> Contracts);
public sealed record CurveExpiryStatus(DateTimeOffset At,bool Expired,string Status);

// Display selection is independent of pricing dependencies and held positions.
public static class CurveDisplay
{
    public const string Version="curve_display_v1",Live="eligible_contracts_v1",All="captured_contracts_with_status_v1";
    public static bool Expired(MarketContract contract,DateTimeOffset at)=>at>=contract.Expiry;
    static string Status(EligibilityState state)=>state switch{
        EligibilityState.Eligible=>"Eligible for live marking",
        EligibilityState.NotYetEligible=>"Marking has not started",
        EligibilityState.EligibilityEnded=>"Live marking ended",
        EligibilityState.Restricted=>"Marking restricted",
        _=>throw new MarketDataException("contract_eligibility_context","Unknown eligibility state.")};
    public static CurveExpiryStatus? ExpiryStatus(MarketContract contract,DateTimeOffset? at)=>at is {} selected?
        new(selected,Expired(contract,selected),Expired(contract,selected)?"Past marking boundary":"Before marking boundary"):null;
    // Source revision applicability has already been resolved by the pinned
    // reader. The native point contract declares a marking cutoff, not an
    // exchange first-trading time; do not fabricate one from revision creation.
    public static ContractOperationWindow Window(MarketContract contract)=>
        new(contract.Key,ContractOperation.LiveCurveOutput,null,contract.Expiry,ContractOperationWindow.MarkingCutoffVersion);
    public static ImmutableArray<CurveRequest> LiveRequests(PreviewData data,InputFrame frame,string curve)=>
        data.Model.SelectLiveOutputs(frame.At,data.Requests(frame,curve),CurveModel.LiveOutputsPolicy).Selected;
    public static void Require(CurveDisplaySelection selection)
    {
        if(selection is null||selection.Version!=Version||selection.Contracts is not(Live or All))
            throw new MarketDataException("curve_display_policy","Choose an explicit supported contract display policy.");
    }
    public static ImmutableArray<ContractOperationWindow> Select(ImmutableArray<ContractOperationWindow> windows,DateTimeOffset at,CurveDisplaySelection selection)
    {
        Require(selection);
        if(windows.IsDefault)throw new MarketDataException("curve_display_context","Supply captured contract-operation windows.");
        var resolved=windows.Select(w=>(Window:w,State:ContractEligibility.At(w,at))).ToArray();
        return [..resolved.Where(w=>selection.Contracts==All||w.State==EligibilityState.Eligible).Select(w=>w.Window)];
    }

    private sealed record Prepared(ImmutableArray<CurveRequest> Requests,CurveBuild? Build,CurveDisplayInfo Info);
    private static Prepared Prepare(PreviewData data,MarketSnapshot snapshot,InputFrame frame,string curve,CurveDisplaySelection selection,bool allowUnvalued)
    {
        Require(selection);
        if(snapshot.Id!=frame.Id||snapshot.ValuationAt!=frame.At||snapshot.Definitions!=data.DefinitionRevision)
            throw new MarketDataException("curve_display_context","The view requires the exact captured frame and source definitions.");
        var captured=data.Requests(frame,curve);
        var contracts=captured.ToDictionary(r=>r.Contract.Id,r=>data.ContractRows(r.Contract.Id).Single());
        var windows=contracts.ToDictionary(k=>k.Key,k=>Window(k.Value));
        var scope=data.Model.SelectLiveOutputs(frame.At,captured,CurveModel.LiveOutputsPolicy);
        var states=scope.Selected.ToDictionary(r=>r.Contract.Id,_=>EligibilityState.Eligible);
        foreach(var excluded in scope.Excluded)states.Add(excluded.Request.Contract.Id,excluded.State);
        // Rendering preferences never decide financial eligibility. Inactive rows
        // are metadata; the same eligible output set is priced in either view.
        var requests=scope.Selected;
        CurveBuild? build=null;ImmutableDictionary<CurveRequest,string> unvalued=ImmutableDictionary<CurveRequest,string>.Empty;
        if(!requests.IsEmpty)
        {
            // Shapes describe unavailable contracts; an evaluation still requires
            // every eligible output's declared price inputs and final evidence.
            if(allowUnvalued)(build,unvalued)=ValuationStatus.BuildValued(set=>data.Model.Build(snapshot,set),requests);
            else build=data.Model.Build(snapshot,requests);
        }
        var display=captured.Where(r=>selection.Contracts==All||states[r.Contract.Id]==EligibilityState.Eligible).Select(request=>
        {
            var contract=contracts[request.Contract.Id];var state=states[request.Contract.Id];
            return new CurveContractDisplay(contract.Key.Id,Day(contract.DeliveryFrom),Day(contract.DeliveryTo),contract.Expiry.ToString("O"),Expired(contract,frame.At),
                Status(state),state==EligibilityState.Eligible?unvalued.GetValueOrDefault(request):state==EligibilityState.NotYetEligible?"eligibility_not_started":"eligibility_ended")
                {Window=windows[request.Contract.Id],Eligibility=state};
        }).ToImmutableArray();
        return new(requests,build,new(selection,captured.Length,requests.Length,display));
    }
    public static object Result(PreviewData data,InputFrame frame,string curve,CurveDisplaySelection selection)
    {
        var prepared=Prepare(data,data.Snapshot(frame),frame,curve,selection,allowUnvalued:false);
        var value=prepared.Build is {} build?data.Result(frame,curve,viewBuild:build):data.EmptyContractResult(frame,curve);
        var json=JsonSerializer.SerializeToNode(value,new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        json["contractDisplay"]=JsonSerializer.SerializeToNode(prepared.Info,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var front=Front(prepared);
        json["summary"]=JsonSerializer.SerializeToNode(new{kind="contracts",contractCount=prepared.Requests.Length,front,
            availability=Availability(prepared,front)},new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if(prepared.Build is null)json["availability"]=Availability(prepared,front);
        return json;
    }
    public static CurveShape Shape(PreviewData data,MarketSnapshot snapshot,InputFrame frame,string curve,CurveDisplaySelection selection)
    {
        var prepared=Prepare(data,snapshot,frame,curve,selection,allowUnvalued:true);
        var values=(prepared.Build?.Values??[]).ToDictionary(v=>v.Request.Contract.Id,v=>v.Price.Value.ToString());
        var points=prepared.Info.Contracts.Select(c=>new CurveShapePoint(c.ContractId,c.DeliveryFrom,c.DeliveryTo,values.GetValueOrDefault(c.ContractId),c.UnavailableReason)).ToImmutableArray();
        var front=Front(prepared);
        return new(curve,data.PriceUnit(curve),points,null,points.Length,front,Availability(prepared,front))
            {DefinitionRevisionId=data.DefinitionRevision.RevisionId,ContractDisplay=prepared.Info};
    }
    static CurveFront? Front(Prepared prepared)
    {
        var unexpired=prepared.Info.Contracts.Where(c=>c.Eligibility==EligibilityState.Eligible).ToDictionary(c=>c.ContractId);
        return CurveFront.First((prepared.Build?.Values??[]).Where(v=>unexpired.ContainsKey(v.Request.Contract.Id)).Select(v=>
        {
            var contract=unexpired[v.Request.Contract.Id];
            return new CurveShapePoint(v.Request.Contract.Id,contract.DeliveryFrom,contract.DeliveryTo,v.Price.Value.ToString(),null);
        }));
    }
    static string? Availability(Prepared prepared,CurveFront? front)=>front is not null?null:
        prepared.Info.EligibleCount==0?CurveSummary.NoRemainingContracts:CurveSummary.NoPricedContract;
    static string Day(DateOnly value)=>value.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);
}
