using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

// Close-out drafts of real isolated bookings (tools/check_close_out_drafts.py). The expected marks, quantities and
// settled periods come from the Blotter, the swap editor and the recorded settlement, never from the draft read itself.
internal static class CloseOutDraftChecks
{
    static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);

    public static async Task Run(string path)
    {
        var root=Environment.GetEnvironmentVariable("OPENETRM_PREVIEW_ROOT")??throw new InvalidOperationException("Use the owned preview runner.");
        await using var database=new PreviewDatabase(root);
        using var document=JsonDocument.Parse(File.ReadAllText(path));var fixture=document.RootElement;
        var reads=new TradeReads(database);
        var passed=0;
        void Check(string name,bool valid,object? detail=null)
        {
            if(!valid)throw new Exception(name+(detail is null?"":": "+JsonSerializer.Serialize(detail,Json)));
            passed++;Console.WriteLine("PASS close-out draft: "+name);
        }
        InputFrame Frame(string name)=>fixture.GetProperty("frames").GetProperty(name).Deserialize<InputFrame>(Json)!;
        JsonElement Position(string name)=>fixture.GetProperty("positions").GetProperty(name);
        // One draft read under the trader's own authority, at a working frame or under a context captured earlier.
        Task<JsonElement> Draft(string frameName,JsonElement position,int? revision=null,string? type=null,ScheduleContext? earlier=null)=>
            database.WithIdentity("trader","ReadTrades",async(connection,ct)=>
            {
                var frame=Frame(frameName);
                var view=earlier is null?await TradingView.Capture(connection,new KnowledgeStore(database.Manifest.CatalogId),frame,ct):TradingView.Reread(frame,earlier);
                var request=new CloseOutDraftRequest(new(frame.Id,TradingBlotter.Convention,earlier is null?"current":"retained",earlier?.ContextId),
                    position.Id("tradeId"),position.Id("bindingId"),revision??position.GetProperty("revision").GetInt32(),type??position.Text("instrumentType"));
                return JsonSerializer.SerializeToElement(await reads.CloseOutDraft(connection,view,request,ct),Json);
            },CancellationToken.None);
        async Task Refused(string name,Func<Task<JsonElement>> read,string reason)
        {
            try{var draft=await read();throw new Exception(name+": a draft was returned: "+draft.GetRawText());}
            catch(MarketDataException error){Check(name,error.Code=="close_out"&&error.Message.Contains(reason,StringComparison.Ordinal),new{error.Code,error.Message});}
        }
        static string[] Strings(JsonElement array,string field)=>array.EnumerateArray().Select(e=>e.Text(field)).ToArray();

        // A long future closes with a sale of the same lots of the same contract in the same book, at the Blotter's mark.
        var future=Position("future");
        var draft=await Draft("booking",future);var terms=draft.GetProperty("terms");var price=draft.GetProperty("price");
        Check("a long 40-lot future drafts a 40-lot sale of its contract at the Blotter's mark",draft.Text("kind")=="future"&&
            terms.Text("lots")=="-40"&&terms.Text("contractId")==future.Text("contractId")&&terms.Text("curve")==future.Text("curve")&&
            terms.Text("bookId")==future.Text("bookId")&&terms.Text("executionPrice")==future.Text("mark")&&price.Text("value")==future.Text("mark")&&
            terms.Text("legalEntityId")==future.Text("legalEntityId")&&terms.Text("counterpartyId")==future.Text("counterpartyId"),draft);
        Check("the future's price names its source, unit and close",price.Text("source")=="curve_price"&&price.Text("unit")==future.Text("priceUnit")&&
            DateTimeOffset.Parse(price.Text("closeAt"))==Frame("booking").At&&!string.IsNullOrWhiteSpace(price.Text("closeLabel")),price);
        Check("the future draft carries no server-assigned booking facts or operation identity",
            !new[]{"operationId","executedAt","traderId","traderRevision","tradeDate"}.Any(key=>terms.TryGetProperty(key,out _)),terms);
        Check("the same request twice returns identical JSON",(await Draft("booking",future)).GetRawText()==draft.GetRawText());
        await Refused("a future with no mark at the pinned close is refused",()=>Draft("unpriced",future),"no curve price");

        // Revisions resolve under the knowledge cutoff: a superseded or not-yet-known revision is refused, never substituted.
        var amended=Position("amendedFuture");var earlier=fixture.GetProperty("earlierContext").Deserialize<ScheduleContext>(Json)!;
        await Refused("a superseded revision is refused",()=>Draft("booking",amended,revision:1),"not the current revision");
        Check("the current revision drafts its own lots",(await Draft("booking",amended)).GetProperty("terms").Text("lots")=="-6");
        Check("under an earlier cutoff, the revision then current drafts its own lots",
            (await Draft("booking",amended,revision:1,earlier:earlier)).GetProperty("terms").Text("lots")=="-5");
        await Refused("under an earlier cutoff, a revision recorded later is refused",()=>Draft("booking",amended,revision:2,earlier:earlier),"not the current revision");

        // A one-period pay-fixed swap closes receiving fixed on the same quantity at its mark.
        var swap=Position("swap");
        draft=await Draft("booking",swap);terms=draft.GetProperty("terms");var periods=draft.GetProperty("periods");
        Check("a pay-fixed swap drafts the receive-fixed swap of its period at the Blotter's mark",draft.Text("kind")=="financial_swap"&&
            terms.Text("fixedCoefficient")=="1"&&terms.Text("quantity")==swap.Text("quantity")&&terms.GetProperty("fixed").Text("price")==swap.Text("mark")&&
            terms.Text("bookId")==swap.Text("bookId")&&periods.GetArrayLength()==1&&periods[0].Text("price")==swap.Text("mark")&&
            periods[0].Text("quantity")==swap.Text("quantity")&&draft.GetProperty("settledPeriodsExcluded").GetInt32()==0,draft);
        Check("the swap draft carries no server-assigned facts or amendment terms",
            !new[]{"operationId","executedAt","traderId","traderRevision","tradeDate","tradeId","expectedRevision","effectiveAt","reason"}.Any(key=>terms.TryGetProperty(key,out _)),terms);

        // A scheduled swap (D214) reverses every period, each at its own mark and with its own quantity.
        var scheduled=Position("scheduled");
        draft=await Draft("booking",scheduled);terms=draft.GetProperty("terms");periods=draft.GetProperty("periods");
        var first=terms.GetProperty("scheduled").GetProperty("first").GetProperty("periods");
        Check("an unsettled scheduled swap reverses every period at each period's mark",terms.Text("firstCoefficient")=="1"&&
            Strings(periods,"from").SequenceEqual(Strings(scheduled.GetProperty("periods"),"from"))&&
            Strings(periods,"price").SequenceEqual(Strings(scheduled.GetProperty("periods"),"bookingMark"))&&
            first.EnumerateArray().Select(p=>p.GetProperty("fixed").Text("price")).SequenceEqual(Strings(scheduled.GetProperty("periods"),"bookingMark")),draft);
        Check("the scheduled swap keeps its per-period quantities",
            Strings(periods,"quantity").SequenceEqual(Strings(scheduled.GetProperty("periods"),"quantity"))&&
            Strings(first,"amount").SequenceEqual(Strings(scheduled.GetProperty("periods"),"quantity"))&&terms.GetProperty("quantity").ValueKind==JsonValueKind.Null,draft);
        await Refused("a period fixed but not settled refuses the whole draft and names that period",()=>Draft("february",scheduled),"2021-01-01");

        // Two settled periods are left untouched; the draft covers only the remaining ones.
        var settled=Position("settled");
        draft=await Draft("february",settled);terms=draft.GetProperty("terms");periods=draft.GetProperty("periods");
        var remaining=scheduled.GetProperty("periods").EnumerateArray().Where(p=>p.Text("from") is "2021-03-01" or "2021-04-01").ToArray();
        Check("a swap with two settled periods drafts only its remaining periods",draft.GetProperty("settledPeriodsExcluded").GetInt32()==2&&
            Strings(periods,"from").SequenceEqual(["2021-03-01","2021-04-01"])&&
            Strings(periods,"price").SequenceEqual(remaining.Select(p=>p.Text("februaryMark")))&&
            Strings(periods,"quantity").SequenceEqual(remaining.Select(p=>p.Text("quantity")))&&
            terms.GetProperty("scheduled").GetProperty("second").GetProperty("periods").GetArrayLength()==2,draft);
        await Refused("a period settled on one side only refuses the whole draft and names that period",()=>Draft("february",Position("partlySettled")),"2021-01-01");

        // Close-out of other instrument types is not yet defined.
        foreach(var type in new[]{"heat_rate","ppa"})
            await Refused($"a {type} position is refused",()=>Draft("booking",swap,type:type),$"Close-out drafts are not available for {type} yet.");
        Console.WriteLine($"PASS close-out drafts: {passed} checks");
    }
}
