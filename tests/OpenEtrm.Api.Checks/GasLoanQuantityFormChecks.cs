using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasLoanQuantityFormChecks
{
    internal static async Task Run(string path)
    {
        using var packet=JsonDocument.Parse(File.ReadAllText(path));var fixture=packet.RootElement;
        var manifest=fixture.GetProperty("manifest").Deserialize<PreviewManifest>(LifecycleNative.Json)!;
        var host=Path.GetFullPath(fixture.Text("host"));
        if(!host.StartsWith("/private/tmp/oetrm-ref-",StringComparison.Ordinal)&&!host.StartsWith("/tmp/oetrm-ref-",StringComparison.Ordinal))
            throw new Exception("Loan form checks require an owned disposable PostgreSQL cluster.");
        var login=new NpgsqlConnectionStringBuilder{Host=host,Port=55439,Database="postgres",Username="preview_runtime",Timezone="UTC",Pooling=false};
        await using var database=new PreviewDatabase(manifest,new NpgsqlConnectionStringBuilder(login.ConnectionString){Username="preview_authenticator"},login);
        // Capture a new source after the qualified producer; guessing the new
        // financial heads under its older review source would be invalid.
        var context=await database.WithIdentity("trader",null,async(connection,ct)=>
        {
            await using var capture=ScheduleStore.Command(connection,"SELECT security.resolve_operation_context($1,$2,NULL)",manifest.CatalogId,new DateOnly(2020,12,20));
            return(Guid)(await capture.ExecuteScalarAsync(ct))!;
        },default);
        var selected=new LifecycleReadContext(context,fixture.Instant("at"),new(2020,12,20),Guid.NewGuid());
        var frame=new InputFrame(selected.SnapshotId!.Value,"Synthetic loan form source",selected.AsOf,selected.BusinessDate,[],
            ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context);
        var operations=new LifecycleOperations(database,id=>id==frame.Id?frame:throw new Exception("Loan metadata requested another source frame."));
        var target=new LifecycleTarget(GasLoanQuantityReconciliationActions.Handler,fixture.Id("accountId"),fixture.Id("loanId"));
        var failures=new List<Exception>();GasSetup? setup=null;
        try
        {
            var discovery=await database.WithIdentity("trader",null,(connection,ct)=>operations.Discover(connection,new(selected,target),ct),default);
            if(discovery.Actions.Length!=1||discovery.Actions[0].Target!=target||discovery.Actions[0].Action!=GasLoanQuantityReconciliationActions.Action)
                throw new Exception("Default lifecycle discovery omitted the exact native loan target.");
        }
        catch(Exception error){failures.Add(new Exception("Normal lifecycle loan discovery failed.",error));}
        try{setup=await database.WithIdentity("trader",null,(connection,ct)=>GasOwnedSetup.Read(connection,database,new(selected,target),ct),default);}
        catch(Exception error){failures.Add(new Exception("Normal lifecycle loan form metadata failed.",error));}
        if(failures.Count>0)throw new AggregateException("Both ordinary loan discovery and its exact form must succeed under their original source admission.",failures);
        if(setup!.Target!=target||setup.Context!=selected||!setup.Forms.TryGetValue(GasLoanQuantityReconciliationActions.Action,out var form))
            throw new Exception("The normal lifecycle setup must offer the exact selected native loan correction.");
        var observed=form.Fields.Single(f=>f.Name=="observedLoan");
        if(observed.Kind!="reference"||!observed.Members.SequenceEqual(["loanId","expectedRevision","stockExpectedRevision","returnExpectedRevision"])||observed.Choices.Length!=1)
            throw new Exception("Loan forms must retain one exact native loan/stock/return financial revision tuple.");
        foreach(var field in new[]{"quantity","titleEvidenceId","agreementEvidenceId","sourceAllocation","feeDisposition","evidence"})
            if(!form.Fields.Any(f=>f.Name==field))throw new Exception("Loan correction form omitted explicit input: "+field);
        var tuple=JsonSerializer.SerializeToElement(observed.Choices[0].Value,GasOwnedOperations.Json);
        if(tuple.Id("loanId")!=fixture.Id("loanId")||tuple.GetProperty("expectedRevision").GetInt32()<1||
            form.Fields.Single(f=>f.Name=="quantity").Kind!="text"||form.Fields.Single(f=>f.Name=="feeDisposition").Choices.Length!=1||
            JsonSerializer.SerializeToElement(form.Fields.Single(f=>f.Name=="feeDisposition").Choices[0].Value).GetString()!=GasLoanQuantityReconciliationActions.RetainFees)
            throw new Exception("Loan form inferred quantity, selected a different native loan or omitted the independent fixed-fee contract.");
        var uses=form.Fields.Single(f=>f.Name=="consumptions");
        if(uses.Kind!="array"||uses.Variants is not {} variants||variants.Length!=2||variants.Select(v=>v.Target.BindingId).Distinct().Count()!=2||
            variants.Any(v=>v.Target.ScopeId!=fixture.Id("loanId")||v.Action!=GasLoanQuantityReconciliationActions.Action))
            throw new Exception("Loan resource rows must lazy-read only exact editable native stock uses linked to this loan.");
        Console.WriteLine("PASS normal loan correction setup: exact loan/stock/debt tuple, explicit quantity/source allocation/fixed fee and scoped lazy resource rows");
    }
}
