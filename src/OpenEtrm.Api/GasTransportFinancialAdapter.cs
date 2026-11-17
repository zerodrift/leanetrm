using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Shared.Commands;

namespace OpenEtrm.Api;

internal sealed record GasTransportFinancialTerms(DefinitionRevision Definitions,GasTransportDirectRightsTerms Rights,
    ReferenceRevision OperationalReference,Guid EntitlementId,string Perspective,Money FixedFee,ExactDecimal FeeDirection,
    DateTimeOffset? FeeDueAt,string ExpiryPolicy)
{internal Money SignedFee=>FixedFee.Scale(FeeDirection);}
internal sealed record GasTransportFinancialState(Guid Id,int Revision,Guid? PreviousId,GasTransportDirectRightsState Rights,
    string Status,DateTimeOffset From,DateTimeOffset Through,DateTimeOffset EffectiveAt,DateTimeOffset KnownAt,
    Guid SourceContextId,DateTimeOffset SourceKnownAt,DateTimeOffset SourceAsOf,Guid? PolicyId,int? PolicyRevision);
internal sealed record GasTransportFinancialMark(Guid Id,int Revision,Guid? PreviousId,Guid FinancialStateId,DateTimeOffset ValuationAt,
    CurveRequest Market,SeriesKey Series,ObservationKey Observation,ReferenceKey Source,ReferenceKey Quote,
    DateTimeOffset EffectiveAt,DateTimeOffset KnownAt,Guid SourceContextId,DateTimeOffset SourceKnownAt,DateTimeOffset SourceAsOf);
internal sealed record GasTransportFinancialCash(Guid BindingId,TimedCashPayment Instrument,Quantity Quantity,RecordedCashValuation State);
internal sealed record GasTransportFinancialAgreement(Guid TradeId,Guid InstrumentId,Guid BookId,Guid ContextId,DateTimeOffset At,
    DateTimeOffset KnownAt,GasTransportFinancialTerms Terms,GasTransportFinancialState? State,GasTransportFinancialMark? Mark,
    GasTransportDirectRightsExpiry? Expiry,GasTransportFinancialCash? Cash,string? UnpricedReason);

// Native source readers retain authority and immutable facts. This parser checks
// the complete financial meaning without inventing a mark, fee or state change.
internal static class GasTransportFinancialAdapter
{
    internal static GasTransportFinancialTerms Terms(Guid catalog,JsonElement agreement)=>Structured(()=>
    {
        var t=agreement.GetProperty("terms");
        if(catalog==Guid.Empty||Text(agreement,"implementation")!=GasTransportFinancialProfile.Version||
            Text(t,"profile")!=GasTransportFinancialProfile.Version||Text(t,"rightsValuationPolicy")!=GasTransportDirectRightsPolicy.Version||
            Text(t,"feeProfile")!=GasTransportFinancialProfile.Fee||Text(t,"expiryPolicy")!=GasTransportDirectRightsPolicy.ExpiryAtThrough||
            Text(t,"perspective") is not("customer" or "provider"))throw Invalid();
        var reference=new ReferenceRevision(Id(t,"valuationReferenceCatalogId"),Id(t,"valuationReferenceRevisionId"));
        var currency=new ReferenceKey(reference,Text(t,"currency"));var amount=t.Number("fixedFeeAmount");
        var direction=Text(t,"feeDirection") switch{"pay"=>ExactDecimal.Parse("-1"),"receive"=>ExactDecimal.Parse("1"),_=>throw Invalid()};
        var due=t.GetProperty("feeDueAt").ValueKind==JsonValueKind.Null?(DateTimeOffset?)null:At(t,"feeDueAt");
        if(amount.CompareTo(default)<0||amount.CompareTo(ExactDecimal.Parse("100000000000000000000"))>=0||
            (amount==default)!=(due is null)||due is {} dueAt&&(dueAt.Year<1901||dueAt.Year>2199))throw Invalid();
        return new GasTransportFinancialTerms(new(catalog,Id(t,"definitionRevisionId")),new(GasTransportDirectRightsPolicy.Version,
            new(reference,Text(t,"packageQuantity")),currency),new(Id(t,"operationalReferenceCatalogId"),Id(t,"operationalReferenceRevisionId")),
            Id(t,"entitlementId"),Text(t,"perspective"),new(amount,currency),direction,due,Text(t,"expiryPolicy"));
    });

    internal static GasTransportFinancialAgreement Read(Guid catalog,JsonElement agreement,bool draft=false)=>Structured(()=>
    {
        var terms=Terms(catalog,agreement);var trade=Id(agreement,"tradeId");var book=Id(agreement,"bookId");
        var at=At(agreement,"asOf");var known=At(agreement,"knownAt");var context=Id(agreement,"contextId");var birth=At(agreement,"effectiveAt");
        _=DateWorkspace.Date(Text(agreement,"effectiveOn"));
        if(Int(agreement,"revision")!=1||birth>at)throw Invalid();
        var states=Rows(agreement,"stateHistory").Select(r=>State(r,terms,trade,known)).OrderBy(s=>s.Revision).ToArray();
        if(states.Select(s=>s.Id).Distinct().Count()!=states.Length)throw Invalid();
        for(var n=0;n<states.Length;n++)
        {
            var s=states[n];
            if(s.Revision!=n+1||(n==0?s.PreviousId is not null:s.PreviousId!=states[n-1].Id||s.KnownAt<=states[n-1].KnownAt||states[n-1].Status!="live")||
                n==0&&(s.Id!=Id(agreement,"operationId")||s.EffectiveAt!=birth)||
                s.Status=="closed"&&(n==0||s.Rights.CapacityVersionId!=states[n-1].Rights.CapacityVersionId||s.EffectiveAt!=s.Through))throw Invalid();
        }
        var history=Rows(agreement,"markHistory").Select(r=>Mark(r,terms,known)).ToArray();
        if(history.Select(m=>m.Id).Distinct().Count()!=history.Length||history.Any(m=>!states.Any(s=>s.Id==m.FinancialStateId&&s.Status=="live"&&s.KnownAt<=m.SourceKnownAt)))throw Invalid();
        foreach(var group in history.GroupBy(m=>(m.FinancialStateId,m.ValuationAt)))
        {
            var chain=group.OrderBy(m=>m.Revision).ToArray();
            for(var n=0;n<chain.Length;n++)
                if(chain[n].Revision!=n+1||(n==0?chain[n].PreviousId is not null:chain[n].PreviousId!=chain[n-1].Id||chain[n].KnownAt<=chain[n-1].KnownAt))throw Invalid();
        }
        var rawState=agreement.GetProperty("financialState");
        var state=rawState.ValueKind==JsonValueKind.Null?null:State(rawState,terms,trade,known);
        var reason=agreement.GetProperty("unpricedReason").ValueKind==JsonValueKind.Null?null:Text(agreement,"unpricedReason");
        var rawMark=agreement.GetProperty("mark");var mark=rawMark.ValueKind==JsonValueKind.Null?null:Mark(rawMark,terms,known);
        if(state is null)
        {
            if(mark is not null||reason is not("financial_state_future" or "financial_state_unavailable")||
                reason=="financial_state_future"&&(states.Length==0||states[^1].EffectiveAt<=at)||
                reason=="financial_state_unavailable"&&states.Length!=0)throw Invalid();
        }
        else
        {
            if(states.Length==0||state!=states[^1]||state.EffectiveAt>at)throw Invalid();
            if(state.Status=="closed") {if(mark is not null||reason is not null)throw Invalid();}
            else if(mark is not null)
            {
                var head=history.Where(m=>m.FinancialStateId==state.Id&&m.ValuationAt==at).MaxBy(m=>m.Revision);
                if(reason is not null||mark!=head||mark.FinancialStateId!=state.Id||mark.ValuationAt!=at||mark.EffectiveAt>at||at>=state.Through)throw Invalid();
            }
            else if(reason is not("rights_mark_required" or "rights_mark_future" or "rights_mark_corrected" or "expiry_transition_required")||
                (reason=="expiry_transition_required")!=(at>=state.Through))throw Invalid();
        }
        GasTransportDirectRightsExpiry? expiry=null;var terminal=agreement.GetProperty("expiry");
        if(state?.Status=="closed")
        {
            if(terminal.ValueKind!=JsonValueKind.Object||Id(terminal,"id")!=state.Id||Id(terminal,"agreementId")!=trade||
                At(terminal,"effectiveAt")!=state.EffectiveAt||At(terminal,"knownAt")!=state.KnownAt||
                Id(terminal,"policyId")!=state.PolicyId||Int(terminal,"policyRevision")!=state.PolicyRevision)throw Invalid();
            expiry=new(state.Id,state.Rights,state.PreviousId!.Value,state.Through,state.EffectiveAt,state.KnownAt);
        }
        else if(terminal.ValueKind!=JsonValueKind.Null)throw Invalid();
        var cash=Cash(agreement,terms,trade,book,context,birth,known,draft);
        return new GasTransportFinancialAgreement(trade,Id(agreement,"instrumentId"),book,context,at,known,terms,state,mark,expiry,cash,reason);
    });

    internal static GasTransportDirectRightsInstrument Instrument(GasTransportFinancialAgreement agreement,CapturedPriceSource? source,
        GasTransportDirectRightsEvaluation? evaluation)
    {
        if(agreement.State is not {} state||agreement.UnpricedReason is not null)
            throw Unpriced(agreement.UnpricedReason??"financial_state_unavailable");
        GasTransportDirectRightsMark? mark=null;
        if(agreement.Expiry is null)
        {
            if(agreement.Mark is not {} adopted)throw Unpriced("rights_mark_required");
            if(source is null||evaluation is null||source.Market!=adopted.Market||source.Snapshot.Definitions!=agreement.Terms.Definitions||
                source.Snapshot.ValuationAt!=agreement.At||source.Snapshot.Knowledge is not {} knowledge||knowledge.ContextId!=agreement.ContextId||
                knowledge.KnownAt!=agreement.KnownAt)throw Invalid();
            var provenance=new ResolvedCalculationContext(adopted.SourceContextId,"native_transport_financial_mark_v1/"+adopted.Id.ToString("D"),
                adopted.SourceAsOf,adopted.SourceKnownAt,adopted.ValuationAt,adopted.ValuationAt);
            mark=new(state.Rights,source.Point,new(adopted.Series,InputUse.Variable),adopted.Observation,adopted.Source,adopted.Quote,
                ObservationRole.TraderMark,provenance);
        }
        var valuationId=OperationIdentity.For("gas_transport_financial_rights_state_v1",state.Id);
        return new(valuationId,agreement.Terms.Rights,state.Rights,state.Through,agreement.Terms.ExpiryPolicy,
            agreement.Expiry,source,mark,evaluation);
    }

    static GasTransportFinancialState State(JsonElement row,GasTransportFinancialTerms terms,Guid trade,DateTimeOffset known)
    {
        var id=Id(row,"id");var previous=OptionalId(row,"previousId");var status=Text(row,"status");
        var from=At(row,"from");var through=At(row,"through");var effective=At(row,"effectiveAt");var recorded=At(row,"knownAt");
        var sourceKnown=At(row,"sourceKnownAt");var sourceAsOf=At(row,"sourceAsOf");var policy=OptionalId(row,"policyId");
        var policyRevision=row.GetProperty("policyRevision").ValueKind==JsonValueKind.Null?(int?)null:Int(row,"policyRevision");
        if(Id(row,"agreementId")!=trade||Id(row,"entitlementId")!=terms.EntitlementId||Text(row,"remainingRightsIdentity")!=id.ToString("D")||
            status is not("live" or "closed")||from>=through||sourceKnown>recorded||recorded>known||effective>sourceAsOf||
            (status=="closed")!=(policy is not null)||(policy is null)!=(policyRevision is null)||policyRevision is <=0)throw Invalid();
        return new(id,Int(row,"revision"),previous,new(trade,terms.EntitlementId,Id(row,"capacityVersionId"),id.ToString("D")),status,
            from,through,effective,recorded,Id(row,"sourceContextId"),sourceKnown,sourceAsOf,policy,policyRevision);
    }
    static GasTransportFinancialMark Mark(JsonElement row,GasTransportFinancialTerms terms,DateTimeOffset known)
    {
        var definition=new DefinitionRevision(terms.Definitions.CatalogId,Id(row,"marketDefinitionRevisionId"));
        var at=At(row,"valuationAt");var effective=At(row,"effectiveAt");var recorded=At(row,"knownAt");var sourceKnown=At(row,"sourceKnownAt");
        if(definition!=terms.Definitions||At(row,"sourceAsOf")!=at||effective!=at||sourceKnown<at||sourceKnown>recorded||recorded>known)throw Invalid();
        var reference=terms.Rights.Currency.Revision;
        return new(Id(row,"id"),Int(row,"revision"),OptionalId(row,"previousId"),Id(row,"financialStateId"),at,
            new(new(definition,Text(row,"curveCode")),new(definition,Id(row,"contractId"))),new(definition,Id(row,"seriesId")),
            new(definition.CatalogId,Id(row,"observationId")),new(reference,Text(row,"source")),new(reference,Text(row,"quote")),
            effective,recorded,Id(row,"sourceContextId"),sourceKnown,at);
    }
    static GasTransportFinancialCash? Cash(JsonElement agreement,GasTransportFinancialTerms terms,Guid trade,Guid book,Guid context,
        DateTimeOffset birth,DateTimeOffset known,bool draft)
    {
        var row=agreement.GetProperty("cash");
        if(terms.FixedFee.Amount==default||draft)
        {
            if(row.ValueKind!=JsonValueKind.Null)throw Invalid();return null;
        }
        if(row.ValueKind!=JsonValueKind.Object||row.Number("amount")!=terms.SignedFee.Amount||Text(row,"currency")!=terms.Rights.Currency.Code||
            new ReferenceRevision(Id(row,"referenceCatalogId"),Id(row,"referenceRevisionId"))!=terms.Rights.Currency.Revision||
            At(row,"dueAt")!=terms.FeeDueAt||At(row,"effectiveAt")!=birth)throw Invalid();
        var instrument=new TimedCashPayment(terms.Rights.Currency,terms.FeeDueAt!.Value);var binding=Id(row,"bindingId");
        if(Id(row,"instrumentId")!=instrument.Id)throw Invalid();
        var owner=new AccountingPositionKey(trade,binding,book);var quantity=new Quantity(terms.SignedFee.Amount,terms.Rights.Currency);
        var flows=Rows(row,"payments").Select(p=>
        {
            var amount=p.Number("amount");var paid=At(p,"paid_at");var recorded=At(p,"known_at");
            if(Id(p,"trade_id")!=trade||Id(p,"binding_id")!=binding||Id(p,"book_id")!=book||paid<birth||paid>recorded||
                new ReferenceKey(new(Id(p,"reference_catalog_id"),Id(p,"reference_revision_id")),Text(p,"currency_code"))!=terms.Rights.Currency||
                (amount*terms.FeeDirection).CompareTo(default)<0||(amount*terms.FeeDirection).CompareTo(terms.FixedFee.Amount)>0)throw Invalid();
            return new AccountingFlow(Id(p,"allocation_id"),Int(p,"revision"),owner,paid,recorded,ExactMoney.FromMoney(new(amount,terms.Rights.Currency)),AccountingFlowKind.EconomicCash);
        }).ToImmutableArray();
        foreach(var allocation in flows.GroupBy(f=>f.AllocationId))
            if(!allocation.OrderBy(f=>f.Revision).Select(f=>f.Revision).SequenceEqual(Enumerable.Range(1,allocation.Count())))throw Invalid();
        var state=new RecordedCashValuation(owner,instrument,quantity,flows,RecordedCashValuation.Policy,context,known);
        if(!SameSignCashCapacity.IsWithin(terms.SignedFee,flows))throw Invalid();
        return new(binding,instrument,quantity,state);
    }
    internal static MarketDataException Unpriced(string reason)=>new("gas_transport_financial_unpriced",
        "Complete Transport value is unavailable: "+reason+". Retained rights, fees and independently owned gas remain separate.");
    static ImmutableArray<JsonElement> Rows(JsonElement row,string name)=>row.GetProperty(name).ValueKind==JsonValueKind.Array?[..row.GetProperty(name).EnumerateArray()]:throw Invalid();
    static Guid Id(JsonElement row,string name){var id=row.Id(name);return id==Guid.Empty?throw Invalid():id;}
    static Guid? OptionalId(JsonElement row,string name)=>row.GetProperty(name).ValueKind==JsonValueKind.Null?null:Id(row,name);
    static int Int(JsonElement row,string name)=>row.GetProperty(name).TryGetInt32(out var n)?n:throw Invalid();
    static string Text(JsonElement row,string name)=>row.GetProperty(name).ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(row.GetProperty(name).GetString())?row.GetProperty(name).GetString()!:throw Invalid();
    static DateTimeOffset At(JsonElement row,string name)=>TradingInstant.Parse(Text(row,name),"gas_transport_financial_source");
    static T Structured<T>(Func<T> read)
    {
        try{return read();}
        catch(MarketDataException error)when(error.Code!="gas_transport_financial_source"){throw new MarketDataException("gas_transport_financial_source","Retain the exact financial terms, state/mark provenance and complete independent cash history.",error);}
        catch(Exception error)when(error is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or ArgumentException or OverflowException)
        {throw new MarketDataException("gas_transport_financial_source","Retain the complete exact native financial source.",error);}
    }
    static MarketDataException Invalid()=>new("gas_transport_financial_source","Retain the complete exact native financial terms, state/mark heads and independent cash memberships.");
}
