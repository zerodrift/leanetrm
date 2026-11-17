using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class GasParkCustodySetupChecks
{
    internal static void Run()
    {
        foreach(var perspective in new[]{"customer","provider"})
        {
            var row=GasParkCustodyAdapterChecks.Agreement(perspective);var customer=perspective=="customer";var catalog=Guid.NewGuid();
            var context=new LifecycleReadContext(row.Id("contextId"),row.Instant("asOf"),new(2032,1,16),Guid.NewGuid());
            var target=new LifecycleTarget("gas_park_custody",row.Id("tradeId"),row.Id("instrumentId"));
            var selected=JsonNode.Parse(row.GetRawText())!.AsObject();selected["stateVersion"]=9;selected["effectiveOn"]="2032-01-16";
            selected["policies"]=JsonSerializer.SerializeToNode(new[]{new{id=Guid.NewGuid(),revision=2,action="gas.park_custody.accept",mode="manual",from_at="2031-01-01T00:00:00Z",until_at="2033-01-01T00:00:00Z"},
                new{id=Guid.NewGuid(),revision=3,action="gas.park_custody.return",mode="review",from_at="2031-01-01T00:00:00Z",until_at="2033-01-01T00:00:00Z"}});
            row=JsonSerializer.SerializeToElement(selected);JsonElement? inventory=null;
            if(customer)
            {
                var terms=row.GetProperty("terms");var account=row.Id("ownedAccountId");var lot=row.GetProperty("acceptance").GetProperty("allocations")[0].Id("lotId");
                inventory=JsonSerializer.SerializeToElement(new{contextId=context.ContextId,asOf=context.AsOf,effectiveOn="2032-01-16",
                    accounts=new[]{new{id=account,kind="owned_inventory",code="synthetic_customer_stock",book_id=row.Id("bookId"),reference_catalog_id=terms.Id("referenceCatalogId"),
                        reference_revision_id=terms.Id("referenceRevisionId"),location_code=terms.Text("point"),quantity_code=terms.Text("quantity")}},
                    lots=new[]{new{id=lot,available_at="2032-01-14T10:00:00Z"}},
                    positions=new[]{new{id=lot,kind="owned_stock",account_id=account,effective_at="2032-01-14T10:00:00Z"}}});
            }
            var setup=GasParkCustodySetup.Build(catalog,context,target,row,inventory);
            if(setup.Target!=target||!setup.Accounts.IsEmpty||setup.Context!=context||setup.Forms.Count!=2||
                setup.Forms.Values.Any(f=>f.Permission!="RecordGasOperations")||setup.Policies.Length!=2||setup.Policies.Any(p=>p.AccountId!=target.ScopeId.ToString()))
                throw new Exception("Custody setup must retain the exact target/source and explicit manual policies without a provider account or blanket owned permission.");
            var evidence=setup.Configuration["evidence"];
            if(evidence.Permission!="RecordGasOperations"||evidence.Fields.Select(f=>f.Name).Order().SequenceEqual(new[]{"sourceKey","expectedRevision","kind","quantity","observedAt","evidence"}.Order()) is false||
                evidence.Fields.Single(f=>f.Name=="observedAt").Kind!="instant")
                throw new Exception("Custody observations need a target-scoped typed form with their own revision, quantity and user-clock time.");
            foreach(var action in new[]{"gas.park_custody.accept","gas.park_custody.return"})
            {
                var fields=setup.Forms[action].Fields;var version=fields.Single(f=>f.Name=="serviceVersion");
                if(version.Members is not ["expectedVersion"]||JsonSerializer.SerializeToElement(version.Choices.Single().Value).GetProperty("expectedVersion").GetInt32()!=9)
                    throw new Exception("Performance must explicitly retain the captured native agreement head.");
                var allocations=fields.Single(f=>f.Name=="allocations");
                if(allocations.Kind!="array"||allocations.MinItems!=(customer?1:0)||allocations.Fields.IsEmpty||
                    !customer&&allocations.Fields.Any(f=>!f.Choices.IsEmpty))
                    throw new Exception("Customer lot allocations are explicit and provider ownership allocations remain explicitly empty.");
            }
            var payment=setup.Configuration["payment"];
            if(payment.Permission!="AmendTrade"||payment.Fields.Any(f=>f.Name is "policyId" or "accountId"))
                throw new Exception("Independent bank payment must use the ordinary cash operation without an inventory or lifecycle policy account.");
            var owner=JsonSerializer.SerializeToElement(payment.Fields.Single(f=>f.Name=="cashOwner").Choices.Single().Value);
            if(owner.Id("tradeId")!=target.ScopeId||owner.Id("bindingId")!=row.GetProperty("cash").Id("bindingId")||owner.Id("bindingId")==target.BindingId||
                owner.Id("snapshotId")!=context.SnapshotId||owner.Text("knowledgeMode")!="current")
                throw new Exception("The ordinary payment form must retain its exact independent cash owner and selected frame.");
            var allocationsForCash=payment.Fields.Single(f=>f.Name=="allocation").Choices.Select(c=>JsonSerializer.SerializeToElement(c.Value)).ToArray();
            if(allocationsForCash.Count(v=>v.GetProperty("expectedRevision").GetInt32()==0)!=1||
                !allocationsForCash.Any(v=>v.Id("allocationId")==row.GetProperty("cash").GetProperty("payments")[0].Id("allocation_id")&&v.GetProperty("expectedRevision").GetInt32()==1)||
                payment.Fields.Single(f=>f.Name=="policy").Choices.Single().Value as string!=RecordedCashValuation.Policy)
                throw new Exception("Choose a new allocation or an exact known revision and the installed shared payment policy explicitly.");
            void Refuse(LifecycleTarget selectedTarget,JsonElement source,JsonElement? stock)
            {
                try{GasParkCustodySetup.Build(catalog,context,selectedTarget,source,stock);}
                catch(MarketDataException error)when(error.Code.StartsWith("gas_custody_",StringComparison.Ordinal)){return;}
                throw new Exception("Mismatched custody form targets or customer stock sources must refuse before disclosure.");
            }
            Refuse(target with{BindingId=Guid.NewGuid()},row,inventory);
            if(customer)
            {
                Refuse(target,row,null);
                var wrong=JsonNode.Parse(inventory!.Value.GetRawText())!.AsObject();wrong["contextId"]=Guid.NewGuid();Refuse(target,row,JsonSerializer.SerializeToElement(wrong));
            }
            else Refuse(target,row,JsonSerializer.SerializeToElement(new{accounts=Array.Empty<object>()}));
        }
        Console.WriteLine("PASS custody setup: exact source/policy/lot tuples, explicit provider emptiness, scoped observations and independent ordinary cash payment metadata");
    }
}
