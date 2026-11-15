using System.Collections.Immutable;
using System.Text.Json;

namespace OpenEtrm.Api;

internal static partial class GasOwnedSetup
{
    private static ImmutableDictionary<string,GasForm> QuantityBundleForms(LifecycleReadContext context,LifecycleTarget target,
        JsonElement state,JsonElement known,JsonElement? batch)
    {
        if(target.ScopeId!=target.BindingId||batch is null)throw GasOwnedOperations.Error("Choose the owned account and its linked delivery correction source.");
        var account=state.GetProperty("accounts").EnumerateArray().Single(a=>a.Id("id")==target.ScopeId);
        var ordinary=QuantityForms(context,new(GasOwnedQuantityReconciliationActions.Handler,target.ScopeId,target.ScopeId),state,known)
            [GasOwnedQuantityReconciliationActions.Action];
        var rows=batch.Value.GetProperty("corrections").EnumerateArray().Where(row=>row.Id("bookId")==account.Id("book_id")&&row.OptionalId("policyId") is not null).ToImmutableArray();
        if(rows.Select(row=>row.Id("performanceId")).Distinct().Count()!=rows.Length||rows.Any(row=>Int(row,"policyRevision")<1))
            throw GasOwnedOperations.Error("Retain each exact delivery member and saved correction policy revision.");
        var policies=rows.GroupBy(row=>(Account:row.Id("accountId"),Policy:row.Id("policyId"),Revision:Int(row,"policyRevision"))).ToImmutableArray();
        if(policies.Select(group=>group.Key.Account).Distinct().Count()!=policies.Length)
            throw GasOwnedOperations.Error("The delivery correction source has conflicting saved policies for one account.");
        var policyChoices=policies.Select(group=>Choice($"{group.Key.Account}:{group.Key.Policy}:{group.Key.Revision}",
            $"{group.First().Text("account")} · correction policy revision {group.Key.Revision}",
            new{physicalAccountId=group.Key.Account,physicalPolicyId=group.Key.Policy,physicalPolicyRevision=group.Key.Revision})).ToImmutableArray();
        var members=rows.Select(row=>new GasFormVariant(row.Id("performanceId").ToString(),
            $"{row.Text("account")} · {Parent(row)} · {row.Text("reference")} · quantity {row.Text("quantity")} · financial revision {Int(row,"financialRevision")}",
            new(GasFuturesReconciliationActions.Handler,row.Id("positionId"),row.Id("performanceId")),Parent(row)){
                Action=GasFuturesReconciliationActions.Action}).ToImmutableArray();
        var form=new GasForm("Correct linked title and futures delivery","RecordGasOwnedHoldings",[
            new("ordinary","Owned title and use corrections","object",false,[],ordinary.Fields,[]),
            new("physicalAccountPolicy","Delivery account and saved correction policy","reference",false,policyChoices,[],
                ["physicalAccountId","physicalPolicyId","physicalPolicyRevision"]),
            new("physical","Futures delivery corrections","object",false,[],[
                new GasFormField("corrections","Observed delivery corrections","array",false,[],[],[]){
                    MinItems=1,MemberLabel="Observed delivery",Variants=members}],[])]);
        return ImmutableDictionary<string,GasForm>.Empty.Add(GasOwnedQuantityReconciliationActions.Action,form);
        static string Parent(JsonElement row)=>$"{TradingReference.Trade(Int(row,"parentTradeReference"))} · {row.Text("deliverySeries")} · {row.Text("deliveryProfile")} · profile revision {Int(row,"deliveryProfileRevision")}";
    }
}
