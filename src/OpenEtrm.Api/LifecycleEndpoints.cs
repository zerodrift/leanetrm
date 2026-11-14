using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecyclePolicySave(Guid OperationId,Guid PolicyId,[property:JsonRequired]int ExpectedRevision,string Action,
    string Mode,string FromAt,string UntilAt,string Implementation);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record OptionLifecycleProfileSave(Guid OperationId,Guid TradeId,string Action,[property:JsonRequired]int ExpectedRevision,
    Guid PolicyId,int PolicyRevision,Guid MarginPolicyId,int MarginPolicyRevision,string FutureMarkCurve,string? ExpiryCurve);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SwapLifecycleProfileSave(Guid OperationId,Guid ProfileId,Guid TradeId,[property:JsonRequired]int ExpectedRevision,int OriginRevision,string Action,
    Guid PolicyId,int PolicyRevision,[property:JsonRequired]int MembershipOrdinal,[property:JsonRequired]int PaymentOrdinal,string DeliveryFrom,string DeliveryTo,
    Guid ReferenceCatalogId,Guid ReferenceRevisionId,string Currency,[property:JsonRequired]int DecimalPlaces,string RoundingPolicy,string DuePolicy,string RecognitionPolicy,string CorrectionPolicy);

internal static class LifecycleEndpoints
{
    internal static void Map(RouteGroupBuilder api,PreviewDatabase database,
        Func<HttpContext,Func<PreviewSession,Task<IResult>>,Task<IResult>> locked,
        Func<PreviewSession,string> profile,Func<PreviewSession,Guid,InputFrame> frame,
        Func<PreviewSession,IEnumerable<ILifecycleActionHandler>>? additional=null)
    {
        LifecycleOperations Operations(PreviewSession session)=>new(database,id=>frame(session,id),additional?.Invoke(session));
        async Task<IResult> Discover(HttpContext c,LifecycleDiscoveryRequest body)=>await locked(c,async session=>
        {
            var result=await database.WithIdentity(profile(session),"ReadTrades",(connection,ct)=>Operations(session).Discover(connection,body,ct),c.RequestAborted);
            return Results.Ok(result);
        });
        api.MapPost("/lifecycle/discover",Discover);
        api.MapPost("/lifecycle/view",(HttpContext c,FuturesReadRequest body)=>locked(c,async session=>
        {
            var selected=frame(session,body.SnapshotId);var source=TradingReadSelection.Capture(session,body,selected);
            var result=await database.WithIdentity(profile(session),"ReadTrades",(connection,ct)=>Operations(session).View(connection,body,source,ct),c.RequestAborted);
            return Results.Ok(result);
        }));
        api.MapPost("/lifecycle/policies/read",Discover);
        api.MapPost("/lifecycle/preview",(HttpContext c,LifecycleActionRequest body)=>locked(c,async session=>
        {
            var result=await database.WithIdentity(profile(session),"ReadTrades",(connection,ct)=>Operations(session).Preview(connection,body,ct),c.RequestAborted);
            return Results.Ok(result);
        }));
        api.MapPost("/lifecycle/execute",(HttpContext c,LifecycleActionRequest body)=>locked(c,async session=>
        {
            if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before recording lifecycle facts.");
            var result=await database.WithIdentity(profile(session),null,(connection,ct)=>Operations(session).Execute(connection,body,ct),c.RequestAborted);
            return Results.Ok(result.Outcome);
        }));
        async Task<IResult> Save<T>(HttpContext c,T body,string kind)=>await locked(c,async session=>
        {
            if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Edit lifecycle settings in current knowledge.");
            var payload=JsonSerializer.SerializeToElement(body,LifecycleNative.Json);
            var result=await database.WithIdentity(profile(session),null,(connection,ct)=>LifecycleSettings.Save(connection,database,payload,kind,ct),c.RequestAborted);
            return Results.Ok(result.Outcome);
        });
        api.MapPost("/lifecycle/policies/save",(HttpContext c,LifecyclePolicySave body)=>Save(c,body,"policy"));
        api.MapPost("/lifecycle/options/configure",(HttpContext c,OptionLifecycleProfileSave body)=>Save(c,body,"option_profile"));
        api.MapPost("/lifecycle/swaps/configure",(HttpContext c,SwapLifecycleProfileSave body)=>Save(c,body,"swap_profile"));
        api.MapPost("/lifecycle/swaps/net/configure",(HttpContext c,SwapNetLifecycleProfileSave body)=>Save(c,body,"swap_net_profile"));
        api.MapPost("/lifecycle/gas/read",(HttpContext c,GasOwnedReadRequest body)=>locked(c,async session=>
        {
            var result=await database.WithIdentity(profile(session),"ReadGasOwnedHoldings",(connection,ct)=>GasOwnedOperations.ReadScoped(connection,database,body,ct),c.RequestAborted);
            return Results.Ok(result);
        }));
        api.MapPost("/lifecycle/gas/configure",(HttpContext c,GasOwnedConfigurationRequest body)=>locked(c,async session=>
        {
            if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Return to working data before recording gas ownership settings or evidence.");
            var result=await database.WithIdentity(profile(session),null,(connection,ct)=>GasOwnedOperations.Configure(connection,database,body,ct),c.RequestAborted);
            return Results.Ok(result.Outcome);
        }));
        void RequireSource(PreviewSession session,LifecycleReadContext context)
        {
            if(session.MarketContext is not null||context is null||context.SnapshotId is not {} id)throw new MarketDataException("historical_read_only","Choose an explicit working source frame before changing futures settlement settings.");
            var selected=frame(session,id);
            if(selected.ContextId is not null||selected.At!=context.AsOf||selected.BusinessDate!=context.BusinessDate)
                throw new MarketDataException("lifecycle_context","Retain this exact working frame, instant and business date.");
        }
        api.MapPost("/lifecycle/futures/policies/save",(HttpContext c,LifecycleMarginPolicySave body)=>locked(c,async session=>
        {
            if(body.Context is null)throw new MarketDataException("lifecycle_context","Supply the original exact source context.");
            var payload=JsonSerializer.SerializeToElement(new{body.OperationId,body.ExpectedRevision,body.Context.ContextId,asOf=LifecycleNative.Instant(body.Context.AsOf),body.Policy},LifecycleNative.Json);
            var result=await database.WithIdentity(profile(session),"WriteMarketDefinitions",(connection,ct)=>LifecycleFuturesSettings.Save(connection,database,body.Context,payload,true,()=>RequireSource(session,body.Context),ct),c.RequestAborted);
            return Results.Ok(result.Outcome);
        }));
        api.MapPost("/lifecycle/futures/adopt",(HttpContext c,LifecycleFuturesAdoption body)=>locked(c,async session=>
        {
            if(body.Context is null)throw new MarketDataException("lifecycle_context","Supply the original exact source context.");
            var payload=JsonSerializer.SerializeToElement(new{body.OperationId,body.TradeId,body.BindingId,body.ExpectedRevision,body.PolicyId,body.PolicyRevision,
                body.Context.ContextId,asOf=LifecycleNative.Instant(body.Context.AsOf),body.Adoption},LifecycleNative.Json);
            var result=await database.WithIdentity(profile(session),"AmendTrade",(connection,ct)=>LifecycleFuturesSettings.Save(connection,database,body.Context,payload,false,()=>RequireSource(session,body.Context),ct),c.RequestAborted);
            return Results.Ok(result.Outcome);
        }));
        api.MapGet("/jobs/lifecycle/{id:guid}",(HttpContext c,Guid id)=>locked(c,async session=>
        {
            var result=await database.WithIdentity(profile(session),"ReadTrades",(connection,ct)=>LifecycleScheduleJobs.Read(connection,database,id,ct),c.RequestAborted);
            return Results.Ok(result.Outcome);
        }));
        api.MapPost("/jobs/lifecycle/create",(HttpContext c,LifecycleScheduleCreate body)=>locked(c,async session=>
        {
            if(session.MarketContext is not null)throw new MarketDataException("historical_read_only","Schedule lifecycle work in the current workspace.");
            var result=await database.WithIdentity(profile(session),"RunJobs",(connection,ct)=>LifecycleScheduleJobs.Create(connection,database,body,ct),c.RequestAborted);
            return Results.Ok(result.Outcome);
        }));
        LifecyclePhysicalSettings.Map(api,database,locked,profile,RequireSource);
    }
}
