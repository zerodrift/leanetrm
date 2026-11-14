using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleDeliveryProfileSelection(Guid Id,[property:JsonRequired]int Revision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleGasDeliveryProfile(Guid Id,string Name,string Implementation,Guid DefinitionRevisionId,Guid ContractId,Guid AccountId,
    string FromAt,string ToAt,string CashDueAt,string Carrying,string PrincipalIncrement,string PrincipalTie,string PrincipalScope,string CorrectionDuePolicy,
    Guid ActionPolicyId,[property:JsonRequired]int ActionPolicyRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LifecycleDeliveryProfileSave(Guid OperationId,[property:JsonRequired]int ExpectedRevision,LifecycleReadContext Context,LifecycleGasDeliveryProfile Profile);

internal static class LifecyclePhysicalSettings
{
    internal static async Task<(ImmutableArray<JsonElement> Profiles,ImmutableArray<JsonElement> Accounts)> Read(NpgsqlConnection connection,PreviewDatabase database,
        LifecycleReadContext context,CancellationToken ct)
    {
        await using var available=ScheduleStore.Command(connection,"SELECT security.trading_access($1,'ReadGasOwnedHoldings')",database.Manifest.CatalogId);
        if((bool)(await available.ExecuteScalarAsync(ct))! is false)return([],[]);
        await using var read=ScheduleStore.Command(connection,"SELECT settlement_data.read_futures_delivery_settings($1,$2,$3)::text",database.Manifest.CatalogId,context.ContextId,context.AsOf);
        var result=JsonSerializer.Deserialize<JsonElement>((string)(await read.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(connection,new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,result.Instant("authorityUntil")));
        return([..result.GetProperty("profiles").EnumerateArray().Select(p=>p.Clone())],[..result.GetProperty("accounts").EnumerateArray().Select(p=>p.Clone())]);
    }
    internal static async Task<LifecycleActionWrite> Save(NpgsqlConnection connection,PreviewDatabase database,LifecycleDeliveryProfileSave request,Action requireSource,CancellationToken ct)
    {
        var catalog=database.Manifest.CatalogId;
        if(request.Context is null||request.Profile is null)throw new MarketDataException("futures_delivery_input","Select explicit delivery terms and their original source context.");
        var body=JsonSerializer.SerializeToElement(new{request.OperationId,request.ExpectedRevision,request.Context.ContextId,
            asOf=LifecycleNative.Instant(request.Context.AsOf),request.Profile},LifecycleNative.Json);
        await using var capture=ScheduleStore.Command(connection,"SELECT security.gas_owned_admission($1,'futures_delivery_profile',$2::jsonb,false)::text",catalog,body.GetRawText());
        var authority=JsonSerializer.Deserialize<JsonElement>((string)(await capture.ExecuteScalarAsync(ct))!);
        var admission=new GasOwnedWriteAdmission(catalog,authority.Instant("until"),true,false);database.RegisterCommitAdmission(connection,admission);
        await using var status=ScheduleStore.Command(connection,"SELECT security.gas_owned_command_receipt($1,$2,'futures_delivery_profile',$3,$4::jsonb,$5,$6,NULL,NULL,false)::text",
            catalog,request.OperationId,request.Profile.AccountId,body.GetRawText(),request.Context.AsOf,request.Context.ContextId);
        string? outcome=await status.ExecuteScalarAsync(ct) as string;
        if(outcome is null)
        {
            requireSource();await LifecycleNative.Source(connection,database,request.Context,ct);
            await using var save=ScheduleStore.Command(connection,"SELECT settlement_data.save_futures_delivery_profile($1,$2::jsonb)::text",catalog,body.GetRawText());
            outcome=(string)(await save.ExecuteScalarAsync(ct))!;
        }
        var recorded=JsonSerializer.Deserialize<JsonElement>(outcome);
        database.RegisterCommitAdmission(connection,new FuturesPhysicalReadAdmission(catalog,recorded.Id("profileId"),int.Parse(recorded.Text("revision"),System.Globalization.CultureInfo.InvariantCulture),admission.Until));
        return new(recorded,admission);
    }
    internal static void Map(RouteGroupBuilder api,PreviewDatabase database,Func<HttpContext,Func<PreviewSession,Task<IResult>>,Task<IResult>> locked,
        Func<PreviewSession,string> profile,Action<PreviewSession,LifecycleReadContext> requireSource)
    {
        api.MapPost("/lifecycle/futures/delivery/configure",(HttpContext c,LifecycleDeliveryProfileSave body)=>locked(c,async session=>
        {
            var result=await database.WithIdentity(profile(session),null,(connection,ct)=>Save(connection,database,body,()=>requireSource(session,body.Context),ct),c.RequestAborted);
            return Results.Ok(result.Outcome);
        }));
    }
}
