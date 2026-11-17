using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasTransportDirectRightsChecks
{
    public static void Run()
    {
        var customer=new MarkFixture("20");var provider=new MarkFixture("-20");
        Equal(customer.Value(),"20");Equal(provider.Value(),"-20");
        customer.CompiledPrimitive();
        Equal(customer.Value().Add(customer.OtherOwners(customer.Base,"-12.5","0")),"7.5");
        Equal(provider.Value().Add(provider.OtherOwners(provider.Base,"12.5","0")),"-7.5");
        Equal(customer.Value().Add(customer.OtherOwners(customer.Base,"-12.5","100")),"307.5");
        if(GasTransportDirectRightsValuation.SupportsQuantityScaling)
            throw new Exception("A total rights-package mark must not declare quantity scalability.");

        var serviceUp=customer.Shift(customer.Service,"1");var serviceDown=customer.Shift(customer.Service,"-1");
        var gasUp=customer.Shift(customer.Gas,"1");
        Equal(customer.Value(serviceUp),"21");Equal(customer.Value(serviceDown),"19");
        Equal(customer.Value(gasUp),"20");
        Equal(customer.Value(serviceUp).Add(customer.OtherOwners(serviceUp,"-12.5","100")),"308.5");
        Equal(customer.Value(gasUp).Add(customer.OtherOwners(gasUp,"-12.5","100")),"407.5");
        Equal(customer.Value(),"20");
        var nested=serviceUp.WithShifts([customer.Move(customer.Service,"2")]);
        Equal(customer.Value(nested),"23");Equal(customer.Value(serviceUp),"21");
        // A provider's signed mark is an independent input, not a customer value negated by its role.
        Equal(provider.Value(provider.Shift(provider.Service,"1")),"-19");
        var zero=new MarkFixture("0");Equal(zero.Value(),"0");Equal(zero.Value(zero.Shift(zero.Service,"1")),"1");

        Reject("gas_transport_rights_policy",()=>GasTransportDirectRightsValuation.PV(customer.Terms with{Policy=""},customer.State,customer.Mark,customer.Evaluation,customer.Base));
        Reject("gas_transport_rights_policy",()=>GasTransportDirectRightsValuation.PV(customer.Terms with{Policy="future_rights_value_v1"},customer.State,customer.Mark,customer.Evaluation,customer.Base));
        Reject("gas_transport_rights_mark",()=>GasTransportDirectRightsValuation.PV(customer.Terms,customer.State,null,customer.Evaluation,customer.Base));
        foreach(var changed in new[]{
            customer.State with{AgreementId=Guid.NewGuid()},customer.State with{EntitlementId=Guid.NewGuid()},
            customer.State with{CapacityVersionId=Guid.NewGuid()},
            customer.State with{RemainingRightsIdentity="synthetic_changed_remaining_rights"}})
            Reject("gas_transport_rights_state",()=>GasTransportDirectRightsValuation.PV(customer.Terms,changed,customer.Mark,customer.Evaluation,customer.Base));
        foreach(var invalid in new[]{customer.State with{AgreementId=Guid.Empty},customer.State with{EntitlementId=Guid.Empty},
            customer.State with{RemainingRightsIdentity=""}})
            Reject("gas_transport_rights_state",()=>GasTransportDirectRightsValuation.PV(customer.Terms,invalid,customer.Mark with{State=invalid},customer.Evaluation,customer.Base));
        var newContext=new ResolvedCalculationContext(Guid.NewGuid(),"synthetic_new_read_same_rights_and_quote",customer.At,customer.At.AddDays(2),customer.At,customer.At);
        var newRead=customer.ReadContext(newContext);
        var evaluation=new GasTransportDirectRightsEvaluation(newContext,newRead.Id,ConnectedCurves.NumericalPolicy);
        Equal(GasTransportDirectRightsValuation.PV(customer.Terms,customer.State,customer.Mark,evaluation,newRead),"20");
        Reject("gas_transport_rights_context",()=>GasTransportDirectRightsValuation.PV(customer.Terms,customer.State,customer.Mark,customer.Evaluation,newRead));
        foreach(var invalid in new[]{customer.Evaluation with{BaseScenarioId=provider.Base.Id},customer.Evaluation with{NumericalPolicy="working_decimal"}})
            Reject("gas_transport_rights_context",()=>GasTransportDirectRightsValuation.PV(customer.Terms,customer.State,customer.Mark,invalid,customer.Base));
        foreach(var mark in new[]{
            customer.Mark with{Observation=new(customer.Reference.CatalogId,Guid.NewGuid())},
            customer.Mark with{Input=customer.Mark.Input with{Series=new(customer.Definition,Guid.NewGuid())}},
            customer.Mark with{Source=customer.R("another_source")},customer.Mark with{Quote=customer.R("another_quote")},
            customer.Mark with{Role=ObservationRole.DailySettlement},customer.Mark with{Point=customer.Gas.Key}})
            Reject("gas_transport_rights_mark",()=>GasTransportDirectRightsValuation.PV(customer.Terms,customer.State,mark,customer.Evaluation,customer.Base));
        foreach(var terms in new[]{customer.Terms with{Currency=customer.R("other_currency")},
            customer.Terms with{PackageQuantity=customer.R("gas_quantity")}})
            Reject("gas_transport_rights_unit",()=>GasTransportDirectRightsValuation.PV(terms,customer.State,customer.Mark,customer.Evaluation,customer.Base));
        var late=customer.Base.WithClock(new(customer.At.AddMicroseconds(1),ClockChange.FrozenInputs));
        Reject("gas_transport_rights_time",()=>customer.Value(late));
        var stale=new MarkFixture("20",observedAt:customer.At.AddMicroseconds(-1));
        Reject("gas_transport_rights_mark",()=>stale.Value());
        var multiplied=new MarkFixture("20",multiplier:"100");
        Reject("gas_transport_rights_unit",()=>multiplied.Value());
        var basis=new MarkFixture("20",kind:QuoteKind.Basis);
        Reject("gas_transport_rights_mark",()=>basis.Value());
        var computed=new MarkFixture("20",computed:true);
        Reject("gas_transport_rights_mark",()=>computed.Value());
        Console.WriteLine("PASS current Transport rights: explicit signed whole-package PV, exact source/state/scenario, independent cash/gas and no scaling or clock continuation");
    }

    internal sealed class MarkFixture
    {
        internal readonly ReferenceRevision Reference=new(Guid.Parse("b0100000-0000-0000-0000-000000000001"),Guid.Parse("b0100000-0000-0000-0000-000000000002"));
        internal readonly DefinitionRevision Definition=new(Guid.Parse("b0100000-0000-0000-0000-000000000001"),Guid.NewGuid());
        internal readonly DateTimeOffset At=TradingInstant.Parse("2035-01-15T12:00:00Z","synthetic_time");
        internal readonly ResolvedCalculationContext Context;
        internal readonly ConnectedCurvePoint Service,Gas;
        internal readonly ConnectedScenario Base;
        internal readonly MarketSnapshot Snapshot;
        internal readonly GasTransportDirectRightsTerms Terms;
        internal readonly GasTransportDirectRightsState State;
        internal readonly GasTransportDirectRightsMark Mark;
        internal readonly GasTransportDirectRightsEvaluation Evaluation;
        readonly ImmutableArray<ConnectedCurveDefinition> definitions;
        readonly TemporalCurveBindings resolver;
        internal ReferenceKey R(string code)=>new(Reference,code);
        internal MarkFixture(string value,DateTimeOffset? observedAt=null,string multiplier="1",QuoteKind kind=QuoteKind.Outright,bool computed=false,bool otherPackage=false)
        {
            Context=new(Guid.NewGuid(),"synthetic_current_rights_and_independent_gas",At,At.AddDays(1),At,At);
            ConnectedCurvePoint Point(string code,string amount,string quantity,QuoteKind quoteKind,string scale)
            {
                var curve=new CurveKey(Definition,code);var contract=new MarketContract(new(Definition,Guid.NewGuid()),ContractKind.Forward,
                    new(2035,1,15),new(2035,2,15),At.AddDays(31),null);
                var quote=new QuoteSpecification(R(code+"_quote"),quoteKind,R("currency"),R(quantity),ExactDecimal.Parse(scale),
                    ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01"),ExactDecimal.Parse("-10000"),ExactDecimal.Parse("10000"));
                var series=new MarketSeries(new(Definition,Guid.NewGuid()),contract.Key,quote.Key,null);
                var observation=new MarketObservation(new(Definition.CatalogId,Guid.NewGuid()),series.Key,R("synthetic_manual_source"),
                    ObservationRole.TraderMark,PublicationState.Final,code=="service"?observedAt??At:At,ExactDecimal.Parse(amount));
                return new(new(curve,Guid.NewGuid()),contract.DeliveryFrom,contract.DeliveryTo,PriceUnit.FromQuote(quote),
                    new ConnectedSource(new(curve,contract.Key),new(series.Key,InputUse.Variable),observation,quote,series),contract);
            }
            Service=Point("service",value,"whole_rights_package",kind,multiplier);
            Gas=otherPackage?Point("other_package","99","whole_rights_package",QuoteKind.Outright,"1"):
                Point("gas","3","gas_quantity",QuoteKind.Outright,"1");
            var raw=Service;var source=(ConnectedSource)raw.Expression;
            definitions=ImmutableArray.Create(new ConnectedCurveDefinition(Service.Key.Curve,[Service]),new(Gas.Key.Curve,[Gas]));
            if(computed)
            {
                var target=new CurveKey(Definition,"computed_service");
                Service=Service with{Key=new(target,Guid.NewGuid()),Expression=new ConnectedWeightedSum([new(CurvePointReference.Exact(raw.Key),ExactDecimal.Parse("1"))])};
                definitions=definitions.Add(new(target,[Service]));
            }
            resolver=new TemporalCurveBindings([],definitions.Select(d=>new CurvePublication(d.Key,At.AddDays(-1))));
            Base=ConnectedScenario.Base(ConnectedCurves.Compile(definitions,[Service.Key,Gas.Key],Context,resolver));
            Snapshot=new MarketSnapshot(Guid.NewGuid(),Definition,At,At,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,
                [new(source.Input,source.Observation),new(((ConnectedSource)Gas.Expression).Input,((ConnectedSource)Gas.Expression).Observation)])
                .WithKnowledge(Context.ContextId,Context.KnownAt);
            Terms=new(GasTransportDirectRightsPolicy.Version,R("whole_rights_package"),R("currency"));
            State=new(Guid.NewGuid(),Guid.NewGuid(),Guid.NewGuid(),"synthetic_remaining_rights_state");
            Mark=new(State,Service.Key,source.Input,source.Observation.Key,source.Observation.Source,source.Quote.Key,source.Observation.Role,Context);
            Evaluation=new(Context,Base.Id,ConnectedCurves.NumericalPolicy);
        }
        internal ExactMoney Value(ConnectedScenario? scenario=null)=>GasTransportDirectRightsValuation.PV(Terms,State,Mark,Evaluation,scenario??Base);
        internal ConnectedScenario ReadContext(ResolvedCalculationContext context)=>ConnectedScenario.Base(ConnectedCurves.Compile(definitions,[Service.Key,Gas.Key],context,resolver));
        internal void CompiledPrimitive()
        {
            var sources=new[]{Service,Gas}.Select(p=>(ConnectedSource)p.Expression).ToArray();var model=Model();
            var export=model.Compile(Snapshot,[..sources.Select(s=>s.Request)],false,Context);
            var point=export.Points[(sources[0].Request,InputUse.Variable)];
            var compiled=ConnectedScenario.Base(ConnectedCurves.Compile(export.Definitions,[point],Context,resolver));
            Equal(GasTransportDirectRightsValuation.PV(Terms,State,Mark with{Point=point},
                new(Context,compiled.Id,ConnectedCurves.NumericalPolicy),compiled),"20");
            var frozen=model.Compile(Snapshot,[sources[0].Request],false,Context,freezeObservation:true);
            var frozenPoint=frozen.Points[(sources[0].Request,InputUse.Variable)];
            var frozenScenario=ConnectedScenario.Base(ConnectedCurves.Compile(frozen.Definitions,[frozenPoint],Context,resolver));
            Reject("gas_transport_rights_mark",()=>GasTransportDirectRightsValuation.PV(Terms,State,
                Mark with{Point=frozenPoint,Input=Mark.Input with{Use=InputUse.FrozenFact}},
                new(Context,frozenScenario.Id,ConnectedCurves.NumericalPolicy),frozenScenario));
        }
        internal CurveModel Model()
        {
            var points=new[]{Service,Gas};var sources=points.Select(p=>(ConnectedSource)p.Expression).ToArray();
            var quantities=sources.Select(s=>s.Quote.Quantity).Distinct().Select(q=>new QuantitySpecification(q,R("synthetic_commodity"),
                R("native_basis"),R(q.Code+"_unit"),R(q.Code+"_dimension")));
            var curves=sources.Select(s=>new Curve(s.Request.Curve,s.Quote.Key,R("synthetic_location"),R("uniform"),
                new(SettlementKind.Index,R("synthetic_index"),null),CurveRule.Direct,[s.Request.Contract],[],s.Observation.Role,s.Observation.Source,null));
            return new(Definition,Reference,quantities,sources.Select(s=>s.Quote),points.Select(p=>p.Contract!),
                curves,[],[],sources.Select(s=>s.Series));
        }
        internal CurveShift Move(ConnectedCurvePoint point,string amount)=>new(CurveShockCoordinate.MarketQuote,
            new PointCurveTarget(point.Key.Curve,[point.Key.PointId]),point.Unit,ExactDecimal.Parse(amount),CurveShift.ParentAdditive);
        internal ConnectedScenario Shift(ConnectedCurvePoint point,string amount)=>Base.WithShifts([Move(point,amount)]);

        // Exercise the real independent cash and stock consumers, not a second service formula.
        internal ExactMoney OtherOwners(ConnectedScenario scenario,string fee,string stock)
        {
            var cash=new TimedCashPayment(Terms.Currency,At);var cashBinding=Guid.NewGuid();
            var gas=(ConnectedSource)Gas.Expression;
            var inventory=new GasCommodityHoldingInstrument(Guid.NewGuid(),gas.Request,Gas.Unit.Quantity,Terms.Currency,null,GasHoldingCarrying.Nominal);
            var trade=new Trade(Guid.NewGuid(),Guid.NewGuid(),[new(cashBinding,cash,new(ExactDecimal.Parse(fee),Terms.Currency)),
                new(Guid.NewGuid(),inventory,new(ExactDecimal.Parse(stock),Gas.Unit.Quantity))]);
            var paid=new RecordedCashValuation(trade,cashBinding,[],RecordedCashValuation.Policy,Context.ContextId,Context.KnownAt);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
                {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(trade.Id,cashBinding),paid)};
            var prepared=RiskEngine.PrepareConnected(ConnectedValuation.Prepare(scenario,Snapshot),
                ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(gas.Request,Gas.Key),[ValuationPortfolio.FromTrades([trade],setup)]);
            return RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy).Groups.Single().BaseValues.Single().Value;
        }
    }
    static void Equal(ExactMoney actual,string expected)
    {
        if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception($"Expected {expected}, got {actual.ToExactString()}.");
    }
    static void Reject(string code,Action action)
    {
        try{action();throw new Exception("Expected "+code+".");}catch(MarketDataException error)when(error.Code==code){}
    }
}
