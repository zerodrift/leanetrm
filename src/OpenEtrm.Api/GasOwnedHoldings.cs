using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasOwnedMarket(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot);
internal sealed record GasOwnedPosition(AccountingPositionKey Position,Guid AccountId,PartyKey LegalOwner,string Kind,string SettlementMode,
    int Revision,int PublicationOrder,DateTimeOffset EffectiveAt,Guid EvidenceId,GasCommodityHoldingInstrument Instrument,Quantity Quantity);
internal sealed record GasOwnedFeePosition(AccountingPositionKey Position,Guid AccountId,Guid? LoanId,TimedCashPayment Instrument,
    Quantity Quantity,RecordedCashValuation State);
internal sealed record GasOwnedPortfolioRead(Guid ContextId,DateTimeOffset At,DateTimeOffset KnownAt,
    ImmutableArray<GasOwnedPosition> Positions,ImmutableArray<GasOwnedFeePosition> Fees,
    ImmutableDictionary<Guid,GasOwnedMarket> Markets,ImmutableDictionary<MeasureBinding,BindingAccountingFacts> AccountingFacts);

// Inventory is captured by its independent owner/book, never by whether a
// source purchase or loan trade remains in the requested trade population.
internal static class GasOwnedHoldings
{
    private sealed record Admission(Guid Catalog,Guid Context,DateTimeOffset At,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT security.require_gas_owned_read($1,$2,$3,$4)",Catalog,Context,At,Until);
            await command.ExecuteNonQueryAsync(ct);
        }
    }
    internal static async Task<GasOwnedPortfolioRead> Read(NpgsqlConnection connection,PreviewDatabase database,
        InputFrame frame,MarketSnapshot snapshot,ImmutableArray<Guid> bookIds,CancellationToken ct)
    {
        if(frame.ContextId is not {} context||snapshot.Knowledge?.ContextId!=context||snapshot.ValuationAt!=frame.At||bookIds.IsDefault)
            throw Error("Select exact certified gas-owned holdings, an explicit book scope and their source frame.");
        var catalog=database.Manifest.CatalogId;
        await using var command=ScheduleStore.Command(connection,"SELECT security.read_gas_owned_holdings($1,$2,$3,$4::uuid[])::text",catalog,context,frame.At,bookIds.ToArray());
        var envelope=JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
        if(envelope.Id("contextId")!=context||envelope.Instant("knownAt")!=snapshot.Knowledge.KnownAt||envelope.Instant("asOf")!=frame.At||
            DateWorkspace.Date(envelope.Text("effectiveOn"))!=frame.BusinessDate)
            throw Error("The gas-owned read must retain the exact business and knowledge coordinates.");
        var admission=new Admission(catalog,context,frame.At,envelope.Instant("authorityUntil"));
        database.RegisterCommitAdmission(connection,admission);
        var root=await MarketInputSelection.Root(database,connection,frame,ct);
        var markets=ImmutableDictionary.CreateBuilder<Guid,GasOwnedMarket>();
        foreach(var revision in envelope.GetProperty("positions").EnumerateArray().Concat(envelope.GetProperty("fees").EnumerateArray()).Select(r=>r.Id("definition_revision_id")).Distinct())
        {
            var source=await MarketInputSelection.DefinitionInput(database,connection,root,frame,revision,ct);
            markets.Add(revision,new(source.Data,source.Frame,source.Data.Snapshot(source.Frame).WithKnowledge(context,snapshot.Knowledge.KnownAt)));
        }
        var positions=ImmutableArray.CreateBuilder<GasOwnedPosition>();
        var fees=ImmutableArray.CreateBuilder<GasOwnedFeePosition>();
        var accounting=ImmutableDictionary.CreateBuilder<MeasureBinding,BindingAccountingFacts>();
        foreach(var row in envelope.GetProperty("positions").EnumerateArray())
        {
            if(row.GetProperty("requires_reconciliation").GetBoolean())
                throw new MarketDataException("gas_owned_reconciliation","Accepted gas ownership references corrected source evidence. Reconcile it before a complete valuation.");
            if(row.GetProperty("requires_cashout_determination").GetBoolean())
                throw new MarketDataException("gas_owned_cashout_required","Determine the operator cashout from its selected final source before valuing the remaining claim.");
            var reference=new ReferenceRevision(row.Id("reference_catalog_id"),row.Id("reference_revision_id"));
            var revision=new DefinitionRevision(catalog,row.Id("definition_revision_id"));
            var position=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,row.Id("id")),row.Id("id"),row.Id("book_id"));
            var due=row.OptionalText("due_at") is {} dueText?TradingInstant.Parse(dueText,"gas_holding_terms"):(DateTimeOffset?)null;
            var instrument=new GasCommodityHoldingInstrument(row.Id("id"),new(new(revision,row.Text("mark_curve_code")),new(revision,row.Id("mark_contract_id"))),
                new(reference,row.Text("quantity_code")),new(reference,row.Text("currency_code")),due,row.Text("carrying") switch{
                    "nominal_mark_v1"=>GasHoldingCarrying.Nominal,"currency_curve_v1"=>GasHoldingCarrying.CurrencyCurve,_=>throw Error("Unknown gas carrying convention.")});
            if(markets[revision.RevisionId].Data.ReferenceRevision!=reference)throw Error("Gas holdings require their exact mark-reference revision.");
            positions.Add(new(position,row.Id("account_id"),new(catalog,row.Id("legal_entity_id"),Integer(row,"legal_entity_revision")),row.Text("kind"),row.Text("settlement_mode"),
                1,Integer(row,"publication_order"),row.Instant("effective_at"),row.Id("evidence_id"),instrument,new(row.Number("remaining_quantity"),instrument.QuantitySpecification)));
            // This quantity ledger alone cannot attest purchase cash or capital
            // transfers. The actual P&L adapter must require the native source
            // handoff ledger rather than treating missing coverage as no cash.
        }
        foreach(var row in envelope.GetProperty("fees").EnumerateArray())
        {
            var reference=new ReferenceRevision(row.Id("reference_catalog_id"),row.Id("reference_revision_id"));
            var instrument=new TimedCashPayment(new(reference,row.Text("currency_code")),row.Instant("due_at"));
            var position=new AccountingPositionKey(new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,row.Id("position_id")),row.Id("id"),row.Id("book_id"));
            var quantity=new Quantity(row.Number("amount"),instrument.Currency);
            var flows=envelope.GetProperty("payments").EnumerateArray().Where(p=>p.Id("fee_id")==row.Id("id")).Select(p=>
                new AccountingFlow(p.Id("allocation_id"),Integer(p,"revision"),position,p.Instant("paid_at"),p.Instant("known_at"),
                    ExactMoney.Parse(p.Text("amount"),instrument.Currency),AccountingFlowKind.EconomicCash)).ToImmutableArray();
            if(flows.Any(f=>f.EffectiveAt<row.Instant("effective_at")))
                throw new MarketDataException("gas_owned_payment_origin","A payment before this owned cash obligation existed requires a separately qualified advance asset.");
            var state=new RecordedCashValuation(position,instrument,quantity,flows,RecordedCashValuation.Policy,context,snapshot.Knowledge.KnownAt);
            fees.Add(new(position,row.Id("account_id"),row.OptionalId("loan_id"),instrument,quantity,state));
            accounting.Add(new(position.Owner,position.BindingId),new(position,context,snapshot.Knowledge.KnownAt,flows));
        }
        await admission.RequireCommit(connection,ct);
        return new(context,frame.At,snapshot.Knowledge.KnownAt,positions.ToImmutable(),fees.ToImmutable(),markets.ToImmutable(),accounting.ToImmutable());
    }
    private static int Integer(JsonElement row,string field)=>int.Parse(row.Text(field),CultureInfo.InvariantCulture);
    private static MarketDataException Error(string message)=>new("gas_owned_context",message);
}
