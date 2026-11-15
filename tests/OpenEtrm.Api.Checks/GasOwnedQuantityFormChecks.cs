using System.Text.Json;
using OpenEtrm.Api;

internal static class GasOwnedQuantityFormChecks
{
    internal static void Run()
    {
        var account=Guid.NewGuid();var stock=Guid.NewGuid();var use=Guid.NewGuid();var linked=Guid.NewGuid();
        var title=Guid.NewGuid();var withdrawn=Guid.NewGuid();var delivery=Guid.NewGuid();var next=Guid.NewGuid();var policy=Guid.NewGuid();
        var context=new LifecycleReadContext(Guid.NewGuid(),DateTimeOffset.Parse("2020-12-21T16:00:00Z"),new(2020,12,21));
        var evidence=new object[]{
            new{id=title,account_id=account,source_key="synthetic_observed_title",revision=1,kind="title_receipt",quantity="1",occurred_at="2020-12-20T12:00:00Z"},
            new{id=withdrawn,account_id=account,source_key="synthetic_observed_title",revision=2,kind="title_receipt",quantity="0",occurred_at="2020-12-20T12:00:00Z"},
            new{id=delivery,account_id=account,source_key="synthetic_observed_dispatch",revision=1,kind="physical_delivery",quantity="-0.125",occurred_at="2020-12-20T14:00:00Z"}};
        var values=new Dictionary<string,object>{
            ["accounts"]=new[]{new{id=account,code="synthetic_quantity_account",kind="owned_inventory",book_name="Synthetic owned book"}},
            ["positions"]=new[]{new{id=stock,account_id=account,kind="owned_stock",origin_policy="opening_owned_stock_v1",quantity="0",financial_revision=3,
                retracted=true,evidence_id=withdrawn,effective_at="2020-12-20T12:00:00Z"}},
            ["consumptions"]=new[]{use,linked}.Select(id=>new{id,lot_id=stock,kind="delivery",quantity="0.125",financial_revision=2,retracted=false,evidence_id=delivery,
                reservation_id=(Guid?)null,effective_at="2020-12-20T14:00:00Z"}).ToArray(),
            ["lots"]=new[]{new{id=stock,title_at="2020-12-20T12:00:00Z",available_at="2020-12-20T12:00:00Z"}},
            ["evidence"]=evidence,["reservations"]=Array.Empty<object>(),["releases"]=Array.Empty<object>(),["loans"]=Array.Empty<object>(),["fees"]=Array.Empty<object>(),
            ["policies"]=new[]{new{account_id=account,action="gas.owned.reconcile_quantity",revision=1,policy_id=policy,policy_revision=1}},
            ["quantityChoiceProfile"]="ordinary_gas_quantity_choices_v1",
            ["quantityPositionChoices"]=new[]{new{positionId=stock,accountId=account,expectedRevision=3}},
            ["quantityConsumptionChoices"]=new[]{new{consumptionId=use,accountId=account,expectedRevision=2}}};
        var policies=new[]{JsonSerializer.SerializeToElement(new{id=policy,revision=1,action="gas.owned.reconcile_quantity",mode="manual",from_at="2020-01-01T00:00:00Z",until_at="2022-01-01T00:00:00Z"})};
        GasSetup Build(LifecycleTarget target)=>GasOwnedSetup.Build(context,target,JsonSerializer.SerializeToElement(values),[..policies],[],[],[],[],[],[],JsonSerializer.SerializeToElement(values),[]);
        var setup=Build(new("gas_owned_quantity_reconciliation",account,account));
        var fields=setup.Forms["gas.owned.reconcile_quantity"].Fields;
        var titles=fields.Single(f=>f.Name=="positions").Variants!.Value;
        var uses=fields.Single(f=>f.Name=="consumptions").Variants!.Value;
        if(titles is not [{Id:var titleId}]||titleId!=stock.ToString()||uses is not [{Id:var useId}]||useId!=use.ToString())
            throw new Exception("Ordinary corrections must retain retired title and exact eligible uses, excluding native-linked consumptions.");
        var member=Build(titles[0].Target);var selected=member.Forms["gas.owned.reconcile_quantity"].Fields;
        var tuple=JsonSerializer.SerializeToElement(selected.Single(f=>f.Name=="observedQuantity").Choices.Single().Value);
        if(tuple.GetProperty("positionId").GetGuid()!=stock||tuple.GetProperty("expectedRevision").GetInt32()!=3||tuple.TryGetProperty("quantity",out _))
            throw new Exception("Selecting a native title must pin its financial version without supplying a default quantity.");
        if(selected.Single(f=>f.Name=="quantity").Kind!="text"||selected.Any(f=>f.Kind=="instant")||
            selected.Single(f=>f.Name=="evidenceId").Choices is not [{Id:var document}]||document!=withdrawn.ToString())
            throw new Exception("The correction must preserve decimal text, observed clocks and the exact zero-capacity source head.");
        var row=Build(uses[0].Target).Forms["gas.owned.reconcile_quantity"].Fields;
        var useTuple=JsonSerializer.SerializeToElement(row.Single(f=>f.Name=="observedQuantity").Choices.Single().Value);
        if(useTuple.GetProperty("consumptionId").GetGuid()!=use||useTuple.GetProperty("expectedRevision").GetInt32()!=2||row.Single(f=>f.Name=="evidenceId").Choices is not [{Id:var signed}]||signed!=delivery.ToString())
            throw new Exception("Ordinary delivery correction must retain its exact use revision and signed source evidence.");
        var book=Guid.NewGuid();var physicalPolicy=Guid.NewGuid();var performance=Guid.NewGuid();var obligation=Guid.NewGuid();
        values["accounts"]=new[]{new{id=account,code="synthetic_quantity_account",kind="owned_inventory",book_id=book,book_name="Synthetic owned book"}};
        var batch=JsonSerializer.SerializeToElement(new{corrections=new[]{new{accountId=account,account="Synthetic owned account",bookId=book,positionId=obligation,
            performanceId=performance,reference="Synthetic dispatch",quantity="0.125",financialRevision=2,parentTradeReference=101,
            deliverySeries="synthetic_monthly_future",deliveryProfile="Synthetic delivery",deliveryProfileRevision=1,policyId=physicalPolicy,policyRevision=3}}});
        var mixed=GasOwnedSetup.Build(context,new("gas_quantity_bundle",account,account),JsonSerializer.SerializeToElement(values),[..policies],[],[],[],[],[],[],
            JsonSerializer.SerializeToElement(values),[],batch:batch).Forms["gas.owned.reconcile_quantity"].Fields;
        var ordinaryFields=mixed.Single(f=>f.Name=="ordinary");var physicalFields=mixed.Single(f=>f.Name=="physical");
        if(ordinaryFields.Kind!="object"||ordinaryFields.Fields.Single(f=>f.Name=="positions").Variants!.Value.Single().Target!=titles[0].Target||
            physicalFields.Kind!="object"||physicalFields.Fields.Single().Variants!.Value.Single().Target!=new LifecycleTarget("gas_futures_reconciliation",obligation,performance))
            throw new Exception("Mixed correction forms must reuse exact lazy title and physical member sources.");
        var policyChoice=mixed.Single(f=>f.Name=="physicalAccountPolicy");var policyTuple=JsonSerializer.SerializeToElement(policyChoice.Choices.Single().Value);
        if(policyChoice.Members is not ["physicalAccountId","physicalPolicyId","physicalPolicyRevision"]||
            policyTuple.GetProperty("physicalAccountId").GetGuid()!=account||policyTuple.GetProperty("physicalPolicyId").GetGuid()!=physicalPolicy||policyTuple.GetProperty("physicalPolicyRevision").GetInt32()!=3)
            throw new Exception("The physical account and saved policy revision require one explicit exact choice.");
        values["evidence"]=evidence.Append(new{id=next,account_id=account,source_key="synthetic_observed_title",revision=3,kind="title_receipt",quantity="1",occurred_at="2020-12-22T12:00:00Z"}).ToArray();
        if(Build(titles[0].Target).Forms["gas.owned.reconcile_quantity"].Fields.Single(f=>f.Name=="evidenceId").Choices.Length!=0)
            throw new Exception("A later source head cannot fall back to older evidence merely because its observed clock is in the future.");
        try{Build(titles[0].Target with{BindingId=Guid.NewGuid()});throw new Exception("An unrelated native title acquired this row's scoped metadata.");}
        catch(OpenEtrm.MarketData.MarketDataException error)when(error.Code=="gas_owned_input"){}
        Console.WriteLine("PASS ordinary correction form: exact retired title/use revisions, linked-use exclusion, zero/signed source heads, no quantity or clock defaults and strict row scope");
    }
}
