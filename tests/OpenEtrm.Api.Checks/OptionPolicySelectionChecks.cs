using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Trading;

// A calculation selects its option valuation in its pricing policy. The catalog states the rebuilt tree for American
// series and the Black-76 formula for European ones (D446); a request may name the strike-centred tree instead (D396),
// and nothing else.
internal static class OptionPolicySelectionChecks
{
    private const string Release1Json="{\"observationRole\":\"daily_settlement\",\"numericalPolicy\":\"american_futures_option_crr_v1\","+
        "\"scenarioPolicy\":\"option_implied_volatility_held_v1\",\"calibrationFallback\":\"option_settlement_at_intrinsic_v1\"}";
    private const string EuropeanJson="\"european\":{\"numericalPolicy\":\"european_futures_option_black76_v1\","+
        "\"calibrationFallback\":\"option_settlement_at_discounted_intrinsic_v1\"}";

    private static void Reject(string code,Action action)
    {try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
    private static void Check(string label,Action run){run();Console.WriteLine($"PASS {label}");}
    private static void Equal(string actual,string expected){if(actual!=expected)throw new Exception($"Expected {expected}; got {actual}.");}

    internal static void Run()
    {
        StatedTreeAndSelection();
        Check("Release1 still serializes to its four members, byte for byte",Release1KeepsItsBytes);
        Check("the stated policy names both models",StatedPolicyNamesBothModels);
        Check("a European series under a policy without the member is refused and named",EuropeanSeriesNeedsTheMember);
        Check("the member refuses the tree's policy and an unknown fallback, and the American member refuses the formula",MemberRefusals);
        Check("an American and a European series on one future are each calibrated by their own model",MixedBook);
        Check("a run retained under Release1 is not the current report under the stated policy and stays readable",RetainedUnderRelease1);
        Check("a retained European calibration that recomputes differently is refused",ReplayedCalibration);
    }

    private static void Release1KeepsItsBytes()
    {
        Equal(JsonSerializer.Serialize(FuturesOptionValuationPolicy.Release1,RiskJobs.Json),Release1Json);
        var read=JsonSerializer.Deserialize<FuturesOptionValuationPolicy>(Release1Json,RiskJobs.Json)!;
        if(read!=FuturesOptionValuationPolicy.Release1||read.European is not null)throw new Exception("A retained Release1 policy must read back as itself, with no European member.");
        Equal(JsonSerializer.Serialize(read,RiskJobs.Json),Release1Json);
    }

    private static void StatedPolicyNamesBothModels()
    {
        var stated=FuturesOptionValuationPolicy.Release2;
        Equal(JsonSerializer.Serialize(stated,RiskJobs.Json),Release1Json[..^1]+","+EuropeanJson+"}");
        if(JsonSerializer.Deserialize<FuturesOptionValuationPolicy>(JsonSerializer.Serialize(stated,RiskJobs.Json),RiskJobs.Json)!=stated)
            throw new Exception("The stated policy must read back as itself.");
        if(stated.Validate().Id!=OptionNumericalPolicies.CoxRossRubinstein)throw new Exception("The stated policy keeps the rebuilt tree for American series.");
        if(PortfolioRiskPreview.AccountingPricingPolicy.FuturesOptions!=stated)throw new Exception("The risk catalog, the Blotter and the trading P&L state the policy that names both models.");
        // The member is closed: an unknown or a missing member is refused in transport, never filled in.
        foreach(var member in new[]{"{\"numericalPolicy\":\"european_futures_option_black76_v1\"}",
            "{\"calibrationFallback\":\"none\"}","{\"numericalPolicy\":\"european_futures_option_black76_v1\",\"calibrationFallback\":\"none\",\"steps\":400}"})
        {
            try{_=JsonSerializer.Deserialize<FuturesOptionValuationPolicy>(Release1Json[..^1]+",\"european\":"+member+"}",RiskJobs.Json);}
            catch(JsonException){continue;}
            throw new Exception($"An incomplete or extended European member was read: {member}.");
        }
    }

    private static void EuropeanSeriesNeedsTheMember()
    {
        var (american,european)=(Mixed.Series("american_call",FuturesOptionTerms.American),Mixed.Series("european_call",FuturesOptionTerms.European));
        try{FuturesOptionValuationPolicy.Release1.NumericalFor(european);throw new Exception("Expected option_policy.");}
        catch(MarketDataException error)when(error.Code=="option_policy")
        {if(!error.Message.Contains(european.Key.Code,StringComparison.Ordinal))throw new Exception("The refusal must name the series.");}
        Reject("option_policy",()=>FuturesOptionValuationPolicy.Release1.FallbackDeclaredFor(european));
        // A policy with the member and no European series is accepted; an American series never reads the member.
        if(FuturesOptionValuationPolicy.Release1.NumericalFor(american).Id!=OptionNumericalPolicies.CoxRossRubinstein||
            FuturesOptionValuationPolicy.Release2.NumericalFor(american).Id!=OptionNumericalPolicies.CoxRossRubinstein||
            FuturesOptionValuationPolicy.Release2.NumericalFor(european).Id!=OptionNumericalPolicies.Black76)
            throw new Exception("Each style reads its own member's numerical policy.");
        var stated=FuturesOptionValuationPolicy.Release2;
        var americanOff=stated with{CalibrationFallback=FuturesOptionValuationPolicy.NoFallback};
        var europeanOff=stated with{European=stated.European! with{CalibrationFallback=FuturesOptionValuationPolicy.NoFallback}};
        if(!stated.FallbackDeclaredFor(american)||!stated.FallbackDeclaredFor(european)||americanOff.FallbackDeclaredFor(american)||!americanOff.FallbackDeclaredFor(european)||
            !europeanOff.FallbackDeclaredFor(american)||europeanOff.FallbackDeclaredFor(european))
            throw new Exception("Each style reads its own member's calibration fallback.");
    }

    private static void MemberRefusals()
    {
        var stated=FuturesOptionValuationPolicy.Release2;
        foreach(var numerical in new[]{OptionNumericalPolicies.CoxRossRubinstein,OptionNumericalPolicies.LeisenReimer,"european_futures_option_black76_v2",""})
            Reject("option_policy",()=>(stated with{European=stated.European! with{NumericalPolicy=numerical}}).Validate());
        foreach(var fallback in new[]{ImpliedVolatility.FallbackMode,"intrinsic",""})
            Reject("option_policy",()=>(stated with{European=stated.European! with{CalibrationFallback=fallback}}).Validate());
        _=(stated with{European=stated.European! with{CalibrationFallback=FuturesOptionValuationPolicy.NoFallback}}).Validate();
        // The existing members govern American series only: they name a tree and the undiscounted fallback.
        Reject("option_policy",()=>(stated with{NumericalPolicy=OptionNumericalPolicies.Black76}).Validate());
        Reject("option_policy",()=>(stated with{CalibrationFallback=ImpliedVolatility.DiscountedFallbackMode}).Validate());
        PortfolioValuation.ValidatePolicy(PortfolioRiskPreview.AccountingPricingPolicy);
        Reject("option_policy",()=>PortfolioValuation.ValidatePolicy(PortfolioRiskPreview.AccountingPricingPolicy with
            {FuturesOptions=stated with{European=new(OptionNumericalPolicies.CoxRossRubinstein,ImpliedVolatility.DiscountedFallbackMode)}}));
    }

    // One synthetic future at 3 with two calls struck at 3 on it, each settling at 0.08 six days out: one American, one
    // European. Under one policy the first is calibrated on the tree and the second by the formula.
    private static void MixedBook()
    {
        var (american,european)=Mixed.Bindings(FuturesOptionValuationPolicy.Release2);
        Equal(american.Receipt.NumericalPolicy,OptionNumericalPolicies.CoxRossRubinstein);
        Equal(european.Receipt.NumericalPolicy,OptionNumericalPolicies.Black76);
        Equal(american.Receipt.Mode,FuturesOptionPricing.SolvedMode);
        Equal(european.Receipt.Mode,FuturesOptionPricing.SolvedMode);
        // Each reprices a scenario futures price of 3.1 by its own model at its own held volatility, and the two differ.
        var (futures,discount)=(LatticeDecimal.Parse("3.1"),LatticeDecimal.Parse("0.999"));
        LatticeDecimal Held(FuturesOptionMarketBinding binding)=>((SolvedVolatility)binding.Calibration).Volatility;
        LatticeDecimal Value(FuturesOptionMarketBinding binding)=>binding.UnitPrice(futures,Mixed.At,CancellationToken.None,discount,Held(binding));
        Equal(Value(american).ToString(),AmericanFuturesOptionModel.UnitPrice(american.Instrument.Series.Terms,futures,Held(american),discount,Mixed.At,
            OptionNumericalPolicies.Require(OptionNumericalPolicies.CoxRossRubinstein),CancellationToken.None).ToString());
        Equal(Value(european).ToString(),EuropeanFuturesOptionModel.UnitPrice(european.Instrument.Series.Terms,futures,Held(european),discount,Mixed.At,
            OptionNumericalPolicies.Require(OptionNumericalPolicies.Black76)).ToString());
        if(Value(american)==Value(european)||european.TreePrices!=0||american.TreePrices==0)throw new Exception("The two series must be valued by different models.");
        // The work counted for the pair is the tree's alone: both bracket ends and each halving of the American series.
        ImmutableArray<FuturesOptionCalibrationReceipt> receipts=[american.Receipt,european.Receipt];
        if(FuturesOptionMarket.CalibrationTreePrices(receipts)!=american.Receipt.Iterations+2)throw new Exception("A calibration by the formula counts no tree price.");
        if(!ImpliedVolatility.IsFallback(ImpliedVolatility.FallbackMode)||!ImpliedVolatility.IsFallback(ImpliedVolatility.DiscountedFallbackMode)||
            ImpliedVolatility.IsFallback(FuturesOptionPricing.SolvedMode)||ImpliedVolatility.IsFallback(null))
            throw new Exception("Both styles' fallbacks are counted as fallbacks; a solved series is not.");
        Reject("option_policy",()=>Mixed.Bindings(FuturesOptionValuationPolicy.Release1));
    }

    private static void RetainedUnderRelease1()
    {
        static JsonNode Result(PortfolioValuationPolicy policy)=>new JsonObject{["inputs"]=new JsonObject{["pricingPolicy"]=JsonSerializer.SerializeToNode(policy,RiskJobs.Json)}};
        var catalog=PortfolioRiskPreview.AccountingPricingPolicy;var earlier=catalog with{FuturesOptions=FuturesOptionValuationPolicy.Release1};
        if(!PortfolioRiskPreview.UnderStatedPricingPolicy(Result(catalog))||PortfolioRiskPreview.UnderStatedPricingPolicy(Result(earlier)))
            throw new Exception("Only a run under the policy that names both models is a scope's current report.");
        // It stays a valid policy, so a retained run under it is read and replayed as it was.
        PortfolioValuation.ValidatePolicy(earlier);
        var read=JsonSerializer.Deserialize<PortfolioValuationPolicy>(JsonSerializer.Serialize(earlier,RiskJobs.Json),RiskJobs.Json)!;
        if(!RiskPricingPolicies.Same(read,earlier)||read.FuturesOptions!.European is not null)throw new Exception("A retained Release1 pricing policy must read back unchanged.");
    }

    private static void ReplayedCalibration()
    {
        var (american,european)=Mixed.Bindings(FuturesOptionValuationPolicy.Release2);
        ImmutableArray<FuturesOptionCalibrationReceipt> receipts=[american.Receipt,european.Receipt];
        var retained=JsonSerializer.SerializeToNode(receipts,RiskJobs.Json);
        RiskJobs.RequireOptionCalibrations(retained,receipts);
        RiskJobs.RequireOptionCalibrations(retained,[Mixed.Bindings(FuturesOptionValuationPolicy.Release2).American.Receipt,Mixed.Bindings(FuturesOptionValuationPolicy.Release2).European.Receipt]);
        var moved=european.Receipt with{Volatility=(LatticeDecimal.Parse(european.Receipt.Volatility!)+LatticeDecimal.Parse("0.000000000000000000000000000001")).ToString()};
        Reject("risk_option_calibration",()=>RiskJobs.RequireOptionCalibrations(retained,[american.Receipt,moved]));
        Reject("risk_option_calibration",()=>RiskJobs.RequireOptionCalibrations(retained,[american.Receipt,european.Receipt with{NumericalPolicy=OptionNumericalPolicies.CoxRossRubinstein}]));
        Reject("risk_option_calibration",()=>RiskJobs.RequireOptionCalibrations(retained,[american.Receipt]));
        Reject("risk_option_calibration",()=>RiskJobs.RequireOptionCalibrations(null,receipts));
        Reject("risk_option_calibration",()=>RiskJobs.RequireOptionCalibrations(new JsonObject(),receipts));
    }

    private static class Mixed
    {
        private static Guid G(int n)=>Guid.Parse($"00000000-0000-0000-0000-{n:000000000000}");
        private static readonly DefinitionRevision Revision=new(G(901),G(902));
        private static readonly ReferenceRevision Reference=new(G(903),G(904));
        internal static readonly DateTimeOffset At=new(2040,12,18,0,0,0,TimeSpan.Zero);
        private static readonly DateTimeOffset Expiry=new(2040,12,24,19,30,0,TimeSpan.Zero);
        private static readonly ExactDecimal Tick=ExactDecimal.Parse("0.001"),One=ExactDecimal.Parse("1"),Lot=ExactDecimal.Parse("10000");
        private static ReferenceKey R(string code)=>new(Reference,code);
        private static ContractKey K(int n)=>new(Revision,G(n));
        private static SeriesKey S(int n)=>new(Revision,G(500+n));

        internal static FuturesOptionSeriesSpecification Series(string code,string style)=>new(R(code),R("option"),R("future"),
            new(OptionRight.Call,ExactDecimal.Parse("3"),Expiry,style,"exercise_into_future"),Lot,R("usd"),R("contract"),Tick,"synthetic_auto_exercise_v1",false,null,
            FuturesOptionInstrument.PremiumPaidUpFront);

        internal static (FuturesOptionMarketBinding American,FuturesOptionMarketBinding European) Bindings(FuturesOptionValuationPolicy policy)
        {
            QuoteSpecification Quote(string code)=>new(R(code),QuoteKind.Outright,R("usd"),R("mmbtu"),One,Tick,Tick,ExactDecimal.Parse("0"),ExactDecimal.Parse("1000"));
            var lastTrading=new DateTimeOffset(2040,12,28,19,30,0,TimeSpan.Zero);
            var underlying=new FuturesSeriesSpecification(R("future"),R("mmbtu"),R("usd"),One,Tick,R("hub"),R("uniform"),new(2041,1,1),new(2041,2,1),lastTrading,
                R("mmbtu"),Lot,LotBasis.FixedTotal,new DateTimeOffset(2040,1,2,0,0,0,TimeSpan.Zero),"cash");
            Curve Direct(string code,string quote,ImmutableArray<ContractKey> members)=>new(new(Revision,code),R(quote),R("hub"),R("uniform"),
                new(SettlementKind.FutureSeries,null,null),CurveRule.Direct,members,[],ObservationRole.DailySettlement,R("exchange"),null);
            MarketContract Listed(int n,string series)=>new(K(n),ContractKind.FutureOption,new(2041,1,1),new(2041,2,1),Expiry,R(series));
            var model=new CurveModel(Revision,Reference,
                [new(R("mmbtu"),R("gas"),R("energy_basis"),R("mmbtu_unit"),R("energy")),new(R("contract"),R("gas"),R("contract_basis"),R("contract_unit"),R("count"))],
                [Quote("future_quote"),Quote("option_quote")],
                [new(K(1),ContractKind.Future,new(2041,1,1),new(2041,2,1),lastTrading,R("future")),Listed(2,"american_call"),Listed(3,"european_call")],
                [Direct("future","future_quote",[K(1)]),Direct("option_settle","option_quote",[K(2),K(3)])],[underlying],[],
                [new(S(1),K(1),R("future_quote"),null),new(S(2),K(2),R("option_quote"),null),new(S(3),K(3),R("option_quote"),null)]);
            SelectedObservation Observed(int series,string value)=>new(new(S(series),InputUse.Variable),new(new(Revision.CatalogId,G(700+series)),S(series),R("exchange"),
                ObservationRole.DailySettlement,PublicationState.Final,At,ExactDecimal.Parse(value)));
            var snapshot=new MarketSnapshot(G(800),Revision,At,At,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[Observed(1,"3"),Observed(2,"0.08"),Observed(3,"0.08")]);
            var options=new[]{new FuturesOptionInstrument(Series("american_call",FuturesOptionTerms.American)),new FuturesOptionInstrument(Series("european_call",FuturesOptionTerms.European))};
            var requests=options.Select(o=>FuturesOptionPricing.Requests(model,o.Series,policy.ObservationRole)).ToArray();
            var basis=model.Build(snapshot,[..requests.SelectMany(r=>new[]{r.Underlying,r.Settlement}).Distinct()]);
            var discounting=new ExplicitDiscounting(ExactDecimal.Parse("0.999"),DiscountFactorSource.Explicit);
            var bound=options.Select(o=>FuturesOptionPortfolio.Bind(o,basis,snapshot,discounting,policy,CancellationToken.None)).ToArray();
            return(bound[0],bound[1]);
        }
    }

    private static void StatedTreeAndSelection()
    {
        var stated=FuturesOptionValuationPolicy.Release2;
        if(stated.NumericalPolicy!=OptionNumericalPolicies.CoxRossRubinstein||stated.Validate() is not {Lattice:OptionLattice.RebuiltAroundFutures,Steps:400})
            throw new Exception("The stated option policy is the tree rebuilt around the futures price at 400 steps.");
        var centred=stated with{NumericalPolicy=OptionNumericalPolicies.LeisenReimer};
        if(centred.Validate() is not {Id:"american_futures_option_leisen_reimer_v1",Lattice:OptionLattice.CentredOnStrike,Steps:401})
            throw new Exception("A request may select the strike-centred tree at 401 steps by its policy id.");
        // The rest of the selection stands as it was, and nothing fills in for a name that is not a policy.
        Reject("option_numerical_policy",()=>(stated with{NumericalPolicy="american_futures_option_leisen_reimer_v2"}).Validate());
        Reject("option_numerical_policy",()=>(stated with{NumericalPolicy=""}).Validate());
        Reject("option_policy",()=>(centred with{ScenarioPolicy="option_implied_volatility_resolved_v1"}).Validate());
        Reject("option_policy",()=>(centred with{CalibrationFallback=""}).Validate());
        // Selecting it is part of the whole pricing policy a risk request states.
        PortfolioValuation.ValidatePolicy(PortfolioRiskPreview.AccountingPricingPolicy with{FuturesOptions=centred});
        // A job read says whether its run was valued under the pricing policy the catalog states now, so that a page opens a
        // retained run as a scope's current report only then. A result that states no policy is not under the stated one.
        static JsonNode Result(PortfolioValuationPolicy policy)=>new JsonObject{["inputs"]=new JsonObject{["pricingPolicy"]=JsonSerializer.SerializeToNode(policy,RiskJobs.Json)}};
        var catalog=PortfolioRiskPreview.AccountingPricingPolicy;
        if(!PortfolioRiskPreview.UnderStatedPricingPolicy(Result(catalog)))throw new Exception("A run under the catalog's pricing policy is under the stated policy.");
        foreach(var other in new[]{catalog with{FuturesOptions=centred},catalog with{FuturesOptions=stated with{CalibrationFallback=FuturesOptionValuationPolicy.NoFallback}},
            catalog with{FuturesOptions=null},catalog with{BumpTicks=2}})
            if(PortfolioRiskPreview.UnderStatedPricingPolicy(Result(other)))throw new Exception("A run under another pricing policy is not under the stated policy.");
        foreach(var unstated in new JsonNode?[]{null,new JsonObject(),new JsonObject{["inputs"]=new JsonObject()},new JsonObject{["inputs"]=new JsonObject{["pricingPolicy"]=null}}})
            if(PortfolioRiskPreview.UnderStatedPricingPolicy(unstated))throw new Exception("A result that states no pricing policy is not under the stated policy.");
        Console.WriteLine("PASS option policy selection: the stated policy is the rebuilt tree; a request may name the strike-centred tree; any other name is refused; a job read says whether its run used the stated pricing policy.");
    }
}
