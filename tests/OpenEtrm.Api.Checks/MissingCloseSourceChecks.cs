using System.Text.Json;
using Npgsql;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

internal static class MissingCloseSourceChecks
{
    internal static async Task Run(PreviewDatabase database,NpgsqlConnection connection,PreviewData data,
        InputFrame complete,Guid omitted,JsonElement baseline)
    {
        var store=new FuturesStore(database);
        var frame=complete with{SourceInputs=complete.SourceInputs!.Remove(omitted)};
        JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value,TradingBlotter.Json);
        var report=Json(await store.Holdings(connection,data,frame,default));
        var before=baseline.GetProperty("holdings").EnumerateArray().ToArray();
        var after=report.GetProperty("holdings").EnumerateArray().ToArray();
        if(after.Length!=before.Length)throw new Exception("An omitted close source hid or duplicated a holding.");
        foreach(var original in before)
        {
            var held=after.Single(h=>h.GetProperty("tradeId").GetGuid()==original.GetProperty("tradeId").GetGuid());
            var missing=held.GetProperty("definitionRevisionId").GetGuid()==omitted;
            foreach(var property in original.EnumerateObject())
            {
                var value=held.GetProperty(property.Name);
                if(missing&&property.Name is "mark" or "markMinusPrice" or "executionPnl")
                {
                    if(value.ValueKind!=JsonValueKind.Null)throw new Exception("An absent source produced a value instead of an unavailable cell.");
                }
                else if(missing&&property.Name=="unavailableReason")
                {
                    if(value.GetString()!="market_inputs_missing")throw new Exception("An absent close source lost its exact valuation status.");
                }
                else if(value.GetRawText()!=property.Value.GetRawText())
                    throw new Exception($"An omitted source changed another value or a booked fact: {property.Name}.");
            }
        }
        var covered=before.Single(h=>h.GetProperty("definitionRevisionId").GetGuid()!=omitted);
        var absent=before.Single(h=>h.GetProperty("definitionRevisionId").GetGuid()==omitted);
        var total=report.GetProperty("totals").EnumerateArray().Single();
        if(total.GetProperty("executionPnl").GetString()!=covered.GetProperty("executionPnl").GetString()||
            total.GetProperty("unvaluedPositions").GetInt32()!=1)
            throw new Exception("The mixed-source total did not retain only the covered value and its exclusion count.");
        var selected=new TradePositionSelection(absent.GetProperty("tradeId").GetGuid(),absent.GetProperty("bindingId").GetGuid(),
            int.Parse(absent.GetProperty("revision").GetString()!),"future");
        var position=Json(await store.Position(connection,data,frame,selected,default));
        var emptyTotal=position.GetProperty("totals").EnumerateArray().Single();
        if(position.GetProperty("holdings").GetArrayLength()!=1||emptyTotal.GetProperty("executionPnl").ValueKind!=JsonValueKind.Null||
            emptyTotal.GetProperty("unvaluedPositions").GetInt32()!=1)
            throw new Exception("An entirely unvalued selected position became zero or disappeared.");
        var pivot=Json(await store.Blotter(connection,data,frame,new("pivot",[],[],["market","contract"],
            ["tradeCount","lots","quantity","executionPnl"],FuturesBlotter.Implementation),default));
        var unvalued=pivot.GetProperty("groups").EnumerateArray().Single(g=>g.GetProperty("market").GetString()==absent.GetProperty("curve").GetString());
        if(unvalued.GetProperty("executionPnl").ValueKind!=JsonValueKind.Null||unvalued.GetProperty("unvaluedPositions").GetInt32()!=1||
            unvalued.GetProperty("lots").GetString()!=absent.GetProperty("lots").GetString()||
            unvalued.GetProperty("quantity").GetString()!=absent.GetProperty("quantity").GetString())
            throw new Exception("An omitted-source pivot lost booked quantities or presented an unvalued amount as zero.");
        var stream=await store.BlotterRun(connection,data,frame,new("trades",[],[],[],[],null),default);
        var streamed=0;var unavailable=0;
        await foreach(var unit in stream.Units(default))
        foreach(var row in unit){streamed++;if(row["unavailableReason"]?.GetValue<string>()=="market_inputs_missing")unavailable++;}
        if(streamed!=before.Length||unavailable!=1)throw new Exception("Streaming changed missing-source coverage.");
        foreach(var invalid in new[]{complete with{SourceInputs=null},complete with{SourceInputs=complete.SourceInputs!.SetItem(omitted,
            complete.SourceInputs[omitted] with{Source=complete.SourceInputs[omitted].Source with{ReferenceRevisionId=Guid.NewGuid()}})}})
        {
            try
            {
                await store.Holdings(connection,data,invalid,default);
                throw new Exception("A malformed close source matrix was accepted as a position-level exclusion.");
            }
            catch(MarketDataException error)when(error.Code=="market_inputs"){}
        }
        var restored=Json(await store.Holdings(connection,data,complete,default));
        if(restored.GetRawText()!=baseline.GetRawText())throw new Exception("Switching back to complete inputs changed the original valuations.");
        Console.WriteLine("PASS missing close source retains futures, booked facts, blank values, explicit totals, pivots and streaming; malformed matrices still fail");
    }
}
