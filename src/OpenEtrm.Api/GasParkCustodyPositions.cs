using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

internal sealed record GasParkCustodyPosition(JsonElement Agreement,JsonElement Book,JsonElement Metadata)
{
    JsonElement Terms=>Agreement.GetProperty("terms");
    JsonElement Booking=>Agreement.GetProperty("booking");
    internal Guid Trade=>Agreement.Id("tradeId");
    internal Guid Binding=>Agreement.Id("instrumentId");
    internal Guid Definition=>Terms.Id("definitionRevisionId");
    internal ReferenceRevision Reference=>new(Terms.Id("referenceCatalogId"),Terms.Id("referenceRevisionId"));
    internal ReferenceKey Quantity=>new(Reference,Terms.Text("quantity"));
    internal ReferenceKey Currency=>new(Reference,Terms.Text("currency"));
    internal TradePositionFact Fact()=>new(Trade,Binding,Agreement.GetProperty("revision").GetInt32(),TradingReference.Trade(Agreement.GetProperty("tradeReference").GetInt32()),
        Book.Id("id"),Book.Text("name"),GasParkCustodyTicket.Product,Metadata.Text("point_name"),Terms.Text("parkAt"),Terms.Text("returnAt"),Agreement.Text("quantity"),Metadata.Text("unit"));
    internal JsonObject Holding()=>JsonSerializer.SerializeToNode(new{
        booking=new{tradeDate=Booking.Text("trade_date"),legalEntityId=Agreement.Id("legalEntityId"),legalEntityRevision=Agreement.GetProperty("legalEntityRevision").GetInt32(),
            legalEntity=Metadata.Text("legal_entity"),counterpartyId=Agreement.Id("counterpartyId"),counterpartyRevision=Agreement.GetProperty("counterpartyRevision").GetInt32(),counterparty=Metadata.Text("counterparty")},
        traderId=Booking.Id("booker_id"),trader=Booking.Text("booker_name"),traderCode=Booking.Text("booker_login"),memo=(string?)null,
        tradeId=Trade,bindingId=Binding,instrumentId=Binding,revision=Agreement.Text("revision"),tradeReference=TradingReference.Trade(Agreement.GetProperty("tradeReference").GetInt32()),
        instrumentReference=TradingReference.Instrument(Agreement,"instrumentReference"),bookId=Book.Id("id"),book=Book.Text("name"),portfolioId=Book.Id("portfolio_id"),portfolio=Book.Text("portfolio"),
        instrumentType=GasParkCustodyTicket.Product,contractIdentityKind="gas_park_custody_agreement",contractId=Binding,definitionRevisionId=Definition,
        curve="",projectionCurves=Array.Empty<string>(),priceNavigation=false,marketKey=JsonSerializer.Serialize(new[]{"custody",Terms.Text("point")}),marketLabel=Metadata.Text("point_name"),
        commodity=new{code=Metadata.Text("commodity_code"),name=Metadata.Text("commodity_name")},direction="multi_side",serviceKind=Terms.Text("serviceKind"),perspective=Terms.Text("perspective"),
        deliveryFrom=Terms.Text("parkAt"),deliveryTo=Terms.Text("returnAt"),lots=(string?)null,quantity=Agreement.Text("quantity"),unit=Metadata.Text("unit"),quantitySpecification=Quantity,
        currency=Metadata.Text("currency"),currencyReference=Currency,quoteMultiplier=(string?)null,
        executedAt=Booking.Text("executed_at"),bookingFacts=new{implementation=Booking.Text("implementation"),labelCapturedAt=Booking.Text("label_captured_at")},
        executionPrice=(string?)null,fixedPrice=(string?)null,mark=(string?)null,markMinusPrice=(string?)null,executionPnl=(string?)null,
        settlement="unprocessed",measure=GasParkCustodyTicket.Measure,measureLabel=GasParkCustodyTicketStore.MeasureLabel,quantityPolicy=Agreement.Text("implementation"),ownershipNote=GasParkCustodyTicketStore.OwnershipNote,
        presentValue=(string?)null,unavailableReason=(string?)null
    },TradingBlotter.Json)!.AsObject();
}

internal static class GasParkCustodyPositions
{
    internal const string Type=GasParkCustodyTicket.Product;
    sealed record PresenceAdmission(Guid Catalog,Guid Context,DateTimeOffset At,ImmutableArray<Guid> Books,ImmutableArray<Guid> Trades,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var guard=ScheduleStore.Command(connection,"SELECT security.require_gas_park_custody_presence($1,$2,$3,$4::uuid[],$5::uuid[],$6)",
                Catalog,Context,At,Books.ToArray(),Trades.ToArray(),Until);
            await guard.ExecuteNonQueryAsync(ct);
        }
    }
    internal static async Task<bool> Present(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ImmutableArray<Guid> books,ImmutableArray<Guid>? trades,CancellationToken ct)
    {
        if(frame.ContextId is not {} context||books.IsDefault||books.Any(id=>id==Guid.Empty)||books.Distinct().Count()!=books.Length||
            trades is {} selected&&(selected.IsDefault||selected.Any(id=>id==Guid.Empty)||selected.Distinct().Count()!=selected.Length))throw Missing();
        if(books.IsEmpty||trades is {} empty&&empty.IsEmpty)return false;
        await using var capture=ScheduleStore.Command(connection,"SELECT settlement_data.futures_margin_preview_authority($1)",database.Manifest.CatalogId);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        await using var probe=ScheduleStore.Command(connection,"SELECT security.require_gas_park_custody_scope($1,$2,$3,$4::uuid[],$5::uuid[])",
            database.Manifest.CatalogId,context,frame.At,books.ToArray(),trades?.ToArray()??Array.Empty<Guid>());
        var present=(bool)(await probe.ExecuteScalarAsync(ct))!;
        database.RegisterCommitAdmission(connection,new PresenceAdmission(database.Manifest.CatalogId,context,frame.At,books,trades??[],until));
        return present;
    }
    internal static async Task<ImmutableArray<GasParkCustodyPosition>> Read(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct,TradePositionSelection? position=null,ImmutableHashSet<Guid>? trades=null)
    {
        if(position is not null&&(position.InstrumentType!=Type||trades is not null&&!trades.Contains(position.TradeId)))throw TradePositionSelection.Unavailable();
        var books=BookScope.Select(await BookScope.Read(connection,database.Manifest.CatalogId,ct),bookIds,portfolioIds).ToImmutableDictionary(b=>b.Id("id"));
        ImmutableArray<Guid>? selected=position is not null?[position.TradeId]:trades is null?null:[..trades];
        if(!await Present(connection,database,frame,[..books.Keys],selected,ct))
        {
            if(position is not null)throw TradePositionSelection.Unavailable();
            return [];
        }
        var known=(await new KnowledgeResolver(database.Manifest.CatalogId).Read(connection,frame.ContextId!.Value,ct)).Context;
        var directory=await LifecycleNative.Read(connection,"SELECT security.gas_park_custody_directory($1,$2,$3,$4::uuid[],$5::uuid[],$6::uuid[])::text",ct,
            database.Manifest.CatalogId,frame.ContextId.Value,frame.At,books.Keys.ToArray(),Array.Empty<Guid>(),selected?.ToArray()??Array.Empty<Guid>());
        if(directory.ValueKind!=JsonValueKind.Array)throw Missing();
        var agreements=directory.EnumerateArray().ToImmutableArray();
        if(agreements.IsEmpty||agreements.Select(a=>a.Id("tradeId")).Distinct().Count()!=agreements.Length)throw Missing();
        DateTimeOffset? until=null;var inventory=false;
        foreach(var agreement in agreements)
        {
            var booking=agreement.GetProperty("booking");var state=GasParkCustodyAdapter.Read(database.Manifest.CatalogId,agreement);
            if(agreement.Id("contextId")!=frame.ContextId||agreement.Instant("asOf")!=frame.At||agreement.Instant("knownAt")!=SchedulePreview.ParseAt(known.KnownAt)||
                DateWorkspace.Date(agreement.Text("effectiveOn"))!=frame.BusinessDate||!books.ContainsKey(agreement.Id("bookId"))||
                selected is {} exact&&!exact.Contains(agreement.Id("tradeId"))||booking.ValueKind!=JsonValueKind.Object||booking.Id("catalog_id")!=database.Manifest.CatalogId||
                booking.Id("trade_id")!=agreement.Id("tradeId")||booking.Id("booker_id")==Guid.Empty||
                new[]{"booker_name","booker_login","trade_date","executed_at","implementation","label_captured_at"}.Any(k=>string.IsNullOrWhiteSpace(booking.Text(k))))throw Missing();
            var deadline=agreement.Instant("authorityUntil");until=until is null||deadline<until?deadline:until;inventory|=state.Terms.Perspective=="customer";
        }
        if(position is not null)
        {
            if(agreements.Length!=1||agreements[0].Id("tradeId")!=position.TradeId||agreements[0].Id("instrumentId")!=position.BindingId)throw TradePositionSelection.Unavailable();
            if(agreements[0].GetProperty("revision").GetInt32()!=position.Revision)throw new MarketDataException("position_revision","The selected source does not contain the requested custody revision.");
        }
        var labels=await PreviewDatabase.Rows(connection,"""
            SELECT jsonb_build_object('trade_id',(a->>'tradeId')::uuid,'legal_entity',e.name,'counterparty',p.name,'point_name',l.name,
             'commodity_code',co.code,'commodity_name',co.name,'unit',u.symbol,'currency',c.display_code)::text
            FROM jsonb_array_elements($2::jsonb)a
            JOIN trading.legal_entities e ON(e.catalog_id,e.id,e.revision)=($1,(a->>'legalEntityId')::uuid,(a->>'legalEntityRevision')::integer)
            JOIN trading.counterparties p ON(p.catalog_id,p.id,p.revision)=($1,(a->>'counterpartyId')::uuid,(a->>'counterpartyRevision')::integer)
            JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=((a->'terms'->>'referenceCatalogId')::uuid,(a->'terms'->>'referenceRevisionId')::uuid,a->'terms'->>'quantity')
            JOIN ref_data.commodities co ON(co.catalog_id,co.revision_id,co.code)=(q.catalog_id,q.revision_id,q.commodity_code)
            JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
            JOIN ref_data.currencies c ON(c.catalog_id,c.revision_id,c.code)=(q.catalog_id,q.revision_id,a->'terms'->>'currency')
            JOIN ref_data.locations l ON(l.catalog_id,l.revision_id,l.code)=(q.catalog_id,q.revision_id,a->'terms'->>'point')
            """,[database.Manifest.CatalogId,JsonSerializer.Serialize(agreements,GasOwnedOperations.Json)],ct);
        if(labels.Length!=agreements.Length||labels.Select(m=>m.Id("trade_id")).Distinct().Count()!=labels.Length||
            !labels.Select(m=>m.Id("trade_id")).ToHashSet().SetEquals(agreements.Select(a=>a.Id("tradeId")))||
            labels.Any(m=>new[]{"legal_entity","counterparty","point_name","commodity_code","commodity_name","unit","currency"}.Any(k=>string.IsNullOrWhiteSpace(m.Text(k)))))throw Missing();
        var metadata=labels.ToDictionary(m=>m.Id("trade_id"));
        var result=agreements.OrderBy(a=>a.GetProperty("tradeReference").GetInt32()).Select(a=>new GasParkCustodyPosition(a,books[a.Id("bookId")],metadata[a.Id("tradeId")])).ToImmutableArray();
        foreach(var item in result)AsyncReadVisibility.Bundle(database.Manifest with{RevisionId=item.Definition,ReferenceCatalogId=item.Reference.CatalogId,ReferenceRevisionId=item.Reference.RevisionId});
        var admission=new GasParkCustodyReadAdmission(database.Manifest.CatalogId,frame.ContextId.Value,frame.At,until!.Value,inventory);
        database.RegisterCommitAdmission(connection,admission);await admission.RequireCommit(connection,ct);return result;
    }
    static MarketDataException Missing()=>new("position_facts","Retain the exact custody owner, source, immutable booking facts and independently selected reference labels.");
}
