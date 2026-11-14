using System.Collections.Immutable;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Risk;

// A factor's exact price change between the closes. It has none when the closing endpoint has no exact price for it.
public sealed record PnlFactorMovement(string Axis,PriceUnit Unit,ExactDecimal? Change);
public sealed record PnlGreekRun
{
    public ResolvedCalculationContext Context{get;}
    public string GraphManifestId{get;}
    public ConnectedFiniteDifferenceRun Risk{get;}
    internal PreparedConnectedPortfolioRisk Prepared{get;}
    private PnlGreekRun(PreparedConnectedPortfolioRisk prepared,ConnectedFiniteDifferenceRun risk)
    {Prepared=prepared;Context=prepared.Scenario.Plan.Context;GraphManifestId=prepared.Scenario.Plan.ManifestId;Risk=risk;}
    public PnlAccountingPoint OpeningPoint(ImmutableArray<AccountingFlow> flows,PnlAccountingCoverage coverage)
    {
        RiskEngine.RequireActualAccounting(Prepared);
        if(Risk.Repricing.Groups.Any(g=>g.Measure!=ValuationMeasure.PresentValue))
            throw new MarketDataException("pnl_pv_basis","A remaining-value accounting point requires actual present value.");
        return new(Context.ContextId,Risk.Repricing.SnapshotId,Context.ValuationAt,Context.KnownAt,
            [..Risk.Repricing.Groups.SelectMany(g=>g.Positions).Select(p=>new PnlPositionValue(new(p.Owner,p.BindingId,p.BookId),p.Value))],flows,coverage)
            {ValueSources=[Prepared]};
    }
    public static PnlGreekRun Capture(PreparedConnectedPortfolioRisk prepared,ConnectedFiniteDifferenceRun risk)
    {
        if(prepared is null||risk?.Repricing is not {} run||run.BaseScenario.Id!=prepared.Scenario.Id||
            run.BaseScenario.ManifestId!=prepared.Scenario.Plan.ManifestId||run.SnapshotId!=prepared.Market.Snapshot.Id)
            throw new MarketDataException("pnl_greek_context","Capture Greeks only from their exact prepared valuation scenario.");
        if(!risk.Derivatives.IsDefaultOrEmpty)RiskEngine.RequireStaticDerivatives(prepared.UnitContracts);
        return new(prepared,risk);
    }
}
public sealed record PnlGreekContribution(string Formula,ImmutableArray<string> Axes,ImmutableArray<PriceUnit> Units,
    ExactFiniteDifferenceEstimate Estimate,ExactMoney? Amount,string? UnavailableReason);
public sealed record PnlAttributionPosition(PnlAccountingPosition Accounting,ImmutableArray<PnlGreekContribution> Contributions,
    ExactMoney ExplainedValueMovement,ExactMoney ResidualValueMovement);
public sealed record PnlGreekEffect(ExactMoney? Amount,int Unavailable);
public sealed record PnlAttributionReport(PnlAccountingReport Accounting,string Policy,
    ImmutableArray<PnlAttributionPosition> Positions,ImmutableArray<PnlGreekRun> Evidence,ImmutableArray<PnlFactorMovement> Moves);

public static class TradingPnlAttribution
{
    public const string OpeningGreeks="opening_central_greeks_value_movement_v1";
    public const string UnstableEstimate="finite_difference_unstable",ClosingPriceUnavailable="closing_exact_price_unavailable";
    public static PnlAttributionReport Explain(PnlAccountingReport accounting,ImmutableArray<PnlGreekRun> evidence,
        ImmutableArray<PnlFactorMovement> moves,string policy)
    {
        if(policy!=OpeningGreeks)throw Error("pnl_attribution_policy","Select the explicit opening-central-Greeks value-movement policy.");
        if(accounting is null||evidence.IsDefault||moves.IsDefault||moves.Any(m=>m is null||string.IsNullOrWhiteSpace(m.Axis)||m.Unit is null)||
            moves.Select(m=>m.Axis).Distinct(StringComparer.Ordinal).Count()!=moves.Length)
            throw Error("pnl_factor_movement","Supply distinct exact factor movements and their units.");
        var changes=moves.ToDictionary(m=>m.Axis,StringComparer.Ordinal);
        var opening=accounting.Opening.Positions.ToDictionary(p=>p.Position);
        var actual=accounting.Positions.ToDictionary(p=>p.Position);
        var contributions=new Dictionary<AccountingPositionKey,List<PnlGreekContribution>>();
        var inputOwners=new Dictionary<InputKey,string>();var axisInputs=new Dictionary<string,InputKey>(StringComparer.Ordinal);
        var used=new HashSet<string>(StringComparer.Ordinal);var distinct=new HashSet<(AccountingPositionKey,string,string,string?)>();
        foreach(var source in evidence)
        {
            if(source?.Context is not {} context||source.Risk is not {} run||run.Repricing is not {} repricing||repricing.Detail!=RiskDetail.Holdings||
                context.ContextId!=accounting.Opening.ContextId||context.ValuationAt!=accounting.Opening.At||context.KnownAt!=accounting.Opening.KnownAt||
                repricing.SnapshotId!=accounting.Opening.SnapshotId||repricing.BaseScenario.ManifestId!=source.GraphManifestId||repricing.NumericalPolicy!=RiskEngine.ExactNumericalPolicy||
                run.Request.Convention!=ConnectedFiniteDifferenceRequest.HalfNominalDouble||!accounting.Opening.ValueSources.Any(p=>ReferenceEquals(p,source.Prepared)))
                throw Error("pnl_greek_context","Opening Greeks require the exact opening retained valuation context, manifest, snapshot and holding details.");
            if(run.Derivatives.IsDefault||run.Derivatives.Length!=2*run.Request.Axes.Length+run.Request.Mixed.Length)
                throw Error("pnl_greek_coverage","Every selected axis and mixed pair requires its complete derivative result.");
            foreach(var group in repricing.Groups)
            {
                if(group.Measure!=ValuationMeasure.PresentValue)throw Error("pnl_pv_basis","Attribution requires remaining present value, not an execution-P&L substitute.");
                foreach(var position in group.Positions)
                {
                    var key=new AccountingPositionKey(position.Owner,position.BindingId,position.BookId);
                    if(!opening.TryGetValue(key,out var value)||value.RemainingValue!=position.Value)
                        throw Error("pnl_greek_context","Each repriced opening holding must match its exact owned accounting value.");
                }
            }
            var runInputs=new Dictionary<string,InputKey>(StringComparer.Ordinal);
            foreach(var axis in run.Request.Axes)
            {
                if(!changes.TryGetValue(axis.Id,out var movement)||axis.Bump.Unit.Price!=movement.Unit)
                    throw Error("pnl_factor_unit","Each opening sensitivity axis requires an explicit closing movement in the exact same unit.");
                var input=PrimitiveInput(repricing,axis);
                if(inputOwners.TryGetValue(input,out var priorAxis)&&priorAxis!=axis.Id||axisInputs.TryGetValue(axis.Id,out var priorInput)&&priorInput!=input)
                    throw Error("pnl_factor_overlap","Attribution requires one canonical axis per primitive live quote, across every market partition.");
                inputOwners[input]=axis.Id;axisInputs[axis.Id]=input;runInputs[axis.Id]=input;
                used.Add(axis.Id);
            }
            var declared=source.Prepared.DeclaredPriceInputs;
            foreach(var derivative in run.Derivatives)
            {
                FiniteDifferenceTolerance tolerance;
                var diagonal=derivative.Formula is RiskEngine.CentralDelta or RiskEngine.CentralGamma;
                if(diagonal)
                {
                    var axis=run.Request.Axes.SingleOrDefault(a=>a.Id==derivative.Id);
                    if(axis is null||!derivative.Axes.SequenceEqual(new[]{axis.Id}))throw Error("pnl_greek_context","A derivative must retain its selected axis.");
                    tolerance=derivative.Formula==RiskEngine.CentralDelta?axis.DeltaTolerance:axis.GammaTolerance;
                }
                else if(derivative.Formula==RiskEngine.CentralMixedGamma)
                {
                    var pair=run.Request.Mixed.SingleOrDefault(p=>p.Id==derivative.Id);
                    if(pair is null||!derivative.Axes.SequenceEqual(new[]{pair.FirstAxis,pair.SecondAxis}))throw Error("pnl_greek_context","A mixed derivative must retain its selected ordered axes.");
                    tolerance=pair.Tolerance;
                }
                else throw UnsupportedFormula();
                var denominators=derivative.Formula==RiskEngine.CentralGamma?ImmutableArray.Create(derivative.Axes[0],derivative.Axes[0]):derivative.Axes;
                if(derivative.FactorUnits.Length!=denominators.Length||denominators.Where((a,n)=>!changes.TryGetValue(a,out var m)||m.Unit!=derivative.FactorUnits[n].Price).Any())
                    throw Error("pnl_factor_unit","Derivative movements require their exact retained factor units.");
                if(derivative.Groups.IsDefault||derivative.Groups.Length!=repricing.Groups.Length||derivative.Groups.Select(g=>g.Group).Distinct().Count()!=derivative.Groups.Length)
                    throw Error("pnl_greek_coverage","Every derivative requires the complete selected holding groups.");
                foreach(var group in derivative.Groups)
                {
                    if(group.Group<0||group.Group>=repricing.Groups.Length||group.Positions.IsDefault)throw Error("pnl_greek_coverage","A derivative group must belong to its captured base.");
                    var basis=repricing.Groups[group.Group].Positions;
                    if(group.Positions.Length!=basis.Length||!group.Positions.Select(p=>(p.Owner,p.BindingId,p.BookId,p.ValuationId)).ToHashSet().SetEquals(basis.Select(p=>(p.Owner,p.BindingId,p.BookId,p.ValuationId))))
                        throw Error("pnl_greek_coverage","Every derivative must retain the exact base trade, binding, book and valuation identities.");
                }
                // The inputs this derivative moves, and each factor's closing movement: none without an exact closing price.
                var inputs=derivative.Axes.Select(a=>runInputs.TryGetValue(a,out var input)?input:
                    throw Error("pnl_greek_context","A derivative must address the axes its own run selected.")).ToArray();
                var moved=derivative.Axes.Select(a=>changes[a].Change).ToArray();
                foreach(var group in derivative.Groups)foreach(var position in group.Positions)
                {
                    var key=new AccountingPositionKey(position.Owner,position.BindingId,position.BookId);
                    var first=derivative.Axes[0];string? second=derivative.Axes.Length==2?derivative.Axes[1]:null;
                    if(second is not null&&StringComparer.Ordinal.Compare(first,second)>0)(first,second)=(second,first);
                    if(!actual.TryGetValue(key,out var row)||!row.HeldAtOpening||!distinct.Add((key,derivative.Formula,first,second)))
                        throw Error("pnl_greek_context","A Greek must address one distinct opening holding, formula and axis tuple.");
                    var estimate=position.Estimate;
                    if(new[]{estimate.Half,estimate.Nominal,estimate.Double}.Any(v=>v.Currency!=row.Currency)||
                        RiskEngine.AssessFiniteDifference(estimate.Half,estimate.Nominal,estimate.Double,tolerance).Stable!=estimate.Stable)
                        throw Error("pnl_greek_context","Derivative currencies and convergence must match their retained numerical contract.");
                    // A holding is explained by the price inputs its valuation declares; no other input can move its value.
                    if(!declared.TryGetValue((position.Owner,position.BindingId),out var priced))
                        throw Error("pnl_greek_context","A repriced holding must belong to its captured valuation source.");
                    if(!inputs.All(priced.Contains))
                    {
                        if(new[]{estimate.Half,estimate.Nominal,estimate.Double}.Any(v=>v.Numerator!="0"))
                            throw Error("pnl_greek_context","A holding's value moved with a price input outside its declared dependencies.");
                        continue;
                    }
                    // An estimate explains an amount only when it converged and every factor it uses has a closing movement.
                    ExactMoney? amount=null;
                    if(estimate.Stable&&moved.All(m=>m is not null))
                    {
                        amount=estimate.Nominal.Scale(moved[0]!.Value);
                        if(derivative.Formula==RiskEngine.CentralGamma)amount=amount.Scale(moved[0]!.Value).DivideBy(ExactDecimal.Parse("2"));
                        if(derivative.Formula==RiskEngine.CentralMixedGamma)amount=amount.Scale(moved[1]!.Value);
                    }
                    if(!contributions.TryGetValue(key,out var values))contributions.Add(key,values=[]);
                    values.Add(new(derivative.Formula,derivative.Axes,[..derivative.FactorUnits.Select(u=>u.Price??throw Error("pnl_factor_unit","This attribution policy requires price-coordinate units."))],estimate,amount,amount is not null?null:moved.Any(m=>m is null)?ClosingPriceUnavailable:UnstableEstimate));
                }
            }
        }
        if(changes.Keys.Any(k=>!used.Contains(k)))throw Error("pnl_factor_movement","A movement must belong to a selected sensitivity axis.");
        var rows=accounting.Positions.Select(row=>
        {
            var terms=contributions.TryGetValue(row.Position,out var found)?found.ToImmutableArray():ImmutableArray<PnlGreekContribution>.Empty;
            var explained=terms.Where(t=>t.Amount is not null).Select(t=>t.Amount!).Aggregate(ExactMoney.Parse("0",row.Currency),(a,b)=>a.Add(b));
            return new PnlAttributionPosition(row,terms,explained,row.ValueMovement.Subtract(explained));
        }).ToImmutableArray();
        return new(accounting,policy,rows,evidence,moves);
    }
    // The mixed second differences an attribution needs: one for each pair of axes whose price inputs a single
    // holding's valuation declares together. Dependencies identify this work; every other pair's mixed difference
    // is exactly zero.
    public static ImmutableArray<ConnectedMixedSensitivity> JointPairs(PreparedConnectedPortfolioRisk prepared,
        ImmutableArray<(string Axis,InputKey Input)> axes,FiniteDifferenceTolerance tolerance)
    {
        if(prepared is null||axes.IsDefault)throw Error("pnl_greek_context","Supply the prepared opening valuation and its selected price axes.");
        var priced=prepared.DeclaredPriceInputs.Values.Distinct().ToArray();
        return [..axes.SelectMany((a,n)=>axes.Skip(n+1).Where(b=>priced.Any(inputs=>inputs.Contains(a.Input)&&inputs.Contains(b.Input)))
            .Select(b=>new ConnectedMixedSensitivity(a.Axis+"_"+b.Axis,a.Axis,b.Axis,tolerance)))];
    }
    // One formula's opening-Greek amounts over holdings of one currency, for every factor or for one axis.
    public static PnlGreekEffect Effect(IEnumerable<PnlAttributionPosition> positions,string formula,string? axis=null)=>
        formula is RiskEngine.CentralDelta or RiskEngine.CentralGamma or RiskEngine.CentralMixedGamma
            ?Summarize(positions,c=>c.Formula==formula&&(axis is null||c.Axes.Contains(axis))):throw UnsupportedFormula();
    // What every opening Greek explains together, over holdings of one currency.
    public static PnlGreekEffect Explained(IEnumerable<PnlAttributionPosition> positions)=>Summarize(positions,_=>true);
    // Unavailable estimates are counted, never read as zero: an effect none of whose estimates is available has no amount.
    private static PnlGreekEffect Summarize(IEnumerable<PnlAttributionPosition> positions,Func<PnlGreekContribution,bool> selected)
    {
        var rows=positions?.ToArray()??[];
        if(rows.Length==0||rows.Any(r=>r is null||r.Accounting.Currency!=rows[0].Accounting.Currency))
            throw Error("pnl_position","Summarize the holdings of one currency.");
        var terms=rows.SelectMany(r=>r.Contributions).Where(selected).ToArray();
        var unavailable=terms.Count(c=>c.Amount is null);
        var amount=unavailable>0&&unavailable==terms.Length?null:
            terms.Where(c=>c.Amount is not null).Select(c=>c.Amount!).Aggregate(ExactMoney.Parse("0",rows[0].Accounting.Currency),(a,b)=>a.Add(b));
        return new(amount,unavailable);
    }
    private static InputKey PrimitiveInput(ConnectedScenarioRiskRun run,ConnectedSensitivityAxis axis)
    {
        if(axis.Bump is not PriceSensitivityInput {Bump:{} bump}||bump.Coordinate!=CurveShockCoordinate.MarketQuote)throw Error("pnl_factor_basis","This policy explains primitive live market quotes only.");
        var indices=run.Set.Scenarios.Select((s,n)=>(Sample:s,Index:n)).Where(s=>s.Sample.Shifts.Length==1&&s.Sample.Shifts[0].Coordinate==bump.Coordinate&&s.Sample.Shifts[0].Target==bump.Target).Select(s=>s.Index).ToArray();
        if(indices.Length==0||indices.Any(i=>i>=run.Scenarios.Length))throw Error("pnl_factor_basis","A selected axis needs its actual retained repricing shifts.");
        var shifts=indices.SelectMany(i=>run.Scenarios[i].Scenario.Shifts).ToArray();
        var inputs=shifts.Where(s=>s.Input is not null).Select(s=>s.Input!.Value).Distinct().ToArray();
        if(inputs.Length!=1||inputs[0].Use!=InputUse.Variable||shifts.Any(s=>s.Input is null||s.Unit!=bump.Unit))
            throw Error("pnl_factor_basis","One explicit scalar movement must resolve to one primitive live input in the exact unit; baskets and output overlays require a different policy.");
        return inputs[0];
    }
    private static MarketDataException UnsupportedFormula()=>
        Error("pnl_attribution_policy","Only the declared central first, diagonal second and mixed second derivatives are supported.");
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
