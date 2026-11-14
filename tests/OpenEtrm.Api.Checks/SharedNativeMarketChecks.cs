using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

internal static class SharedNativeMarketChecks
{
    internal static void Run()
    {
        var a=PreviewDataChecks.Fixture(2);var b=PreviewDataChecks.Fixture(2);
        a.References["units"]=[JsonSerializer.SerializeToElement(new{code="mwh",symbol="MWh"})];
        foreach(var f in new[]{a,b})f.Definitions["curves"]=[..f.Definitions["curves"].Select(r=>Change(r,"name","Synthetic "+r.Text("code")))];
        var manifestB=a.Manifest with{RevisionId=b.Manifest.RevisionId};
        b.Definitions["revisions"]=[JsonSerializer.SerializeToElement(new{catalog_id=manifestB.CatalogId,id=manifestB.RevisionId,
            reference_catalog_id=manifestB.ReferenceCatalogId,reference_revision_id=manifestB.ReferenceRevisionId,state="sealed"})];
        var dataA=new PreviewData(a.Manifest,a.Definitions,a.References,a.Observations);
        var dataB=new PreviewData(manifestB,b.Definitions,a.References,b.Observations.SetItem(0,Change(b.Observations[0],"value","103")));
        var context=Guid.NewGuid();var sourceA=dataA.Snapshot(a.Frame).WithKnowledge(context,a.Frame.At);var sourceB=dataB.Snapshot(b.Frame).WithKnowledge(context,b.Frame.At);
        var resolved=new ResolvedCalculationContext(context,"synthetic-common-native-inputs",sourceA.ValuationAt,sourceA.Knowledge!.KnownAt,sourceA.ValuationAt,sourceA.MarketCutoff);
        var requestsA=dataA.Contracts.Select(c=>new CurveRequest(new(dataA.DefinitionRevision,"future"),c.Key)).ToImmutableArray();
        var requestB=new CurveRequest(new(dataB.DefinitionRevision,"future"),dataB.Contracts[0].Key);
        var exportA=dataA.Model.Compile(sourceA,requestsA,false,resolved);var exportB=dataB.Model.Compile(sourceB,[requestB],false,resolved);
        var pointA=exportA.Points[(requestsA[0],InputUse.Variable)];var pointB=exportB.Points[(requestB,InputUse.Variable)];
        var planA=ConnectedCurves.Compile(exportA.Definitions,[..exportA.Points.Values],resolved,new Visible());
        var aInB=dataA.Model.Compile(sourceA,[requestsA[0]],false,resolved);
        var planB=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(exportB.Definitions.Concat(aInB.Definitions)),[pointB,pointA],resolved,new Visible());
        var book=Guid.NewGuid();
        ValuationPortfolio Hold(Instrument instrument)=>ValuationPortfolio.FromTrades([new(Guid.NewGuid(),book,[new(Guid.NewGuid(),instrument,new(ExactDecimal.Parse("40"),instrument.UnitQuantity.Specification))])],
            new(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null));
        var unit=planA.Points[pointA].Unit;
        var vm=Hold(new GasCommodityHoldingInstrument(Guid.NewGuid(),requestsA[0],unit.Quantity,unit.Currency,null,GasHoldingCarrying.Nominal));
        var terms=new GasPhysicalDeliveryTerms(Guid.NewGuid(),new(dataA.ReferenceRevision,"synthetic_0"),unit.Quantity,unit.Currency,new(dataA.ReferenceRevision,"hub"),
            sourceA.ValuationAt.AddDays(3),sourceA.ValuationAt.AddDays(4),sourceA.ValuationAt.AddDays(5),requestB,GasHoldingCarrying.Nominal,GasPhysicalDeliveryTerms.Version);
        var basis=Hold(GasUnfixedPhysicalDeliveryInstrument.Create(terms,requestsA[0],unit));
        var connectedA=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(ConnectedScenario.Base(planA),sourceA),
            exportA.Points.ToImmutableDictionary(p=>p.Key.Request,p=>p.Value),[vm]);
        var connectedB=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(ConnectedScenario.Base(planB),sourceB,[sourceA]),
            ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(requestB,pointB).Add(requestsA[0],pointA),[basis]);
        PortfolioValuation.MarketPartition Market(PreviewData data,InputFrame frame,MarketSnapshot source,PreparedConnectedPortfolioRisk connected)=>
            new(data,frame,source,connected,["synthetic_composite_value"],new(data.DefinitionRevision.RevisionId,connected.Scenario.Plan.ManifestId,FuturesMarketBinding.Implementation,[],[]),new(1,3));
        var policy=new PortfolioValuationPolicy(CurveModel.NumericalPolicy,RiskEngine.ExactNumericalPolicy,FuturesRisk.Implementation,SwapStore.Convention,
            ZeroRateCurve.Policy,DiscountInterpolation.Policy,QuoteBump.Convention,"trading_tick",1);
        var prepared=new PortfolioValuation.Prepared(a.Frame.Id,new("2040-01-10",null,a.Frame.At.ToString("O"),context),policy,
            [Market(dataA,a.Frame,sourceA,connectedA),Market(dataB,b.Frame,sourceB,connectedB)],[],[],[]);
        var axisA=CurveRiskAxes.Resolve(prepared,new("market_quote",new("contract",requestsA[0].Curve,Contract:requestsA[0].Contract)),default);
        var axisB=CurveRiskAxes.Resolve(prepared,new("market_quote",new("contract",requestB.Curve,Contract:requestB.Contract)),default);
        var parallel=CurveRiskAxes.Resolve(prepared,new("market_quote",new("parallel",requestsA[0].Curve)),default);
        var result=PortfolioValuation.Calculate(prepared,[new(axisA,ExactDecimal.Parse("0.1")),new(axisB,ExactDecimal.Parse("0.1")),new(parallel,ExactDecimal.Parse("0.1"))],[],RiskDetail.Holdings,default);
        Equal(result.Totals.Single().Value,"4120");
        foreach(var factor in result.Risk)
        {
            var isB=factor.Factor==axisB.Id;Equal(factor.Up,isB?"4124":"4120");Equal(factor.Down,isB?"4116":"4120");
            Equal(factor.Delta.Value,isB?"40":"0");Equal(factor.Gamma.Value,"0");
        }
        var joint=PortfolioJointRisk.Resolve(prepared,new(PortfolioJointRiskRequest.Version1,new("synthetic_shared_native","1",ConnectedRiskScenarioSet.Independent,
            [new("a","A only",[new(axisA.Request,"0.1",CurveShift.ParentAdditive)]),new("b","B only",[new(axisB.Request,"0.1",CurveShift.ParentAdditive)]),
             new("both","Both",[new(axisA.Request,"0.1",CurveShift.ParentAdditive),new(axisB.Request,"0.1",CurveShift.ParentAdditive)])]),null),default);
        var jointValue=PortfolioJointCalculation.Calculate(prepared,joint,[],RiskDetail.Holdings,default);
        foreach(var sample in jointValue.Samples)Equal(sample.Totals.Single().Values.Value,sample.Id=="a"?"4120":"4124");
        var tolerance=new RiskToleranceRequest("0","0");
        var derivatives=PortfolioJointRisk.Resolve(prepared,new(PortfolioJointRiskRequest.Version1,null,new(ConnectedFiniteDifferenceRequest.HalfNominalDouble,
            [new("a",new(axisA.Request,"0.1",ConnectedRiskBump.Convention),tolerance,tolerance),new("b",new(axisB.Request,"0.1",ConnectedRiskBump.Convention),tolerance,tolerance)],
            [new("ab","a","b",tolerance)])),default);
        var differentiated=PortfolioJointCalculation.Calculate(prepared,derivatives,[],RiskDetail.Holdings,default);
        foreach(var value in differentiated.Derivatives)
            Equal(value.Totals.Single().Estimate.Nominal,value.Definition.Id=="b"&&value.Definition.Formula==RiskEngine.CentralDelta?"40":"0");
        if(prepared.Factors.Length!=2||CurveRiskAxes.Catalog(prepared,default).Count(x=>x.Id==axisA.Id)!=1)
            throw new Exception("A native factor and its canonical coordinate must occur once across connected financial partitions.");
        var forgedA=sourceA with{Selections=[..sourceA.Selections.Select(s=>s.Input==((ConnectedSource)planA.Points[pointA].Expression).Input?
            s with{Observation=s.Observation with{Value=ExactDecimal.Parse("101")}}:s)]};
        var forged=dataA.Model.Compile(forgedA,[requestsA[0]],false,resolved);
        var forgedPlan=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(exportB.Definitions.Concat(forged.Definitions)),[pointB,pointA],resolved,new Visible());
        var forgedConnected=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(ConnectedScenario.Base(forgedPlan),sourceB,[forgedA]),
            ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(requestB,pointB).Add(requestsA[0],pointA),[basis]);
        var inconsistent=prepared with{Markets=[prepared.Markets[0],Market(dataB,b.Frame,sourceB,forgedConnected)]};
        Reject("scenario_target",()=>CurveRiskAxes.Resolve(inconsistent,axisA.Request,default));
        Reject("risk_factor_conflict",()=>_=inconsistent.Factors);
        var earlier=sourceA with{Id=Guid.NewGuid(),ValuationAt=sourceA.ValuationAt.AddDays(-1),MarketCutoff=sourceA.MarketCutoff.AddDays(-1)};
        var frozen=dataA.Model.Compile(earlier,[requestsA[0]],true,resolved);var frozenA=frozen.Points[(requestsA[0],InputUse.Variable)];
        var frozenPlan=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(exportB.Definitions.Concat(frozen.Definitions)),[pointB,frozenA],resolved,new Visible());
        var frozenConnected=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(ConnectedScenario.Base(frozenPlan),sourceB,[earlier]),
            ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(requestB,pointB).Add(requestsA[0],frozenA),[basis]);
        var frozenOperation=prepared with{Markets=[prepared.Markets[0],Market(dataB,b.Frame,sourceB,frozenConnected)]};
        var frozenAxis=CurveRiskAxes.Resolve(frozenOperation,axisA.Request,default);
        var frozenRisk=PortfolioValuation.Calculate(frozenOperation,[new(frozenAxis,ExactDecimal.Parse("0.1"))],[],RiskDetail.Holdings,default);
        Equal(frozenRisk.Risk.Single().Up,"4124");Equal(frozenRisk.Risk.Single().Down,"4116");
        SharedComputedOutput();
        Console.WriteLine("PASS shared native markets: original A source cancels across VM and B exchange, B-only risk survives, exact parallel subsets and factor union retained");

        void SharedComputedOutput()
        {
            var output=new CurvePointKey(new(sourceA.Definitions,"synthetic_sum"),Guid.NewGuid());
            var original=planA.Points[pointA];var source=(ConnectedSource)original.Expression;
            var computed=new ConnectedCurveDefinition(output.Curve,[original with{Key=output,Expression=new ConnectedBuiltCurve(CurveRule.Add,
                CurvePointReference.Exact(pointA),CurvePointReference.Exact(exportA.Points[(requestsA[1],InputUse.Variable)]),null,null,source.Quote,source.Sample!,CurveModel.NumericalPolicy)}]);
            var definitions=exportA.Definitions.Add(computed);
            var computedPlan=ConnectedCurves.Compile(definitions,[output],resolved,new Visible());
            var dependency=new CurveRequest(output.Curve,requestsA[0].Contract);
            var position=Hold(new GasCommodityHoldingInstrument(Guid.NewGuid(),dependency,unit.Quantity,unit.Currency,null,GasHoldingCarrying.Nominal));
            var bindings=ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(dependency,output);
            var own=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(ConnectedScenario.Base(computedPlan),sourceA),bindings,[position]);
            var copy=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(ConnectedScenario.Base(computedPlan),sourceB,[sourceA]),bindings,
                [Hold(new GasCommodityHoldingInstrument(Guid.NewGuid(),dependency,unit.Quantity,unit.Currency,null,GasHoldingCarrying.Nominal))]);
            var operation=prepared with{Markets=[Market(dataA,a.Frame,sourceA,own),Market(dataB,b.Frame,sourceB,copy)]};
            var axis=CurveRiskAxes.Resolve(operation,new("computed_output",new("contract",output.Curve,Contract:dependency.Contract)),default);
            var risk=PortfolioValuation.Calculate(operation,[new(axis,ExactDecimal.Parse("0.1"))],[],RiskDetail.Holdings,default);
            Equal(risk.Totals.Single().Value,"16000.08");Equal(risk.Risk.Single().Up,"16008.08");Equal(risk.Risk.Single().Down,"15992.08");
            var untouched=dataA.Model.Compile(forgedA,[requestsA[1]],false,resolved);
            var changedPlan=ConnectedCurves.Compile(ConnectedCurves.MergeDefinitions(forged.Definitions.Concat(untouched.Definitions).Append(computed)),[output],resolved,new Visible());
            var changed=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(ConnectedScenario.Base(changedPlan),sourceB,[forgedA]),bindings,[position]);
            var substituted=operation with{Markets=[operation.Markets[0],Market(dataB,b.Frame,sourceB,changed)]};
            Reject("scenario_target",()=>CurveRiskAxes.Resolve(substituted,axis.Request,default));
        }
    }
    private static JsonElement Change(JsonElement row,string name,string value){var n=JsonNode.Parse(row.GetRawText())!.AsObject();n[name]=value;return JsonSerializer.SerializeToElement(n);}
    private static void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");}
    private static void Reject(string code,Action action){try{action();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
    private sealed class Visible:ICurveBindingResolver
    {public CurveKey Resolve(LogicalCurveKey key,ResolvedCalculationContext context)=>throw new NotSupportedException();public void RequireVisible(CurveKey key,ResolvedCalculationContext context){}}
}
