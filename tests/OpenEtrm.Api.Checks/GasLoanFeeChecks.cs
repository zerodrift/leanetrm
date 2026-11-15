using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasLoanFeeChecks
{
    internal static async Task Run(string path,bool zeroFeePvOnly=false)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Fee amendment checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        if(zeroFeePvOnly)
        {
            await GasLoanFeePvChecks.Verify(database,fixture.Id("accountId"),fixture.Id("loanId"),fixture.Id("markId"),fixture.Instant("at"),4,6,"0","0.5","0.475");
            return;
        }
        var context=new LifecycleReadContext(fixture.Id("contextId"),fixture.Instant("at"),new(2020,12,20),Guid.NewGuid());
        var frame=new InputFrame(context.SnapshotId!.Value,"Synthetic fixed loan fee",context.AsOf,context.BusinessDate,[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context.ContextId);
        var operations=new LifecycleOperations(database,id=>id==frame.Id?frame:throw new Exception("Fee amendment requested an unrelated source frame."));
        var request=new LifecycleActionRequest(fixture.Id("operationId"),context,new("gas_loan_fee_amendment",fixture.Id("accountId"),fixture.Id("loanId")),
            "gas.loan.amend_fee",fixture.Id("policyId"),1,false,new('0',64),fixture.GetProperty("selection").Clone());
        var setup=await database.WithIdentity("trader",null,(connection,ct)=>GasOwnedSetup.Read(connection,database,new(context,request.Target),ct),default);
        var fields=setup.Forms[request.Action].Fields;
        if(setup.Configuration.Count!=0||fields.Single(field=>field.Name=="dueAt").Nullable!=true||
            fields.Single(field=>field.Name=="feeRevision").Members is not ["loanId","expectedRevision"]||
            fields.Single(field=>field.Name=="implementation").Choices.Length!=1||setup.Policies.Single().Action!=request.Action)
            throw new Exception("The fee form must retain the exact loan/revision, explicit adjustment law and nullable zero-fee date.");
        var review=await database.WithIdentity("trader",null,(connection,ct)=>operations.Preview(connection,request,ct),default);
        if(!review.Details.GetProperty("willChange").GetBoolean()||review.Details.GetProperty("outcome").GetProperty("cashChanges").GetArrayLength()!=2||
            review.Details.GetProperty("outcome").Text("currentAmount")!="-3"||review.Details.GetProperty("payments").GetArrayLength()!=2)
            throw new Exception("Review must preserve recorded bank allocations and show both timed cash changes.");
        if(review.Details.GetProperty("rootFee").GetProperty("amount").ValueKind!=JsonValueKind.String||
            review.Details.GetProperty("cash").EnumerateArray().Any(row=>row.GetProperty("amount").ValueKind!=JsonValueKind.String)||
            review.Details.GetProperty("payments").EnumerateArray().Any(row=>row.GetProperty("amount").ValueKind!=JsonValueKind.String))
            throw new Exception("Browser review amounts must retain exact backend decimal text.");
        var accepted=request with{PreviewHash=review.Hash};
        try
        {
            await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted with{PreviewHash=new('f',64)},ct),default);
            throw new Exception("Fee amendment accepted an unreviewed financial proposal.");
        }
        catch(MarketDataException error)when(error.Code=="gas_fee_stale_review"){}
        var first=await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted,ct),default);
        var evicted=new LifecycleOperations(database,_=>throw new Exception("A retained fee receipt reloaded its evicted source frame."));
        var retry=await database.WithIdentity("trader",null,(connection,ct)=>evicted.Execute(connection,accepted,ct),default);
        if(first.Outcome.GetRawText()!=retry.Outcome.GetRawText()||!first.Outcome.GetProperty("changed").GetBoolean())
            throw new Exception("Fee amendment did not retain its exact native first outcome.");
        var ack=await database.WithIdentity("trader",null,(connection,ct)=>evicted.Execute(connection,accepted with{OperationId=Guid.NewGuid()},ct),default);
        if(ack.Outcome.GetProperty("changed").GetBoolean()||!ack.Outcome.GetProperty("semanticAcknowledgement").GetBoolean())
            throw new Exception("Acknowledging an existing fee fact published another cash adjustment.");
        Console.WriteLine("PASS managed fixed loan fee: default registry, timed cash and bank review, stale hash, native producer and evicted-source first outcomes");
        await GasLoanFeePvChecks.Verify(database,fixture.Id("accountId"),fixture.Id("loanId"),fixture.Id("markId"),context.AsOf,3,5,"-3","-2.5","-2.375");
    }
}
