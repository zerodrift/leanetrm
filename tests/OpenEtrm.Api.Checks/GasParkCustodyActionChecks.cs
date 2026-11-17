using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasParkCustodyActionChecks
{
    internal static void Run()
    {
        var target=new LifecycleTarget("gas_park_custody",Guid.NewGuid(),Guid.NewGuid());
        var context=new LifecycleReadContext(Guid.NewGuid(),DateTimeOffset.Parse("2032-01-16T10:00:00Z"),new(2032,1,16));
        var policy=Guid.NewGuid();var agreement=Guid.NewGuid();var receipt=Guid.NewGuid();var lot=Guid.NewGuid();
        LifecycleActionRequest Request(object selection)=>new(Guid.NewGuid(),context,target,"gas.park_custody.accept",policy,1,false,new('0',64),JsonSerializer.SerializeToElement(selection));
        var valid=Request(new{expectedVersion=7,agreementEvidenceId=agreement,receiptEvidenceId=receipt,effectiveAt="2032-01-15T10:00:00Z",allocations=new[]{new{lotId=lot,quantity="720.00"}}});
        var normalized=GasParkCustodyActions.Normalize(valid);
        if(normalized.Selection.GetProperty("allocations")[0].Text("quantity")!="720")
            throw new Exception("Reviewed custody quantities must retain one exact canonical decimal meaning.");
        var provider=Request(new{expectedVersion=7,agreementEvidenceId=agreement,receiptEvidenceId=receipt,effectiveAt="2032-01-15T10:00:00Z",allocations=Array.Empty<object>()});
        if(GasParkCustodyActions.Normalize(provider).Selection.GetProperty("allocations").GetArrayLength()!=0)
            throw new Exception("Provider custody must preserve its explicit empty owned-lot selection.");
        void Refuse(LifecycleActionRequest request)
        {
            try{GasParkCustodyActions.Normalize(request);}
            catch(MarketDataException error)when(error.Code is "gas_custody_input" or "lifecycle_input" or "lifecycle_policy"){return;}
            throw new Exception("Invalid or ambiguous custody action inputs must refuse before native execution.");
        }
        Refuse(valid with{Automatic=true});Refuse(valid with{PolicyId=Guid.Empty});Refuse(valid with{Action="gas.loan.borrow"});
        Refuse(valid with{Target=target with{Handler="gas_owned"}});
        Refuse(Request(new{expectedVersion=0,agreementEvidenceId=agreement,receiptEvidenceId=receipt,effectiveAt="2032-01-15T10:00:00Z",allocations=Array.Empty<object>()}));
        Refuse(Request(new{expectedVersion=7,agreementEvidenceId=agreement,receiptEvidenceId=receipt,effectiveAt="2032-01-15T10:00:00.0000001Z",allocations=Array.Empty<object>()}));
        Refuse(Request(new{expectedVersion=7,agreementEvidenceId=agreement,receiptEvidenceId=receipt,effectiveAt="2032-01-15T10:00:00.00000001Z",allocations=Array.Empty<object>()}));
        Refuse(Request(new{expectedVersion=7,agreementEvidenceId=agreement,receiptEvidenceId=receipt,effectiveAt="2032-01-15T10:00:00Z",allocations=(object?)null}));
        Refuse(Request(new{expectedVersion=7,agreementEvidenceId=agreement,receiptEvidenceId=receipt,effectiveAt="2032-01-15T10:00:00Z"}));
        Refuse(Request(new{expectedVersion=7,agreementEvidenceId=agreement,receiptEvidenceId=receipt,effectiveAt="2032-01-15T10:00:00Z",allocations=new[]{new{lotId=lot,quantity="1"},new{lotId=lot,quantity="2"}}}));
        Refuse(Request(new{expectedVersion=7,agreementEvidenceId=agreement,receiptEvidenceId=receipt,effectiveAt="2032-01-15T10:00:00Z",allocations=new[]{new{lotId=Guid.Empty,quantity="1"}}}));
        Refuse(Request(new{expectedVersion=7,agreementEvidenceId=agreement,receiptEvidenceId=receipt,effectiveAt="2032-01-15T10:00:00Z",allocations=new[]{new{lotId=lot,quantity="0"}}}));
        Refuse(Request(new{expectedVersion=7,agreementEvidenceId=agreement,receiptEvidenceId=receipt,effectiveAt="2032-01-15T10:00:00Z",allocations=Array.Empty<object>(),ownedAccountId=Guid.NewGuid()}));

        LifecycleActionRequest Returning(string key)=>valid with{Action="gas.park_custody.return",Selection=JsonSerializer.SerializeToElement(new{
            expectedVersion=7,businessKey=key,evidenceId=receipt,quantity="720",effectiveAt="2032-01-16T10:00:00Z",allocations=Array.Empty<object>()})};
        foreach(var key in new[]{"\u00a0",string.Concat(Enumerable.Repeat("\U0001F680",65)),string.Concat(Enumerable.Repeat("\U0001F680",128))})
        {
            var opaque=GasParkCustodyActions.Normalize(Returning(" "+key+" "));
            if(opaque.Selection.GetProperty("businessKey").GetString()!=key)
                throw new Exception("Opaque return keys retain Unicode scalar identity and trim only ASCII padding, consistently with native keys.");
        }
        Refuse(Returning(string.Concat(Enumerable.Repeat("\U0001F680",129))));
        Refuse(Returning("synthetic\nreturn"));

        var outcome=JsonSerializer.SerializeToElement(new{tradeId=target.ScopeId,instrumentId=target.BindingId,id=Guid.NewGuid(),changed=true});
        GasParkCustodyActions.RequireOutcomeTarget(target,outcome);
        void WrongTarget(LifecycleTarget expected,JsonElement value)
        {
            try{GasParkCustodyActions.RequireOutcomeTarget(expected,value);}
            catch(MarketDataException error)when(error.Code=="gas_custody_source"){return;}
            throw new Exception("A custody outcome, including first-outcome recovery, must attest both exact native target identities.");
        }
        WrongTarget(target with{BindingId=Guid.NewGuid()},outcome);
        WrongTarget(target with{ScopeId=Guid.NewGuid()},outcome);
        WrongTarget(target,JsonSerializer.SerializeToElement(new{tradeId=target.ScopeId,id=Guid.NewGuid(),changed=false}));
        WrongTarget(target,JsonSerializer.SerializeToElement(new{tradeId=target.ScopeId,instrumentId=Guid.Empty,id=Guid.NewGuid(),changed=false}));
        Console.WriteLine("PASS custody action inputs: exact manual policy, closed observed selections, explicit provider emptiness and native outcome target binding");
    }
}
