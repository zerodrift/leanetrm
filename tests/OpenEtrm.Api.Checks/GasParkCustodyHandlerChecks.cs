using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasParkCustodyHandlerChecks
{
    internal static async Task Run()
    {
        // These permission/input refusals must precede any database access.
        // An unopened connection makes an accidental authority/source read fail.
        ILifecycleActionHandler handler=new GasParkCustodyActions(null!);
        await using var unopened=new NpgsqlConnection();
        var context=new LifecycleReadContext(Guid.NewGuid(),DateTimeOffset.Parse("2032-01-16T10:00:00Z"),new(2032,1,16));
        var noCustody=new LifecycleDiscoveryAccess(ImmutableHashSet.Create(StringComparer.Ordinal,"ReadMarketData","ReadTrades"));
        var target=new LifecycleTarget("gas_park_custody",Guid.NewGuid(),Guid.NewGuid());
        if(handler.Kind!=target.Handler||!handler.Permissions.Contains("ReadGasOperations")||!handler.Permissions.Contains("RecordGasOperations")||
            handler.Permissions.Any(p=>p is "ReadGasOwnedHoldings" or "RecordGasOwnedHoldings"))
            throw new Exception("Custody handler admission must declare its own permissions without imposing customer-stock permissions on providers.");
        if(!(await handler.Discover(unopened,context,null,noCustody,default)).IsEmpty)
            throw new Exception("Optional custody discovery must stay empty without operational authority and must not block unrelated lifecycle work.");
        try
        {
            await handler.Discover(unopened,context,target,noCustody,default);
            throw new Exception("An explicit custody target must refuse missing custody authority.");
        }
        catch(PreviewDeniedException){}
        var request=new LifecycleActionRequest(Guid.NewGuid(),context,target,"gas.park_custody.accept",Guid.NewGuid(),1,true,new('0',64),
            JsonSerializer.SerializeToElement(new{expectedVersion=1,agreementEvidenceId=Guid.NewGuid(),receiptEvidenceId=Guid.NewGuid(),
                effectiveAt="2032-01-15T10:00:00Z",allocations=Array.Empty<object>()}));
        foreach(var execute in new[]{false,true})
        {
            try
            {
                if(execute)await handler.Execute(unopened,request,default);else await handler.Preview(unopened,request,default);
                throw new Exception("Automatic custody performance must refuse before reading or writing native state.");
            }
            catch(MarketDataException error)when(error.Code=="lifecycle_policy"){}
        }
        var completed=JsonNode.Parse(GasParkCustodyAdapterChecks.Agreement("provider").GetRawText())!.AsObject();
        completed["balances"]!["inCustody"]="0";
        completed["tradeReference"]=271;
        completed["bookId"]=Guid.NewGuid().ToString();
        completed["stateVersion"]=4;
        completed["policies"]=new JsonArray();
        var completedSource=JsonSerializer.SerializeToElement(completed);
        var completedContext=new LifecycleReadContext(completedSource.Id("contextId"),completedSource.Instant("asOf"),new(2032,1,16));
        var closed=GasParkCustodyActions.Offer(completedSource,completedContext);
        if(closed is null||closed.Target.ScopeId!=completedSource.Id("tradeId")||closed.Target.BindingId!=completedSource.Id("instrumentId")||
            closed.Action!="gas.park_custody.return"||closed.Eligible||closed.Reason!="gas_custody_complete"||!closed.RequiredInputs.IsEmpty)
            throw new Exception("Fully returned custody must retain a non-executable workbench target for independent surviving fee cash and observation history.");
        Console.WriteLine("PASS custody handler admission: optional discovery, explicit denied target, manual performance and completed cash workbench");
    }
}
