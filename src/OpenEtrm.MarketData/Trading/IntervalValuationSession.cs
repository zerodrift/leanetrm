using System.Collections.Immutable;
using OpenEtrm.MarketData.CustomCurves;
using OpenEtrm.MarketData.Power;

namespace OpenEtrm.MarketData.Trading;

// One scenario's side calculations use the shared price resolver; no side-value cache exists.
public sealed class IntervalValuationSession
{
    public IntervalValuationInputs Inputs { get; }
    public IntervalPriceResolver Resolver { get; }
    public CurveBuild Scenario { get; }
    public int UniqueSideCalculations { get; private set; }
    public long RowEvaluations { get; private set; }
    public int UnitValuations { get; private set; }
    public int ReusedUnitValues { get; private set; }
    private readonly Dictionary<(Guid,DeliveryCoverage),List<(ImmutableArray<SidePaymentFactor> Factors,InstrumentValuation Value)>> cache=[];
    private readonly Dictionary<(CurveKey,DateOnly,bool),ImmutableDictionary<DateTimeOffset,IntervalPriceResolution>> priceRows=[];
    private readonly IntervalValuationSession? basis;
    internal IntervalValuationSession(IntervalValuationInputs inputs,CurveBuild scenario,IntervalValuationSession? basis)
    {
        if(inputs is null || scenario is null || !scenario.Snapshot.HasSameInputs(inputs.Snapshot) || !ReferenceEquals(scenario.Model,inputs.BaseMarket.Model) ||
            basis is not null && (!ReferenceEquals(basis.Inputs,inputs) || basis.Scenario.Shock is not null))
            throw PpaErrors.Error("ppa_context","scenario","Reuse requires the same prepared request and immutable base inputs.");
        Inputs=inputs;Scenario=scenario;this.basis=basis;Resolver=new(scenario,inputs.Read.Curves,basis?.Resolver);
    }
    public InstrumentValuation Value(PpaInstrument instrument,DeliveryCoverage coverage,ISideDiscounting discounting)
    {
        Inputs.ValidateCoverage(instrument,coverage);
        if(discounting is null)throw PpaErrors.Error("ppa_context","discounting","Select explicit discount inputs.");
        var prepared=instrument.Memberships.Select(m=>(m.Side,Rows:Inputs.Rows(instrument,m.Side,coverage))).ToArray();
        var work=prepared.Sum(p=>(long)p.Rows.Length);
        var detail=prepared.Sum(p=>p.Rows.LongCount(r=>Inputs.DetailMonths.Contains(new(r.Quantity.Interval.OperatingDate.Year,r.Quantity.Interval.OperatingDate.Month,1))));
        RequireWork(work,detail);
        var key=(instrument.Id,coverage);
        if(!cache.ContainsKey(key))RequireWork(RowEvaluations+work,detail);
        var keys=prepared.SelectMany(p=>FutureKeys(p.Side,p.Rows)).Distinct().ToImmutableArray();
        var resolved=keys.IsEmpty?ImmutableArray<SidePaymentFactor>.Empty:discounting.Resolve(keys,Scenario.Snapshot);
        ValidateFactors(instrument,keys,resolved);
        var byKey=resolved.ToImmutableDictionary(f=>f.Payment);
        var factors=keys.Select(k=>byKey[k]).ToImmutableArray();
        if(basis is not null && basis.cache.TryGetValue(key,out var baseValues) && !baseValues.Any(v=>SameFactors(v.Factors,factors)))
            throw PpaErrors.Error("ppa_context","discounting","Commodity scenarios must retain the base calculation's resolved discount factors.");
        if(cache.TryGetValue(key,out var prior))
            foreach(var entry in prior)if(SameFactors(entry.Factors,factors))
            {ReusedUnitValues++;return entry.Value;}
        // Cache admission needs the resolved immutable factor identity. Only a miss consumes rows.
        RequireWork(RowEvaluations+work,detail);
        // Resolve once before reuse admission. A provider object is not an immutable factor identity.
        var context=new SideValuationContext(Scenario,new ResolvedDiscounting(byKey)){Interval=new(this,instrument,coverage)};
        // The common SidedInstrument path owns delegation, signs and the exact sum.
        var result=instrument.PV(context);
        if(!cache.TryGetValue(key,out prior))cache[key]=prior=[];
        prior.Add((factors,result));UnitValuations++;return result;
    }
    private static bool SameFactors(ImmutableArray<SidePaymentFactor> left,ImmutableArray<SidePaymentFactor> right)=>
        left.Length==right.Length && left.Zip(right).All(p=>p.First.Payment==p.Second.Payment &&
            p.First.DiscountFactor==p.Second.DiscountFactor && p.First.Source==p.Second.Source);
    public static void RequireWork(long rowEvaluations,long returnedRows)
    {
        if(rowEvaluations is <0 or >2_000_000 || returnedRows is <0 or >35_136)
            throw PpaErrors.Error("ppa_size","work","Interactive valuations admit at most two million row evaluations and 35,136 detailed rows; select period summaries for larger detail requests.");
    }
    private IEnumerable<SidePaymentKey> FutureKeys(PpaSide side,ImmutableArray<PreparedSideRow> rows)=>
        rows.Where(r=>r.ValueAt>Scenario.Snapshot.ValuationAt && !(side is CertificateDeliverySide && r.Quantity.Interval.End<=Scenario.Snapshot.ValuationAt))
            .Select(r=>PaymentKey(side,r)).Distinct();
    private void ValidateFactors(PpaInstrument instrument,ImmutableArray<SidePaymentKey> keys,ImmutableArray<SidePaymentFactor> factors)
    {
        if(factors.IsDefault || factors.Length!=keys.Length || factors.Any(f=>f is null || !keys.Contains(f.Payment) ||
            f.DiscountFactor.CompareTo(default)<=0 || !Enum.IsDefined(f.Source) || !Scenario.Snapshot.HasSameInputs(f.Snapshot) ||
            instrument.Discounting==(f.Source==DiscountFactorSource.Undiscounted) || f.Source==DiscountFactorSource.Undiscounted && f.DiscountFactor!=PpaErrors.One) ||
            factors.Select(f=>f.Payment).Distinct().Count()!=factors.Length)
            throw PpaErrors.Error("side_payment_factors","discounting","Each future value date requires its exact selected factor and context.");
    }
    private sealed class ResolvedDiscounting(ImmutableDictionary<SidePaymentKey,SidePaymentFactor> factors):ISideDiscounting
    {
        public ImmutableArray<SidePaymentFactor> Resolve(ImmutableArray<SidePaymentKey> keys,MarketSnapshot snapshot)=>
            [..keys.Select(k=>factors.TryGetValue(k,out var factor)?factor:throw PpaErrors.Error("side_payment_factors","discounting","A prepared value factor is missing."))];
    }
    internal IntervalSideValuation Evaluate(PpaSide side,PpaInstrument instrument,DeliveryCoverage coverage,SideValuationContext context)
    {
        var slot=instrument.Memberships.SingleOrDefault(m=>ReferenceEquals(m.Side,side))?.Slot
            ??throw PpaErrors.Error("ppa_context","side","This side is not a member of the selected instrument.");
        var rows=Inputs.Rows(instrument,side,coverage);
        var keys=FutureKeys(side,rows).ToImmutableArray();
        var factors=keys.IsEmpty?ImmutableArray<SidePaymentFactor>.Empty:context.Discounting.Resolve(keys,Scenario.Snapshot);
        ValidateFactors(instrument,keys,factors);
        var selected=factors.ToDictionary(f=>f.Payment);var total=new MeasureAccumulator(instrument.Currency);
        foreach(var row in rows)
        {
            var q=row.Quantity;var delivered=q.Interval.End<=Scenario.Snapshot.ValuationAt;
            var holding=side is CertificateDeliverySide && delivered;
            var payment=side is PpaFixedSide or PpaFloatingSide;
            var include=IntervalValuationInputs.InMeasure(side,row,Scenario.Snapshot.ValuationAt);
            var components=Components(side,row,!include);
            var knownQuantity=q.State is VolumeState.Metered or VolumeState.Contractual;
            var known=knownQuantity && components.All(p=>p.State is RowPriceState.Fixed or RowPriceState.Actual or RowPriceState.PublishedFixing);
            var classification=holding && knownQuantity?EconomicClass.Held:!delivered?EconomicClass.Unrealised:known?EconomicClass.Realised:EconomicClass.Estimated;
            var amount=components.Select(p=>p.Quantity.Times(p.Price)).Aggregate((a,b)=>a.Add(b));
            ValueCoverage coverageState;ExactMoney? pv;SidePaymentFactor? factor=null;string? unavailable=null;
            if(holding){coverageState=ValueCoverage.HoldingValue;pv=amount;}
            else if(row.ValueAt>Scenario.Snapshot.ValuationAt)
            {coverageState=ValueCoverage.Discounted;factor=selected[PaymentKey(side,row)];pv=amount.Scale(factor.DiscountFactor);}
            else if(payment)
            {
                if(components.Any(p=>p.State is RowPriceState.Projected or RowPriceState.Modeled))throw PpaErrors.Error("ppa_due_leg_unresolved",slot.ToString(),"A due payment cannot consume an unsupported projected or modeled amount.");
                coverageState=known?ValueCoverage.OverdueBalance:ValueCoverage.OverdueEstimate;pv=null;unavailable="due_cash_policy_required";
            }
            else{coverageState=ValueCoverage.DeliveredValue;pv=null;}
            var result=new ScheduleRowValue(side.Id,slot,holding?ComponentKind.Holding:payment?ComponentKind.Payment:ComponentKind.Delivery,
                q.Interval,q.Quantity,q.State,q.Stage,q.Lineage,q.CapacityVersions,components,amount,PpaErrors.One,
                payment?PaymentRule.Implementation:"monthly_physical_value_date_v1",row.ValueAt,factor,pv,classification,
                payment?delivered?PaymentLifecycle.DeliveredUnsettled:PaymentLifecycle.RemainingProjected:null,coverageState,unavailable){CapacityDays=q.CapacityDays};
            total.Add(result,Inputs.DetailMonths.Contains(new(q.Interval.OperatingDate.Year,q.Interval.OperatingDate.Month,1)));
            RowEvaluations++;
        }
        UniqueSideCalculations++;return new(side.GenerateSchedule(),context,total.Finish(),
            new(Inputs.Receipt,instrument.EconomicIdentity,side.Id,slot,coverage,Scenario.Shock,"ppa_interval_leg_pv_v1","exact_interval_leg_pv_v1",factors));
    }
    private static SidePaymentKey PaymentKey(PpaSide side,PreparedSideRow row)
    {
        var month=row.Quantity.Interval.OperatingDate;
        return new(side.Id,(month.Year-side.Terms.Delivery.From.Year)*12+month.Month-side.Terms.Delivery.From.Month,row.ValueAt,side.Terms.Currency);
    }
    private ImmutableArray<PriceComponent> Components(PpaSide side,PreparedSideRow row,bool frozen)
    {
        if(side is PpaFixedSide fixedSide)return [new(row.Quantity.Quantity,fixedSide.Price.At(row.Quantity.Interval.OperatingDate),RowPriceState.Fixed,null,null,PpaErrors.One,null)];
        return [..row.Prices.Select(binding=>
        {
            var date=row.Quantity.Interval.OperatingDate;var month=new DateOnly(date.Year,date.Month,1);
            var curve=Inputs.Read.Curves[binding.Curve];var period=curve.Curve is PeriodContractCurve;
            var key=(binding.Curve,month,frozen);
            if(!priceRows.TryGetValue(key,out var selected))
            {
                var values=(frozen?Inputs.BaseResolver:Resolver).Prices(binding.Curve,month);
                priceRows.Add(key,selected=values.ToImmutableDictionary(p=>p.Start));
            }
            var start=period?new DateTimeOffset(curve.Period(month).Contracts.Single(c=>c.DeliveryFrom<=date && date<c.DeliveryTo)
                .DeliveryFrom.ToDateTime(TimeOnly.MinValue,DateTimeKind.Utc)):row.Quantity.Interval.Start;
            if(!selected.TryGetValue(start,out var resolved))throw PpaErrors.Error("ppa_cadence","price","A price is missing for the exact originating interval or vintage.");
            ExactUnitPrice price;RowPriceState state;
            if(resolved.State==IntervalPriceState.Unresolved)
            {
                if(Inputs.Policy!=PendingActualsPolicy.EstimateUntilActual)throw new MarketDataException("actuals_required","Delivered prices require an eligible actual under the selected policy.");
                price=resolved.Estimate??throw new MarketDataException("interval_estimate_unavailable","The bound price resolver supplied no eligible estimate.");state=RowPriceState.Estimated;
            }
            else
            {
                price=resolved.Price??throw new MarketDataException("interval_estimate_unavailable","The bound price is unavailable.");
                state=resolved.State switch {IntervalPriceState.Actual=>RowPriceState.Actual,IntervalPriceState.PublishedFixing=>RowPriceState.PublishedFixing,
                    IntervalPriceState.Modeled=>RowPriceState.Modeled,_=>RowPriceState.Projected};
                if(period && side is not CertificateDeliverySide && row.Quantity.Interval.End<=Scenario.Snapshot.ValuationAt && state==RowPriceState.Projected)
                {
                    if(Inputs.Policy!=PendingActualsPolicy.EstimateUntilActual)throw new MarketDataException("actuals_required","The delivered period requires its eligible fixing.");
                    state=RowPriceState.Estimated;
                }
            }
            if(price.Unit!=binding.Adder.Unit)throw PpaErrors.Error("ppa_price_rule","price","An adder must use the exact resolved price unit.");
            var scaled=price.ScaleFraction(binding.Multiplier.ScaledValue,ExactDecimal.WorkingScale);
            var value=ExactUnitPrice.FromFraction(scaled.NumeratorValue*ExactDecimal.WorkingScale+binding.Adder.Value.ScaledValue*scaled.DenominatorValue,
                scaled.DenominatorValue*ExactDecimal.WorkingScale,price.Unit);
            return new PriceComponent(binding.Quantity,value,state,binding.Curve,resolved,binding.Multiplier,binding.Adder);
        })];
    }
}
