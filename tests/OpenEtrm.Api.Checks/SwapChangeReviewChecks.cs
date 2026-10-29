using OpenEtrm.Api;
using OpenEtrm.MarketData;

static class SwapChangeReviewChecks
{
    public static void Run()
    {
        var original = new SwapWriteRequest(Guid.NewGuid(), SwapStore.Convention, "current", Guid.NewGuid(), Guid.NewGuid(),
            "2020-12-15T16:00:00Z", "100", "-1", false, new("3", "usd", "2021-01-01", "2021-02-01"),
            new("daily_gas", "gas_index", "usd", "2021-01-01", "2021-02-01", ResetFrequency:"daily", AveragingMethod:"arithmetic", Spread:"0",Settlement:new("curve_target",null,null)),
            TradeId:Guid.NewGuid(), ExpectedRevision:1);
        var proposed = original with { Floating=original.Floating! with { AveragingMethod="observation_arithmetic" } };
        var review = SwapStore.ReviewChanges(original, proposed);
        if(review.Action!="amend_trade" || review.Changes.Length!=1 || review.Changes[0].Before!="calendar_day_arithmetic" || review.Changes[0].After!="observation_arithmetic")
            throw new Exception("An averaging change must explain the exact booked and proposed terms and require an amendment: "+System.Text.Json.JsonSerializer.Serialize(review));
        var settlement=original with{Floating=original.Floating! with{Settlement=new("override","alternate_index","forward_curve_no_basis_v1")}};
        if(!SwapStore.ReviewChanges(original,settlement).Changes.Any(change=>change.Field.Contains("settlement",StringComparison.OrdinalIgnoreCase)))
            throw new Exception("Changing the selected fixing index and its projection policy is a contractual amendment.");
        var cosmeticNumbers = original with { Quantity="100.0", Fixed=original.Fixed! with { Price="3.00" }, Floating=original.Floating! with { Spread="0.0" } };
        if(SwapStore.ReviewChanges(original,cosmeticNumbers).Changes.Length!=0)
            throw new Exception("Equivalent decimal spellings are not trade changes.");
        var projection = original with { Floating=original.Floating! with { CustomProjection=new(Guid.NewGuid(),Guid.NewGuid(),2,"output",0,"output_position_v1","custom_curve_projection_v1") } };
        var projected=SwapStore.ReviewChanges(original,projection);
        if(projected.Action!="amend_trade" || !projected.Changes.Any(c=>c.Category=="forecast_source"))
            throw new Exception("A saved projection binding cannot be accepted as a market-data-only update.");
        var operation = original with { OperationId=Guid.NewGuid(), SnapshotId=Guid.NewGuid(), Reason="Review only", KnowledgeMode="retained" };
        if(SwapStore.ReviewChanges(original,operation).Changes.Length!=0)
            throw new Exception("Request/context coordinates are not amended trade terms.");
        var reference=new ReferenceKey(new ReferenceRevision(Guid.NewGuid(),Guid.NewGuid()),"synthetic_quantity");
        var side=new SwapScheduledSideInput("period_total",reference,null,null,"period_average_times_quantity_v1",null,[new("100",original.Fixed,null)]);
        var scheduled=original with { Fixed=null,Floating=null,FixedCoefficient=null,FirstCoefficient="-1",Quantity="100",Scheduled=new("whole_term_scheduled_financial_swap_v1",side,side) };
        var memoOnly=scheduled with { Quantity=null,Memo="Corrected memo" };
        var memoReview=SwapStore.ReviewChanges(scheduled,memoOnly);
        if(memoReview.Changes.Length!=1||memoReview.Changes[0].Category!="trade_details"||memoReview.Changes[0].After!="Corrected memo")
            throw new Exception("The derived scheduled binding quantity is not an authored quantity removal.");
        var observation=new SwapObservationQuantityInput("2021-01-01","2021-01-02",new("1",reference));
        var weighted=original.Floating! with { AveragingMethod="quantity_weighted",ObservationSchedule=new("side_observation_schedule_v1",null,[observation]) };
        var weightedSide=side with { Periods=[new("100",null,weighted)] };
        var weightedBooked=scheduled with { Scheduled=scheduled.Scheduled! with { Second=weightedSide } };
        var equivalentWeighted=weightedBooked with { Quantity=null,Scheduled=weightedBooked.Scheduled! with { Second=weightedSide with { Periods=[new("100.00",null,weighted with { ObservationSchedule=weighted.ObservationSchedule! with { Quantities=[observation with { Quantity=new("1.00",reference) }] } })] } } };
        if(SwapStore.ReviewChanges(weightedBooked,equivalentWeighted).Action!="keep_booked_terms")
            throw new Exception("Equivalent scheduled observation quantities cannot offer a trade amendment.");
        Console.WriteLine("PASS amendment change review: booked/proposed averaging, exact numeric equivalence, saved projection policy and request-coordinate exclusion");
    }
}
