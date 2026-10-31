using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.MarketData.Calculation;

internal static class CloseRiskChecks
{
    internal static void Run()
    {
        var json=new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var legacy=System.Text.Json.JsonSerializer.SerializeToElement(PilotCloseJobs.QualifiedPolicy,json);
        if(legacy.EnumerateObject().Count()!=9||legacy.TryGetProperty("ppaPendingActuals",out _))
            throw new Exception("The existing-close valuation policy must retain its nine-field wire contract when no portfolio PPA policy is selected.");
        var ppa=System.Text.Json.JsonSerializer.SerializeToElement(PilotCloseJobs.QualifiedPolicy with{PpaPendingActuals="estimate_until_actual_v1"},json);
        if(ppa.GetProperty("ppaPendingActuals").GetString()!="estimate_until_actual_v1")
            throw new Exception("A selected portfolio PPA policy must remain explicit in retained JSON.");
        Console.WriteLine("PASS existing-close policy wire compatibility and explicit portfolio PPA policy retention");
        CloseRiskPreflightChecks.Run();
        var fixture=new PpaAcceptanceFixture();var agreement=fixture.Physical(volume:true);
        var market=fixture.Market(fixture.From.AddDays(-1));var inputs=fixture.Prepare(market,[agreement]);
        var balances=ValueMeasures.From(inputs.Session(market.Build).Value(agreement,new(fixture.From,fixture.From.AddHours(1)),
            new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
        var report=new PortfolioValuationResult(market.Snapshot.Id,new("2031-10-31",null),PilotCloseJobs.QualifiedPolicy,[],[],[],[],[],new(0,0,0,1,0,0,0))
            {PpaMeasures=[new(Guid.NewGuid(),agreement.Currency,balances,[])],PpaReceipt=inputs.Receipt};
        var adapted=PilotCloseReports.Adapt(report);
        if(!adapted.PpaMeasures.SequenceEqual(report.PpaMeasures)||adapted.PpaReceipt!=inputs.Receipt)
            throw new Exception("Shared close report conversion dropped retained PPA receipt or book measures.");

        var factor=market.Model.QuoteFactors(market.Snapshot).First();var curve=factor.Curves.First();
        var parallel=new PortfolioRiskAxis("parallel",market.Snapshot.Definitions,new("market_quote",new("parallel",curve)),
            CurveShockCoordinate.MarketQuote,new ParallelCurveTarget(curve),factor.Unit,"Synthetic curve",[],factor);
        var contract=parallel with{Id="contract",Request=new("market_quote",new("contract",curve,Contract:factor.Contract)),Target=new ContractCurveTarget(curve,factor.Contract)};
        var tick=ExactDecimal.Parse("0.1");
        if(!PortfolioValuation.CanCountNativeWork([new(parallel,tick)])||
            PortfolioValuation.CanCountNativeWork([new(parallel,tick),new(contract,tick)])||
            PortfolioValuation.CanCountNativeWork([new(parallel with{Coordinate=CurveShockCoordinate.ComputedOutput},tick)]))
            throw new Exception("Distinct curve target scenarios sharing one quote must retain their separate workload bound.");
        var native=new ConnectedMarketEvidence(Guid.NewGuid(),new string('a',64),FuturesRisk.Implementation,[],[]);
        CloseBookRiskJobs.RequireNativeEvidence([native]);
        foreach(var unsupported in new[]{native with{Recipes=[new(Guid.NewGuid(),Guid.NewGuid(),1,"output",0,CustomCurveProjectionBinding.SupportedSelection,CustomCurveProjectionBinding.SupportedImplementation)]},
            native with{Samples=[new(Guid.NewGuid(),market.Snapshot.ValuationAt,"synthetic_source",Guid.NewGuid(),false,[],"contract_observation",true)]}})
        {
            try{CloseBookRiskJobs.RequireNativeEvidence([unsupported]);throw new Exception("A native-only close admitted unretained connected source evidence.");}
            catch(MarketDataException error)when(error.Code=="close_risk_source_evidence"){}
        }
        var revision=new DefinitionRevision(Guid.NewGuid(),Guid.NewGuid());
        var quantity=new ReferenceKey(new(revision.CatalogId,revision.RevisionId),"synthetic_quantity");
        var currency=new ReferenceKey(quantity.Revision,"synthetic_currency");
        var id=Guid.NewGuid();
        CurveRequest Dependency()=>new(new(revision,"synthetic_curve"),new(revision,Guid.NewGuid()));
        var first=new UnitPricingContract(id,"swap_binding_dispatch_v1/synthetic_v1",[Dependency()],quantity,currency);
        var second=first with{Dependencies=[Dependency()]};
        var models=PortfolioValuation.ModelContracts([first,second,first]);
        if(models.Length!=1||models[0].Dependencies.Length!=2)throw new Exception("Repeated binding models did not retain the union of exact dependencies.");
        try
        {
            PortfolioValuation.ModelContracts([first,second with{ModelVersion="conflicting_v1"}]);
            throw new Exception("A receipt silently selected one of two incompatible model versions.");
        }
        catch(MarketDataException e)when(e.Code=="portfolio_model_receipt"){}
        foreach(var inconsistent in new[]{second with{ScheduleIdentity="different_retained_manifest"},second with{SettlementProjections=
            [new(Guid.NewGuid(),new(revision,"synthetic_curve"),OpenEtrm.MarketData.Trading.PriceSettlementSelection.CurveTarget(new(quantity.Revision,"synthetic_index")))]}})
        {
            try{PortfolioValuation.ModelContracts([first,inconsistent]);throw new Exception("A receipt discarded inconsistent retained schedule or settlement evidence.");}
            catch(MarketDataException error)when(error.Code=="portfolio_model_receipt"){}
        }
        Console.WriteLine("PASS portfolio receipt canonicalizes repeated binding declarations and refuses incompatible model lineage");
        CloseRiskAdmission.RequireRowScenarios(2_000_000_000,480);
        try
        {
            CloseRiskAdmission.RequireRowScenarios(2_000_000_001,480);
            throw new Exception("An operation above two billion declared row-scenarios was admitted.");
        }
        catch(MarketDataException e)when(e.Code=="risk_size"){}
        Console.WriteLine("PASS close risk refuses declared row-scenario work before evaluation");
    }
}
