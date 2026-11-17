using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasParkCustodyCash(Guid BindingId,TimedCashPayment Instrument,Quantity Quantity,RecordedCashValuation State);
internal sealed record GasParkCustodyState(GasParkCustodyTerms Terms,GasParkCustodySource Source,GasParkCustodyBalance Balance,
    ImmutableArray<GasParkCustodyReservation> Reservations,GasParkCustodyCash? Cash,bool ReconciliationRequired);

// Native authorization resolves the immutable prefix. This adapter verifies its
// financial meaning without deriving custody from title or physical movement.
internal static class GasParkCustodyAdapter
{
    sealed record Observation(Guid Id,string Key,int Revision,Guid? Previous,string Kind,ExactDecimal Quantity,
        DateTimeOffset At,DateTimeOffset KnownAt);

    internal static GasParkCustodyState Read(Guid catalog,JsonElement agreement,bool draft=false)=>Structured(()=>
    {
        var terms=Terms(catalog,agreement);var at=Instant(agreement,"asOf");var known=Instant(agreement,"knownAt");
        var trade=Id(agreement,"tradeId");var book=Id(agreement,"bookId");var context=Id(agreement,"contextId");
        _=Id(agreement,"instrumentId");
        if(Integer(agreement,"revision")!=1||(draft?Integer(agreement,"publicationOrder")!=0:Integer(agreement,"publicationOrder")<1)||
            (terms.Perspective=="customer")!=(agreement.OptionalId("ownedAccountId") is not null)||agreement.OptionalId("ownedAccountId")==Guid.Empty)
            throw Invalid();
        var reconciled=agreement.GetProperty("reconciliationRequired");
        if(reconciled.ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw Invalid();
        var observations=Array(agreement,"evidence").Select(r=>new Observation(Id(r,"id"),Key(r,"sourceKey"),Integer(r,"revision"),r.OptionalId("previousId"),
            Text(r,"kind"),r.Number("rawQuantity"),Instant(r,"observedAt"),Instant(r,"knownAt"))).ToImmutableArray();
        if(observations.Any(e=>e.Revision<1||e.Previous==Guid.Empty||
                e.Kind is not("custody_agreement" or "custody_receipt" or "custody_return")||e.Quantity.CompareTo(default)<0||e.At>e.KnownAt||e.KnownAt>known)||
            observations.Select(e=>e.Id).Distinct().Count()!=observations.Length||observations.Select(e=>(e.Key,e.Revision)).Distinct().Count()!=observations.Length)
            throw Invalid();
        var byId=observations.ToDictionary(e=>e.Id);
        foreach(var item in observations)
        {
            if(item.Revision==1?item.Previous is not null:item.Previous is not {} previous||!byId.TryGetValue(previous,out var prior)||
                prior.Key!=item.Key||prior.Revision!=item.Revision-1||prior.KnownAt>=item.KnownAt)throw Invalid();
        }
        var heads=Array(agreement,"evidenceHeads");
        if(heads.Select(e=>Id(e,"id")).Distinct().Count()!=heads.Length||heads.Length!=observations.Select(e=>e.Key).Distinct().Count())throw Invalid();
        var latest=observations.GroupBy(e=>e.Key).ToDictionary(g=>g.Key,g=>g.MaxBy(e=>e.Revision)!);
        foreach(var row in heads)
            if(!byId.TryGetValue(Id(row,"id"),out var item)||item!=latest[item.Key]||Key(row,"sourceKey")!=item.Key||Integer(row,"revision")!=item.Revision||
                row.Number("rawQuantity")!=item.Quantity||Instant(row,"observedAt")!=item.At||Instant(row,"knownAt")!=item.KnownAt||Text(row,"kind")!=item.Kind||row.OptionalId("previousId")!=item.Previous)
                throw Invalid();
        var correction=false;
        Observation Evidence(Guid id,string key,int revision,string kind,ExactDecimal quantity,DateTimeOffset effective,bool agreementEvidence=false)
        {
            if(!byId.TryGetValue(id,out var item)||item.Key!=key||item.Revision!=revision||item.Kind!=kind||item.Quantity!=quantity||
                (agreementEvidence?item.At>effective:item.At!=effective))throw Invalid();
            if(latest[key].Id!=id)correction=true;
            return item;
        }
        var committed=new Quantity(agreement.Number("quantity"),terms.Quantity);
        var accepted=agreement.GetProperty("acceptance");GasParkCustodyAcceptance? acceptance=null;
        var active=new Dictionary<Guid,GasParkCustodyReservation>();var usedReservations=new HashSet<Guid>();
        if(accepted.ValueKind!=JsonValueKind.Null)
        {
            if(draft||accepted.ValueKind!=JsonValueKind.Object)throw Invalid();
            var effective=Instant(accepted,"effectiveAt");var acceptedKnown=Instant(accepted,"knownAt");
            var quantity=new Quantity(accepted.Number("quantity"),terms.Quantity);
            var agreementEvidence=Evidence(Id(accepted,"agreementEvidenceId"),Key(accepted,"agreementSourceKey"),Integer(accepted,"agreementRevision"),"custody_agreement",quantity.Amount,effective,true);
            var receipt=Evidence(Id(accepted,"receiptEvidenceId"),Key(accepted,"receiptSourceKey"),Integer(accepted,"receiptRevision"),"custody_receipt",quantity.Amount,effective);
            if(agreementEvidence.KnownAt>acceptedKnown||receipt.KnownAt>acceptedKnown)throw Invalid();
            var lots=Array(accepted,"allocations").Select(r=>
            {
                var lot=Id(r,"lotId");var reservation=Id(r,"reservationId");var amount=new Quantity(r.Number("quantity"),terms.Quantity);
                if(!usedReservations.Add(reservation)||!active.TryAdd(lot,new(lot,reservation,amount)))throw Invalid();
                return new GasParkCustodyAllocation(lot,amount);
            }).ToImmutableArray();
            acceptance=new(Id(accepted,"id"),quantity,effective,acceptedKnown,receipt.Id,lots);
        }
        var returns=ImmutableArray.CreateBuilder<GasParkCustodyReturn>();var observedReturns=new HashSet<string>();
        foreach(var row in Array(agreement,"returns"))
        {
            if(acceptance is null)throw Invalid();
            var key=Key(row,"evidenceSourceKey");_=Key(row,"businessKey");
            var effective=Instant(row,"effectiveAt");var recorded=Instant(row,"knownAt");
            var quantity=new Quantity(row.Number("quantity"),terms.Quantity);
            var observation=Evidence(Id(row,"evidenceId"),key,Integer(row,"evidenceRevision"),"custody_return",quantity.Amount,effective);
            if(!observedReturns.Add(key)||observation.KnownAt>recorded)throw Invalid();
            var lots=Array(row,"allocations").Select(r=>
            {
                var lot=Id(r,"lotId");var returned=new Quantity(r.Number("quantity"),terms.Quantity);var remainder=r.Number("remainderQuantity");
                var successor=r.OptionalId("remainderReservationId");
                if(!active.TryGetValue(lot,out var prior)||Id(r,"reservationId")!=prior.ReservationId||returned.Amount.CompareTo(default)<=0||
                    remainder!=prior.Quantity.Amount-returned.Amount||remainder.CompareTo(default)<0||(remainder==default)!=(successor is null)||successor==Guid.Empty)
                    throw Invalid();
                active.Remove(lot);
                if(successor is {} next)
                {
                    if(!usedReservations.Add(next))throw Invalid();
                    active.Add(lot,new(lot,next,new(remainder,terms.Quantity)));
                }
                return new GasParkCustodyAllocation(lot,returned);
            }).ToImmutableArray();
            returns.Add(new(Id(row,"id"),Id(row,"acceptanceId"),quantity,effective,recorded,observation.Id,lots));
        }
        var source=new GasParkCustodySource(trade,committed,acceptance,returns.ToImmutable(),at,known);
        var balance=GasParkCustodyLedger.Balance(terms,source);var balances=agreement.GetProperty("balances");
        if(balances.Number("inCustody")!=balance.InCustody.Amount||balances.Number("reserved")!=balance.Reserved.Amount||
            balances.Number("overdue")!=balance.Overdue.Amount||balances.Number("ownedQuantityChange")!=default||correction&&!reconciled.GetBoolean())throw Invalid();
        var reservations=Array(agreement,"reservations").Select(r=>new GasParkCustodyReservation(Id(r,"lotId"),Id(r,"reservationId"),new(r.Number("quantity"),terms.Quantity))).ToImmutableArray();
        if(reservations.Length!=active.Count||reservations.Select(r=>r.LotId).Distinct().Count()!=reservations.Length||
            reservations.Any(r=>!active.TryGetValue(r.LotId,out var expected)||r!=expected))throw Invalid();
        var cash=Cash(agreement,terms,source,book,context);
        return new GasParkCustodyState(terms,source,balance,reservations,cash,reconciled.GetBoolean());
    });

    internal static ImmutableArray<GasParkAndLoanComponent> Components(GasParkCustodyState state)
    {
        if(state.ReconciliationRequired)throw new MarketDataException("gas_custody_reconciliation","Accepted custody references corrected observations. A qualified reconciliation is required before complete service or portfolio valuation; retained cash remains independent.");
        return GasParkCustodyValuation.Components(state.Terms,state.Source);
    }

    internal static GasParkCustodyTerms Terms(Guid catalog,JsonElement agreement)=>Structured(()=>
    {
        if(catalog==Guid.Empty||Text(agreement,"implementation")!=GasParkCustodyTerms.Version)throw Invalid();
        var t=agreement.GetProperty("terms");_=Id(t,"definitionRevisionId");
        var reference=new ReferenceRevision(Id(t,"referenceCatalogId"),Id(t,"referenceRevisionId"));
        var currency=new ReferenceKey(reference,Text(t,"currency"));
        var direction=Text(t,"feeDirection") switch{"pay"=>ExactDecimal.Parse("-1"),"receive"=>ExactDecimal.Parse("1"),_=>throw Invalid()};
        return new GasParkCustodyTerms(GasParkCustodyTerms.Version,Text(t,"serviceKind"),Text(t,"perspective"),Text(t,"titlePolicy"),Text(t,"returnPolicy"),
            Text(t,"returnTiming"),Text(t,"returnOrder"),Text(t,"usePolicy"),Text(t,"lossPolicy"),Text(t,"physicalPolicy"),Text(t,"planPolicy"),
            new(reference,Text(t,"point")),new(reference,Text(t,"quantity")),currency,Instant(t,"parkAt"),Instant(t,"returnAt"),
            new(t.Number("fixedFeeAmount"),currency),direction,t.GetProperty("feeDueAt").ValueKind==JsonValueKind.Null?null:Instant(t,"feeDueAt"),Text(t,"feeRecognition"));
    });

    static GasParkCustodyCash? Cash(JsonElement agreement,GasParkCustodyTerms terms,GasParkCustodySource source,Guid book,Guid context)
    {
        var row=agreement.GetProperty("cash");
        if(source.Acceptance is null||terms.FixedFee.Amount==default)
        {
            if(row.ValueKind!=JsonValueKind.Null)throw Invalid();
            return null;
        }
        if(row.ValueKind!=JsonValueKind.Object||row.Number("amount")!=terms.SignedFixedFee.Amount||Text(row,"currency")!=terms.Currency.Code||
            Instant(row,"dueAt")!=terms.FeeDueAt||Instant(row,"effectiveAt")!=source.Acceptance.At)throw Invalid();
        var binding=Id(row,"bindingId");var instrument=new TimedCashPayment(terms.Currency,terms.FeeDueAt!.Value);
        if(Id(row,"instrumentId")!=instrument.Id)throw Invalid();
        var position=new AccountingPositionKey(source.AgreementId,binding,book);var quantity=new Quantity(terms.SignedFixedFee.Amount,terms.Currency);
        var flows=Array(row,"payments").Select(r=>
        {
            if(Id(r,"trade_id")!=source.AgreementId||Id(r,"binding_id")!=binding||Id(r,"book_id")!=book||
                new ReferenceKey(new(Id(r,"reference_catalog_id"),Id(r,"reference_revision_id")),Text(r,"currency_code"))!=terms.Currency||
                Instant(r,"paid_at")<source.Acceptance.At||Instant(r,"paid_at")>Instant(r,"known_at"))throw Invalid();
            var amount=r.Number("amount");
            if((amount*terms.FeeDirection).CompareTo(default)<0)throw Invalid();
            return new AccountingFlow(Id(r,"allocation_id"),Integer(r,"revision"),position,Instant(r,"paid_at"),Instant(r,"known_at"),
                ExactMoney.FromMoney(new(amount,terms.Currency)),AccountingFlowKind.EconomicCash);
        }).ToImmutableArray();
        var state=new RecordedCashValuation(position,instrument,quantity,flows,RecordedCashValuation.Policy,context,source.KnownAt);
        if(!SameSignCashCapacity.IsWithin(terms.SignedFixedFee,flows))throw Invalid();
        return new(binding,instrument,quantity,state);
    }
    static ImmutableArray<JsonElement> Array(JsonElement row,string field)
    {
        var value=row.GetProperty(field);if(value.ValueKind!=JsonValueKind.Array)throw Invalid();
        return [..value.EnumerateArray()];
    }
    static Guid Id(JsonElement row,string field){var value=row.Id(field);return value==Guid.Empty?throw Invalid():value;}
    static string Key(JsonElement row,string field)
    {
        var value=row.GetProperty(field);if(value.ValueKind!=JsonValueKind.String)throw Invalid();
        var text=value.GetString()!;
        return GasOperationText.Key(text,"gas_custody_source")==text?text:throw Invalid();
    }
    static string Text(JsonElement row,string field){var value=row.GetProperty(field);return value.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(value.GetString())?value.GetString()!:throw Invalid();}
    static DateTimeOffset Instant(JsonElement row,string field)=>TradingInstant.Parse(Text(row,field),"gas_custody_source");
    static int Integer(JsonElement row,string field)=>int.Parse(row.Text(field),CultureInfo.InvariantCulture);
    static T Structured<T>(Func<T> read)
    {
        try{return read();}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or ArgumentException or OverflowException)
        {throw new MarketDataException("gas_custody_source","Retain the complete native custody, observation, reservation and independent cash source at the exact certified cut.",error);}
    }
    static MarketDataException Invalid()=>new("gas_custody_source","Retain the exact native custody terms, observed return manifest, reservation lineage and independent cash membership.");
}
