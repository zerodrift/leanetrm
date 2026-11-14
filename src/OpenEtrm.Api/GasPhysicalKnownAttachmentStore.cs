using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasPhysicalKnownRead(GasOwnedKnownHistory Journal,ImmutableArray<GasPhysicalKnownAttachment> Attachments);

// Knowledge-only native closure. No source clock is advanced to discover a
// future obligation, title, performance, principal contribution or payment.
internal static class GasPhysicalKnownAttachmentStore
{
    internal static async Task<GasPhysicalKnownRead> Read(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        MarketSnapshot snapshot,ImmutableArray<Guid> books,CancellationToken ct,ImmutableArray<MeasureBinding> parents=default)
    {
        if(frame.ContextId is not {} context||snapshot.Knowledge is not {} knowledge||knowledge.ContextId!=context||snapshot.ValuationAt!=frame.At||books.IsDefaultOrEmpty||
            !parents.IsDefault&&parents.Any(p=>p.TradeId==Guid.Empty||p.BindingId==Guid.Empty))
            throw Error("Retain an exact certified source, explicit books and native parent future bindings.");
        await using var capture=ScheduleStore.Command(connection,"SELECT least(security.authority_until('ReadGasOwnedHoldings'),security.authority_until('ReadMarketData'),security.authority_until('ReadTrades'))");
        var originalUntil=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(connection,new GasOwnedSourceReadAdmission(database.Manifest.CatalogId,context,frame.At,originalUntil));
        var root=await MarketInputSelection.Root(database,connection,frame,ct);
        if(!snapshot.HasSameInputs(root.Snapshot(frame).WithKnowledge(context,knowledge.KnownAt)))
            throw Error("A physical attachment cannot replace the exact captured market source.");
        var catalog=database.Manifest.CatalogId;
        var selected=parents.IsDefault?null:JsonSerializer.Serialize(parents.Select(p=>new{tradeId=p.TradeId,bindingId=p.BindingId}),LifecycleNative.Json);
        var native=await LifecycleNative.Read(connection,"SELECT security.read_gas_physical_known_attachments($1,$2,$3,$4::uuid[],$5::jsonb)::text",ct,
            catalog,context,frame.At,books.ToArray(),(object?)selected??DBNull.Value);
        if(native.Text("stateProfile")!="known_gas_physical_attachments_v1"||native.Id("contextId")!=context||native.Instant("asOf")!=frame.At||
            native.Instant("knownAt")!=knowledge.KnownAt||DateWorkspace.Date(native.Text("effectiveOn"))!=frame.BusinessDate)
            throw Error("Native physical evidence must retain its original business date, instant and knowledge cutoff.");
        var nativeUntil=native.Instant("authorityUntil");
        var admission=new GasOwnedSourceReadAdmission(catalog,context,frame.At,originalUntil<nativeUntil?originalUntil:nativeUntil);
        database.RegisterCommitAdmission(connection,admission);
        var nativeJournal=native.GetProperty("journal");
        var journal=GasOwnedKnownHistoryStore.Materialize(nativeJournal,catalog,snapshot,books,admission.Until,
            GasOwnedAppliedSources.Materialize(nativeJournal.GetProperty("sourceConflicts"),snapshot));
        if(journal.Scope!=GasOwnedKnownScope.PhysicalDependencies)throw Error("A physical attachment requires the exact native dependency source contract.");
        var result=Materialize(native,journal,snapshot);
        if(!parents.IsDefault&&result.Any(a=>!parents.Contains(new(a.FutureOwner.Owner,a.FutureOwner.BindingId))))
            throw Error("A physical attachment escaped the exact selected parent bindings.");
        await admission.RequireCommit(connection,ct);return new(journal,result);
    }

    internal static ImmutableArray<GasPhysicalKnownAttachment> Materialize(JsonElement native,GasOwnedKnownHistory journal,MarketSnapshot snapshot)
    {
        if(!journal.Source.HasSameInputs(snapshot)||snapshot.Knowledge is not {} knowledge)throw Error("Retain one exact knowledge certificate across the physical closure.");
        if(native.Text("quantityStateProfile")!="gas_owned_financial_quantities_v1")throw Error("Retain the explicit native financial quantity source contract.");
        var consumptionRows=native.GetProperty("journal").GetProperty("consumptions").EnumerateArray().ToImmutableDictionary(row=>row.Id("id"));
        var result=ImmutableArray.CreateBuilder<GasPhysicalKnownAttachment>();
        GasPhysicalKnownReceipt Receipt(JsonElement row)
        {
            var receipt=new GasPhysicalKnownReceipt(Integer(row,"publication_order"),row.Instant("known_at"));
            if(receipt.PublicationOrder<1||receipt.KnownAt>knowledge.KnownAt)throw Error("Each physical fact requires its exact accepted native publication receipt.");
            return receipt;
        }
        GasPhysicalKnownReceipt ReceiptOf(GasOwnedKnownTime time)=>new(time.PublicationOrder,time.KnownAt);
        foreach(var source in native.GetProperty("attachments").EnumerateArray())
        {
            var profile=FuturesPhysicalDeliveryStore.Materialize(source);var link=source.GetProperty("link");var position=source.GetProperty("position");
            var parent=new AccountingPositionKey(link.Id("trade_id"),link.Id("binding_id"),profile.BookId);
            var owner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,position.Id("id")),position.Id("id"),profile.BookId);
            var held=journal.Positions[owner.Owner.Id];var original=new Quantity(position.Number("quantity"),profile.Terms.Quantity);
            var occurrence=source.GetProperty("occurrence");var finalOccurrence=new FuturesMarginOccurrence(occurrence.Id("id"),occurrence.Instant("at"),occurrence.Instant("due_at"),FuturesMarginPhase.Final);
            var unit=new PriceUnit(profile.Terms.Currency,profile.Terms.Quantity,profile.QuoteMultiplier);
            if(held.Owner!=owner||held.Quantity!=original||held.Kind!="delivery_obligation"||held.AccountId!=profile.AccountId||link.Id("position_id")!=owner.Owner.Id||
                link.Id("profile_id")!=profile.Id||Integer(link,"profile_revision")!=profile.Revision||link.Id("final_occurrence_id")!=finalOccurrence.Id||
                occurrence.Text("phase")!="final"||position.Instant("effective_at")!=finalOccurrence.At)
                throw Error("Retain the exact native future-to-obligation link, signed quantity and original final occurrence.");
            var finals=source.GetProperty("finals").EnumerateArray().Select(row=>new GasPhysicalKnownFinal(row.Id("id"),Integer(row,"run_revision"),row.OptionalId("previous_id"),
                row.Id("source_observation_id"),ExactUnitPrice.Parse(row.Text("final_price"),unit),Receipt(row))).ToImmutableArray();
            var current=source.GetProperty("final");
            if(finals.IsEmpty||finals.Select(f=>f.DeterminationId).Distinct().Count()!=finals.Length||finals.Any(f=>f.Price.Unit!=unit)||
                finals[^1].DeterminationId!=current.Id("determination_id")||finals[^1].Price!=ExactUnitPrice.Parse(current.Text("final_price"),unit)||
                !finals.Any(f=>f.DeterminationId==link.Id("initial_determination_id")))
                throw Error("The complete accepted final history must retain both its initial native link and exact source-known head.");
            var performanceRows=source.GetProperty("performances").EnumerateArray().ToImmutableArray();
            var splits=source.GetProperty("splits").EnumerateArray().ToImmutableArray();
            var performances=performanceRows.Select(row=>
            {
                var supplies=splits.Where(s=>s.Id("performance_id")==row.Id("id")).Select(s=>
                {
                    var consumption=consumptionRows[s.Id("consumption_id")];var stock=journal.Positions[consumption.Id("lot_id")];
                    var selected=journal.Quantities.Consumptions[consumption.Id("id")];
                    return new GasPhysicalKnownSupply(Integer(s,"ordinal"),consumption.Id("id"),stock.Owner,consumption.Id("evidence_id"),consumption.OptionalId("reservation_id"),
                        new(consumption.Number("quantity"),stock.Quantity.Specification),selected.Revision,selected.Retracted,Receipt(consumption));
                }).OrderBy(s=>s.Ordinal).ToImmutableArray();
                var receiptStock=row.OptionalId("receipt_lot_id") is {} id?journal.Positions[id].Owner:(AccountingPositionKey?)null;
                return new GasPhysicalKnownPerformance(row.Id("id"),row.Text("business_reference"),new(row.Number("quantity")*(original.Amount.CompareTo(default)>0?ExactDecimal.Parse("1"):ExactDecimal.Parse("-1")),original.Specification),
                    row.Instant("performed_at"),row.OptionalId("title_evidence_id"),receiptStock,supplies,Integer(row,"financial_revision"),row.GetProperty("retracted").GetBoolean(),row.Text("source_allocation"),Receipt(row));
            }).ToImmutableArray();
            if(performances.Select(p=>p.Id).Distinct().Count()!=performances.Length||performances.Any(p=>p.PerformedAt<finalOccurrence.At||p.FinancialRevision<1||p.Retracted!=(p.SignedQuantity.Amount==default)||
                (p.FinancialRevision==1?p.SourceAllocation!=GasSourceAllocation.Initial:p.SourceAllocation is not(GasSourceAllocation.Preserve or GasSourceAllocation.Withdraw)))||
                splits.Any(s=>!performances.Any(p=>p.Id==s.Id("performance_id"))))throw Error("Every performance and identified supply must belong to this accepted final handoff.");
            var stepRows=source.GetProperty("steps").EnumerateArray().ToImmutableArray();
            var runs=source.GetProperty("runs").EnumerateArray().Select(row=>new GasPhysicalKnownPrincipalRun(Integer(row,"revision"),row.Id("final_determination_id"),row.Id("source_context_id"),row.Instant("as_of"),
                [..stepRows.Where(s=>Integer(s,"run_revision")==Integer(row,"revision")).Select(s=>new GasPhysicalKnownPrincipalStep(s.Id("id"),s.Id("performance_id"),s.OptionalId("previous_id"),s.OptionalId("basis_id"),
                    new(s.Number("cumulative_quantity"),profile.Terms.Quantity),ExactMoney.Parse(s.Text("exact_total"),profile.Terms.Currency),new(s.Number("rounded_total"),profile.Terms.Currency),
                    new(s.Number("contribution"),profile.Terms.Currency),new(s.Number("adjustment"),profile.Terms.Currency),Integer(s,"performance_revision")))],Receipt(row))).ToImmutableArray();
            if(runs.Any(r=>!finals.Any(f=>f.DeterminationId==r.FinalDeterminationId)||r.Steps.Any(s=>s.PerformanceRevision<1||!performances.Any(p=>p.Id==s.PerformanceId&&p.FinancialRevision>=s.PerformanceRevision)))||
                runs.Sum(r=>r.Steps.Length)!=stepRows.Length)throw Error("Retain every sealed principal prefix and its exact accepted final/performance lineage.");
            var paymentRows=source.GetProperty("payments").EnumerateArray().ToImmutableArray();
            var cash=source.GetProperty("cash").EnumerateArray().Select(row=>
            {
                var instrument=new TimedCashPayment(profile.Terms.Currency,row.Instant("due_at"));var cashOwner=new AccountingPositionKey(owner.Owner,row.Id("id"),owner.BookId);
                var quantity=new Quantity(row.Number("amount"),profile.Terms.Currency);var time=Receipt(row);
                var payments=paymentRows.Where(p=>p.Id("cash_id")==row.Id("id")).ToArray();
                var allocations=payments.GroupBy(p=>p.Text("business_reference")).ToDictionary(g=>g.Key,g=>g.OrderBy(p=>Integer(p,"revision")).First().Id("id"));
                var flows=payments.Select(p=>new AccountingFlow(allocations[p.Text("business_reference")],Integer(p,"revision"),cashOwner,p.Instant("paid_at"),p.Instant("known_at"),
                    ExactMoney.Parse(p.Text("amount"),profile.Terms.Currency),AccountingFlowKind.EconomicCash)).ToImmutableArray();
                if(flows.Any(f=>f.EffectiveAt<row.Instant("effective_at")||f.KnownAt<time.KnownAt)||!stepRows.Any(s=>s.Id("id")==row.Id("determination_id")))
                    throw Error("Cash and payment revisions require their complete accepted principal membership and original effective time.");
                return new GasPhysicalKnownCash(row.Id("id"),row.Id("determination_id"),cashOwner,instrument,quantity,
                    new(cashOwner,instrument,quantity,flows,RecordedCashValuation.Policy,knowledge.ContextId,knowledge.KnownAt),row.Instant("effective_at"),time);
            }).ToImmutableArray();
            var stockIds=performances.Where(p=>p.ReceiptStockOwner is not null).Select(p=>p.ReceiptStockOwner!.Value.Owner.Id)
                .Concat(performances.SelectMany(p=>p.Supply).Select(s=>s.StockOwner.Owner.Id)).Distinct();
            var stocks=stockIds.Select(id=>
            {
                var stock=journal.Positions[id];var account=journal.Accounts[stock.AccountId];var lot=journal.Lots.Single(l=>l.Id==id);
                var instrument=new GasCommodityHoldingInstrument(GasCommodityHoldingInstrument.SelectedNativeMarkIdentity(id,stock.Mark,0),stock.Mark,stock.Quantity.Specification,account.Currency,stock.DueAt,stock.Carrying);
                var reservations=journal.Reservations.Where(r=>r.LotId==id).ToImmutableArray();
                return new GasPhysicalKnownStock(stock.Owner,stock.AccountId,account.Account.LegalOwner,instrument,lot,
                    [..journal.MarkBindings.Where(m=>m.PositionId==id).Select(m=>new GasPhysicalKnownMark(m.Revision,m.Mark,m.Time.EffectiveAt,ReceiptOf(m.Time)))],reservations,
                    [..journal.Releases.Where(r=>reservations.Any(s=>s.Id==r.ReservationId)).Select(r=>new GasPhysicalKnownRelease(r.ReservationId,r.Time.EffectiveAt,ReceiptOf(r.Time)))],
                    [..journal.Consumptions.Where(c=>c.LotId==id)],journal.Quantities.Positions[id].Revision,journal.Quantities.Positions[id].Retracted,ReceiptOf(stock.Time));
            }).ToImmutableArray();
            result.Add(new(snapshot,parent,owner,Integer(link,"origin_revision"),link.Id("policy_id"),Integer(link,"policy_revision"),profile.Id,profile.Revision,profile.Terms,profile.PrincipalRounding,
                profile.PrincipalScope,profile.CorrectionDuePolicy,finalOccurrence,original,link.Id("initial_determination_id"),finals[^1],finals,held.Time.EffectiveAt,ReceiptOf(held.Time),performances,runs,cash,stocks));
        }
        if(result.Select(a=>a.FutureOwner).Distinct().Count()!=result.Count||result.Select(a=>a.DeliveryOwner).Distinct().Count()!=result.Count)
            throw Error("Each native future final has one distinct surviving physical obligation.");
        return result.ToImmutable();
    }
    private static int Integer(JsonElement row,string field)=>int.Parse(row.Text(field),CultureInfo.InvariantCulture);
    private static MarketDataException Error(string message)=>new("gas_physical_known_source",message);
}
