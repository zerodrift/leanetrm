using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed partial class GasParkCustodyActions
{
    sealed record AllocationEffect(Guid LotId,Guid ReservationId,string Quantity,Guid? RemainderReservationId,string RemainderQuantity);

    // Review calculations use the admitted source and a proposed selection.
    // They never manufacture an actual event or a provisional knowledge time.
    internal static object Effects(Guid catalog,JsonElement source,LifecycleActionRequest request,JsonElement outcome)
    {
        request=Normalize(request);RequireOutcomeTarget(request.Target,outcome);
        try{return ReviewEffects(catalog,source,request,outcome);}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or ArgumentException or OverflowException)
        {throw new MarketDataException("gas_custody_source","Retain the complete native custody and exact independently checked review effects.",error);}
    }

    static object ReviewEffects(Guid catalog,JsonElement source,LifecycleActionRequest request,JsonElement outcome)
    {
        var state=GasParkCustodyAdapter.Read(catalog,source);var terms=state.Terms;
        if(state.Source.AgreementId!=request.Target.ScopeId||source.Id("instrumentId")!=request.Target.BindingId||
            source.Id("contextId")!=request.Context.ContextId||state.Source.At!=request.Context.AsOf||
            outcome.Id("operationId")!=request.OperationId||outcome.Id("id")==Guid.Empty||outcome.Text("action")!=(request.Action==Accept?"accept":"return"))throw Source();
        var changed=outcome.GetProperty("changed").GetBoolean();var customer=terms.Perspective=="customer";var outsideSource=false;
        if(changed&&state.ReconciliationRequired)
            throw new MarketDataException("gas_custody_reconciliation","Reconcile corrected custody observations before recording another performance. Accepted title and cash remain unchanged.");
        var allocations=ReadAllocations(outcome.GetProperty("allocations"));
        var committed=state.Source.Committed.Amount;var amount=default(ExactDecimal);var fee=default(ExactDecimal);
        var before=state.Balance.InCustody.Amount;var after=before;var effective=default(DateTimeOffset);
        if(request.Action==Accept)
        {
            var selected=request.Selection.Deserialize<GasParkCustodyAcceptSelection>(GasOwnedOperations.Json)!;effective=selected.EffectiveAt;
            amount=committed;fee=terms.SignedFixedFee.Amount;
            var lots=selected.Allocations.Select(a=>new GasParkCustodyAllocation(a.LotId,new(ExactDecimal.Parse(a.Quantity),terms.Quantity))).ToImmutableArray();
            if(changed)
            {
                GasParkCustodyLedger.RequireAcceptanceSelection(terms,state.Source,state.Source.Committed,effective,lots);
                after=committed;
            }
            else
            {
                if(state.Source.Acceptance is {} accepted)
                {
                    var recorded=source.GetProperty("acceptance");
                    if(accepted.Id!=outcome.Id("id")||accepted.At!=effective||recorded.Id("agreementEvidenceId")!=selected.AgreementEvidenceId||
                        recorded.Id("receiptEvidenceId")!=selected.ReceiptEvidenceId)throw Source();
                    var original=recorded.GetProperty("allocations").EnumerateArray().Select(r=>new AllocationEffect(r.Id("lotId"),r.Id("reservationId"),r.Number("quantity").ToString(),null,"0")).OrderBy(r=>r.LotId);
                    if(!allocations.SequenceEqual(original))throw Source();
                }
                else
                {
                    // Record returned an authenticated, sealed original fact.
                    // Its birth balances are proof, not facts in this cut.
                    outsideSource=true;
                    if(effective<terms.ParkAt||effective>=terms.ReturnAt||
                        lots.Aggregate(default(ExactDecimal),(sum,l)=>sum+l.Quantity.Amount)!=(customer?committed:default))throw Source();
                }
            }
            if(allocations.Length!=lots.Length||lots.Any(l=>!allocations.Any(a=>a.LotId==l.LotId&&a.Quantity==l.Quantity.Amount.ToString()&&a.RemainderReservationId is null&&a.RemainderQuantity=="0")))
                throw Source();
            RequireNativeBalances(outcome,default,committed,customer,amount,fee);
            var cash=NullableId(outcome,"cashBindingId");
            if((fee==default)!=(cash is null)||!changed&&!outsideSource&&cash!=state.Cash?.BindingId)throw Source();
        }
        else
        {
            var selected=request.Selection.Deserialize<GasParkCustodyReturnSelection>(GasOwnedOperations.Json)!;effective=selected.EffectiveAt;
            amount=ExactDecimal.Parse(selected.Quantity);
            if(NullableId(outcome,"cashBindingId") is not null)throw Source();
            if(changed)
            {
                var lots=selected.Allocations.Select(a=>
                {
                    var reservation=state.Reservations.SingleOrDefault(r=>r.ReservationId==a.ReservationId)??throw Source();
                    return new GasParkCustodyAllocation(reservation.LotId,new(ExactDecimal.Parse(a.Quantity),terms.Quantity));
                }).ToImmutableArray();
                GasParkCustodyLedger.RequireReturn(terms,state.Source,new(amount,terms.Quantity),effective,lots);
                var changes=customer?GasParkCustodyLedger.ReturnReservations(state.Reservations,lots,new(amount,terms.Quantity)):[];
                if(allocations.Length!=changes.Length||changes.Any(c=>!allocations.Any(a=>a.LotId==c.LotId&&a.ReservationId==c.ReleaseReservationId&&
                    a.Quantity==c.Returned.Amount.ToString()&&a.RemainderQuantity==c.Remainder.Amount.ToString()&&(a.RemainderReservationId is null)==(c.Remainder.Amount==default))))
                    throw Source();
                after=before-amount;
                RequireNativeBalances(outcome,before,after,customer,amount,default);
            }
            else
            {
                var original=state.Source.Returns.SingleOrDefault(r=>r.Id==outcome.Id("id"));
                ExactDecimal birthBefore;
                if(original is not null)
                {
                    if(original.Quantity.Amount!=amount||original.At!=effective||original.EvidenceId!=selected.EvidenceId)throw Source();
                    var row=source.GetProperty("returns").EnumerateArray().Single(r=>r.Id("id")==original.Id);
                    if(!allocations.SequenceEqual(ReadAllocations(row.GetProperty("allocations"))))throw Source();
                    birthBefore=committed-state.Source.Returns.TakeWhile(r=>r.Id!=original.Id).Aggregate(default(ExactDecimal),(sum,r)=>sum+r.Quantity.Amount);
                }
                else
                {
                    outsideSource=true;birthBefore=outcome.Number("custodyBefore");
                    if(effective<=terms.ParkAt||birthBefore.CompareTo(committed)>0||birthBefore.CompareTo(amount)<0||
                        allocations.Aggregate(default(ExactDecimal),(sum,a)=>sum+ExactDecimal.Parse(a.Quantity))!=(customer?amount:default)||
                        allocations.Aggregate(default(ExactDecimal),(sum,a)=>sum+ExactDecimal.Parse(a.Quantity)+ExactDecimal.Parse(a.RemainderQuantity)).CompareTo(birthBefore)>0)
                        throw Source();
                }
                if(selected.Allocations.Length!=allocations.Length||selected.Allocations.Any(s=>!allocations.Any(r=>r.ReservationId==s.ReservationId&&r.Quantity==s.Quantity)))throw Source();
                RequireNativeBalances(outcome,birthBefore,birthBefore-amount,customer,amount,default);
            }
        }
        if(TradingInstant.Parse(outcome.Text("returnAt"),"gas_custody_source")!=terms.ReturnAt)throw Source();
        return new{
            performedQuantity=(changed?amount:default).ToString(),acknowledgedQuantity=changed?(string?)null:amount.ToString(),acknowledgementOutsideSource=outsideSource,quantitySpecification=terms.Quantity.Code,
            custodyBefore=before.ToString(),custodyAfter=after.ToString(),reservedBefore=state.Balance.Reserved.Amount.ToString(),reservedAfter=(customer?after:default).ToString(),
            ownedQuantityChange="0",feeAmount=(changed?fee:default).ToString(),currency=terms.Currency.Code,effectiveAt=effective,returnAt=terms.ReturnAt,feeDueAt=terms.FeeDueAt,
            titleRetainedByCustomer=true,physicalActivityUnchanged=true,feeCashUnchanged=!changed||request.Action==Return,
            allocations=changed?allocations:Array.Empty<AllocationEffect>()};
    }

    static void RequireNativeBalances(JsonElement outcome,ExactDecimal before,ExactDecimal after,bool customer,ExactDecimal quantity,ExactDecimal fee)
    {
        if(outcome.Number("custodyBefore")!=before||outcome.Number("custodyAfter")!=after||outcome.Number("reservedBefore")!=(customer?before:default)||
            outcome.Number("reservedAfter")!=(customer?after:default)||outcome.Number("quantity")!=quantity||outcome.Number("ownedQuantityChange")!=default||outcome.Number("feeAmount")!=fee)
            throw Source();
    }

    static AllocationEffect[] ReadAllocations(JsonElement rows)
    {
        if(rows.ValueKind!=JsonValueKind.Array)throw Source();
        var result=rows.EnumerateArray().Select(r=>
        {
            var lot=r.Id("lotId");var reservation=r.Id("reservationId");var quantity=r.Number("quantity");var remainder=r.Number("remainderQuantity");
            var successor=NullableId(r,"remainderReservationId");
            if(lot==Guid.Empty||reservation==Guid.Empty||quantity.CompareTo(default)<=0||remainder.CompareTo(default)<0||(remainder==default)!=(successor is null))throw Source();
            return new AllocationEffect(lot,reservation,quantity.ToString(),successor,remainder.ToString());
        }).OrderBy(r=>r.LotId).ToArray();
        if(result.Select(r=>r.LotId).Distinct().Count()!=result.Length||result.Select(r=>r.ReservationId).Distinct().Count()!=result.Length||
            result.Where(r=>r.RemainderReservationId is not null).Select(r=>r.RemainderReservationId).Distinct().Count()!=result.Count(r=>r.RemainderReservationId is not null)||
            result.Any(r=>r.RemainderReservationId is {} next&&result.Any(other=>other.ReservationId==next)))throw Source();
        return result;
    }

    static Guid? NullableId(JsonElement row,string name)
    {
        var value=row.GetProperty(name);
        if(value.ValueKind==JsonValueKind.Null)return null;
        var id=value.GetGuid();return id==Guid.Empty?throw Source():id;
    }
}
