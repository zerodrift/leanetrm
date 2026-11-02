using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class RiskTradeScopeChecks
{
    internal static void Run()
    {
        var a=Guid.NewGuid();var b=Guid.NewGuid();var book=Guid.NewGuid();
        ImmutableArray<RiskTradeChoice> available=[new(a,book,"T000001","future"),new(b,book,"T000002","ppa")];
        if(RiskTradeScope.Select(available,null) is not null)throw new Exception("Explicit all-trades scope must preserve whole-book behavior.");
        var selected=RiskTradeScope.Select(available,[b])!;
        if(!selected.Ids.SetEquals([b])||!selected.For("ppa").SequenceEqual([b])||!selected.For("future").IsEmpty)
            throw new Exception("A selected trade scope must include only its exact IDs, grouped independently of instrument bindings.");
        void Reject(ImmutableArray<Guid> ids)
        {try{RiskTradeScope.Select(available,ids);}catch(MarketDataException e)when(e.Code=="risk_trade_scope"){return;}throw new Exception("An invalid trade selection broadened or disappeared.");}
        Reject([]);Reject([a,a]);Reject([Guid.Empty]);Reject([Guid.NewGuid()]);Reject(default);
        Reject(Enumerable.Range(0,257).Select(_=>Guid.NewGuid()).ToImmutableArray());
        try{RiskTradeScope.Select([available[0] with{Available=false}],[a]);throw new Exception("An unreadable selected identity was accepted.");}
        catch(MarketDataException e)when(e.Code=="risk_trade_scope"){}
        if(RiskJobs.ScopeFrame([book],[],null).ToJsonString()!=RiskJobs.ScopeFrame([book],[]).ToJsonString())
            throw new Exception("Whole-book retries must preserve the legacy frame shape exactly.");
        if(!RiskJobs.ScopeFrame([book],[],[a]).ContainsKey("tradeIds"))throw new Exception("Selected trades must be retained explicitly.");
        RiskJobs.RequireTradeScope(null,RiskJobs.ScopeFrame([book],[]));
        RiskJobs.RequireTradeScope([a,b],RiskJobs.ScopeFrame([book],[],[b,a]));
        foreach(var mismatch in new[]{RiskJobs.ScopeFrame([book],[]),RiskJobs.ScopeFrame([book],[],[b])})
        {try{RiskJobs.RequireTradeScope([a],mismatch);throw new Exception("A retained subset was widened or replaced.");}catch(MarketDataException e)when(e.Code=="risk_trade_scope"){}}
        // A later authorized scope cannot reuse an earlier book/context's choices.
        try{RiskTradeScope.Select([available[0]],[b]);throw new Exception("A missing selected trade became all trades.");}
        catch(MarketDataException e)when(e.Code=="risk_trade_scope"){}
        Console.WriteLine("PASS risk trade scope: explicit all versus selected, exact IDs, empty/unknown/duplicate refusal and context replacement");
    }
}
