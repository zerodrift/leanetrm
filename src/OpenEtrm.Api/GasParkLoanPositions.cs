using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

internal sealed record GasParkLoanPosition(JsonElement Agreement,JsonElement Book,JsonElement Metadata)
{
    private JsonElement Terms=>Agreement.GetProperty("terms");
    private JsonElement Booking=>Agreement.GetProperty("booking");
    internal Guid Trade=>Agreement.Id("tradeId");
    internal Guid Binding=>Agreement.Id("instrumentId");
    internal Guid Definition=>Terms.Id("definitionRevisionId");
    internal ReferenceRevision Reference=>new(Terms.Id("referenceCatalogId"),Terms.Id("referenceRevisionId"));
    internal ReferenceKey Quantity=>new(Reference,Terms.Text("quantity"));
    internal ReferenceKey Currency=>new(Reference,Terms.Text("currency"));
    internal string Curve=>Terms.Text("markCurve");
    internal string From=>Terms.Text("advanceAt");
    internal string To=>Terms.Text("returnAt");
    internal TradePositionFact Fact()=>new(Trade,Binding,Agreement.GetProperty("revision").GetInt32(),TradingReference.Trade(Agreement.GetProperty("tradeReference").GetInt32()),
        Book.Id("id"),Book.Text("name"),GasParkLoanTicket.Product,Metadata.Text("curve_name"),From,To,Agreement.Text("quantity"),Metadata.Text("unit"));
    internal JsonObject Holding()=>JsonSerializer.SerializeToNode(new{
        booking=new{tradeDate=Booking.Text("trade_date"),legalEntityId=Agreement.Id("legalEntityId"),legalEntityRevision=Agreement.GetProperty("legalEntityRevision").GetInt32(),
            legalEntity=Metadata.Text("legal_entity"),counterpartyId=Agreement.Id("counterpartyId"),counterpartyRevision=Agreement.GetProperty("counterpartyRevision").GetInt32(),counterparty=Metadata.Text("counterparty")},
        traderId=Booking.Id("booker_id"),trader=Booking.Text("booker_name"),traderCode=Booking.Text("booker_login"),memo=(string?)null,
        tradeId=Trade,bindingId=Binding,instrumentId=Binding,revision=Agreement.Text("revision"),tradeReference=TradingReference.Trade(Agreement.GetProperty("tradeReference").GetInt32()),
        instrumentReference=TradingReference.Instrument(Agreement,"instrumentReference"),bookId=Book.Id("id"),book=Book.Text("name"),portfolioId=Book.Id("portfolio_id"),portfolio=Book.Text("portfolio"),
        instrumentType=GasParkLoanTicket.Product,contractIdentityKind="gas_park_loan_agreement",contractId=Binding,definitionRevisionId=Definition,
        curve=Curve,projectionCurves=new[]{Curve},marketKey=JsonSerializer.Serialize(new[]{Curve}),marketLabel=Metadata.Text("curve_name"),
        commodity=new{code=Metadata.Text("commodity_code"),name=Metadata.Text("commodity_name")},direction="multi_side",serviceKind=Terms.Text("serviceKind"),perspective=Terms.Text("perspective"),
        deliveryFrom=From,deliveryTo=To,lots=(string?)null,quantity=Agreement.Text("quantity"),unit=Metadata.Text("unit"),quantitySpecification=Quantity,
        currency=Metadata.Text("currency"),currencyReference=Currency,quoteMultiplier=Metadata.Text("multiplier"),
        executedAt=Booking.Text("executed_at"),bookingFacts=new{implementation=Booking.Text("implementation"),labelCapturedAt=Booking.Text("label_captured_at")},
        executionPrice=(string?)null,fixedPrice=(string?)null,mark=(string?)null,markMinusPrice=(string?)null,executionPnl=(string?)null,
        settlement="unprocessed",measure=GasParkLoanTicket.Measure,measureLabel=GasParkLoanTicketStore.MeasureLabel(Agreement),quantityPolicy=Agreement.Text("implementation"),ownershipNote=GasParkLoanTicketStore.OwnershipNote(Agreement),
        presentValue=(string?)null,unavailableReason=(string?)null
    },TradingBlotter.Json)!.AsObject();
}

internal static class GasParkLoanPositions
{
    internal const string Type=GasParkLoanTicket.Product;
    private sealed record PresenceAdmission(Guid Catalog,Guid Context,DateTimeOffset At,ImmutableArray<Guid> Books,
        ImmutableArray<Guid> Trades,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var guard=ScheduleStore.Command(connection,"SELECT security.require_gas_park_loan_presence($1,$2,$3,$4::uuid[],$5::uuid[],$6)",
                Catalog,Context,At,Books.ToArray(),Trades.ToArray(),Until);
            await guard.ExecuteNonQueryAsync(ct);
        }
    }

    internal static async Task<bool> Present(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ImmutableArray<Guid> books,ImmutableArray<Guid>? selectedTrades,CancellationToken ct)
    {
        if(frame.ContextId is not {} context||books.IsDefault||books.Any(id=>id==Guid.Empty)||books.Distinct().Count()!=books.Length||
            selectedTrades is {} trades&&(trades.IsDefault||trades.Any(id=>id==Guid.Empty)||trades.Distinct().Count()!=trades.Length))
            throw Missing("Select the certified service context and explicit exact book and trade scopes.");
        if(books.IsEmpty||selectedTrades is {} empty&&empty.IsEmpty)return false;
        await using var capture=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_preview_authority($1)",database.Manifest.CatalogId);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        await using var probe=ScheduleStore.Command(connection,"SELECT security.require_gas_park_loan_scope($1,$2,$3,$4::uuid[],$5::uuid[])",
            database.Manifest.CatalogId,context,frame.At,books.ToArray(),selectedTrades?.ToArray()??Array.Empty<Guid>());
        var present=(bool)(await probe.ExecuteScalarAsync(ct))!;
        // An existence read does not own the command's single source slot. Its
        // original authority must survive a caller restoring a temporary view.
        database.RegisterCommitAdmission(connection,new PresenceAdmission(database.Manifest.CatalogId,context,frame.At,books,selectedTrades??[],until));
        return present;
    }

    internal static async Task<ImmutableArray<GasParkLoanPosition>> Read(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct,TradePositionSelection? position=null,Guid? trade=null,ImmutableHashSet<Guid>? trades=null)
    {
        if(position is not null&&position.InstrumentType!=Type)throw TradePositionSelection.Unavailable();
        var selectedTrade=position?.TradeId??trade;
        if(selectedTrade is {} one&&trades is not null&&!trades.Contains(one))throw TradePositionSelection.Unavailable();
        var allBooks=await BookScope.Read(connection,database.Manifest.CatalogId,ct);
        var books=BookScope.Select(allBooks,bookIds,portfolioIds).ToImmutableDictionary(b=>b.Id("id"));
        ImmutableArray<Guid>? selected=selectedTrade is {} exact?[exact]:trades is null?null:[..trades];
        if(!await Present(connection,database,frame,[..books.Keys],selected,ct))
        {
            if(position is not null||trade is not null)throw TradePositionSelection.Unavailable();
            return [];
        }
        var context=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(connection,frame.ContextId!.Value,ct)).Context;
        var until=(DateTimeOffset?)null;
        var directory=await LifecycleNative.Read(connection,"SELECT security.gas_park_loan_directory($1,$2,$3,$4::uuid[],$5::uuid[],$6::uuid[])::text",ct,
            database.Manifest.CatalogId,frame.ContextId.Value,frame.At,books.Keys.ToArray(),Array.Empty<Guid>(),selected?.ToArray()??Array.Empty<Guid>());
        var agreements=directory.EnumerateArray().Where(a=>selected is null||selected.Value.Contains(a.Id("tradeId"))).ToImmutableArray();
        if(agreements.Select(a=>a.Id("tradeId")).Distinct().Count()!=agreements.Length)throw Missing("The native directory repeated a held service agreement.");
        foreach(var agreement in agreements)
        {
            var booking=agreement.GetProperty("booking");
            if(agreement.Id("contextId")!=frame.ContextId||agreement.Instant("asOf")!=frame.At||agreement.Instant("knownAt")!=SchedulePreview.ParseAt(context.KnownAt)||
                DateWorkspace.Date(agreement.Text("effectiveOn"))!=frame.BusinessDate||agreement.Text("implementation") is not(GasParkAndLoanTerms.Version or GasParkLoanBalanceFeeTerms.Version)||
                !books.ContainsKey(agreement.Id("bookId"))||agreement.GetProperty("revision").GetInt32()!=1||agreement.Number("quantity").CompareTo(default)<=0||
                booking.ValueKind!=JsonValueKind.Object||booking.Id("catalog_id")!=database.Manifest.CatalogId||booking.Id("trade_id")!=agreement.Id("tradeId")||
                new[]{"booker_name","booker_login","trade_date","executed_at","implementation","label_captured_at"}.Any(k=>string.IsNullOrWhiteSpace(booking.Text(k)))||booking.Id("booker_id")==Guid.Empty)
                throw Missing("A held agreement requires its exact source, positive principal and original native booking facts.");
            var deadline=agreement.Instant("authorityUntil");until=until is null||deadline<until?deadline:until;
        }
        if(position is not null)
        {
            if(agreements.Length!=1||agreements[0].Id("tradeId")!=position.TradeId||agreements[0].Id("instrumentId")!=position.BindingId)throw TradePositionSelection.Unavailable();
            if(agreements[0].GetProperty("revision").GetInt32()!=position.Revision)
                throw new MarketDataException("position_revision","The selected context does not contain the requested service revision.");
        }
        if(agreements.IsEmpty)return [];
        var metadata=await PreviewDatabase.Rows(connection,"""
            SELECT jsonb_build_object('trade_id',(a->>'tradeId')::uuid,'legal_entity',e.name,'counterparty',p.name,
             'curve_name',c.name,'commodity_code',co.code,'commodity_name',co.name,'unit',u.symbol,'currency',cu.display_code,'multiplier',q.multiplier::text)::text
            FROM jsonb_array_elements($2::jsonb)a
            JOIN trading.legal_entities e ON(e.catalog_id,e.id,e.revision)=($1,(a->>'legalEntityId')::uuid,(a->>'legalEntityRevision')::integer)
            JOIN trading.counterparties p ON(p.catalog_id,p.id,p.revision)=($1,(a->>'counterpartyId')::uuid,(a->>'counterpartyRevision')::integer)
            JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=($1,(a->'terms'->>'definitionRevisionId')::uuid,a->'terms'->>'markCurve')
            JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=((a->'terms'->>'referenceCatalogId')::uuid,(a->'terms'->>'referenceRevisionId')::uuid,c.quote_code)
            JOIN ref_data.quantity_specifications n ON(n.catalog_id,n.revision_id,n.code)=(q.catalog_id,q.revision_id,q.quantity_code)
            JOIN ref_data.commodities co ON(co.catalog_id,co.revision_id,co.code)=(n.catalog_id,n.revision_id,n.commodity_code)
            JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(n.catalog_id,n.revision_id,n.unit_code)
            JOIN ref_data.currencies cu ON(cu.catalog_id,cu.revision_id,cu.code)=(q.catalog_id,q.revision_id,q.currency_code)
            WHERE q.kind='outright' AND q.quantity_code=a->'terms'->>'quantity' AND q.currency_code=a->'terms'->>'currency' AND c.location_code=a->'terms'->>'location'
            """,[database.Manifest.CatalogId,JsonSerializer.Serialize(agreements,TradingBlotter.Json)],ct);
        if(metadata.Length!=agreements.Length||metadata.Select(m=>m.Id("trade_id")).Distinct().Count()!=metadata.Length||
            !metadata.Select(m=>m.Id("trade_id")).ToHashSet().SetEquals(agreements.Select(a=>a.Id("tradeId")))||
            metadata.Any(m=>new[]{"legal_entity","counterparty","curve_name","commodity_code","commodity_name","unit","currency","multiplier"}.Any(k=>string.IsNullOrWhiteSpace(m.Text(k)))))
            throw Missing("Every held agreement requires its exact visible party, mark and native quote labels.");
        var labels=metadata.ToDictionary(m=>m.Id("trade_id"));
        var result=agreements.OrderBy(a=>a.GetProperty("tradeReference").GetInt32()).Select(a=>new GasParkLoanPosition(a,books[a.Id("bookId")],labels[a.Id("tradeId")])).ToImmutableArray();
        foreach(var item in result)AsyncReadVisibility.Bundle(database.Manifest with{RevisionId=item.Definition,ReferenceCatalogId=item.Reference.CatalogId,ReferenceRevisionId=item.Reference.RevisionId});
        var admission=new GasOwnedSourceReadAdmission(database.Manifest.CatalogId,frame.ContextId.Value,frame.At,until!.Value);
        database.RegisterCommitAdmission(connection,admission);await admission.RequireCommit(connection,ct);
        return result;
    }
    private static MarketDataException Missing(string message)=>new("position_facts",message);
}
