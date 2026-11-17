using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using System.Collections.Immutable;

internal static class GasTransportFinancialAdapterChecks
{
    internal static void Run()
    {
        var f=new Fixture();var read=GasTransportFinancialAdapter.Read(f.Catalog,f.Row);
        Equal(read.Cash!.State.Outstanding(f.Snapshot()),"-12.5");
        var market=f.Market("20");var rights=GasTransportFinancialAdapter.Instrument(read,market.Source,market.Evaluation);
        var trade=new Trade(read.TradeId,read.BookId,[new(read.InstrumentId,rights,rights.UnitQuantity,retainedInstrumentId:read.InstrumentId),
            new(read.Cash.BindingId,read.Cash.Instrument,read.Cash.Quantity)]);
        var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
            {BindingStates=ImmutableDictionary<MeasureBinding,IBindingValuationState>.Empty.Add(new(read.TradeId,read.Cash.BindingId),read.Cash.State)};
        var portfolio=ValuationPortfolio.FromTrades([trade],setup);
        var prepared=RiskEngine.PrepareConnected(market.Source.Valuation,ImmutableDictionary<CurveRequest,CurvePointKey>.Empty.Add(market.Source.Market,market.Source.Point),[portfolio]);
        var point=market.Source.Valuation.Scenario.Plan.Points[market.Source.Point];
        var bump=new ConnectedRiskBump(CurveShockCoordinate.MarketQuote,new PointCurveTarget(point.Key.Curve,[point.Key.PointId]),point.Unit,ExactDecimal.Parse("1"),ConnectedRiskBump.Convention);
        var values=RiskEngine.CalculateExact(prepared,[bump],RiskEngine.ExactNumericalPolicy).Groups.Single().Factors.Single().Totals.Single().Values;
        Equal(values.Base,"7.5");Equal(values.Up,"8.5");Equal(values.Down,"6.5");
        if(read.State!.Rights.CapacityVersionId!=f.Capacity||read.Terms.Rights.Currency.Revision!=f.Valuation||
            read.Terms.OperationalReference!=f.Operational||read.Mark!.Market.Curve.Revision.RevisionId!=f.Definition)
            throw new Exception("The financial source must retain independent exact operating/valuation references and selected capacity/quote definitions.");
        var amended=GasTransportFinancialAdapter.Read(f.Catalog,f.Amended());
        var amendedRights=GasTransportFinancialAdapter.Instrument(amended,market.Source,market.Evaluation);
        if(amended.InstrumentId!=read.InstrumentId||amended.TradeId!=read.TradeId||amended.State!.Rights.CapacityVersionId==read.State.Rights.CapacityVersionId||
            amendedRights.EconomicIdentity==rights.EconomicIdentity)
            throw new Exception("The independent amendment must change financial terms while retaining its native agreement identity and exact same quote.");
        if(amendedRights.Id==rights.Id)
            throw new Exception("Distinct accepted financial states must have distinct valuation identities without changing their retained agreement.");
        if(rights.Id==read.InstrumentId||GasTransportFinancialAdapter.Instrument(read,market.Source,market.Evaluation).Id!=rights.Id||
            GasTransportFinancialAdapter.Instrument(amended,market.Source,market.Evaluation).Id!=amendedRights.Id||
            trade.Instruments.Single(p=>p.Id==read.InstrumentId).RetainedInstrumentId!=read.InstrumentId)
            throw new Exception("A financial state's valuation identity must be stable and separate from its retained native binding.");
        foreach(var mutate in new Action<JsonObject>[] {
            o=>o["terms"]!["rightsValuationPolicy"]="",o=>o["terms"]!["expiryPolicy"]="",
            o=>o["terms"]!["feeProfile"]="",o=>o["terms"]!["fixedFeeAmount"]="-1",
            o=>o["terms"]!["feeDirection"]="",o=>o["terms"]!["feeDueAt"]=null,
            o=>o["mark"]!["marketDefinitionRevisionId"]=Guid.NewGuid().ToString(),
            o=>o["financialState"]!["remainingRightsIdentity"]=Guid.NewGuid().ToString(),
            o=>o["cash"]!["amount"]="12.5",o=>o["cash"]!["instrumentId"]=Guid.NewGuid().ToString(),
            o=>o["cash"]!["referenceRevisionId"]=Guid.NewGuid().ToString(),o=>o["cash"]=null,
            o=>o["knownAt"]="2035-01-15T11:59:59Z"})
            Reject("gas_transport_financial_source",()=>GasTransportFinancialAdapter.Read(f.Catalog,f.Change(mutate)));
        var missing=GasTransportFinancialAdapter.Read(f.Catalog,f.Change(o=>{o["mark"]=null;o["unpricedReason"]="rights_mark_required";}));
        Reject("gas_transport_financial_unpriced",()=>GasTransportFinancialAdapter.Instrument(missing,null,null));
        if(missing.Cash is null)throw new Exception("Missing rights value must not erase inspectable independent cash.");
        var future=f.Change(o=>
        {
            var head=o["financialState"]!.DeepClone();var id=Guid.NewGuid();
            head["id"]=id.ToString();head["revision"]=2;head["previousId"]=f.FinancialState.ToString();head["remainingRightsIdentity"]=id.ToString("D");
            head["capacityVersionId"]=Guid.NewGuid().ToString();head["effectiveAt"]=f.At.AddDays(1).ToString("O");head["sourceAsOf"]=f.At.AddDays(1).ToString("O");
            head["sourceContextId"]=Guid.NewGuid().ToString();head["sourceKnownAt"]=f.At.AddMinutes(20).ToString("O");head["knownAt"]=f.At.AddMinutes(21).ToString("O");
            o["stateHistory"]!.AsArray().Add(head);o["financialState"]=null;o["unpricedReason"]="financial_state_future";o["mark"]=null;
        });
        Reject("gas_transport_financial_unpriced",()=>GasTransportFinancialAdapter.Instrument(GasTransportFinancialAdapter.Read(f.Catalog,future),null,null));

        var paid=f.Change(o=>o["cash"]!["payments"]=JsonSerializer.SerializeToNode(new[]{
            f.Payment(f.PaymentId,1,"-5",f.At.AddMinutes(-30),f.At.AddMinutes(-20)),
            f.Payment(f.PaymentId,2,"-5",f.At.AddMinutes(30),f.At.AddHours(1))}));
        var history=GasTransportFinancialAdapter.Read(f.Catalog,paid).Cash!.State;
        Equal(history.Outstanding(f.Snapshot()),"-12.5");
        Equal(history.Outstanding(f.Snapshot().WithKnowledge(f.Context,f.At.AddMinutes(-10))),"-7.5");
        var incomplete=f.Change(o=>o["cash"]!["payments"]=JsonSerializer.SerializeToNode(new[]{
            f.Payment(f.PaymentId,2,"-5",f.At.AddMinutes(-30),f.At.AddHours(1))}));
        Reject("gas_transport_financial_source",()=>GasTransportFinancialAdapter.Read(f.Catalog,incomplete));
        var overpaid=f.Change(o=>o["cash"]!["payments"]=JsonSerializer.SerializeToNode(new[]{
            f.Payment(f.PaymentId,1,"-20",f.At.AddMinutes(-30),f.At.AddMinutes(-20)),
            f.Payment(f.PaymentId,2,"-5",f.At.AddMinutes(-30),f.At.AddHours(1))}));
        Reject("gas_transport_financial_source",()=>GasTransportFinancialAdapter.Read(f.Catalog,overpaid));
        var a=Guid.NewGuid();var b=Guid.NewGuid();
        var simultaneous=new[]{f.Payment(a,1,"-10",f.At.AddMinutes(-30),f.At.AddMinutes(-20)),
            f.Payment(a,2,"-2.5",f.At.AddMinutes(-30),f.At.AddHours(1)),f.Payment(b,1,"-10",f.At.AddMinutes(-30),f.At.AddHours(1))};
        foreach(var rows in new[]{simultaneous,simultaneous.Reverse().ToArray()})
            Equal(GasTransportFinancialAdapter.Read(f.Catalog,f.Change(o=>o["cash"]!["payments"]=JsonSerializer.SerializeToNode(rows))).Cash!.State.Outstanding(f.Snapshot()),"0");
        var zero=f.Change(o=>{o["terms"]!["fixedFeeAmount"]="0";o["terms"]!["feeDueAt"]=null;o["cash"]=null;});
        if(GasTransportFinancialAdapter.Read(f.Catalog,zero).Cash is not null)throw new Exception("Zero fee must not invent a zero cash owner.");
        var provider=f.Change(o=>o["terms"]!["perspective"]="provider");
        Equal(GasTransportFinancialAdapter.Read(f.Catalog,provider).Cash!.Quantity.Amount,"-12.5");
        var closed=GasTransportFinancialAdapter.Read(f.Catalog,f.Closed());
        var terminal=GasTransportFinancialAdapter.Instrument(closed,null,null);
        if(terminal.Expiry is null||terminal.Source is not null||closed.Cash is null)
            throw new Exception("Accepted closed rights retain exact expiry proof, no live mark and independent cash.");
        if(closed.InstrumentId!=read.InstrumentId||terminal.Id==rights.Id||terminal.Id==amendedRights.Id||terminal.Id==closed.InstrumentId||
            GasTransportFinancialAdapter.Instrument(closed,null,null).Id!=terminal.Id||closed.Cash.Instrument.Id!=read.Cash.Instrument.Id)
            throw new Exception("Accepted expiry must identify its closed valuation state while retaining the agreement and independent fee instrument.");
        var crossDate=new Fixture(TradingInstant.Parse("2020-12-19T23:00:00Z","synthetic_time"));
        var selectedDay=crossDate.Change(o=>o["effectiveOn"]="2020-12-20");
        var dated=GasTransportFinancialAdapter.Read(crossDate.Catalog,selectedDay);
        if(dated.At!=crossDate.At||dated.ContextId!=crossDate.Context||dated.Terms.Definitions.RevisionId!=crossDate.Definition)
            throw new Exception("An explicit business day must preserve its independently supplied valuation instant, context and financial source.");
        Console.WriteLine("PASS financial Transport source: exact state/mark/reference pins, missing value refusal, explicit expiry and complete independent cash history");
    }

    internal sealed class Fixture
    {
        internal Guid Catalog=Guid.NewGuid(),Definition=Guid.NewGuid(),Trade=Guid.NewGuid(),Instrument=Guid.NewGuid(),Book=Guid.NewGuid(),Context=Guid.NewGuid(),
            Operation=Guid.NewGuid(),Entitlement=Guid.NewGuid(),Capacity=Guid.NewGuid(),FinancialState=Guid.NewGuid(),Mark=Guid.NewGuid(),Binding=Guid.NewGuid(),PaymentId=Guid.NewGuid();
        internal ReferenceRevision Valuation=new(Guid.NewGuid(),Guid.NewGuid()),Operational=new(Guid.NewGuid(),Guid.NewGuid());
        internal DateTimeOffset At=TradingInstant.Parse("2035-01-15T12:00:00Z","synthetic_time");
        internal JsonElement Row;
        internal Fixture(DateTimeOffset? at=null)
        {
            if(at is {} selected)At=selected;
            FinancialState=Operation;
            var currency=new ReferenceKey(Valuation,"synthetic_currency");var cash=new TimedCashPayment(currency,At);
            var state=new{id=FinancialState,agreementId=Trade,revision=1,previousId=(Guid?)null,entitlementId=Entitlement,capacityVersionId=Capacity,
                remainingRightsIdentity=FinancialState.ToString("D"),status="live",from=At.AddDays(-1),through=At.AddDays(1),effectiveAt=At.AddHours(-1),
                knownAt=At.AddMinutes(-59),sourceContextId=Guid.NewGuid(),sourceKnownAt=At.AddHours(-1),sourceAsOf=At.AddHours(-1),policyId=(Guid?)null,policyRevision=(int?)null,evidence="Synthetic legal agreement"};
            var mark=new{id=Mark,revision=1,previousId=(Guid?)null,financialStateId=FinancialState,valuationAt=At,marketDefinitionRevisionId=Definition,
                curveCode="synthetic_rights",contractId=Guid.NewGuid(),seriesId=Guid.NewGuid(),observationId=Guid.NewGuid(),source="synthetic_manual",quote="synthetic_package_quote",
                effectiveAt=At,knownAt=At.AddMinutes(1),sourceContextId=Guid.NewGuid(),sourceKnownAt=At.AddSeconds(1),sourceAsOf=At,evidence="Synthetic explicit signed current value"};
            Row=JsonSerializer.SerializeToElement(new{implementation=GasTransportFinancialProfile.Version,tradeId=Trade,instrumentId=Instrument,revision=1,bookId=Book,
                operationId=Operation,publicationOrder=1,effectiveAt=At.AddHours(-1),legalEntityId=Guid.NewGuid(),legalEntityRevision=1,counterpartyId=Guid.NewGuid(),counterpartyRevision=1,
                tradeReference=1,instrumentReference=1,contextId=Context,asOf=At,knownAt=At.AddHours(3),effectiveOn=At.UtcDateTime.ToString("yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture),authorityUntil=At.AddDays(100),
                terms=new{definitionRevisionId=Definition,valuationReferenceCatalogId=Valuation.CatalogId,valuationReferenceRevisionId=Valuation.RevisionId,
                    operationalReferenceCatalogId=Operational.CatalogId,operationalReferenceRevisionId=Operational.RevisionId,packageQuantity="synthetic_package",currency="synthetic_currency",
                    profile=GasTransportFinancialProfile.Version,perspective="customer",rightsValuationPolicy=GasTransportDirectRightsPolicy.Version,feeProfile=GasTransportFinancialProfile.Fee,
                    fixedFeeAmount="12.5",feeDirection="pay",feeDueAt=(DateTimeOffset?)At,expiryPolicy=GasTransportDirectRightsPolicy.ExpiryAtThrough,entitlementId=Entitlement,
                    operationalProduct="synthetic_product",operationalQuantity="synthetic_gas_quantity",from=At.AddDays(-1),through=At.AddDays(1)},
                financialState=state,stateHistory=new[]{state},mark,markHistory=new[]{mark},unpricedReason=(string?)null,expiry=(object?)null,
                cash=new{bindingId=Binding,instrumentId=cash.Id,amount="-12.5",currency="synthetic_currency",referenceCatalogId=Valuation.CatalogId,referenceRevisionId=Valuation.RevisionId,
                    dueAt=At,effectiveAt=At.AddHours(-1),payments=Array.Empty<object>()}});
        }
        internal object Payment(Guid id,int revision,string amount,DateTimeOffset paidAt,DateTimeOffset knownAt)=>new{trade_id=Trade,binding_id=Binding,book_id=Book,
            reference_catalog_id=Valuation.CatalogId,reference_revision_id=Valuation.RevisionId,currency_code="synthetic_currency",allocation_id=id,revision,paid_at=paidAt,amount,known_at=knownAt};
        internal MarketSnapshot Snapshot()=>new MarketSnapshot(Guid.NewGuid(),new(Catalog,Definition),At,At,
            InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[]).WithKnowledge(Context,At.AddHours(3));
        internal (CapturedPriceSource Source,GasTransportDirectRightsEvaluation Evaluation) Market(string signedValue)
        {
            var m=Row.GetProperty("mark");var d=new DefinitionRevision(Catalog,Definition);
            var curve=new CurveKey(d,m.GetProperty("curveCode").GetString()!);var request=new CurveRequest(curve,new(d,m.GetProperty("contractId").GetGuid()));
            var q=new QuoteSpecification(new(Valuation,m.GetProperty("quote").GetString()!),QuoteKind.Outright,new(Valuation,"synthetic_currency"),
                new(Valuation,"synthetic_package"),ExactDecimal.Parse("1"),ExactDecimal.Parse("0.01"),ExactDecimal.Parse("0.01"),ExactDecimal.Parse("-1000"),ExactDecimal.Parse("1000"));
            var series=new MarketSeries(new(d,m.GetProperty("seriesId").GetGuid()),request.Contract,q.Key,null);
            var observation=new MarketObservation(new(Catalog,m.GetProperty("observationId").GetGuid()),series.Key,new(Valuation,m.GetProperty("source").GetString()!),
                ObservationRole.TraderMark,PublicationState.Final,At,ExactDecimal.Parse(signedValue));
            var context=new ResolvedCalculationContext(Context,"synthetic_exact_financial_quote",At,At.AddHours(3),At,At);
            var contract=new MarketContract(request.Contract,ContractKind.Forward,new(2035,1,15),new(2035,1,16),At.AddDays(2),null);
            var point=new ConnectedCurvePoint(new(curve,Guid.NewGuid()),contract.DeliveryFrom,contract.DeliveryTo,PriceUnit.FromQuote(q),
                new ConnectedSource(request,new(series.Key,InputUse.Variable),observation,q,series),contract);
            var resolver=new TemporalCurveBindings([],[new(curve,At.AddDays(-1))]);
            var scenario=ConnectedScenario.Base(ConnectedCurves.Compile([new(curve,[point])],[point.Key],context,resolver));
            var snapshot=Snapshot() with{Selections=[new(new(series.Key,InputUse.Variable),observation)]};
            return(new(request,ConnectedValuation.Prepare(scenario,snapshot),point.Key),new(context,scenario.Id,ConnectedCurves.NumericalPolicy));
        }
        internal JsonElement Change(Action<JsonObject> mutation)
        {
            var node=JsonNode.Parse(Row.GetRawText())!.AsObject();mutation(node);return JsonSerializer.SerializeToElement(node);
        }
        internal JsonElement Amended()=>Change(o=>
        {
            var state=o["financialState"]!.DeepClone();var id=Guid.NewGuid();
            state["id"]=id.ToString();state["revision"]=2;state["previousId"]=FinancialState.ToString();state["remainingRightsIdentity"]=id.ToString("D");
            state["capacityVersionId"]=Guid.NewGuid().ToString();state["effectiveAt"]=At.ToString("O");state["knownAt"]=At.AddMinutes(3).ToString("O");
            state["sourceContextId"]=Guid.NewGuid().ToString();state["sourceKnownAt"]=At.AddMinutes(2).ToString("O");state["sourceAsOf"]=At.ToString("O");
            o["stateHistory"]!.AsArray().Add(state.DeepClone());o["financialState"]=state;
            var mark=o["mark"]!.DeepClone();mark["id"]=Guid.NewGuid().ToString();mark["financialStateId"]=id.ToString();
            mark["knownAt"]=At.AddMinutes(5).ToString("O");mark["sourceContextId"]=Guid.NewGuid().ToString();mark["sourceKnownAt"]=At.AddMinutes(4).ToString("O");
            o["markHistory"]!.AsArray().Add(mark.DeepClone());o["mark"]=mark;
        });
        internal JsonElement Closed()=>Change(o=>
        {
            var closed=o["financialState"]!.DeepClone();var id=Guid.NewGuid();var end=At.AddDays(1);
            closed["id"]=id.ToString();closed["revision"]=2;closed["previousId"]=FinancialState.ToString();closed["remainingRightsIdentity"]=id.ToString("D");closed["status"]="closed";
            closed["effectiveAt"]=end.ToString("O");closed["knownAt"]=end.AddMinutes(1).ToString("O");closed["sourceAsOf"]=end.ToString("O");closed["sourceKnownAt"]=end.ToString("O");
            closed["policyId"]=Guid.NewGuid().ToString();closed["policyRevision"]=1;
            o["stateHistory"]!.AsArray().Add(closed.DeepClone());o["financialState"]=closed;
            o["asOf"]=end.ToString("O");o["knownAt"]=end.AddHours(3).ToString("O");o["effectiveOn"]="2035-01-16";o["mark"]=null;o["unpricedReason"]=null;
            o["expiry"]=JsonSerializer.SerializeToNode(new{id,agreementId=Trade,effectiveAt=end,knownAt=end.AddMinutes(1),policyId=Guid.Parse(closed["policyId"]!.GetValue<string>()),policyRevision=1});
        });
    }
    static void Equal(ExactMoney actual,string expected){if(actual!=ExactMoney.Parse(expected,actual.Currency))throw new Exception("Expected "+expected+", got "+actual.ToExactString());}
    static void Equal(ExactDecimal actual,string expected){if(actual!=ExactDecimal.Parse(expected))throw new Exception("Expected "+expected+", got "+actual);}
    static void Reject(string code,Action action){try{action();throw new Exception("Expected "+code);}catch(MarketDataException error)when(error.Code==code){}}
}
