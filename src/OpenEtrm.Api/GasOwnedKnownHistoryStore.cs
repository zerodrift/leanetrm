using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasOwnedKnownTime(int PublicationOrder,DateTimeOffset EffectiveAt,DateTimeOffset KnownAt);
internal sealed record GasOwnedKnownAccount(GasProjectionAccount Account,ReferenceRevision Reference,ReferenceKey Quantity,ReferenceKey Currency,
    string Kind,DefinitionRevision Definition,CurveRequest Mark,GasOwnedKnownTime Time);
internal sealed record GasOwnedKnownPosition(AccountingPositionKey Owner,Guid AccountId,string Kind,string OriginPolicy,string SettlementMode,
    Quantity Quantity,Guid EvidenceId,CurveRequest Mark,DateTimeOffset? DueAt,GasHoldingCarrying Carrying,GasOwnedKnownTime Time);
internal sealed record GasOwnedKnownEvidence(Guid Id,Guid AccountId,string SourceKey,int Revision,string Kind,Quantity Quantity,DateTimeOffset OccurredAt,DateTimeOffset KnownAt);
internal sealed record GasOwnedKnownRelease(Guid ReservationId,GasOwnedKnownTime Time);
internal sealed record GasOwnedKnownSettlement(Guid Id,Guid PositionId,Quantity Quantity,Guid? ReceiptLotId,Guid? ConsumptionId,Guid? CashId,
    Guid EvidenceId,Guid? SourceObservationId,GasOwnedKnownTime Time);
internal sealed record GasOwnedKnownFee(AccountingPositionKey Owner,Guid AccountId,Guid? LoanId,TimedCashPayment Instrument,
    Quantity Quantity,RecordedCashValuation State,GasOwnedKnownTime Time);
internal sealed record GasOwnedKnownMark(Guid PositionId,int Revision,CurveRequest Mark,GasOwnedKnownTime Time);
internal sealed record GasOwnedKnownCashout(Guid PositionId,ReferenceKey Source,ObservationRole Role,DateTimeOffset ObservedAt,DateTimeOffset CashDueAt,string Rounding);
internal sealed record GasOwnedKnownHistory(MarketSnapshot Source,[property:JsonIgnore]DateTimeOffset AuthorityUntil,
    ImmutableDictionary<Guid,GasOwnedKnownAccount> Accounts,ImmutableDictionary<Guid,GasOwnedKnownPosition> Positions,
    ImmutableArray<GasOwnedLot> Lots,ImmutableArray<GasStockReservation> Reservations,ImmutableArray<GasOwnedKnownRelease> Releases,
    ImmutableArray<GasStockConsumption> Consumptions,ImmutableArray<GasCommodityLoan> Loans,ImmutableArray<GasLoanRepayment> Repayments,
    ImmutableArray<GasOwnedKnownSettlement> Settlements,ImmutableArray<GasOwnedKnownFee> Fees,ImmutableArray<GasOwnedKnownEvidence> Evidence,
    ImmutableArray<GasOwnedKnownMark> MarkBindings,ImmutableArray<GasOwnedKnownCashout> Cashouts);

// Captures facts known at opening without advancing effectiveness. A future
// title or return stays source evidence until the pure path reaches its event.
internal static class GasOwnedKnownHistoryStore
{
    internal static async Task<ImmutableArray<GasOwnedProjectionAccountChoice>> ReadChoices(NpgsqlConnection connection,PreviewDatabase database,
        InputFrame frame,ImmutableArray<Guid> books,CancellationToken ct)
    {
        if(frame.ContextId is not {} context||books.IsDefaultOrEmpty)throw Error("Retain exact source coordinates and selected books for the gas profile choices.");
        var native=await LifecycleNative.Read(connection,"SELECT security.read_gas_owned_projection_choices($1,$2,$3,$4::uuid[])::text",ct,
            database.Manifest.CatalogId,context,frame.At,books.ToArray());
        if(native.Id("contextId")!=context||native.Instant("asOf")!=frame.At||DateWorkspace.Date(native.Text("effectiveOn"))!=frame.BusinessDate)
            throw Error("The gas profile choices must retain this source business date and opening instant.");
        var admission=new GasOwnedSourceReadAdmission(database.Manifest.CatalogId,context,frame.At,native.Instant("authorityUntil"));database.RegisterCommitAdmission(connection,admission);
        var result=native.GetProperty("accounts").EnumerateArray().Select(a=>new GasOwnedProjectionAccountChoice(a.Id("accountId"),a.Text("code"),a.Id("bookId"),a.Text("book"),a.Instant("effectiveAt"),
            [..a.GetProperty("profiles").EnumerateArray().Select(p=>new GasOwnedProjectionProfileChoice(p.Id("profileId"),p.GetProperty("revision").GetInt32(),p.Text("implementation"),p.Text("quantityDisposition"),p.Instant("effectiveAt")))])).ToImmutableArray();
        await admission.RequireCommit(connection,ct);return result;
    }
    private sealed record ScopeAdmission(Guid Catalog,Guid Context,DateTimeOffset At,ImmutableArray<Guid> Books,bool Configured,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var call=ScheduleStore.Command(connection,"SELECT security.require_gas_owned_projection_scope($1,$2,$3,$4::uuid[],$5,$6)",Catalog,Context,At,Books.ToArray(),Configured,Until);
            await call.ExecuteScalarAsync(ct);
        }
    }
    internal static async Task<bool> RequireScope(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ImmutableArray<Guid> books,bool configured,CancellationToken ct)
    {
        if(frame.ContextId is not {} context||books.IsDefaultOrEmpty)throw Error("Retain the exact source and explicit gas projection book scope.");
        await using var capture=ScheduleStore.Command(connection,configured?
            "SELECT least(settlement_data.futures_margin_preview_authority($1),security.authority_until('ReadGasOwnedHoldings'))":
            "SELECT settlement_data.futures_margin_preview_authority($1)",database.Manifest.CatalogId);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        database.RegisterCommitAdmission(connection,new ScopeAdmission(database.Manifest.CatalogId,context,frame.At,books,configured,until));
        await using var call=ScheduleStore.Command(connection,"SELECT security.require_gas_owned_projection_scope($1,$2,$3,$4::uuid[],$5,$6)",
            database.Manifest.CatalogId,context,frame.At,books.ToArray(),configured,until);
        try{return(bool)(await call.ExecuteScalarAsync(ct))!;}
        catch(PostgresException error)when(error.SqlState=="P0001"&&error.MessageText=="gas_owned_projection_policy_required")
        {throw new MarketDataException(error.MessageText,GasOwnedOperations.Refusal(error.MessageText),error);}
    }
    internal static async Task<GasOwnedKnownHistory> Read(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,MarketSnapshot snapshot,
        ImmutableArray<Guid> books,CancellationToken ct)
    {
        if(frame.ContextId is not {} context||snapshot.Knowledge is not {} knowledge||knowledge.ContextId!=context||snapshot.ValuationAt!=frame.At||books.IsDefaultOrEmpty)
            throw Error("Retain the exact source certificate, opening instant and explicit selected books for the gas journal.");
        var root=await MarketInputSelection.Root(database,connection,frame,ct);
        if(!snapshot.HasSameInputs(root.Snapshot(frame).WithKnowledge(context,knowledge.KnownAt)))
            throw Error("The known gas journal cannot replace the certified source or its exact selected inputs.");
        var catalog=database.Manifest.CatalogId;
        var native=await LifecycleNative.Read(connection,"SELECT security.read_gas_owned_known_history($1,$2,$3,$4::uuid[])::text",ct,catalog,context,frame.At,books.ToArray());
        if(native.Text("stateProfile")!="known_gas_owned_journal_projection_source_v1"||native.Id("contextId")!=context||native.Instant("asOf")!=frame.At||
            native.Instant("knownAt")!=knowledge.KnownAt||DateWorkspace.Date(native.Text("effectiveOn"))!=frame.BusinessDate)
            throw Error("The gas journal must preserve opening business time and its original resolved knowledge cutoff.");
        var admission=new GasOwnedSourceReadAdmission(catalog,context,frame.At,native.Instant("authorityUntil"));database.RegisterCommitAdmission(connection,admission);
        ImmutableArray<JsonElement> Rows(string key)=>[..native.GetProperty(key).EnumerateArray()];
        GasOwnedKnownTime Time(JsonElement row,string effective="effective_at")
        {
            var value=new GasOwnedKnownTime(row.GetProperty("publication_order").GetInt32(),row.Instant(effective),row.Instant("known_at"));
            if(value.PublicationOrder<1||value.KnownAt>knowledge.KnownAt)throw Error("Every accepted gas fact must be in the captured native knowledge prefix.");return value;
        }
        var accounts=Rows("accounts").Select(row=>
        {
            var reference=new ReferenceRevision(row.Id("reference_catalog_id"),row.Id("reference_revision_id"));
            var definition=new DefinitionRevision(catalog,row.Id("definition_revision_id"));
            return new GasOwnedKnownAccount(new(row.Id("id"),row.Id("book_id"),new(catalog,row.Id("legal_entity_id"),row.GetProperty("legal_entity_revision").GetInt32()),
                new(reference,row.Text("location_code"))),reference,new(reference,row.Text("quantity_code")),new(reference,row.Text("currency_code")),row.Text("kind"),definition,
                new(new(definition,row.Text("mark_curve_code")),new(definition,row.Id("mark_contract_id"))),Time(row));
        }).ToImmutableDictionary(a=>a.Account.Id);
        if(accounts.Values.Any(a=>!books.Contains(a.Account.BookId)))throw Error("A known gas account escaped the selected books.");
        var positions=Rows("positions").Select(row=>
        {
            var account=accounts[row.Id("account_id")];var definition=new DefinitionRevision(catalog,row.Id("definition_revision_id"));
            var owner=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,row.Id("id")),row.Id("id"),account.Account.BookId);
            return new GasOwnedKnownPosition(owner,account.Account.Id,row.Text("kind"),row.Text("origin_policy"),row.Text("settlement_mode"),new(row.Number("quantity"),account.Quantity),row.Id("evidence_id"),
                new(new(definition,row.Text("mark_curve_code")),new(definition,row.Id("mark_contract_id"))),OptionalInstant(row,"due_at"),row.Text("carrying") switch{
                    "nominal_mark_v1"=>GasHoldingCarrying.Nominal,"currency_curve_v1"=>GasHoldingCarrying.CurrencyCurve,_=>throw Error("Unknown native gas carrying convention.")},Time(row));
        }).ToImmutableDictionary(p=>p.Owner.Owner.Id);
        var evidence=Rows("evidence").Select(row=>new GasOwnedKnownEvidence(row.Id("id"),row.Id("account_id"),row.Text("source_key"),row.GetProperty("revision").GetInt32(),row.Text("kind"),
            new(row.Number("quantity"),accounts[row.Id("account_id")].Quantity),row.Instant("occurred_at"),row.Instant("known_at"))).ToImmutableArray();
        var lots=Rows("lots").Select(row=>{var p=positions[row.Id("id")];return new GasOwnedLot(row.Id("id"),row.Id("account_id"),p.Quantity,row.Instant("title_at"),row.Instant("available_at"),p.Time.KnownAt,p.EvidenceId);}).ToImmutableArray();
        var releases=Rows("releases").Select(row=>new GasOwnedKnownRelease(row.Id("reservation_id"),Time(row))).ToImmutableArray();
        var reservations=Rows("reservations").Select(row=>new GasStockReservation(row.Id("id"),row.Id("lot_id"),new(row.Number("quantity"),positions[row.Id("lot_id")].Quantity.Specification),
            row.Instant("effective_at"),row.Instant("known_at"),releases.SingleOrDefault(r=>r.ReservationId==row.Id("id"))?.Time.EffectiveAt)).ToImmutableArray();
        var consumptions=Rows("consumptions").Select(row=>new GasStockConsumption(row.Id("id"),row.Id("lot_id"),new(row.Number("quantity"),positions[row.Id("lot_id")].Quantity.Specification),
            row.Text("kind") switch{"delivery"=>GasStockUse.Delivery,"return"=>GasStockUse.Return,"loss"=>GasStockUse.Loss,_=>throw Error("Unknown native gas consumption kind.")},
            row.Instant("effective_at"),row.Instant("known_at"),row.Id("evidence_id"),row.OptionalId("reservation_id"))).ToImmutableArray();
        var loans=Rows("loans").Select(row=>{var p=positions[row.Id("return_position_id")];return new GasCommodityLoan(row.Id("id"),row.Id("account_id"),row.Id("received_lot_id"),
            new(row.Number("quantity"),p.Quantity.Specification),p.Time.EffectiveAt,row.Instant("return_at"),p.Time.KnownAt,row.Text("title_policy"),p.EvidenceId);}).ToImmutableArray();
        var repayments=Rows("repayments").Select(row=>{var use=consumptions.Single(c=>c.Id==row.Id("consumption_id"));return new GasLoanRepayment(row.Id("id"),row.Id("loan_id"),use.LotId,use.Quantity,use.At,use.KnownAt,use.EvidenceId);}).ToImmutableArray();
        var settlements=Rows("settlements").Select(row=>new GasOwnedKnownSettlement(row.Id("id"),row.Id("position_id"),new(row.Number("quantity"),positions[row.Id("position_id")].Quantity.Specification),
            row.OptionalId("receipt_lot_id"),row.OptionalId("consumption_id"),row.OptionalId("cash_id"),row.Id("evidence_id"),row.OptionalId("source_observation_id"),Time(row))).ToImmutableArray();
        var paymentRows=Rows("payments");
        var fees=Rows("fees").Select(row=>
        {
            var parent=positions[row.Id("position_id")];var account=accounts[parent.AccountId];var instrument=new TimedCashPayment(account.Currency,row.Instant("due_at"));
            var owner=new AccountingPositionKey(parent.Owner.Owner,row.Id("id"),parent.Owner.BookId);var quantity=new Quantity(row.Number("amount"),account.Currency);var time=Time(row);
            var payments=paymentRows.Where(p=>p.Id("fee_id")==row.Id("id")).ToArray();
            var allocations=payments.GroupBy(p=>p.Text("business_key")).ToDictionary(g=>g.Key,g=>g.OrderBy(p=>p.GetProperty("revision").GetInt32()).First().Id("id"));
            var flows=payments.Select(p=>new AccountingFlow(allocations[p.Text("business_key")],p.GetProperty("revision").GetInt32(),owner,p.Instant("paid_at"),p.Instant("known_at"),ExactMoney.Parse(p.Text("amount"),account.Currency),AccountingFlowKind.EconomicCash)).ToImmutableArray();
            if(flows.Any(p=>p.EffectiveAt<time.EffectiveAt||p.KnownAt<time.KnownAt))throw Error("A source-known gas payment cannot predate its accepted owned cash membership.");
            return new GasOwnedKnownFee(owner,parent.AccountId,row.OptionalId("loan_id"),instrument,quantity,
                new(owner,instrument,quantity,flows,RecordedCashValuation.Policy,context,knowledge.KnownAt),time);
        }).ToImmutableArray();
        var marks=Rows("markBindings").Select(row=>{var definition=new DefinitionRevision(catalog,row.Id("definition_revision_id"));return new GasOwnedKnownMark(row.Id("position_id"),row.GetProperty("revision").GetInt32(),
            new(new(definition,row.Text("mark_curve_code")),new(definition,row.Id("mark_contract_id"))),Time(row));}).ToImmutableArray();
        var cashouts=Rows("cashoutTerms").Select(row=>new GasOwnedKnownCashout(row.Id("position_id"),
            new(new(row.Id("reference_catalog_id"),row.Id("reference_revision_id")),row.Text("source_code")),row.Text("role") switch{
                "daily_settlement"=>ObservationRole.DailySettlement,"final_contract_settlement"=>ObservationRole.FinalContractSettlement,_=>throw Error("Unknown native operator final role.")},
            row.Instant("observed_at"),row.Instant("cash_due_at"),row.Text("rounding"))).ToImmutableArray();
        if(evidence.Any(e=>e.KnownAt>knowledge.KnownAt)||positions.Values.Any(p=>!evidence.Any(e=>e.Id==p.EvidenceId)))throw Error("Each known gas position requires its exact retained source evidence.");
        await admission.RequireCommit(connection,ct);
        return new(snapshot,admission.Until,accounts,positions,lots,reservations,releases,consumptions,loans,repayments,settlements,fees,evidence,marks,cashouts);
    }
    private static DateTimeOffset? OptionalInstant(JsonElement row,string key)=>row.OptionalText(key) is {} value?TradingInstant.Parse(value,"gas_owned_known_context"):null;
    private static MarketDataException Error(string message)=>new("gas_owned_known_context",message);
}
