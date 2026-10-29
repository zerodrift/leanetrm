using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

static class TradePositionFactsChecks
{
    internal static void Run()
    {
        var first=Guid.Parse("00000000-0000-0000-0000-000000000001");
        var second=Guid.Parse("00000000-0000-0000-0000-000000000002");
        TradePositionFact Position(Guid binding,string reference)=>new(Guid.NewGuid(),binding,2,reference,Guid.NewGuid(),"Synthetic book",
            "ppa",null,"2031-11-01T04:00:00Z","2031-12-01T05:00:00Z","80","MW");
        ImmutableArray<(int Reference,TradePositionFact Position)> values=[(1000000,Position(first,"TR-1000000")),
            (999999,Position(second,"TR-999999")),(999999,Position(first,"TR-999999"))];
        var page=TradePositionFacts.Page(values,0,2);
        if(page.Total!=3||page.NextOffset!=2||page.Positions.Select(p=>p.BindingId).SequenceEqual(new[]{first,second}) is false||
            page.Positions.Any(p=>p.TradeReference!="TR-999999"))
            throw new Exception("Fact pages must follow retained numeric trade references and binding IDs, independent of query or label order.");
        var last=TradePositionFacts.Page(values,2,2);
        if(last.Positions.Length!=1||last.Positions[0].TradeReference!="TR-1000000"||last.NextOffset is not null||
            !TradePositionFacts.Page(values,3,2).Positions.IsEmpty)
            throw new Exception("Fact paging must count bindings and terminate without fabricating positions.");
        foreach(var(offset,limit)in new[]{(-1,1),(0,0),(0,65)})
        {
            try{TradePositionFacts.Page(values,offset,limit);throw new Exception("Accepted an unbounded facts page.");}
            catch(MarketDataException error)when(error.Code=="position_page"){}
        }
        Console.WriteLine("PASS position facts: stable numeric-reference/binding paging and explicit page bounds");
    }
}
