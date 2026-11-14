using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record FuturesPhysicalDeliveryProfile(Guid Id,int Revision,Guid AccountId,Guid BookId,PartyKey LegalOwner,
    DefinitionRevision Definition,ContractKey Contract,ExactDecimal QuoteMultiplier,GasPhysicalDeliveryTerms Terms,
    CashRoundingConvention PrincipalRounding,string PrincipalScope,string CorrectionDuePolicy,Guid ActionPolicyId,int ActionPolicyRevision)
{
    internal FuturesFinalDeliveryConfiguration Configuration=>new(Id,Revision,Terms);
}
internal sealed record FuturesPhysicalReadAdmission(Guid Catalog,Guid Profile,int Revision,DateTimeOffset Until):ICommitAdmission
{
    public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.require_futures_delivery_profile_read($1,$2,$3,$4)",Catalog,Profile,Revision,Until);
        await command.ExecuteNonQueryAsync(ct);
    }
}

internal static class FuturesPhysicalDeliveryStore
{
    internal static async Task<FuturesMarginPolicy> ReadPolicy(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        JsonElement header,JsonElement occurrences,CancellationToken ct)
    {
        if(header.Text("implementation")!=FuturesMarginPolicy.DeliveryVersion)
            return FuturesMarginStore.ReadPolicy(header,occurrences);
        if(header.TryGetProperty("delivery",out var retained))
        {
            if(retained.ValueKind!=JsonValueKind.Null)
            {
                var profile=Materialize(retained);
                database.RegisterCommitAdmission(connection,new FuturesPhysicalReadAdmission(database.Manifest.CatalogId,profile.Id,profile.Revision,retained.Instant("authorityUntil")));
            }
            return FuturesMarginStore.ReadPolicy(header,occurrences);
        }
        var fields=header.Deserialize<Dictionary<string,JsonElement>>()!;
        if(header.OptionalId("delivery_profile_id") is {} profile)
        {
            var revision=Integer(header,"delivery_profile_revision");
            await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.read_futures_delivery_profile($1,$2,$3,$4,$5)::text",
                database.Manifest.CatalogId,profile,revision,frame.ContextId!.Value,frame.At);
            var source=JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
            database.RegisterCommitAdmission(connection,new FuturesPhysicalReadAdmission(database.Manifest.CatalogId,profile,revision,source.Instant("authorityUntil")));
            fields.Add("delivery",source);
        }
        else
        {
            if(header.GetProperty("delivery_profile_revision").ValueKind!=JsonValueKind.Null)throw Error("Retain the exact paired native delivery profile coordinates.");
            fields.Add("delivery",JsonSerializer.SerializeToElement<object?>(null));
        }
        return FuturesMarginStore.ReadPolicy(JsonSerializer.SerializeToElement(fields),occurrences);
    }
    internal static FuturesPhysicalDeliveryProfile Materialize(JsonElement source)
    {
        var p=source.GetProperty("profile");var g=source.GetProperty("gas");var account=source.GetProperty("account");
        var catalog=p.Id("catalog_id");var reference=new ReferenceRevision(p.Id("reference_catalog_id"),p.Id("reference_revision_id"));
        var definition=new DefinitionRevision(catalog,p.Id("definition_revision_id"));var currency=new ReferenceKey(reference,p.Text("currency_code"));
        if(p.Text("implementation")!=GasPhysicalDeliveryTerms.Version||p.Id("id")!=g.Id("profile_id")||Integer(p,"revision")!=Integer(g,"profile_revision")||
            g.Id("account_id")!=account.Id("id")||g.Id("catalog_id")!=catalog||account.Id("catalog_id")!=catalog||account.Text("kind")!="owned_inventory"||
            account.Id("reference_catalog_id")!=reference.CatalogId||account.Id("reference_revision_id")!=reference.RevisionId||
            account.Text("quantity_code")!=p.Text("quantity_code")||account.Text("currency_code")!=currency.Code||account.Text("location_code")!=p.Text("location_code")||
            Integer(g,"publication_order")!=Integer(p,"publication_order")||g.Text("principal_scope")!="delivery_cumulative_principal_total_v1"||
            g.Text("correction_due_policy")!="contractual_principal_due_v1")throw Error("Retain one exact native gas delivery profile, account and principal contract.");
        var markDefinition=new DefinitionRevision(catalog,account.Id("definition_revision_id"));
        var terms=new GasPhysicalDeliveryTerms(p.Id("terms_id"),new(reference,p.Text("series_code")),new(reference,p.Text("quantity_code")),currency,
            new(reference,p.Text("location_code")),g.Instant("delivery_from"),g.Instant("delivery_to"),g.Instant("cash_due_at"),
            new(new(markDefinition,account.Text("mark_curve_code")),new(markDefinition,account.Id("mark_contract_id"))),g.Text("carrying") switch{
                "nominal_mark_v1"=>GasHoldingCarrying.Nominal,"currency_curve_v1"=>GasHoldingCarrying.CurrencyCurve,_=>throw Error("Select an explicit delivery carrying contract.")},p.Text("implementation"));
        var rounding=new CashRoundingConvention(currency,g.Number("principal_increment"),g.Text("principal_tie") switch{
            "to_even"=>CashTieRule.ToEven,"away_from_zero"=>CashTieRule.AwayFromZero,_=>throw Error("Select an explicit delivery principal rounding tie rule.")},CashRoundingStage.PaymentTotal);
        rounding.Require(currency,CashRoundingStage.PaymentTotal);
        return new(p.Id("id"),Integer(p,"revision"),account.Id("id"),account.Id("book_id"),new(catalog,account.Id("legal_entity_id"),Integer(account,"legal_entity_revision")),
            definition,new(definition,p.Id("contract_id")),p.Number("quote_multiplier"),terms,rounding,g.Text("principal_scope"),g.Text("correction_due_policy"),
            g.Id("action_policy_id"),Integer(g,"action_policy_revision"));
    }
    internal static async Task<FuturesPhysicalDeliveryProfile> ReadForPolicy(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ScheduleContext context,FuturesMarginPolicy policy,Guid expectedBookId,PartyKey expectedLegalOwner,CancellationToken ct)
    {
        if(policy.Delivery is not {} selected||frame.ContextId!=context.ContextId)throw Error("Select the exact saved continuation and certified source frame.");
        var catalog=database.Manifest.CatalogId;
        await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.read_futures_delivery_profile($1,$2,$3,$4,$5)::text",
            catalog,selected.ProfileId,selected.ProfileRevision,frame.ContextId!.Value,frame.At);
        var source=JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
        var profile=Materialize(source);var admission=new FuturesPhysicalReadAdmission(catalog,profile.Id,profile.Revision,source.Instant("authorityUntil"));
        database.RegisterCommitAdmission(connection,admission);
        if(profile.Configuration!=selected||profile.BookId!=expectedBookId||profile.LegalOwner!=expectedLegalOwner||profile.Contract!=policy.Contract)
            throw Error("Physical continuation must retain the exact adopted profile, held owner and source contract.");
        await admission.RequireCommit(connection,ct);return profile;
    }
    internal static AcceptedFuturesFinalDelivery MaterializeAccepted(JsonElement source,FuturesContract future,FuturesMarginPolicy policy,
        Quantity quantity,Guid trade,Guid binding,Guid book,PartyKey legalOwner,RecordedFuturesMargin latest)
    {
        var profile=Materialize(source);var link=source.GetProperty("link");var position=source.GetProperty("position");var final=source.GetProperty("final");
        var price=ExactUnitPrice.Parse(final.Text("final_price"),new(profile.Terms.Currency,profile.Terms.Quantity,profile.QuoteMultiplier));
        if(policy.Delivery!=profile.Configuration||profile.BookId!=book||profile.LegalOwner!=legalOwner||profile.Contract!=policy.Contract||
            link.Id("trade_id")!=trade||link.Id("binding_id")!=binding||link.Id("policy_id")!=policy.Id||Integer(link,"policy_revision")!=policy.Revision||
            link.Id("position_id")!=position.Id("id")||link.Id("profile_id")!=profile.Id||Integer(link,"profile_revision")!=profile.Revision||
            position.Id("account_id")!=profile.AccountId||position.Text("kind")!="delivery_obligation"||position.Text("settlement_mode")!="physical_delivery_v1"||
            position.Number("quantity")!=quantity.Amount||final.Id("determination_id")!=latest.DeterminationId||
            final.Id("source_observation_id")!=latest.Evidence.Observation.Id||price!=latest.Price||final.Instant("final_at")!=latest.Evidence.EffectiveAt||
            Integer(final,"publication_order")!=latest.PublicationOrder||final.Instant("known_at")!=latest.KnownAt)
            throw Error("Accepted final margin must retain the exact source determination and separately owned physical obligation.");
        return new(position.Id("id"),profile.Id,profile.Revision,FuturesFinalDeliveryPlan.Create(future,quantity,profile.Terms,price),latest.KnownAt,latest.PublicationOrder);
    }
    internal static async Task<GasPhysicalDeliveryObligationInstrument> ReadForPosition(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        JsonElement selected,CancellationToken ct)
    {
        await using var command=ScheduleStore.Command(connection,"SELECT settlement_data.read_futures_delivery_position($1,$2,$3,$4)::text",
            database.Manifest.CatalogId,selected.Id("id"),frame.ContextId!.Value,frame.At);
        var source=JsonSerializer.Deserialize<JsonElement>((string)(await command.ExecuteScalarAsync(ct))!);
        var profile=Materialize(source);var position=source.GetProperty("position");var final=source.GetProperty("final");
        var admission=new FuturesPhysicalReadAdmission(database.Manifest.CatalogId,profile.Id,profile.Revision,source.Instant("authorityUntil"));
        database.RegisterCommitAdmission(connection,admission);
        var remaining=selected.Number("remaining_quantity");var original=position.Number("quantity");
        if(position.Id("id")!=selected.Id("id")||profile.AccountId!=selected.Id("account_id")||profile.BookId!=selected.Id("book_id")||
            profile.LegalOwner!=new PartyKey(database.Manifest.CatalogId,selected.Id("legal_entity_id"),Integer(selected,"legal_entity_revision"))||
            position.Id("account_id")!=profile.AccountId||position.Text("kind")!="delivery_obligation"||selected.Text("settlement_mode")!="physical_delivery_v1"||
            position.Id("definition_revision_id")!=selected.Id("definition_revision_id")||position.Text("mark_curve_code")!=selected.Text("mark_curve_code")||
            position.Id("mark_contract_id")!=selected.Id("mark_contract_id")||selected.GetProperty("mark_revision").GetInt32()!=0||
            final.Instant("final_at")>frame.At||Math.Sign(remaining.CompareTo(default))!=Math.Sign(original.CompareTo(default))||
            (remaining.CompareTo(default)<0?-remaining:remaining).CompareTo(original.CompareTo(default)<0?-original:original)>0)
            throw Error("The physical residual must retain its native owner, accepted final basis, exact mark and remaining signed quantity.");
        var price=ExactUnitPrice.Parse(final.Text("final_price"),new(profile.Terms.Currency,profile.Terms.Quantity,profile.QuoteMultiplier));
        await admission.RequireCommit(connection,ct);
        return(GasPhysicalDeliveryObligationInstrument)profile.Terms.CreateObligation(price);
    }
    private static int Integer(JsonElement row,string key)=>int.Parse(row.Text(key),CultureInfo.InvariantCulture);
    private static MarketDataException Error(string message)=>new("futures_delivery_state",message);
}
