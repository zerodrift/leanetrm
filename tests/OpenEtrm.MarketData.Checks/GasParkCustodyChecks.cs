using System.Collections.Immutable;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasParkCustodyChecks
{
    static readonly ReferenceRevision Reference=new(Guid.Parse("91111111-1111-1111-1111-111111111111"),Guid.Parse("92222222-2222-2222-2222-222222222222"));
    static readonly ReferenceKey Unit=new(Reference,"synthetic_quantity"),Currency=new(Reference,"synthetic_currency"),Point=new(Reference,"synthetic_point");
    static readonly Guid Agreement=Guid.Parse("93333333-3333-3333-3333-333333333333"),LotA=Guid.Parse("94444444-4444-4444-4444-444444444444"),LotB=Guid.Parse("95555555-5555-5555-5555-555555555555");
    static readonly DateTimeOffset Start=At("2032-01-15T10:00:00Z"),Known=At("2032-01-25T00:00:00Z");
    static DateTimeOffset At(string value)=>TradingInstant.Parse(value,"synthetic_time");
    static Quantity Q(string amount)=>new(ExactDecimal.Parse(amount),Unit);
    static Money M(string amount)=>new(ExactDecimal.Parse(amount),Currency);
    static GasParkCustodyTerms Terms(string perspective,string direction="-1",string fee="12.5",DateTimeOffset? due=null)=>new(
        GasParkCustodyTerms.Version,"park",perspective,GasParkCustodyTerms.RetainTitle,GasParkCustodyTerms.SameBasis,
        GasParkCustodyTerms.ReturnsAfterAcceptance,GasParkCustodyTerms.NondecreasingReturns,GasParkCustodyTerms.NoUse,GasParkCustodyTerms.LossUnqualified,
        GasParkCustodyTerms.SeparatePhysical,GasParkCustodyTerms.UnacceptedPlan,Point,Unit,Currency,Start,Start.AddDays(2),
        M(fee),ExactDecimal.Parse(direction),fee=="0"?null:due??Start.AddDays(4),GasParkCustodyTerms.FeeAtAcceptance);
    static GasParkCustodyAllocation A(Guid lot,string quantity)=>new(lot,Q(quantity));
    static ImmutableArray<GasParkCustodyAllocation> Lots(string perspective)=>perspective=="customer"?[A(LotA,"400"),A(LotB,"320")]:[];
    static GasParkCustodyAcceptance Accepted(string perspective)=>new(Guid.NewGuid(),Q("720"),Start,Known.AddDays(-1),Guid.NewGuid(),Lots(perspective));
    static GasParkCustodySource Source(DateTimeOffset at,GasParkCustodyAcceptance? accepted,params GasParkCustodyReturn[] returns)=>
        new(Agreement,Q("720"),accepted,[..returns],at,Known);

    internal static void Run()
    {
        foreach(var perspective in new[]{"customer","provider"})
        foreach(var sign in new[]{"-1","1"})
        {
            var terms=Terms(perspective,sign);var accepted=Accepted(perspective);
            var before=Source(Start.AddHours(-1),null);
            var empty=GasParkCustodyLedger.Balance(terms,before);
            Equal(empty.InCustody,"0");Equal(empty.Reserved,"0");Equal(empty.OwnedQuantityChange,"0");
            var planned=GasParkCustodyValuation.Plan(terms,before);
            if(planned.PlannedParkAt!=Start)throw new Exception("The selected unaccepted custody plan must retain its explicit earliest date.");
            Equal(planned.ForecastFee,sign=="-1"?"-12.5":"12.5");Equal(planned.DeterminedFee,"0");
            var late=GasParkCustodyValuation.Plan(terms,Source(Start.AddHours(1),null));
            if(late.PlannedParkAt!=Start.AddHours(1))throw new Exception("A late unaccepted plan must not assert past custody.");
            Reject("gas_custody_acceptance_window",()=>GasParkCustodyValuation.Plan(terms,Source(terms.ReturnAt,null)));
            Reject("gas_custody_acceptance_window",()=>GasParkCustodyValuation.Plan(Terms(perspective,sign,"0"),Source(terms.ReturnAt,null)));
            var first=new GasParkCustodyReturn(Guid.NewGuid(),accepted.Id,Q("270"),Start.AddDays(1),Known,Guid.NewGuid(),
                perspective=="customer"?[A(LotA,"70"),A(LotB,"200")]:[]);
            var second=new GasParkCustodyReturn(Guid.NewGuid(),accepted.Id,Q("450"),Start.AddDays(3),Known,Guid.NewGuid(),
                perspective=="customer"?[A(LotA,"330"),A(LotB,"120")]:[]);
            var held=GasParkCustodyLedger.Balance(terms,Source(Start,accepted));
            Equal(held.InCustody,"720");Equal(held.Reserved,perspective=="customer"?"720":"0");Equal(held.OwnedQuantityChange,"0");
            var partial=GasParkCustodyLedger.Balance(terms,Source(terms.ReturnAt,accepted,first));
            Equal(partial.InCustody,"450");Equal(partial.Overdue,"450");Equal(partial.Reserved,perspective=="customer"?"450":"0");
            var finished=GasParkCustodyLedger.Balance(terms,Source(second.At,accepted,first,second));
            Equal(finished.InCustody,"0");Equal(finished.Reserved,"0");Equal(finished.OwnedQuantityChange,"0");
            var current=Source(terms.ReturnAt,accepted,first);
            GasParkCustodyLedger.RequireReturn(terms,current,Q("450"),terms.ReturnAt,perspective=="customer"?[A(LotA,"330"),A(LotB,"120")]:[]);
            Reject("gas_custody_return",()=>GasParkCustodyLedger.RequireReturn(terms,current,Q("451"),terms.ReturnAt,perspective=="customer"?[A(LotA,"331"),A(LotB,"120")]:[]));
            Reject("gas_custody_return",()=>GasParkCustodyLedger.RequireReturn(terms,current,Q("1"),first.At.AddMicroseconds(-1),perspective=="customer"?[A(LotA,"1")]:[]));
            Reject("gas_custody_return",()=>GasParkCustodyLedger.RequireReturn(terms,current,Q("1"),terms.ReturnAt.AddMicroseconds(1),perspective=="customer"?[A(LotA,"1")]:[]));
            Reject("gas_custody_return",()=>GasParkCustodyLedger.RequireReturn(terms,Source(Start,null),Q("1"),Start,[]));
            if(perspective=="customer")Reject("gas_custody_allocation",()=>GasParkCustodyLedger.RequireReturn(terms,current,Q("400"),terms.ReturnAt,[A(LotA,"400")]));
            foreach(var source in new[]{Source(Start,accepted),Source(terms.ReturnAt,accepted,first),Source(second.At,accepted,first,second)})
            {
                var fee=GasParkCustodyValuation.Plan(terms,source);
                Equal(fee.ForecastFee,"0");Equal(fee.DeterminedFee,sign=="-1"?"-12.5":"12.5");
                if(!GasParkCustodyValuation.Components(terms,source).IsEmpty)
                    throw new Exception("Accepted custody replaces the forecast fee with one independent native cash membership; no gas claim or debt is composed.");
            }
            var components=GasParkCustodyValuation.Components(terms,before);
            if(components.Length!=1||components.Single().Instrument is not TimedCashPayment)
                throw new Exception("A custody service composes only its selected fixed-fee plan. It must not invent a cancelling gas pair.");
            Reject("gas_custody_source",()=>GasParkCustodyLedger.Balance(terms,Source(Start,accepted with{Quantity=Q("719")})));
            Reject("gas_custody_source",()=>GasParkCustodyLedger.Balance(terms,Source(first.At,accepted,first,first)));
            Reject("gas_custody_source",()=>GasParkCustodyLedger.Balance(terms,Source(first.At,accepted,first with{At=Start})));
            Reject("gas_custody_source",()=>GasParkCustodyLedger.Balance(terms,Source(first.At,accepted,first with{AcceptanceId=Guid.NewGuid()})));
            Reject("gas_custody_source",()=>GasParkCustodyLedger.Balance(terms,Source(first.At,accepted,first with{KnownAt=Known.AddMicroseconds(1)})));
            Reject("gas_custody_source",()=>GasParkCustodyLedger.Balance(terms,Source(first.At,accepted,first with{Quantity=Q("721")})));
            Reject("gas_custody_source",()=>GasParkCustodyLedger.Balance(terms,Source(first.At,null,first)));
            Reject("gas_custody_source",()=>GasParkCustodyLedger.Balance(terms,Source(second.At,accepted,first,second with{At=first.At.AddMicroseconds(-1)})));
            var observation=Guid.NewGuid();var observedReturn=new GasParkCustodyReturn(Guid.NewGuid(),accepted.Id,Q("100"),first.At,Known,observation,
                perspective=="customer"?[A(LotA,"100")]:[]);
            Reject("gas_custody_source",()=>GasParkCustodyLedger.Balance(terms,Source(first.At,accepted,observedReturn,observedReturn with{Id=Guid.NewGuid()})));
            var identity=DefinitionIdentity.For(terms).Canonical;
            foreach(var policy in new[]{GasParkCustodyTerms.RetainTitle,GasParkCustodyTerms.NoUse,GasParkCustodyTerms.LossUnqualified,GasParkCustodyTerms.SeparatePhysical,GasParkCustodyTerms.UnacceptedPlan,GasParkCustodyTerms.FeeAtAcceptance,GasParkCustodyTerms.NondecreasingReturns})
                if(!identity.Contains(policy,StringComparison.Ordinal))throw new Exception("Every selected custody and fee policy must enter immutable economic identity.");
        }
        Reservations();TermsCapacity();NativeHandoff();
        Console.WriteLine("PASS Park custody: retained title, foreign custody without owned gas, explicit partial release, overdue balances and independent fixed-fee handoff");
    }

    static void Reservations()
    {
        var terms=Terms("customer");var account=Guid.NewGuid();var originalA=Guid.NewGuid();var originalB=Guid.NewGuid();
        GasOwnedLot Lot(Guid id)=>new(id,account,Q("500"),Start.AddDays(-2),Start.AddDays(-1),Start.AddDays(-1),Guid.NewGuid());
        var a=Lot(LotA);var b=Lot(LotB);var other=new GasStockReservation(Guid.NewGuid(),LotA,Q("100"),Start.AddDays(-1),Start.AddDays(-1),null);
        var funding=ImmutableArray.Create(new GasParkCustodyLotSource(a,[other],[]),new GasParkCustodyLotSource(b,[],[]));
        GasParkCustodyLedger.RequireAcceptanceSelection(terms,Source(Start,null),Q("720"),Start,Lots("customer"));
        Reject("gas_custody_acceptance_window",()=>GasParkCustodyLedger.RequireAcceptanceSelection(terms,Source(Start,null),Q("719"),Start,[A(LotA,"400"),A(LotB,"319")]));
        Reject("gas_custody_allocation",()=>GasParkCustodyLedger.RequireAcceptanceSelection(terms,Source(Start,null),Q("720"),Start,[]));
        GasParkCustodyLedger.RequireAcceptance(terms,Source(Start,null),Q("720"),Start,Lots("customer"),funding);
        Reject("gas_custody_funding",()=>GasParkCustodyLedger.RequireAcceptance(terms,Source(Start,null),Q("720"),Start,[A(LotA,"401"),A(LotB,"319")],funding));
        var future=new GasStockConsumption(Guid.NewGuid(),LotB,Q("200"),GasStockUse.Delivery,Start.AddDays(1),Known.AddDays(-1),Guid.NewGuid(),null);
        Reject("gas_custody_funding",()=>GasParkCustodyLedger.RequireAcceptance(terms,Source(Start,null),Q("720"),Start,Lots("customer"),
            [funding[0],new(b,[],[future])]));
        var active=ImmutableArray.Create(new GasParkCustodyReservation(LotA,originalA,Q("400")),new GasParkCustodyReservation(LotB,originalB,Q("320")));
        var transition=GasParkCustodyLedger.ReturnReservations(active,[A(LotA,"70"),A(LotB,"200")],Q("270"));
        if(transition.Length!=2||transition.Single(r=>r.LotId==LotA).ReleaseReservationId!=originalA||transition.Single(r=>r.LotId==LotB).ReleaseReservationId!=originalB)
            throw new Exception("A partial return releases only its exact prior custody reservations.");
        Equal(transition.Single(r=>r.LotId==LotA).Remainder,"330");Equal(transition.Single(r=>r.LotId==LotB).Remainder,"120");
        Reject("gas_custody_allocation",()=>GasParkCustodyLedger.ReturnReservations(active,[A(LotA,"401")],Q("401")));
        Reject("gas_custody_allocation",()=>GasParkCustodyLedger.ReturnReservations(active,[A(Guid.NewGuid(),"1")],Q("1")));
        Reject("gas_custody_allocation",()=>GasParkCustodyLedger.ReturnReservations(active,[A(LotA,"1"),A(LotA,"1")],Q("2")));
        var returned=Start.AddDays(1);var remainderA=Guid.NewGuid();var remainderB=Guid.NewGuid();
        var aBefore=GasOwnedLedger.Balance(a,[other,new(originalA,LotA,Q("400"),Start,Known,null)],[],Start,Known);
        var aAfter=GasOwnedLedger.Balance(a,[other,new(originalA,LotA,Q("400"),Start,Known,returned),new(remainderA,LotA,Q("330"),returned,Known,null)],[],returned,Known);
        var bAfter=GasOwnedLedger.Balance(b,[new(originalB,LotB,Q("320"),Start,Known,returned),new(remainderB,LotB,Q("120"),returned,Known,null)],[],returned,Known);
        Equal(aBefore.Owned,"500");Equal(aAfter.Owned,"500");Equal(bAfter.Owned,"500");
        Equal(aBefore.Available,"0");Equal(aAfter.Available,"70");Equal(bAfter.Available,"380");
        GasParkCustodyLedger.RequireAcceptance(Terms("provider"),Source(Start,null),Q("720"),Start,[],[]);
        Reject("gas_custody_allocation",()=>GasParkCustodyLedger.RequireAcceptance(Terms("provider"),Source(Start,null),Q("720"),Start,Lots("customer"),funding));
    }

    static void TermsCapacity()
    {
        Reject("gas_custody_cash_capacity",()=>Terms("customer",fee:"100000000000000000000"));
        Reject("gas_custody_cash_capacity",()=>Terms("provider",due:At("2200-01-01T00:00:00Z")));
        var zero=Terms("provider",fee:"0");
        if(zero.FeeDueAt is not null||!GasParkCustodyValuation.Components(zero,Source(Start.AddHours(-1),null)).IsEmpty)
            throw new Exception("An explicitly zero fee has no invented due time or native cash membership.");
    }

    static void NativeHandoff()
    {
        var f=new Fixture();var quote=f.Quotes[0];var at=f.Snapshot().ValuationAt;
        var start=at.AddDays(1);var due=start.AddDays(4);var known=due.AddDays(1);
        var mark=new CurveRequest(f.C("future"),f.K(1));
        var bump=new QuoteBump(new(f.S(1),InputUse.Variable),quote.Key,PriceUnit.FromQuote(quote),ExactDecimal.Parse("1"),QuoteBump.Convention);
        Quantity Quantity(string amount)=>new(ExactDecimal.Parse(amount),quote.Quantity);
        foreach(var perspective in new[]{"customer","provider"})
        foreach(var sign in new[]{"-1","1"})
        {
            var terms=new GasParkCustodyTerms(GasParkCustodyTerms.Version,"park",perspective,GasParkCustodyTerms.RetainTitle,
                GasParkCustodyTerms.SameBasis,GasParkCustodyTerms.ReturnsAfterAcceptance,GasParkCustodyTerms.NondecreasingReturns,
                GasParkCustodyTerms.NoUse,GasParkCustodyTerms.LossUnqualified,GasParkCustodyTerms.SeparatePhysical,GasParkCustodyTerms.UnacceptedPlan,
                f.R("location"),quote.Quantity,quote.Currency,start,start.AddDays(2),new(ExactDecimal.Parse("12.5"),quote.Currency),
                ExactDecimal.Parse(sign),due,GasParkCustodyTerms.FeeAtAcceptance);
            ImmutableArray<GasParkCustodyAllocation> Allocation(string a,string b)=>perspective=="customer"?
                [new(LotA,Quantity(a)),new(LotB,Quantity(b))]:[];
            var acceptance=new GasParkCustodyAcceptance(Guid.NewGuid(),Quantity("720"),start,start,Guid.NewGuid(),Allocation("400","320"));
            var partial=new GasParkCustodyReturn(Guid.NewGuid(),acceptance.Id,Quantity("270"),start.AddDays(1),start.AddDays(1),Guid.NewGuid(),Allocation("70","200"));
            var final=new GasParkCustodyReturn(Guid.NewGuid(),acceptance.Id,Quantity("450"),start.AddDays(3),start.AddDays(3),Guid.NewGuid(),Allocation("330","120"));
            var source=new GasParkCustodySource(Guid.NewGuid(),Quantity("720"),null,[],at,known);
            foreach(var (current,paid,expectedCash) in new[]{
                (source,"0","11.875"),
                (source with{At=start,Acceptance=acceptance},"0","11.875"),
                (source with{At=partial.At,Acceptance=acceptance,Returns=[partial]},"0","11.875"),
                (source with{At=final.At,Acceptance=acceptance,Returns=[partial,final]},"0","11.875"),
                (source with{At=final.At,Acceptance=acceptance,Returns=[partial,final]},"5","7.125"),
                (source with{At=due,Acceptance=acceptance,Returns=[partial,final]},"0","12.5"),
                (source with{At=due.AddDays(1),Acceptance=acceptance,Returns=[partial,final]},"0","12.5"),
                (source with{At=due,Acceptance=acceptance,Returns=[partial,final]},"12.5","0")})
            {
                var snapshot=(f.Snapshot() with{ValuationAt=current.At}).WithKnowledge(Guid.NewGuid(),known);
                var book=Guid.NewGuid();var trade=new AccountingOwnerKey(AccountingOwnerKind.Trade,current.AgreementId);
                var positions=new List<RiskPosition>();var instruments=new List<Instrument>();
                var states=ImmutableDictionary.CreateBuilder<MeasureBinding,IBindingValuationState>();
                void Hold(AccountingOwnerKey owner,Instrument instrument,Quantity quantity,bool actualCash)
                {
                    var binding=Guid.NewGuid();instruments.Add(instrument);positions.Add(new(owner,binding,book,instrument.Id,quantity,
                        new(new Money(default,quote.Currency),quantity.Specification),new(default,quote.Currency)));
                    if(instrument is TimedCashPayment cash)
                    {
                        var position=new AccountingPositionKey(owner,binding,book);
                        var flows=!actualCash||paid=="0"?ImmutableArray<AccountingFlow>.Empty:
                            [new(Guid.NewGuid(),1,position,current.At,known,ExactMoney.FromMoney(new(ExactDecimal.Parse(paid)*ExactDecimal.Parse(sign),quote.Currency)),AccountingFlowKind.EconomicCash)];
                        states.Add(new(owner,binding),new RecordedCashValuation(position,cash,quantity,flows,RecordedCashValuation.Policy,snapshot.Knowledge!.ContextId,known));
                    }
                }
                // These two independent titled lots are retained throughout. A
                // custody reservation never replaces owned quantity with a claim.
                if(perspective=="customer")
                    foreach(var (lot,amount) in new[]{(LotA,"400"),(LotB,"320")})
                        Hold(new(AccountingOwnerKind.NativeHolding,lot),new GasCommodityHoldingInstrument(Guid.NewGuid(),mark,quote.Quantity,quote.Currency,null,GasHoldingCarrying.Nominal),Quantity(amount),false);
                foreach(var component in GasParkCustodyValuation.Components(terms,current))Hold(trade,component.Instrument,component.Quantity,false);
                if(current.Acceptance is not null)
                    Hold(trade,new TimedCashPayment(quote.Currency,due),new(terms.SignedFixedFee.Amount,quote.Currency),true);
                var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
                    new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit)){BindingStates=states.ToImmutable()};
                var result=RiskEngine.CalculateExact(RiskEngine.Prepare(f.Model(),snapshot,
                    [new ValuationPortfolio([..instruments.DistinctBy(i=>i.Id)],[..positions],setup)]),perspective=="customer"?[bump]:[],RiskEngine.ExactNumericalPolicy);
                var cash=ExactDecimal.Parse(expectedCash)*ExactDecimal.Parse(sign);var stock=perspective=="customer"?ExactDecimal.Parse("720"):default;
                var group=result.Groups.Single();Equal(group.BaseValues.Single().Value,(stock*ExactDecimal.Parse("3")+cash).ToString());
                if(perspective=="customer")
                {
                    var values=group.Factors.Single().Totals.Single().Values;
                    Equal(values.Base,(stock*ExactDecimal.Parse("3")+cash).ToString());
                    Equal(values.Up,(stock*ExactDecimal.Parse("4")+cash).ToString());
                    Equal(values.Down,(stock*ExactDecimal.Parse("2")+cash).ToString());
                }
                else if(group.Factors.Length!=0)throw new Exception("Provider foreign custody has no owned gas or commodity price exposure.");
            }
        }
    }
    static void Equal(ExactMoney value,string expected){if(value.ToExactString()!=expected)throw new Exception($"Expected {expected}, got {value.ToExactString()}.");}
    static void Equal(Quantity value,string expected)=>Equal(value.Amount,expected);
    static void Equal(Money value,string expected)=>Equal(value.Amount,expected);
    static void Equal(ExactDecimal value,string expected){if(value!=ExactDecimal.Parse(expected))throw new Exception($"Expected {expected}, got {value}.");}
    static void Reject(string code,Action work){try{work();}catch(MarketDataException e)when(e.Code==code){return;}throw new Exception("Expected "+code);}
}
