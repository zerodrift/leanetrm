using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using OpenEtrm.Api;

// The legacy settlement regression keeps its native read and numerical oracles.
// Its financial actions explicitly use the same reviewed Lifecycle adapter as
// the host's approved assistant command, with no direct native writer grants.
internal sealed class LifecycleSwapAgentFixture(PreviewDatabase database,InputFrame frame,Guid binding,Guid netBinding)
{
    private readonly SwapSettlementOperations reads=new(database);
    private readonly LifecycleOperations lifecycle=new(database,id=>id==frame.Id?frame:throw new Exception("Unknown synthetic lifecycle frame."));
    private readonly Dictionary<Guid,(JsonElement Submitted,LifecycleActionRequest Request)> receipts=[];
    private static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,LifecycleNative.Json);
    internal Task<object> ExecuteRetained(AgentIdentity agent,Guid operation,CancellationToken ct)=>
        lifecycle.ExecuteApproved(agent,receipts[operation].Request,ct);
    private async Task<object> Record(AgentIdentity agent,Guid operation,object submitted,CancellationToken ct,Func<Task<LifecycleActionRequest>> prepare)
    {
        var body=Json(submitted);LifecycleActionRequest request;
        if(receipts.TryGetValue(operation,out var prior))request=JsonElement.DeepEquals(body,prior.Submitted)?prior.Request:Changed(prior.Request,submitted);
        else request=await prepare();
        var result=await lifecycle.ExecuteApproved(agent,request,ct);
        receipts[operation]=(body,request);return result;
    }
    private static LifecycleActionRequest Changed(LifecycleActionRequest prior,object submitted)
    {
        var selection=submitted switch{
            SwapSettlementDetermineRequest command=>Json(prior.Selection.Deserialize<SwapActionSelection>(LifecycleNative.Json)! with{
                Determination=new(command.Preview,command.ExpectedVersion,command.DuePolicy,command.RecognitionPolicy,command.CorrectionPolicy,command.CorrectionDueAt)}),
            SwapCashRequest command=>Json(prior.Selection.Deserialize<SwapActionSelection>(LifecycleNative.Json)! with{
                Payment=new(command.ExpectedRevision,command.ExpectedVersion,command.DeterminationId,command.Amount,command.Currency,command.EffectiveAt,command.Reference,command.ReversesId)}),
            SwapNetRecordRequest command=>Json(prior.Selection.Deserialize<SwapNetActionSelection>(LifecycleNative.Json)! with{
                Net=new(command.Preview,command.EffectiveAt,command.Reference)}),
            _=>throw new Exception("Select one closed synthetic lifecycle command.")};
        return prior with{Selection=selection,PreviewHash=new('f',64)};
    }
    private async Task<LifecycleActionRequest> Review(AgentIdentity agent,string action,Guid operation,Guid trade,JsonElement source,
        Func<JsonElement,JsonElement> selection,CancellationToken ct)
    {
        var context=new LifecycleReadContext(source.Id("contextId"),frame.At,frame.BusinessDate,frame.Id);
        var target=new LifecycleTarget(action=="swap.net"?"financial_swap_net":"financial_swap",trade,action=="swap.net"?netBinding:binding);
        var discovery=Json(await lifecycle.Discover(agent,new(context,target),ct));
        var offer=discovery.GetProperty("actions").EnumerateArray().Single(a=>a.Text("action")==action);
        var request=new LifecycleActionRequest(operation,context,target,action,offer.Id("policyId"),offer.GetProperty("policyRevision").GetInt32(),false,new('0',64),selection(offer.GetProperty("selection")));
        var review=Json(await lifecycle.Preview(agent,request,ct));return request with{PreviewHash=review.Text("hash")};
    }
    internal Task<object> Determine(AgentIdentity agent,InputFrame selected,SwapSettlementDetermineRequest command,CancellationToken ct)=>
        Record(agent,command.OperationId,command,ct,async()=>
        {
            var native=Json(await reads.Preview(agent,selected,command.Preview,ct));
            var request=await Review(agent,"swap.determine",command.OperationId,command.Preview.TradeId,native.GetProperty("context"),value=>
            {
                var prior=value.Deserialize<SwapActionSelection>(LifecycleNative.Json)!;
                return Json(prior with{Determination=new(command.Preview,command.ExpectedVersion,command.DuePolicy,command.RecognitionPolicy,command.CorrectionPolicy,command.CorrectionDueAt)});
            },ct);
            return native.GetProperty("value").Text("previewHash")==command.PreviewHash?request:request with{PreviewHash=command.PreviewHash};
        });
    internal Task<object> RecordCash(AgentIdentity agent,InputFrame selected,SwapCashRequest command,CancellationToken ct)=>
        Record(agent,command.OperationId,command,ct,async()=>
        {
            var native=Json(await reads.Inspect(agent,selected,new(command.Context,command.TradeId,null,"swap_due_nominal_v1","swap_completed_period_trading_pnl_v1"),ct));
            return await Review(agent,"swap.payment",command.OperationId,command.TradeId,native.GetProperty("context"),value=>
            {
                var prior=value.Deserialize<SwapActionSelection>(LifecycleNative.Json)!;
                return Json(prior with{Payment=new(command.ExpectedRevision,command.ExpectedVersion,command.DeterminationId,command.Amount,command.Currency,command.EffectiveAt,command.Reference,command.ReversesId)});
            },ct);
        });
    internal Task<object> RecordNet(AgentIdentity agent,InputFrame selected,SwapNetRecordRequest command,CancellationToken ct)=>
        Record(agent,command.OperationId,command,ct,async()=>
        {
            var native=Json(await reads.PreviewNet(agent,selected,command.Preview,ct));
            var request=await Review(agent,"swap.net",command.OperationId,command.Preview.TradeId,native.GetProperty("context"),value=>
            {
                var prior=value.Deserialize<SwapNetActionSelection>(LifecycleNative.Json)!;
                return Json(prior with{Net=new(command.Preview,command.EffectiveAt,command.Reference)});
            },ct);
            return native.GetProperty("value").Text("previewHash")==command.PreviewHash?request:request with{PreviewHash=command.PreviewHash};
        });
}
