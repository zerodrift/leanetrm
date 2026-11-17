using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal static class GasTransportFinancialPositions
{
    sealed record PresenceAdmission(Guid Catalog,Guid Context,DateTimeOffset At,Guid[] Books,Guid[]? Trades,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection c,CancellationToken ct)
        {
            await using var q=ScheduleStore.Command(c,"SELECT security.require_gas_transport_financial_presence($1,$2,$3,$4::uuid[],$5::uuid[],$6)",
                Catalog,Context,At,Books,(object?)Trades??DBNull.Value,Until);
            await q.ExecuteNonQueryAsync(ct);
        }
    }
    internal static async Task<bool> Present(NpgsqlConnection c,PreviewDatabase database,InputFrame frame,ImmutableArray<Guid> books,
        ImmutableArray<Guid>? trades,CancellationToken ct)
    {
        if(frame.ContextId is not {} context||books.IsDefault||books.Any(id=>id==Guid.Empty)||books.Distinct().Count()!=books.Length||
            trades is {} ids&&(ids.IsDefault||ids.Any(id=>id==Guid.Empty)||ids.Distinct().Count()!=ids.Length))throw GasTransportFinancialStore.Source();
        if(books.IsEmpty||trades is {} none&&none.IsEmpty)return false;
        var admission=await new GasTransportFinancialStore(database).Capture(c,null,null,ct);
        var bound=new PresenceAdmission(database.Manifest.CatalogId,context,frame.At,books.ToArray(),trades?.ToArray(),admission.Until);
        database.RegisterCommitAdmission(c,bound);
        await using var q=ScheduleStore.Command(c,"SELECT security.require_gas_transport_financial_scope($1,$2,$3,$4::uuid[],$5::uuid[])",
            database.Manifest.CatalogId,context,frame.At,bound.Books,(object?)bound.Trades??DBNull.Value);
        return (bool)(await q.ExecuteScalarAsync(ct))!;
    }

    internal static async Task<ImmutableArray<GasTransportFinancialPosition>> Read(NpgsqlConnection c,PreviewDatabase database,InputFrame frame,
        ImmutableArray<Guid> bookIds,ImmutableArray<Guid> portfolioIds,CancellationToken ct,TradePositionSelection? position=null,ImmutableHashSet<Guid>? trades=null)
    {
        if(position is not null&&(position.InstrumentType!=GasTransportFinancialTicket.Product||trades is not null&&!trades.Contains(position.TradeId)))
            throw TradePositionSelection.Unavailable();
        await new GasTransportFinancialStore(database).Capture(c,null,null,ct);
        var books=BookScope.Select(await BookScope.Read(c,database.Manifest.CatalogId,ct),bookIds,portfolioIds).ToImmutableDictionary(b=>b.Id("id"));
        ImmutableArray<Guid>? selected=position is not null?[position.TradeId]:trades is null?null:[..trades];
        if(!await Present(c,database,frame,[..books.Keys],selected,ct))
        {
            if(position is not null)throw TradePositionSelection.Unavailable();
            return [];
        }
        var context=GasTransportFinancialTicketStore.Context(frame);
        var known=(await new KnowledgeStore(database.Manifest.CatalogId).Read(c,context.ContextId,ct)).Context;
        var rows=await LifecycleNative.Read(c,"SELECT security.gas_transport_financial_directory($1,$2,$3,$4::uuid[],$5::uuid[])::text",ct,
            database.Manifest.CatalogId,context.ContextId,context.AsOf,books.Keys.ToArray(),(object?)selected?.ToArray()??DBNull.Value);
        if(rows.ValueKind!=JsonValueKind.Array)throw GasTransportFinancialStore.Source();
        var agreements=rows.EnumerateArray().ToImmutableArray();
        if(agreements.IsEmpty||agreements.Select(a=>a.Id("tradeId")).Distinct().Count()!=agreements.Length)throw GasTransportFinancialStore.Source();
        DateTimeOffset? until=null;
        foreach(var agreement in agreements)
        {
            GasTransportFinancialStore.RequireSource(agreement,context,agreement.Id("tradeId"),SchedulePreview.ParseAt(known.KnownAt));
            _=GasTransportFinancialAdapter.Read(database.Manifest.CatalogId,agreement);
            if(!books.ContainsKey(agreement.Id("bookId"))||selected is {} exact&&!exact.Contains(agreement.Id("tradeId")))throw GasTransportFinancialStore.Source();
            var booking=agreement.GetProperty("booking");
            if(booking.Id("catalog_id")!=database.Manifest.CatalogId||booking.Id("trade_id")!=agreement.Id("tradeId")||booking.Id("booker_id")==Guid.Empty||
                new[]{"booker_name","booker_login","trade_date","executed_at","implementation","label_captured_at"}.Any(k=>string.IsNullOrWhiteSpace(booking.Text(k))))
                throw GasTransportFinancialStore.Source();
            var deadline=agreement.Instant("authorityUntil");until=until is null||deadline<until?deadline:until;
        }
        if(position is not null&&(agreements.Length!=1||agreements[0].Id("tradeId")!=position.TradeId||
            agreements[0].Id("instrumentId")!=position.BindingId||position.Revision!=1))throw TradePositionSelection.Unavailable();
        var labels=await ReadLabels(c,database,agreements,ct);
        var metadata=labels.ToDictionary(l=>l.Id("trade_id"));
        var result=agreements.OrderBy(a=>a.GetProperty("tradeReference").GetInt32()).Select(a=>new GasTransportFinancialPosition(a,books[a.Id("bookId")],metadata[a.Id("tradeId")])).ToImmutableArray();
        foreach(var item in result)AsyncReadVisibility.Bundle(database.Manifest with{RevisionId=item.Definition,ReferenceCatalogId=item.Reference.CatalogId,ReferenceRevisionId=item.Reference.RevisionId});
        var read=new GasTransportFinancialReadAdmission(database.Manifest.CatalogId,context.ContextId,context.AsOf,until!.Value);
        database.RegisterCommitAdmission(c,read);await read.RequireCommit(c,ct);return result;
    }

    internal static async Task<ImmutableArray<JsonElement>> ReadLabels(NpgsqlConnection c,PreviewDatabase database,ImmutableArray<JsonElement> agreements,CancellationToken ct)
    {
        var labels=await PreviewDatabase.Rows(c,"""
            SELECT jsonb_build_object('trade_id',(a->>'tradeId')::uuid,'book',b.name,'legal_entity',e.name,'counterparty',p.name,'unit',u.symbol,'currency',currency.display_code,
                'commodity_code',commodity.code,'commodity_name',commodity.name)::text
            FROM jsonb_array_elements($2::jsonb)a
            JOIN trading.books b ON(b.catalog_id,b.id)=($1,(a->>'bookId')::uuid)
            JOIN trading.legal_entities e ON(e.catalog_id,e.id,e.revision)=($1,(a->>'legalEntityId')::uuid,(a->>'legalEntityRevision')::integer)
            JOIN trading.counterparties p ON(p.catalog_id,p.id,p.revision)=($1,(a->>'counterpartyId')::uuid,(a->>'counterpartyRevision')::integer)
            JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=((a->'terms'->>'valuationReferenceCatalogId')::uuid,(a->'terms'->>'valuationReferenceRevisionId')::uuid,a->'terms'->>'packageQuantity')
            JOIN ref_data.units u ON(u.catalog_id,u.revision_id,u.code)=(q.catalog_id,q.revision_id,q.unit_code)
            JOIN ref_data.commodities commodity ON(commodity.catalog_id,commodity.revision_id,commodity.code)=(q.catalog_id,q.revision_id,q.commodity_code)
            JOIN ref_data.currencies currency ON(currency.catalog_id,currency.revision_id,currency.code)=(q.catalog_id,q.revision_id,a->'terms'->>'currency')
            """,[database.Manifest.CatalogId,JsonSerializer.Serialize(agreements,GasOwnedOperations.Json)],ct);
        if(labels.Length!=agreements.Length||labels.Select(l=>l.Id("trade_id")).Distinct().Count()!=labels.Length||
            !labels.Select(l=>l.Id("trade_id")).ToHashSet().SetEquals(agreements.Select(a=>a.Id("tradeId")))||
            labels.Any(l=>new[]{"book","legal_entity","counterparty","unit","currency","commodity_code","commodity_name"}.Any(k=>string.IsNullOrWhiteSpace(l.Text(k)))))throw GasTransportFinancialStore.Source();
        return labels;
    }
}
