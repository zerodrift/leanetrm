using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;

namespace OpenEtrm.Api;

internal static partial class GasOwnedSetup
{
    private static async Task<GasSetup> LoanFeeSetup(NpgsqlConnection connection,PreviewDatabase database,LifecycleDiscoveryRequest request,CancellationToken ct)
    {
        var target=request.Target!;
        var state=await GasOwnedOperations.Read(connection,database,request.Context,target.ScopeId,ct);
        var source=await GasLoanFeeActions.ReadSource(connection,database,request.Context,target.BindingId,ct);
        if(source.Id("accountId")!=target.ScopeId)throw GasOwnedOperations.Error("Select this loan's exact owned account.");
        var account=state.GetProperty("accounts").EnumerateArray().Single(row=>row.Id("id")==target.ScopeId);
        var terms=source.GetProperty("terms");var revision=Int(terms,"revision");
        var policies=await PreviewDatabase.Rows(connection,"SELECT row_to_json(p)::text FROM lifecycle.action_policies p WHERE p.catalog_id=$1 AND p.action=$2 ORDER BY p.id,p.revision",
            [database.Manifest.CatalogId,GasLoanFeeActions.Action],ct);
        var choices=state.GetProperty("policies").EnumerateArray().Where(row=>row.Id("account_id")==target.ScopeId&&row.Text("action")==GasLoanFeeActions.Action)
            .OrderByDescending(row=>Int(row,"revision")).Take(1).Select(binding=>
            {
                var policy=policies.Single(row=>row.Id("id")==binding.Id("policy_id")&&Int(row,"revision")==Int(binding,"policy_revision"));
                return new GasFormPolicy(policy.Id("id").ToString(),"Manual loan fee amendment · revision "+Int(policy,"revision"),Int(policy,"revision"),GasLoanFeeActions.Action,target.ScopeId.ToString());
            }).ToImmutableArray();
        var form=new GasForm("Amend loan fee","RecordGasOwnedHoldings",[
            new("feeRevision","Current loan fee","reference",false,
                [Choice(target.BindingId+":"+revision,$"Fee {terms.Text("amount")} {account.Text("currency_code")} · revision {revision}",new{loanId=target.BindingId,expectedRevision=revision})],[],["loanId","expectedRevision"]),
            new("amount","Revised total fee (negative amount, or 0)","text",false,[],[],[]),
            new("dueAt","Revised fee due (choose Not applicable for a zero fee)","instant",true,[],[],[]),
            new("effectiveAt","Amendment effective time","instant",false,[],[],[]),
            new("implementation","Cash adjustment rule","select",false,
                [Choice(GasLoanFeeActions.Version,"Keep original cash; add dated fee adjustments",GasLoanFeeActions.Version)],[],[]),
            new("evidence","Amendment evidence","text",false,[],[],[])]);
        return new(request.Context,[Choice(target.ScopeId.ToString(),account.Text("code"),target.ScopeId)],choices,
            ImmutableDictionary<string,GasForm>.Empty.Add(GasLoanFeeActions.Action,form),ImmutableDictionary<string,GasForm>.Empty){Target=target};
    }
}
