using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record FuturesMarginRead(FuturesMarginState State,FuturesMarginHistory History,int TradeRevision,DateTimeOffset AuthorityUntil)
{
    internal int MarginRevision{get;init;}=1;
    internal ImmutableArray<TradeInstrument> CashBindings{get;init;}=[];
    internal ImmutableArray<FuturesMarginCashOrigin> CashOrigins{get;init;}=[];
}
internal sealed record FuturesMarginCashOrigin(Guid BindingId,Guid DeterminationId,int PublicationOrder,DateTimeOffset OccurrenceAt);

internal static class FuturesMarginStore
{
    internal static Task<FuturesMarginRead> ReadInception(NpgsqlConnection connection,Guid catalog,PreviewData data,InputFrame frame,
        MarketSnapshot snapshot,Trade trade,Guid bindingId,CancellationToken ct)=>ReadNative(connection,catalog,data,frame,snapshot,trade,bindingId,true,ct);
    internal static Task<FuturesMarginRead> Read(NpgsqlConnection connection,Guid catalog,PreviewData data,InputFrame frame,
        MarketSnapshot snapshot,Trade trade,Guid bindingId,CancellationToken ct)=>ReadNative(connection,catalog,data,frame,snapshot,trade,bindingId,false,ct);
    private static async Task<FuturesMarginRead> ReadNative(NpgsqlConnection connection,Guid catalog,PreviewData data,InputFrame frame,
        MarketSnapshot snapshot,Trade trade,Guid bindingId,bool inceptionOnly,CancellationToken ct)
    {
        if(frame.ContextId is not {} context||snapshot.Knowledge?.ContextId!=context||snapshot.ValuationAt!=frame.At||
            data.DefinitionRevision!=snapshot.Definitions||!snapshot.HasSameInputs(data.Snapshot(frame).WithKnowledge(context,snapshot.Knowledge.KnownAt)))
            throw Invalid("Select the exact certified source frame and its retained inputs.");
        var binding=trade.Instruments.SingleOrDefault(b=>b.Id==bindingId)??throw Invalid("Select this trade's exact held futures binding.");
        var execution=FuturesExecution.Require(binding);var market=execution.MarketBinding;
        await using var command=ScheduleStore.Command(connection,
            inceptionOnly?"SELECT settlement_data.read_futures_margin_inception($1,$2,$3,$4,$5)::text":
                "SELECT settlement_data.read_futures_margin_state($1,$2,$3,$4,$5)::text",catalog,trade.Id,bindingId,context,frame.At);
        var result=JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
        var until=result.Instant("authorityUntil");
        frame.Time.RequireDate(DateWorkspace.Date(result.Text("effectiveOn")));
        if(result.Id("contextId")!=context||result.Instant("knownAt")!=snapshot.Knowledge.KnownAt)
            throw Invalid("Native margin evidence must retain this exact resolved knowledge cutoff.");
        if(!result.GetProperty("available").GetBoolean())
        {
            await RequireRead(connection,catalog,context,frame.At,until,ct);
            throw new MarketDataException("futures_margin_unavailable","This held binding has no visible certified native margin inception.");
        }
        if(result.Text("stateProfile")!=(inceptionOnly?"native_execution_inception_v1":FuturesMarginCoordinator.Version))
            throw Invalid("Select the supported actual margin-state producer; inception cannot substitute for accepted settlement history.");
        var policy=RequireHeldOrigin(catalog,trade,binding,frame,market,result);
        var origin=result.GetProperty("origin");var head=result.GetProperty("head");var adoption=result.GetProperty("adoption");
        var terms=market.Instrument.ProposeMarginTerms(trade,bindingId,policy,data.Model,snapshot);
        var state=new FuturesMarginState(trade.Id,bindingId,trade.BookId,binding.Quantity,market.Series.Key,
            ExactUnitPrice.FromPrice(new(origin.Number("execution_price"),market.Quote)),origin.Instant("execution_at"),result.Instant("knownFrom"),
            FuturesMarginState.Version,FuturesMarginStatus.Open,null,context,snapshot.Knowledge.KnownAt,1,Integer(adoption,"publication_order"));
        ImmutableArray<RecordedFuturesMargin> recorded=[];
        if(result.TryGetProperty("determinations",out var determinations))
            recorded=[..determinations.EnumerateArray().Select(row=>
            {
                var occurrence=policy.Occurrences.Single(o=>o.Id==row.Id("occurrence_id"));
                var observation=data.ReadObservation(row.GetProperty("source"));
                var sample=snapshot with{Selections=[new(new(observation.Series,occurrence.Phase==FuturesMarginPhase.Final?InputUse.FrozenFact:InputUse.Variable),observation)]};
                sample=sample with{Id=CustomCurves.StableSampleId(sample)};
                var fact=market.Instrument.ResolveSettlementFact(market,policy.SettlementCurve,data.Model,sample,observation.Key,
                    row.Instant("source_known_at"),occurrence.At,occurrence.Phase==FuturesMarginPhase.Final);
                return new RecordedFuturesMargin(occurrence.Id,row.Id("id"),row.OptionalId("basis_id"),
                    ExactUnitPrice.FromPrice(new(row.Number("previous_basis"),market.Quote)),fact,
                    ExactMoney.Parse(row.Text("exact_variation"),market.Quote.Currency),new(row.Number("rounded_total"),market.Quote.Currency),
                    row.Instant("known_from"),Integer(row,"publication_order"));
            })];
        var history=new FuturesMarginHistory(terms,context,snapshot.Knowledge.KnownAt,recorded);
        if(!recorded.IsEmpty)
        {
            var accepted=market.Instrument.PlanMarginTrajectory(trade,bindingId,policy,snapshot,recorded[^1].OccurrenceId,
                recorded.ToImmutableDictionary(r=>r.OccurrenceId,r=>r.Evidence),history,ImmutableDictionary<Guid,DateTimeOffset>.Empty);
            if(accepted.Steps.Any(s=>!s.NoChange))throw Invalid("The native accepted prefix must reconcile without a financial restatement.");
            var last=recorded[^1];
            state=state with{SettledBasis=accepted.Basis.Price,SettledAt=accepted.Basis.At,Status=accepted.Basis.Status,
                KnownAt=last.KnownAt,LatestSettlement=last.Evidence,StateRevision=Integer(result,"version"),PublicationOrder=last.PublicationOrder};
        }
        var cash=result.TryGetProperty("obligations",out var obligations)?obligations.EnumerateArray().Select(row=>
        {
            var instrument=CashPaymentStore.Materialize(row);
            return new TradeInstrument(row.Id("cash_binding_id"),instrument,new(row.Number("adjustment"),instrument.Currency));
        }).ToImmutableArray():[];
        FuturesMargin.Require(state,market.Instrument);
        await RequireRead(connection,catalog,context,frame.At,until,ct);
        return new(state,history,Integer(head,"revision"),until){MarginRevision=result.TryGetProperty("version",out _)?Integer(result,"version"):1,CashBindings=cash,
            CashOrigins=result.TryGetProperty("obligations",out var origins)?[..origins.EnumerateArray().Select(row=>
                new FuturesMarginCashOrigin(row.Id("cash_binding_id"),row.Id("id"),Integer(row,"publication_order"),row.Instant("at")))]:[]};
    }

    internal static FuturesMarginPolicy RequireHeldOrigin(Guid catalog,Trade trade,TradeInstrument binding,InputFrame frame,
        FuturesMarketBinding market,JsonElement result)
    {
        var bindingId=binding.Id;var execution=FuturesExecution.Require(binding);
        var origin=result.GetProperty("origin");var head=result.GetProperty("head");var nativeMarket=result.GetProperty("market");
        var adoption=result.GetProperty("adoption");
        var booking=trade.Booking??throw Invalid("Retain the native booking date and exact legal-entity and counterparty versions.");
        if(booking.TradeDate!=DateWorkspace.Date(origin.Text("trade_date"))||
            booking.LegalEntity.Key!=new PartyKey(catalog,origin.Id("legal_entity_id"),Integer(origin,"legal_entity_revision"))||
            booking.Counterparty.Key!=new PartyKey(catalog,origin.Id("counterparty_id"),Integer(origin,"counterparty_revision")))
            throw Invalid("The native inception must retain this exact booking date and party identities.");
        if(origin.Id("trade_id")!=trade.Id||origin.Id("binding_id")!=bindingId||origin.Id("book_id")!=trade.BookId||
            origin.Id("instrument_id")!=binding.RetainedInstrumentId||origin.Number("lots")!=execution.Lots(binding.Quantity)||
            origin.Number("execution_price")!=execution.ExecutionPrice.Value||origin.Instant("execution_at")!=execution.ExecutedAt||
            head.Id("trade_id")!=trade.Id||head.Id("binding_id")!=bindingId||head.Id("book_id")!=trade.BookId||
            head.Id("instrument_id")!=binding.RetainedInstrumentId||head.Number("lots")!=origin.Number("lots")||
            head.Number("execution_price")!=origin.Number("execution_price")||head.Instant("execution_at")!=origin.Instant("execution_at")||
            nativeMarket.Id("definition_revision_id")!=market.Contract.Key.Revision.RevisionId||nativeMarket.Id("contract_id")!=market.Contract.Key.Id||
            nativeMarket.Text("curve_code")!=market.Market.Curve.Code||nativeMarket.Text("implementation")!=market.Convention||
            new ReferenceKey(new(nativeMarket.Id("reference_catalog_id"),nativeMarket.Id("reference_revision_id")),nativeMarket.Text("series_code"))!=market.Series.Key||
            adoption.Id("trade_id")!=trade.Id||adoption.Id("binding_id")!=bindingId||adoption.Id("book_id")!=trade.BookId||
            adoption.Instant("effective_at")!=origin.Instant("effective_at")||origin.Instant("effective_at")>frame.At)
            throw Invalid("The native adoption must retain this exact visible owner, execution, quantity and pricing reference.");
        var policyHeader=result.GetProperty("policy");
        if(policyHeader.Text("quote_code")!=market.Quote.Key.Code)
            throw Invalid("The adopted policy must retain the held contract's exact native quotation.");
        var policy=ReadPolicy(policyHeader,result.GetProperty("occurrences"));
        if(policy.Id!=adoption.Id("policy_id")||policy.Revision!=Integer(adoption,"policy_revision"))
            throw Invalid("The adopted policy must equal the exact native version read at this cutoff.");
        return policy;
    }

    internal static FuturesMarginPolicy ReadPolicy(JsonElement header,JsonElement occurrences)
    {
        var definition=new DefinitionRevision(header.Id("catalog_id"),header.Id("definition_revision_id"));
        var currency=new ReferenceKey(new(header.Id("reference_catalog_id"),header.Id("reference_revision_id")),header.Text("currency_code"));
        if(header.Text("rounding_stage")!="payment_total")throw Invalid("Select the exact supported policy rounding stage.");
        var rounding=new CashRoundingConvention(currency,header.Number("rounding_increment"),header.Text("tie_rule") switch{
            "to_even"=>CashTieRule.ToEven,"away_from_zero"=>CashTieRule.AwayFromZero,_=>throw Invalid("Select the exact supported tie rule.")},CashRoundingStage.PaymentTotal);
        return new(header.Id("id"),Integer(header,"revision"),new(definition,header.Id("contract_id")),new(definition,header.Text("settlement_curve_code")),
            header.Instant("from_at"),header.Instant("until_at"),[..occurrences.EnumerateArray().Select(o=>new FuturesMarginOccurrence(o.Id("id"),o.Instant("at"),
                o.Instant("due_at"),o.Text("phase") switch{"daily"=>FuturesMarginPhase.Daily,"final"=>FuturesMarginPhase.Final,_=>throw Invalid("Select a supported margin phase.")}))],
            rounding,header.Text("rounding_scope"),header.Text("opening") switch{
                "include_opening_instant"=>FuturesMarginOpening.IncludeOpeningInstant,"after_opening_instant"=>FuturesMarginOpening.AfterOpeningInstant,
                _=>throw Invalid("Select the explicit policy opening convention.")},header.Text("implementation"));
    }
    internal static async Task RequireRead(NpgsqlConnection connection,Guid catalog,Guid context,DateTimeOffset at,DateTimeOffset until,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.require_futures_margin_read($1,$2,$3,$4)",catalog,context,at,until);
        await command.ExecuteNonQueryAsync(ct);
    }
    private static int Integer(JsonElement value,string field)=>int.Parse(value.Text(field),CultureInfo.InvariantCulture);
    private static MarketDataException Invalid(string message)=>new("futures_margin_state",message);
}
