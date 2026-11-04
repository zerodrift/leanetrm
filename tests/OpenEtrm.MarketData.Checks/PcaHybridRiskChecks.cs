using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.Shared.Numerics;

internal static class PcaHybridRiskChecks
{
    internal static PcaHybridPolicy Policy(int kept=1)=>new(PcaHybridPolicy.Version1,PcaHybridPolicy.Decomposition,
        PcaHybridPolicy.Scaling,PcaHybridPolicy.Residual,PcaHybridPolicy.Qualification,PcaHybridPolicy.Directions,
        kept,1,"0.0001","0","0.001");
    internal static void Run()
    {
        var f=new ParametricRiskChecks.Fixture();
        var generation=MonteCarloRisk.Generate(f.History,MonteCarloRiskChecks.Policy(128));
        var pca=PcaHybridRisk.Decompose(generation,Policy());
        var correlation=5/Math.Sqrt(52);
        Near(Value(pca.Eigenvalues[0]),1+correlation,2e-14);
        Near(Value(pca.Eigenvalues[1]),1-correlation,2e-14);
        Near(Value(pca.Scales[0]),2,0);Near(Value(pca.Scales[1]),Math.Sqrt(13),1e-15);
        // Analytic two-dimensional eigenvectors, independent of the solver.
        for(var row=0;row<2;row++)Near(Math.Abs(Value(pca.Loadings[row][0])),Math.Sqrt(.5),2e-14);
        if(pca.Samples.Length!=128||pca.Samples.Any(s=>Value(s.ReconstructionError)>1e-12)||
            Value(pca.ReconstructionError)>1e-12||Value(pca.OrthogonalityError)>1e-12||Value(pca.EigenResidual)>1e-12)
            throw new Exception("PCA did not reconstruct the captured covariance and every complete native simulation vector.");
        if(Value(pca.ResidualCovariance[0][0])<=0||Value(pca.ResidualCovariance[1][1])<=0)
            throw new Exception("A retained component silently discarded residual covariance.");
        if(JsonSerializer.Serialize(pca)!=JsonSerializer.Serialize(PcaHybridRisk.Decompose(generation,Policy())))
            throw new Exception("Identical PCA input changed ordering, signs or retained diagnostics.");
        Console.WriteLine("PASS PCA: independent analytic eigenpairs, full covariance/shock reconstruction, residual covariance and replay");
        var zero=new ParametricRiskChecks.Fixture(["1","2","3","4"],["7","7","7","7"]);
        var point=MonteCarloRisk.Generate(zero.History,MonteCarloRiskChecks.Policy(8) with{Drift=ParametricRiskPolicy.Mean});
        var zeros=PcaHybridRisk.Decompose(point,Policy(2));
        if(zeros.Scales.Any(s=>s!="1")||zeros.Eigenvalues.Any(s=>s!="0")||zeros.Samples.Any(s=>s.Scores[0]!="1"||s.ResidualNorm!="0"))
            throw new Exception("Constant columns or selected deterministic drift vanished from PCA coordinates.");
        var duplicate=new ParametricRiskChecks.Fixture(["10","12","12","10"],["20","22","22","20"]);
        var singular=PcaHybridRisk.Decompose(MonteCarloRisk.Generate(duplicate.History,MonteCarloRiskChecks.Policy(8)),Policy());
        Near(Value(singular.Eigenvalues[0]),2,1e-14);Near(Value(singular.Eigenvalues[1]),0,1e-14);
        Console.WriteLine("PASS PCA: zero rank, deterministic drift, constant-unit scaling and singular factors");
        // Independent equicorrelation spectrum at the complete admitted width:
        // diag2/offdiag1 => standardized eigenvalues (d+1)/2 and 1/2.
        const int width=128;
        var columns=Enumerable.Range(0,width).Select(i=>"factor_"+i).ToImmutableArray();
        var calibration=new ParametricCalibration("wide",width+1,columns,[..Enumerable.Repeat("0",width)],
            [..Enumerable.Range(0,width).Select(i=>Enumerable.Range(0,width).Select(j=>i==j?"2":"1").ToImmutableArray())],width,[],0,"full_rank",null);
        var wide=new MonteCarloGeneration("wide",MonteCarloRiskChecks.Policy(2),calibration,[],[],
            [new("mc_00000",0,[..Enumerable.Range(0,width).Select(i=>ExactDecimal.Parse((i%7-3).ToString(CultureInfo.InvariantCulture)))]),
             new("mc_00001",1,[..Enumerable.Repeat(ExactDecimal.Parse("1"),width)])],true,[]);
        var widePca=PcaHybridRisk.Decompose(wide,Policy(3));Near(Value(widePca.Eigenvalues[0]),64.5,1e-11);
        foreach(var value in widePca.Eigenvalues.Skip(1))Near(Value(value),.5,1e-12);
        if(widePca.Samples.Any(s=>Value(s.ReconstructionError)>1e-11))throw new Exception("Complete-width native reconstruction lost a factor.");
        Console.WriteLine("PASS PCA: independent analytic equicorrelation spectrum and full-vector reconstruction at 128 factors");

        var exposure=new HistoricalExposure("quadratic",Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"present_value",f.Currency,"0");
        var fullPca=PcaHybridRisk.Decompose(generation,Policy(2));
        var plan=PcaHybridRisk.PrepareFit(fullPca,[ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01")]);
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();
            try{PcaHybridRisk.PrepareFit(pca,[ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01")],cancelled.Token);throw new Exception("Cancelled fitting plan succeeded.");}
            catch(OperationCanceledException){}
        }

        try{PcaHybridRisk.PrepareFit(fullPca,[ExactDecimal.Parse("0.000000000000000001"),ExactDecimal.Parse("0.01")]);throw new Exception("Unrepresentable native half step succeeded.");}
        catch(MarketDataException error)when(error.Code=="risk_pca_hybrid_numeric"){}

        HistoricalObservation Price(string id,ImmutableArray<ExactDecimal> x)
        {
            var a=Number(x[0].ToString());var b=Number(x[1].ToString());
            return new(id,[new(exposure.Id,ExactNumberText.Format(a+b*new SignedRatio(2,1)+a*a*new SignedRatio(3,1)+a*b*new SignedRatio(2,1)+b*b*new SignedRatio(4,1)))]);
        }
        var reference=generation.Samples.Select(s=>Price(s.Id,s.Changes)).ToImmutableArray();
        var fitted=plan.Samples.Select(s=>Price(s.Id,s.Changes)).ToImmutableArray();
        var compared=PcaHybridRisk.Compare(generation,fullPca,plan,[exposure],reference,fitted,[new(exposure.Id,"valuation")]);
        if(compared.Classifications.Single().Method!="pca_delta_gamma"||compared.Partitions.Single().PortfolioFallback||
            compared.Models.Single().Gamma.Length!=2||compared.Models.Single().Delta.Length!=2)
            throw new Exception("Full retained quadratic with mixed curvature failed same-vector qualification.");
        var orderedLosses=reference.Select(row=>SignedRatio.Zero-Number(row.Values[0].Value)).Order().ToArray();
        var standalone=compared.Exposures.Single();
        var independentEs=(orderedLosses[^1]+orderedLosses[^2]*new SignedRatio(28,100))*new SignedRatio(100,128);
        if(Number(standalone.FullValueAtRisk)!=orderedLosses[^2]||Number(standalone.FullExpectedShortfall)!=independentEs)
            throw new Exception("Standalone holding risk differs from the independent 128-path inverse-CDF/fractional-tail oracle.");
        var truncated=PcaHybridRisk.Decompose(generation,Policy());
        var smallPlan=PcaHybridRisk.PrepareFit(truncated,[ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01")]);
        var fallback=PcaHybridRisk.Compare(generation,truncated,smallPlan,[exposure],reference,
            smallPlan.Samples.Select(s=>Price(s.Id,s.Changes)).ToImmutableArray(),[new(exposure.Id,"valuation")]);
        if(fallback.Classifications.Single().Method!="full_pv"||fallback.Partitions.Single().HybridValueAtRisk!=fallback.Partitions.Single().FullValueAtRisk)
            throw new Exception("Omitted nonlinear residual risk did not fall back for the whole valuation on every path.");
        Console.WriteLine("PASS hybrid: analytic cross-factor quadratic, complete-path qualification and whole-valuation residual fallback");
        // A low-variance spread can carry material risk through large deltas.
        var spreadFixture=new ParametricRiskChecks.Fixture(["0","1","3","2"],["0","1.0001","3","2.0001"]);
        var spreadGeneration=MonteCarloRisk.Generate(spreadFixture.History,MonteCarloRiskChecks.Policy(64));
        var spreadPca=PcaHybridRisk.Decompose(spreadGeneration,Policy());
        var spreadPlan=PcaHybridRisk.PrepareFit(spreadPca,[ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01")]);
        HistoricalObservation Spread(string id,ImmutableArray<ExactDecimal> x)=>new(id,[new(exposure.Id,ExactNumberText.Format((Number(x[0].ToString())-Number(x[1].ToString()))*new SignedRatio(1000000,1)))]);
        var spreadReference=spreadGeneration.Samples.Select(s=>Spread(s.Id,s.Changes)).ToImmutableArray();
        var spread=PcaHybridRisk.Compare(spreadGeneration,spreadPca,spreadPlan,[exposure],spreadReference,
            spreadPlan.Samples.Select(s=>Spread(s.Id,s.Changes)).ToImmutableArray(),[new(exposure.Id,"spread")]);
        if(spread.Classifications.Single().Method!="pca_delta_gamma"||Value(spread.Exposures.Single().MaximumResidualLinearPnl)<1||
            spread.Partitions.Single().CandidateValueAtRisk!=spread.Partitions.Single().FullValueAtRisk)
            throw new Exception("Low-variance spread delta disappeared behind explained market variance.");
        var tiedFixture=new ParametricRiskChecks.Fixture(["0","-1","-1","0"],["0","1","-1","0"]);
        var tied=PcaHybridRisk.Decompose(MonteCarloRisk.Generate(tiedFixture.History,MonteCarloRiskChecks.Policy(8)),Policy());
        if(!tied.Eigenvalues.SequenceEqual(new[]{"1","1"})||!tied.Loadings[0].SequenceEqual(new[]{"1","0"})||!tied.Loadings[1].SequenceEqual(new[]{"0","1"}))
            throw new Exception("A tied eigenspace lost deterministic canonical axes.");
        var rescaledFixture=new ParametricRiskChecks.Fixture(["10","12","12","10"],["20000000","21000000","24000000","20000000"]);
        var originalFixture=new ParametricRiskChecks.Fixture(["10","12","12","10"],["20","21","24","20"]);
        var rescaled=PcaHybridRisk.Decompose(MonteCarloRisk.Generate(rescaledFixture.History,MonteCarloRiskChecks.Policy(8)),Policy());
        var original=PcaHybridRisk.Decompose(MonteCarloRisk.Generate(originalFixture.History,MonteCarloRiskChecks.Policy(8)),Policy());
        for(var axis=0;axis<2;axis++)Near(Value(rescaled.Eigenvalues[axis]),Value(original.Eigenvalues[axis]),1e-14);
        Near(Value(rescaled.Scales[1])/Value(original.Scales[1]),1000000,1e-9);
        for(var n=0;n<8;n++)Near(Value(rescaled.Samples[n].ResidualNorm),Value(original.Samples[n].ResidualNorm),1e-13);
        Console.WriteLine("PASS PCA / hybrid: unit rescaling, tied eigenspaces and material low-variance spread risk retain every residual delta");
        // Selected sample-mean drift belongs in both the linear and quadratic terms.
        var meanFixture=new ParametricRiskChecks.Fixture(["0","1","3","2"],["0","2","1","4"]);
        var withMean=MonteCarloRisk.Generate(meanFixture.History,MonteCarloRiskChecks.Policy(64) with{Drift=ParametricRiskPolicy.Mean});
        if(withMean.Calibration.Means.All(x=>Number(x).Numerator.IsZero))throw new Exception("The drift oracle must have a nonzero mean.");
        var meanPca=PcaHybridRisk.Decompose(withMean,Policy(2));var meanPlan=PcaHybridRisk.PrepareFit(meanPca,[ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01")]);
        var mean=PcaHybridRisk.Compare(withMean,meanPca,meanPlan,[exposure],withMean.Samples.Select(s=>Price(s.Id,s.Changes)).ToImmutableArray(),
            meanPlan.Samples.Select(s=>Price(s.Id,s.Changes)).ToImmutableArray(),[new(exposure.Id,"mean")]);
        if(mean.Classifications.Single().Method!="pca_delta_gamma")throw new Exception("Mean drift was subtracted before the Taylor expansion.");
        // One bad binding falls back every binding of the shared valuation.
        var sibling=exposure with{Id="opposing",TradeId=Guid.NewGuid(),BindingId=Guid.NewGuid()};
        HistoricalObservation Opposing(HistoricalObservation row)=>row with{Values=[..row.Values,new(sibling.Id,ExactNumberText.Format(SignedRatio.Zero-Number(row.Values[0].Value)))]};
        var opposed=PcaHybridRisk.Compare(generation,truncated,smallPlan,[exposure,sibling],reference.Select(Opposing).ToImmutableArray(),
            smallPlan.Samples.Select(s=>Opposing(Price(s.Id,s.Changes))).ToImmutableArray(),[new(exposure.Id,"shared"),new(sibling.Id,"shared")]);
        if(opposed.Classifications.Single().Method!="full_pv"||opposed.Classifications.Single().ExposureIds.Length!=2||opposed.Exposures.All(e=>e.LossesPass))
            throw new Exception("Netting hid an invalid unit model across opposing bindings.");
        // The native PC vectors, rather than independently rounded directions,
        // define exact proportional half/double and combined mixed corners.
        for(var axis=0;axis<2;axis++)for(var n=0;n<2;n++)
        {
            ExactDecimal Cell(int step,int sign)=>plan.Samples.Single(s=>s.Id==$"pc/{axis}/{step}/{sign}").Changes[n];
            if(Cell(0,0)*ExactDecimal.Parse("2")!=Cell(1,0)||Cell(1,0)*ExactDecimal.Parse("2")!=Cell(2,0)||Cell(1,1)!=-Cell(1,0))
                throw new Exception("Quantization changed half/double direction geometry.");
        }
        for(var n=0;n<2;n++)if(plan.Samples.Single(s=>s.Id=="cross/0/1/1/0").Changes[n]!=plan.PcDirections[0][n]+plan.PcDirections[1][n])
            throw new Exception("Overlapping PC shifts were not combined by factor before execution.");
        Console.WriteLine("PASS hybrid: drift-inclusive quadratic, opposing-binding fallback and exact collinear / mixed native direction geometry");
        HistoricalObservation Offset(string id,ImmutableArray<ExactDecimal> x)
        {
            var a=Number(x[0].ToString());var b=Number(x[1].ToString());var large=a*new SignedRatio(1000000000000,1);
            return new(id,[new(exposure.Id,ExactNumberText.Format(large+b*b)),new(sibling.Id,ExactNumberText.Format(SignedRatio.Zero-large))]);
        }
        var offset=PcaHybridRisk.Compare(generation,truncated,smallPlan,[exposure,sibling],generation.Samples.Select(s=>Offset(s.Id,s.Changes)).ToImmutableArray(),
            smallPlan.Samples.Select(s=>Offset(s.Id,s.Changes)).ToImmutableArray(),[new(exposure.Id,"first"),new(sibling.Id,"second")]);
        if(offset.Exposures.Any(e=>!e.LossesPass)||!offset.Partitions.Single().PortfolioFallback||offset.Classifications.Any(c=>c.Method!="full_pv"))
            throw new Exception("Accumulated relative model errors escaped portfolio-level path / VaR / ES validation.");
        HistoricalObservation Kink(string id,ImmutableArray<ExactDecimal> x)=>new(id,[new(exposure.Id,ExactNumberText.Format(Abs(Number(x[0].ToString()))))]);
        var kink=PcaHybridRisk.Compare(generation,truncated,smallPlan,[exposure],generation.Samples.Select(s=>Kink(s.Id,s.Changes)).ToImmutableArray(),
            smallPlan.Samples.Select(s=>Kink(s.Id,s.Changes)).ToImmutableArray(),[new(exposure.Id,"kink")]);
        if(kink.Classifications.Single().Method!="full_pv"||!kink.Classifications.Single().Reasons.Contains("unstable_finite_differences"))
            throw new Exception("Nonsmooth gamma passed derivative stability checks.");
        var model=compared.Models.Single();
        if(!model.Delta.SequenceEqual(new[]{"1","2"})||Value(compared.Exposures.Single().MaximumAbsoluteError)>1e-8)
            throw new Exception("Analytic quadratic coefficients or all-path accuracy differ from the independent polynomial oracle.");
        for(var a=0;a<2;a++)for(var b=0;b<2;b++)
        {
            var ax=Number(plan.PcDirections[a][0].ToString());var ay=Number(plan.PcDirections[a][1].ToString());
            var bx=Number(plan.PcDirections[b][0].ToString());var by=Number(plan.PcDirections[b][1].ToString());
            var expected=(ax*bx*new SignedRatio(3,1)+ax*by+ay*bx+ay*by*new SignedRatio(4,1))*new SignedRatio(2,1);
            if(Number(model.Gamma[a][b])!=expected)throw new Exception("PC mixed gamma differs from the closed-form quadratic bilinear form.");
        }
        Console.WriteLine("PASS hybrid: independent exact quadratic coefficients, nonsmooth fallback and whole-portfolio offset error qualification");
        var failedDirection=smallPlan.Samples[0].Changes[0];
        var restricted=new JointScenarioRiskChecks.Fixture((x,_)=>x==failedDirection?
            throw new MarketDataException("price_domain","This candidate direction is outside its declared pricing domain."):x);
        var referenceSet=restricted.Set([..generation.Samples.Select(s=>restricted.Sample(s.Id,(s.Changes[0]==default?null:s.Changes[0].ToString(),s.Changes[1]==default?null:s.Changes[1].ToString())))])
            with{Id=generation.HistoryId,Version=MonteCarloRiskPolicy.Version1};
        var pricedReference=RiskEngine.CalculateMonteCarloScenarios(restricted.Prepared,referenceSet,RiskEngine.ExactNumericalPolicy);
        var fittingSet=restricted.Set([..smallPlan.Samples.Select(s=>restricted.Sample(s.Id,(s.Changes[0]==default?null:s.Changes[0].ToString(),s.Changes[1]==default?null:s.Changes[1].ToString())))])
            with{Id=generation.HistoryId,Version=MonteCarloRiskPolicy.Version1};
        try{RiskEngine.CalculateMonteCarloScenarios(restricted.Prepared,fittingSet,RiskEngine.ExactNumericalPolicy);throw new Exception("Invalid fitting-only domain succeeded.");}
        catch(MarketDataException error)when(error.Code=="price_domain"){}
        var recovered=RiskEngine.CalculateMonteCarloScenarios(restricted.Prepared,referenceSet,RiskEngine.ExactNumericalPolicy);
        if(!pricedReference.Scenarios.Select(s=>s.Groups[0].Totals[0].Values.Value.ToExactString()).SequenceEqual(recovered.Scenarios.Select(s=>s.Groups[0].Totals[0].Values.Value.ToExactString())))
            throw new Exception("A fitting-only domain failure changed the successful full-PV reference or its parent context.");
        Console.WriteLine("PASS hybrid: black-box fitting-only domain failure preserves every successful full-PV reference path and frozen parent");
        var eight=MonteCarloRisk.Generate(f.History,MonteCarloRiskChecks.Policy(8));var eightPca=PcaHybridRisk.Decompose(eight,Policy());
        var eightPlan=PcaHybridRisk.PrepareFit(eightPca,[ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01")]);
        var cancelExposure=exposure with{Id="cancel",BindingId=Guid.NewGuid()};var denominators=new List<BigInteger>();
        var next=(BigInteger.One<<2100)+1;
        for(var n=0;n<8;n++){while(denominators.Any(d=>BigInteger.GreatestCommonDivisor(d,next)!=1))next+=2;denominators.Add(next);next+=2;}
        var fractions=eight.Samples.Select((sample,n)=>new HistoricalObservation(sample.Id,
            [new(exposure.Id,"1/"+denominators[n]),new(cancelExposure.Id,"-1/"+denominators[n])])).ToImmutableArray();
        var exactCancellation=MonteCarloRisk.Calculate(eight,[exposure,cancelExposure],fractions);
        if(!exactCancellation.Available||exactCancellation.Partitions.Single().ValueAtRisk!="0")throw new Exception("The full-PV exact cancellation fixture must succeed.");
        var zeroFits=eightPlan.Samples.Select(sample=>new HistoricalObservation(sample.Id,[new(exposure.Id,"0"),new(cancelExposure.Id,"0")])).ToImmutableArray();
        try{PcaHybridRisk.Compare(eight,eightPca,eightPlan,[exposure,cancelExposure],fractions,zeroFits,[new(exposure.Id,"shared"),new(cancelExposure.Id,"shared")]);
            throw new Exception("The bounded optional standalone reduction unexpectedly succeeded.");}
        catch(MarketDataException error)when(error.Code=="risk_pca_hybrid_numeric"){}
        Console.WriteLine("PASS hybrid: optional standalone exact-arithmetic limit preserves a successful fully netted reference");

    }
    private static SignedRatio Abs(SignedRatio x)=>x.Numerator.Sign<0?new(-x.Numerator,x.Denominator):x;
    internal static SignedRatio Number(string text){var(n,d)=ExactNumberText.Parse(text);return new(n,d);}
    internal static double Value(string text){var(n,d)=ExactNumberText.Parse(text);return(double)n/(double)d;}
    internal static void Near(double actual,double expected,double tolerance)
    {if(!double.IsFinite(actual)||Math.Abs(actual-expected)>tolerance)throw new Exception($"Expected {expected:R} within {tolerance:R}, got {actual:R}.");}
}
