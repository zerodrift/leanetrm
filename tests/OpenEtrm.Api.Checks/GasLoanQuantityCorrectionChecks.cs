using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.Shared.Temporal;

internal static class GasLoanQuantityCorrectionChecks
{
    internal static async Task Run(string path)
    {
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Loan correction checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        var context=new LifecycleReadContext(fixture.Id("contextId"),fixture.Instant("at"),new(2020,12,20),Guid.NewGuid());
        var frame=new InputFrame(context.SnapshotId!.Value,"Synthetic corrected gas loan",context.AsOf,context.BusinessDate,[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context.ContextId);
        var operations=new LifecycleOperations(database,id=>id==frame.Id?frame:throw new Exception("Loan correction requested an unrelated source frame."));
        var request=new LifecycleActionRequest(fixture.Id("operationId"),context,new("gas_loan_quantity_reconciliation",fixture.Id("accountId"),fixture.Id("loanId")),
            "gas.loan.reconcile_quantity",fixture.Id("policyId"),1,false,new('0',64),fixture.GetProperty("selection").Clone());
        var review=await database.WithIdentity("trader",null,(connection,ct)=>operations.Preview(connection,request,ct),default);
        if(!review.Details.GetProperty("willChange").GetBoolean()||review.Details.GetProperty("sourceAllocations").GetArrayLength()!=2||
            review.Details.GetProperty("outcome").Text("currentQuantity")!=fixture.Text("expectedQuantity")||
            review.Details.GetProperty("repayments").GetArrayLength()!=1||review.Details.GetProperty("fees").GetArrayLength()!=1||
            review.Details.GetProperty("payments").GetArrayLength()!=1)
            throw new Exception("Review each separate stock, debt, repayment, fixed fee and bank counterpart with complete source allocation.");
        var accepted=request with{PreviewHash=review.Hash};
        try
        {
            await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted with{PreviewHash=new('f',64)},ct),default);
            throw new Exception("Loan correction accepted an unreviewed financial proposal.");
        }
        catch(MarketDataException error)when(error.Code=="gas_quantity_stale_review"){}
        var first=await database.WithIdentity("trader",null,(connection,ct)=>operations.Execute(connection,accepted,ct),default);
        var evicted=new LifecycleOperations(database,_=>throw new Exception("A loan correction retry reloaded its evicted source frame."));
        var originalInputs=ReadReferences.Operation.Value;
        LifecycleActionWrite retry;
        ReadReferences.Operation.Value=new();
        try
        {
            retry=await database.WithIdentity("trader",null,(connection,ct)=>evicted.Execute(connection,accepted,ct),default);
            var changed=accepted.Selection.Deserialize<GasLoanQuantityReconciliationSelection>(GasOwnedOperations.Json)! with{Quantity="0.376"};
            try
            {
                await database.WithIdentity("trader",null,(connection,ct)=>evicted.Execute(connection,
                    accepted with{Selection=JsonSerializer.SerializeToElement(changed,GasOwnedOperations.Json)},ct),default);
                throw new Exception("An existing loan correction operation accepted different financial inputs.");
            }
            catch(PostgresException error)when(error.MessageText=="gas_owned_conflict"){}
            try
            {
                await database.WithIdentity("trader",null,(connection,ct)=>evicted.Execute(connection,accepted with{OperationId=Guid.NewGuid()},ct),default);
                throw new Exception("A new loan acknowledgement accepted a correlation without its explicit input coordinates.");
            }
            catch(TemporalException error)when(error.Code=="temporal_context"){}
        }
        finally{ReadReferences.Operation.Value=originalInputs;}
        if(first.Outcome.GetRawText()!=retry.Outcome.GetRawText()||!first.Outcome.GetProperty("changed").GetBoolean())
            throw new Exception("Loan correction did not retain its exact atomic first outcome.");
        var acknowledged=await database.WithIdentity("trader",null,(connection,ct)=>evicted.Execute(connection,accepted with{OperationId=Guid.NewGuid()},ct),default);
        if(acknowledged.Outcome.GetProperty("changed").GetBoolean()||!acknowledged.Outcome.GetProperty("semanticAcknowledgement").GetBoolean())
            throw new Exception("An existing exact loan correction created another financial publication.");
        Console.WriteLine("PASS managed loan correction: default registry, full independent counterparts/allocation review, same native producer, stale review and evicted-source first outcome");
    }
}
