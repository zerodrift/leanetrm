using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;
using OpenEtrm.MarketData.Options;

internal static class HistoricalRiskContractChecks
{
    internal static void Run()
    {
        var context=new FuturesReadRequest(Guid.NewGuid(),TradingBlotter.Convention,"retained",Guid.NewGuid());
        var pricing=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesRisk.Implementation,SwapStore.Convention,
            RateConventions.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1,"estimate_until_actual_v1",FuturesOptionValuationPolicy.Release1);
        var historyPolicy=new RiskHistoryPolicy(RiskHistoryPolicy.Version1,CurveModel.NumericalPolicy,RiskHistoryPolicy.Raw,RiskHistoryPolicy.Heads,
            RiskHistoryPolicy.ExactCloses,RiskHistoryPolicy.Absolute,RiskHistoryPolicy.Complete,RiskHistoryPolicy.Fixed,2);
        var reference=new ReferenceKey(new(Guid.NewGuid(),Guid.NewGuid()),"calendar");
        var history=new PortfolioHistoryRequest(new(context,[Guid.NewGuid()],[],[Guid.NewGuid()]),pricing,historyPolicy,reference,
            "2026-01-01","2026-02-01","16:00:00","reject","reject",new('a',64));
        var policy=new HistoricalRiskPolicy(HistoricalRiskPolicy.Version1,HistoricalRiskPolicy.Numerics,HistoricalRiskPolicy.Session,
            HistoricalRiskPolicy.Sampling,HistoricalRiskPolicy.Quantile,HistoricalRiskPolicy.Tail,HistoricalRiskPolicy.Valuation,"0.99");
        var spec=new PortfolioHistoricalRequest(history,policy);
        var outer=PortfolioRiskPreview.HistoricalRequest(spec);
        PortfolioRiskPreview.ValidateHistorical(outer,spec);
        if(outer.Detail!="holdings"||!outer.Bumps.IsEmpty||outer.Parametric is not null||outer.Joint is not null||outer.Historical!=spec)
            throw new Exception("Historical admission became an FD, joint-command or partial-book envelope.");
        var normalPolicy=new ParametricRiskPolicy(ParametricRiskPolicy.Version1,ParametricRiskPolicy.Numerics,ParametricRiskPolicy.Sample,
            ParametricRiskPolicy.Centered,ParametricRiskPolicy.Mean,ParametricRiskPolicy.Session,ParametricRiskPolicy.Signed,"0.99");
        var fd=new ParametricFiniteDifference(ParametricFiniteDifference.Version1,1,new("0","0.0001"));
        var normal=new PortfolioParametricRequest(history,normalPolicy,fd);
        Reject("risk_historical_scope",()=>PortfolioRiskPreview.ValidateHistorical(outer with{Parametric=normal},spec));
        Reject("risk_historical_scope",()=>PortfolioRiskPreview.ValidateHistorical(outer with{Detail="books"},spec));
        Reject("risk_historical_scope",()=>PortfolioRiskPreview.ValidateHistorical(outer with{TradeIds=[]},spec));
        Reject("risk_historical_scope",()=>PortfolioRiskPreview.ValidateHistorical(outer,spec with{History=history with{ExpectedManifestId=null}}));
        var comparison=new HistoricalParametricComparison(normalPolicy,fd);var compared=spec with{Comparison=comparison};
        PortfolioRiskPreview.ValidateHistorical(PortfolioRiskPreview.HistoricalRequest(compared),compared);
        foreach(var bad in new[]{compared with{Comparison=comparison with{Policy=normalPolicy with{Confidence="0.95"}}},
            compared with{Comparison=comparison with{Policy=normalPolicy with{Drift=ParametricRiskPolicy.Zero}}}})
            Reject("risk_historical_comparison",()=>PortfolioRiskPreview.ValidateHistorical(PortfolioRiskPreview.HistoricalRequest(bad),bad));
        Console.WriteLine("PASS historical API contract: exclusive complete holdings envelope and explicit same-confidence/horizon sample-mean comparison");
        var legacy=JsonSerializer.Serialize(outer with{Historical=null},RiskJobs.Json);
        if(legacy.Contains("historical",StringComparison.Ordinal))throw new Exception("The absent historical method changed legacy request serialization.");
        var json=JsonSerializer.Serialize(spec,RiskJobs.Json);var valid=JsonSerializer.Deserialize<PortfolioHistoricalRequest>(json,RiskJobs.Json)!;
        if(JsonSerializer.Serialize(valid,RiskJobs.Json)!=json)throw new Exception("The historical contract does not roundtrip exactly.");
        try{JsonSerializer.Deserialize<PortfolioHistoricalRequest>(json[..^1]+",\"unknown\":true}",RiskJobs.Json);throw new Exception("Unknown historical input accepted.");}catch(JsonException){}
        Console.WriteLine("PASS historical API contract: explicit payload roundtrip, unknown-field refusal and unchanged legacy-null bytes");
        foreach(var code in new[]{"risk_parametric_numeric","price_domain","option_input","numeric_range"})
        {
            var outcome=PortfolioRiskPreview.HistoricalComparison(()=>throw new MarketDataException(code,"Outside comparison pricing domain."),default);
            if(outcome["report"] is not null||outcome["unavailableReason"]?["code"]?.GetValue<string>()!=code)
                throw new Exception("Comparison-only domain failure discarded or mislabeled the historical result.");
        }
        foreach(var code in new[]{"access_denied","risk_history_manifest","risk_parametric_delta","risk_parametric_covariance","option_context","unit_mismatch","risk_result_limit"})
            Reject(code,()=>PortfolioRiskPreview.HistoricalComparison(()=>throw new MarketDataException(code,"Fatal shared input failure."),default));
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();
            try{PortfolioRiskPreview.HistoricalComparison(()=>throw new MarketDataException("numeric_range","Cancelled."),cancelled.Token);throw new Exception("Comparison swallowed cancellation.");}
            catch(OperationCanceledException){}
        }
        Console.WriteLine("PASS historical comparison: narrow domain unavailability preserves historical risk; shared custody/coverage and cancellation remain fatal");
        var at=DateTimeOffset.Parse("2026-01-01T16:00:00Z");
        var terms=new FuturesOptionTerms(OptionRight.Call,ExactDecimal.Parse("1"),at.AddDays(30),"american","exercise_into_future");
        var tree=OptionNumericalPolicies.Require(AmericanFuturesOptionModel.Id) with{Steps=8};
        string Price(string forward)=>AmericanFuturesOptionModel.UnitPrice(terms,LatticeDecimal.Parse(forward),LatticeDecimal.Parse("0.3"),
            LatticeDecimal.Parse("0.99"),at,tree,default).ToExact().ToString();
        var exposure=new HistoricalExposure("option",Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"present_value",new(reference.Revision,"usd"),Price("1"));
        var priced=HistoricalRisk.Calculate(new('a',64),["up","down"],policy,[exposure],
            [new("up",[new("option",Price("1.05"))]),new("down",[new("option",Price("0.98"))])]);
        // Native double-down FD can leave the positive-forward tree domain although every historical row succeeds.
        var refused=PortfolioRiskPreview.HistoricalComparison(()=>Price("-1"),default);
        if(!priced.Available||refused["unavailableReason"]?["code"]?.GetValue<string>()!="option_input")
            throw new Exception("A real option FD-domain refusal discarded historical full-PV risk.");
        Console.WriteLine("PASS historical option domain: frozen-time/volatility historical tree PVs succeed while a comparison-only negative-forward FD returns unavailable");
    }
    private static void Reject(string code,Action work)
    {
        try{work();}catch(MarketDataException error)when(error.Code==code){return;}
        throw new Exception("Expected historical refusal "+code);
    }
}
