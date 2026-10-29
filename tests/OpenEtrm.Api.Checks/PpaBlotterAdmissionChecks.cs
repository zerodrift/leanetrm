using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

internal static class PpaBlotterAdmissionChecks
{
    internal static async Task Run()
    {
        var good=Guid.NewGuid();var missing=Guid.NewGuid();var unsupported=Guid.NewGuid();
        var instrument=Guid.NewGuid();var absent=Guid.NewGuid();var old=Guid.NewGuid();
        JsonElement Binding(Guid trade,Guid definition)=>JsonSerializer.SerializeToElement(new{trade_id=trade,binding_id=Guid.NewGuid(),instrument_id=definition});
        ImmutableArray<JsonElement> rows=[Binding(good,instrument),Binding(missing,absent),Binding(missing,instrument),Binding(unsupported,old),Binding(good,instrument)];
        var admission=new PpaBlotterAdmission();var calls=new List<Guid>();
        await admission.Definitions(rows,id=>{
            calls.Add(id);
            if(id==absent)throw new MarketDataException("side_schedule_missing","Synthetic absent artifact.");
            if(id==old)throw new MarketDataException("side_schedule_unsupported","Synthetic unsupported artifact.");
            return Task.CompletedTask;
        });
        Require(calls.Count==3&&calls.Distinct().Count()==3,"Every unique definition is admitted once, including after another definition is refused.");
        Require(admission.Includes(good)&&!admission.Includes(missing)&&!admission.Includes(unsupported),"A missing binding excludes its whole trade, while the same healthy instrument in another trade remains included.");
        Require(admission.Refusals[missing].SetEquals(["side_schedule_missing"])&&admission.Refusals[unsupported].SetEquals(["side_schedule_unsupported"]),"Retain exact rebooking codes without classifying corrupt artifacts.");
        // Two distinct ten-year quarter-hour instruments each prepare six asset
        // columns: 2,104,128 cells each. One is missing, so only the healthy
        // instrument is charged against the four-million preparation limit.
        var assets=Enumerable.Range(0,6).Select(n=>"synthetic_asset_"+n).ToArray();
        var payment=new PpaPaymentInput("monthly","day_of_following_month",null,10,null,"reject","unadjusted",null,PaymentRule.Implementation);
        var terms=new PpaTermsInput(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),new("clock",15,"2000-01-01","2010-01-01"),
            "usd","mw",false,PpaInstrument.Implementation,assets,
            [new("energy_delivery",0,"1",new("energy",new("volume",[..Enumerable.Range(0,6).Select(n=>"synthetic_source_"+n)],"generated",null,"mwh",null,[],[]),payment,"node",null,null,null)),
             new("strike_payment",0,"-1",new("fixed",new("slots",[],null,null,null,null,[new("energy_delivery",0)],[]),payment,null,new("flat","mwh","1","40",null,null,null,[]),null,null))]);
        JsonElement Sized(Guid trade,Guid id)=>JsonSerializer.SerializeToElement(new{trade_id=trade,instrument_id=id,coverage_from="2000-01-01T00:00:00Z",coverage_to="2010-01-01T00:00:00Z"});
        JsonElement[] sized=[Sized(good,instrument),Sized(missing,absent)];
        var definitions=new Dictionary<Guid,PpaTermsInput>{{instrument,terms},{absent,terms}};
        PpaPortfolio.RequirePreparationWork([sized[0]],definitions);
        try{PpaPortfolio.RequirePreparationWork(sized,definitions);throw new Exception("Strict aggregate preparation exceeded its explicit budget.");}
        catch(MarketDataException error)when(error.Code=="ppa_limit"){}
        PpaPortfolio.RequirePreparationWork(sized,definitions,admission);
        Console.WriteLine("PASS PPA report admission: excluded artifacts consume no quantity-preparation budget; strict complete scopes retain the same bound");
        Console.WriteLine("PASS PPA report admission: all unique definitions read once; missing/unsupported whole trades isolated before quantity preparation");
        foreach(var code in new[]{"side_schedule_definition","side_schedule_port","reference_pin_mismatch","interval_block_partition"})
        {
            var error=new MarketDataException(code,"Synthetic hard refusal.");
            try{await new PpaBlotterAdmission().Definitions(rows,_=>throw error);throw new Exception("A hard refusal was hidden: "+code);}
            catch(MarketDataException actual)when(ReferenceEquals(actual,error)){}
        }
        var cancelled=new OperationCanceledException();
        try{await new PpaBlotterAdmission().Definitions(rows,_=>throw cancelled);throw new Exception("Cancellation was hidden.");}
        catch(OperationCanceledException actual)when(ReferenceEquals(actual,cancelled)){}
        Console.WriteLine("PASS PPA report admission: corruption, incompatible geometry, reference pin and cancellation stay hard");
        // Independent operation counts: 40 bindings share one valid window, two
        // bindings share the uncovered window. No quantity/PV delegate is used to
        // diagnose coverage, and no successful per-trade valuation is performed.
        var first=DateTimeOffset.Parse("2031-01-01T00:00:00Z");
        var covered=new DeliveryCoverage(first,first.AddHours(1));var uncovered=new DeliveryCoverage(first.AddDays(1),first.AddDays(1).AddHours(1));
        var bad=Guid.NewGuid();
        ImmutableArray<PpaPriceDemand> demands=[..Enumerable.Range(0,40).Select(_=>new PpaPriceDemand(Guid.NewGuid(),Guid.NewGuid(),covered)),new(bad,Guid.NewGuid(),uncovered),new(bad,Guid.NewGuid(),uncovered)];
        var probes=new List<ImmutableArray<DeliveryCoverage>>();var prices=new PpaBlotterAdmission();
        await prices.Price(demands,spans=>{
            probes.Add(spans);
            if(spans.Contains(uncovered))throw new MarketDataException("interval_block_coverage","Synthetic uncovered window.");
            return Task.FromResult<PreparedCurve>(null!); // Admission owns no numerical result in this counter check.
        });
        Require(probes.Count<=4,"One combined admission, two unique coverage probes, one healthy union; never one successful preparation per trade.");
        Require(prices.Refusals.Count==1&&!prices.Includes(bad)&&demands.Where(d=>d.TradeId!=bad).All(d=>prices.Includes(d.TradeId)),"Only the uncovered window's whole trade is refused.");
        var combinedOnly=new PpaBlotterAdmission();var shared=new MarketDataException("interval_actual_missing","Synthetic combined-only refusal.");
        try{await combinedOnly.Price(demands,spans=>spans.Length>1?throw shared:Task.FromResult<PreparedCurve>(null!));throw new Exception("An unexplained shared refusal was hidden.");}
        catch(MarketDataException actual)when(ReferenceEquals(actual,shared)){}
        Console.WriteLine("PASS PPA report admission: price isolation probes distinct requested windows, preserves healthy users of the same curve, and refuses unexplained shared failures");
    }
    private static void Require(bool value,string message){if(!value)throw new Exception(message);}
}
