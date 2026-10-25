using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

// D219: a risk run lists each held position it leaves out with the book it is held in, so that a book's risk totals can say what
// they leave out without a join in the browser. A queued run reads its list back from the retained frame, and leaves out only
// positions held in the books its list names.
static class RiskExclusionChecks
{
    private static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    // A held row as the trade stores read it.
    private static JsonElement Held(Guid trade,Guid binding,Guid book,int reference)=>JsonSerializer.SerializeToElement(new Dictionary<string,object>
        {["trade_id"]=trade,["binding_id"]=binding,["book_id"]=book,["trade_reference"]=reference});
    // A listed position as a retained frame holds it.
    private static ExcludedPosition Listed(Guid trade,Guid binding,Guid book,string reference)=>JsonSerializer.Deserialize<ExcludedPosition>(
        JsonSerializer.Serialize(new{tradeId=trade,bindingId=binding,bookId=book,tradeReference=reference,instrumentType="future",reason="source_expired"},RiskJobs.Json),RiskJobs.Json)!;

    public static void Run()
    {
        var (trade,binding,book,otherBook)=(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid());
        var listed=Listed(trade,binding,book,"TR-000007");
        var wire=JsonSerializer.SerializeToElement(listed,RiskJobs.Json);
        Require(wire.GetProperty("bookId").GetGuid()==book&&wire.GetProperty("tradeId").GetGuid()==trade&&wire.GetProperty("bindingId").GetGuid()==binding,
            "A listed position must state the book it is held in beside its trade and binding.");
        var kept=Guid.NewGuid();
        var held=ImmutableArray.Create(Held(trade,binding,book,7),Held(kept,Guid.NewGuid(),book,8));
        Require(ExcludedPosition.Keep(held,[listed]).Select(row=>row.Id("trade_id")).SequenceEqual([kept]),
            "A run leaves out exactly the listed position, held in the book it names, and keeps the others.");
        try
        {
            ExcludedPosition.Keep(held,[Listed(trade,binding,otherBook,"TR-000007")]);
            throw new Exception("A run left out a position its list names under another book than the one it is held in.");
        }
        catch(MarketDataException error)when(error.Code=="risk_contract"){}
        Console.WriteLine("PASS risk exclusions: each listed position states its book, and a run leaves out only positions held in the books it names");
    }
}
