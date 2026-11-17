using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasTransportFinancialSetupChecks
{
    internal static void Run()
    {
        var f=new GasTransportFinancialAdapterChecks.Fixture();
        JsonElement WithPolicies(JsonElement row)
        {var node=JsonNode.Parse(row.GetRawText())!.AsObject();node["policies"]=new JsonArray();node["bookName"]="Synthetic financial book";return JsonSerializer.SerializeToElement(node);}
        var source=WithPolicies(f.Row);var target=new LifecycleTarget(GasTransportFinancialActions.Handler,f.Trade,f.Instrument);
        var context=new LifecycleReadContext(f.Context,f.At,new(2035,1,15),Guid.NewGuid());
        var version=Guid.NewGuid();
        var marks=ImmutableArray.Create(new GasFormChoice("mark","Signed current value",new{observationId=source.GetProperty("mark").Id("observationId")}));
        var versions=ImmutableArray.Create(new GasFormChoice(version.ToString(),"Replacement capacity",new{capacityVersionId=version}));
        var setup=GasTransportFinancialSetup.Build(f.Catalog,context,target,source,"synthetic owner",marks,versions);
        if(!setup.Accounts.IsEmpty||setup.Target!=target||setup.OwnerId!="synthetic owner"||
            !setup.Configuration.ContainsKey("adopt_rights_mark")||!setup.Configuration.ContainsKey("adopt_capacity_version")||!setup.Configuration.ContainsKey("payment"))
            throw new Exception("Financial setup lost its exact owner/target or introduced a gas account requirement.");
        var captured=setup.Configuration["adopt_rights_mark"].Fields.Single(field=>field.Name=="financialState");
        var value=JsonSerializer.SerializeToElement(captured.Choices.Single().Value,GasOwnedOperations.Json);
        if(value.Id("financialStateId")!=f.FinancialState||value.Id("expectedPreviousMarkId")!=f.Mark||value.Text("valuationAt")!=LifecycleNative.Instant(f.At)||
            value.Text("effectiveAt")!=LifecycleNative.Instant(f.At)||!captured.Members.Contains("expectedPreviousMarkId"))
            throw new Exception("Market-value adoption lost its captured exact state, previous mark or current value instant.");
        var offer=GasTransportFinancialActions.Offer(source,context);
        if(offer.Subject is not {} subject||subject.Reference!="TR-000001"||subject.Product!="Transport"||subject.BookId!=f.Book||subject.Book!="Synthetic financial book")
            throw new Exception("The lifecycle picker must identify the retained Transport trade and its exact named book.");
        if(offer.Eligible||offer.Reason!="gas_transport_financial_not_due"||offer.Target!=target)
            throw new Exception("An expiry not yet due must remain an inspectable financial target without applying an event.");
        var closed=WithPolicies(f.Closed());var closedContext=context with{AsOf=f.At.AddDays(1),BusinessDate=new(2035,1,16)};
        var terminal=GasTransportFinancialSetup.Build(f.Catalog,closedContext,target,closed,"synthetic owner",[],[]);
        if(!terminal.Forms.IsEmpty||terminal.Configuration.Keys.Any(k=>k!="payment")||!terminal.Configuration.ContainsKey("payment"))
            throw new Exception("Closed rights must retain ordinary unpaid-fee payment controls without reopening rights or requiring a mark.");
        var zero=WithPolicies(f.Change(o=>{o["terms"]!["fixedFeeAmount"]="0";o["terms"]!["feeDueAt"]=null;o["cash"]=null;}));
        if(GasTransportFinancialSetup.Build(f.Catalog,context,target,zero,"synthetic owner",[],[]).Configuration.ContainsKey("payment"))
            throw new Exception("An explicit zero fee invented a cash owner or payment operation.");
        Reject(()=>GasTransportFinancialSetup.Build(f.Catalog,context,target with{BindingId=Guid.NewGuid()},source,"synthetic owner",marks,versions),"gas_transport_financial_source");
        var request=new LifecycleActionRequest(Guid.NewGuid(),context,target,GasTransportFinancialActions.Expire,Guid.NewGuid(),1,false,new string('0',64),
            JsonSerializer.SerializeToElement(new{expectedFinancialStateId=f.FinancialState,evidence="Synthetic reviewed expiry"}));
        var command=GasTransportFinancialActions.Command(request);
        if(command.Id("expectedFinancialStateId")!=f.FinancialState||command.Id("policyId")!=request.PolicyId||command.Text("kind")!="expire")
            throw new Exception("The shared lifecycle adapter lost the exact selected financial state and saved expiry policy.");
        Reject(()=>GasTransportFinancialActions.Command(request with{Automatic=true}),"lifecycle_policy");
        Console.WriteLine("PASS financial Transport setup/lifecycle: exact mark/state/policy pins, inspectable not-due target and independent post-expiry cash");
    }
    static void Reject(Action action,string code)
    {try{action();}catch(MarketDataException error)when(error.Code==code){return;}throw new Exception("Expected "+code);}
}
