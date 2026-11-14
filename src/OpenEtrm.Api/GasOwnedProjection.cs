using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasOwnedProjectionPrepared(ImmutableArray<ILifecycleProjection> Components,
    ImmutableHashSet<AccountingPositionKey> Owners,ImmutableArray<JsonElement> ProfileEvidence);
internal sealed record GasOwnedProjectionMarket(MarketSnapshot Snapshot,ImmutableDictionary<ReferenceKey,ISideDiscounting> CurrencyCurves);

internal static class GasOwnedProjection
{
    internal static Task<ImmutableArray<GasOwnedProjectionAccountChoice>> ReadChoices(NpgsqlConnection connection,PreviewDatabase database,
        InputFrame frame,ImmutableArray<Guid> books,CancellationToken ct)=>GasOwnedKnownHistoryStore.ReadChoices(connection,database,frame,books,ct);
    private sealed record Profile(GasOwnedProjectionSelection Selection,JsonElement Header,ImmutableArray<JsonElement> Performances,ImmutableArray<JsonElement> Supply);
    // The caller replaces these exact original commodity members with the
    // connected components. Cash memberships remain their independent native
    // CashLifecycleProjection consumers; none is silently absorbed into stock.
    internal static Task<GasOwnedProjectionPrepared> Prepare(NpgsqlConnection connection,PreviewDatabase database,
        GasOwnedKnownHistory known,GasOwnedPortfolioRead? opening,MarketSnapshot snapshot,DateTimeOffset horizon,
        ImmutableArray<GasOwnedProjectionSelection> selected,ImmutableDictionary<ReferenceKey,ISideDiscounting> currencyCurves,CancellationToken ct)=>
        PreparePartitions(connection,database,known,opening,snapshot,horizon,selected,
            ImmutableDictionary<DefinitionRevision,GasOwnedProjectionMarket>.Empty.Add(snapshot.Definitions,new(snapshot,currencyCurves)),ct);

    internal static async Task<GasOwnedProjectionPrepared> PreparePartitions(NpgsqlConnection connection,PreviewDatabase database,
        GasOwnedKnownHistory known,GasOwnedPortfolioRead? opening,MarketSnapshot source,DateTimeOffset horizon,
        ImmutableArray<GasOwnedProjectionSelection> selected,ImmutableDictionary<DefinitionRevision,GasOwnedProjectionMarket> markets,CancellationToken ct)
    {
        var knowledge=source.Knowledge??throw Error("gas_projection_source","Retain the certified gas opening knowledge context.");
        if(selected.IsDefault||selected.Any(s=>s is null||s.AccountId==Guid.Empty||s.ProfileId==Guid.Empty||s.Revision<1)||
            selected.Select(s=>s.AccountId).Distinct().Count()!=selected.Length||!known.Source.HasSameInputs(source)||markets is null||
            markets.Any(p=>p.Key!=p.Value.Snapshot.Definitions||p.Value.Snapshot.Id!=source.Id||p.Value.Snapshot.ValuationAt!=source.ValuationAt||p.Value.Snapshot.Knowledge!=knowledge)||
            !TradingInstant.IsSupported(horizon)||horizon<=source.ValuationAt||opening is not null&&(opening.ContextId!=knowledge.ContextId||opening.At!=source.ValuationAt||opening.KnownAt!=knowledge.KnownAt))
            throw Error("gas_projection_source","Select exact saved gas profiles and retain the certified source and complete independent opening holdings.");
        var active=opening?.Positions??[];
        var requiredAccounts=active.Select(p=>p.AccountId).Concat(known.Positions.Values.Where(p=>p.Time.EffectiveAt>source.ValuationAt&&p.Time.EffectiveAt<=horizon).Select(p=>p.AccountId)).ToHashSet();
        var selectedAccounts=selected.Select(s=>s.AccountId).ToHashSet();
        if(!requiredAccounts.IsSubsetOf(selectedAccounts))
            throw Error("gas_projection_policy","Select exactly one saved projection profile for each account containing opening or source-known horizon ownership.");
        RequireKnownCoverage(known,source.ValuationAt,horizon);
        var components=ImmutableArray.CreateBuilder<ILifecycleProjection>();var owners=ImmutableHashSet.CreateBuilder<AccountingPositionKey>();var evidence=ImmutableArray.CreateBuilder<JsonElement>();
        var profiles=ImmutableDictionary.CreateBuilder<Guid,Profile>();
        foreach(var selection in selected)
        {
            if(!known.Accounts.ContainsKey(selection.AccountId))throw Error("gas_projection_policy","A saved projection account is outside the retained source.");
            var native=await LifecycleNative.Read(connection,"SELECT security.read_gas_owned_projection_profile($1,$2,$3,$4,$5)::text",ct,
                database.Manifest.CatalogId,selection.ProfileId,selection.Revision,knowledge.ContextId,source.ValuationAt);
            var header=native.GetProperty("profile");
            if(header.Id("id")!=selection.ProfileId||header.Id("account_id")!=selection.AccountId||header.GetProperty("revision").GetInt32()!=selection.Revision)
                throw Error("gas_projection_policy","The selected gas projection profile belongs to another exact account or revision.");
            var admission=new GasOwnedOperationReadAdmission(database.Manifest.CatalogId,native.Instant("authorityUntil"));database.RegisterCommitAdmission(connection,admission);
            var rows=native.GetProperty("performances").EnumerateArray().ToImmutableArray();var supplies=native.GetProperty("supply").EnumerateArray().ToImmutableArray();
            if(rows.Any(row=>known.Positions[row.Id("position_id")].AccountId!=selection.AccountId))
                throw Error("gas_projection_policy","A saved account profile cannot select another account's quantity-performance policy.");
            profiles.Add(selection.AccountId,new(selection,header,rows,supplies));
            evidence.Add(JsonSerializer.SerializeToElement(new{profile=header,performances=rows,supply=supplies},RiskJobs.Json));
            await admission.RequireCommit(connection,ct);
        }
        var links=selectedAccounts.ToDictionary(id=>id,_=>new HashSet<Guid>());
        bool CrossesHorizon(Guid position)=>known.Positions[position].DueAt>source.ValuationAt&&known.Positions[position].DueAt<=horizon;
        foreach(var profile in profiles.Values)
        {
            foreach(var row in profile.Performances.Where(r=>CrossesHorizon(r.Id("position_id"))))if(row.OptionalId("destination_account_id") is {} destination)Link(profile.Selection.AccountId,destination);
            foreach(var row in profile.Supply.Where(r=>CrossesHorizon(r.Id("position_id"))))Link(profile.Selection.AccountId,known.Positions[row.OptionalId("lot_id")??row.Id("incoming_position_id")].AccountId);
        }
        var reached=new HashSet<Guid>();var frontier=new Queue<Guid>(requiredAccounts);
        while(frontier.TryDequeue(out var account))if(reached.Add(account))foreach(var next in links[account])frontier.Enqueue(next);
        if(!reached.SetEquals(selectedAccounts))throw Error("gas_projection_policy","Additional selected accounts must be explicitly connected sources or inventory destinations.");
        var remaining=selectedAccounts.ToHashSet();
        while(remaining.Count>0)
        {
            var connected=new HashSet<Guid>();var pending=new Queue<Guid>();pending.Enqueue(remaining.Order().First());
            while(pending.TryDequeue(out var account))if(connected.Add(account))foreach(var next in links[account])pending.Enqueue(next);
            remaining.ExceptWith(connected);
            var positions=active.Where(p=>connected.Contains(p.AccountId)).ToImmutableArray();
            if(positions.IsEmpty)throw Error("gas_projection_known_transition","This horizon requires a qualified native future-origin gas transition before sampling.");
            var definitions=positions.Select(p=>new DefinitionRevision(database.Manifest.CatalogId,p.DefinitionRevisionId)).Distinct().ToArray();
            if(definitions.Length!=1||!markets.TryGetValue(definitions[0],out var market))
                throw Error("gas_projection_source","Explicitly connected physical resources require one retained market graph; independent groups keep their own exact graphs.");
            var snapshot=market.Snapshot;var currencyCurves=market.CurrencyCurves;
            if(opening is null||!opening.Markets.TryGetValue(snapshot.Definitions.RevisionId,out var originalMarket)||!originalMarket.Snapshot.HasSameInputs(snapshot))
                throw Error("gas_projection_source","Each gas component must retain the exact authorized opening market inputs for its native holdings.");
            var holdings=positions.Select(position=>
            {
                if(position.Instrument is not GasCommodityHoldingInstrument instrument||instrument.Mark.Curve.Revision!=snapshot.Definitions)
                    throw Error("gas_projection_source","The connected quantity adapter requires one exact retained market partition and ordinary native gas unit marks.");
                var original=known.Positions.GetValueOrDefault(position.Position.Owner.Id)??throw Error("gas_projection_source","The opening gas owner is absent from its certified native journal.");
                var account=known.Accounts[position.AccountId].Account;
                if(original.Owner!=position.Position||original.AccountId!=position.AccountId||original.Time.EffectiveAt>snapshot.ValuationAt)
                    throw Error("gas_projection_source","The native gas journal cannot reinterpret an opening owner, binding or title origin.");
                GasProjectionHoldingKind kind;DateTimeOffset? available=null;var reservations=ImmutableDictionary<Guid,ExactDecimal>.Empty;
                switch(position.Kind)
                {
                    case "owned_stock":
                    {
                        kind=GasProjectionHoldingKind.OwnedStock;var lot=known.Lots.Single(l=>l.Id==position.Position.Owner.Id);
                        var balance=GasOwnedLedger.Balance(lot,[..known.Reservations.Where(r=>r.LotId==lot.Id)],
                            [..known.Consumptions.Where(c=>c.LotId==lot.Id)],snapshot.ValuationAt,knowledge.KnownAt);
                        if(balance.Owned!=position.Quantity)throw Error("gas_projection_source","The opening owned stock must equal its exact title and accepted consumption prefix.");
                        available=lot.AvailableAt;
                        reservations=known.Reservations.Where(r=>r.LotId==lot.Id&&r.At<=snapshot.ValuationAt&&r.KnownAt<=knowledge.KnownAt&&
                            (r.ReleasedAt is null||r.ReleasedAt>snapshot.ValuationAt)).ToImmutableDictionary(r=>r.Id,r=>r.Quantity.Amount-known.Consumptions
                                .Where(c=>c.ReservationId==r.Id&&c.At<=snapshot.ValuationAt&&c.KnownAt<=knowledge.KnownAt).Aggregate(default(ExactDecimal),(a,b)=>a+b.Quantity.Amount));
                        break;
                    }
                    case "loan_return":kind=GasProjectionHoldingKind.LoanReturn;break;
                    case "operator_claim" when position.SettlementMode=="return_quantity_v1":kind=GasProjectionHoldingKind.OperatorClaim;break;
                    case "operator_claim":throw Error("gas_projection_cashout","Select a qualified explicit cashout fixing projection before including this operator claim in complete economic risk.");
                    default:throw Error("gas_projection_product","A gas physical-delivery obligation requires its qualified final-principal and supplied-title projection adapter.");
                }
                var discount=instrument.Carrying==GasHoldingCarrying.CurrencyCurve&&instrument.ReturnAt>snapshot.ValuationAt?
                    currencyCurves.GetValueOrDefault(instrument.Currency)??throw Error("projection_cash_policy","Retain this gas return's exact captured currency discount curve."):null;
                return new GasProjectionHolding(position.Position,account,kind,instrument,position.Quantity,available,reservations,discount);
            }).ToImmutableArray();
            var instructions=connected.SelectMany(account=>profiles[account].Performances.Where(row=>CrossesHorizon(row.Id("position_id")))
                .Select(row=>(Profile:profiles[account],Row:row))).Select(selectedRow=>
            {
                var row=selectedRow.Row;var selection=selectedRow.Profile.Selection;var supplies=selectedRow.Profile.Supply;
                var position=known.Positions[row.Id("position_id")];
                if(!positions.Any(p=>p.Position==position.Owner))throw Error("gas_projection_stale","A saved performance refers to an obligation absent from the complete opening holdings.");
                var id=GasQuantityLifecycleProjection.PerformanceIdentity(selection.ProfileId,selection.Revision,position.Owner);
                var quantity=new Quantity(row.Number("quantity"),position.Quantity.Specification);
                GasProjectedInventoryTerms? receipt=null;
                if(row.OptionalId("destination_account_id") is {} destination)
                {
                    var account=known.Accounts[destination];var definition=new DefinitionRevision(database.Manifest.CatalogId,row.Id("inventory_definition_revision_id"));
                    var mark=new CurveRequest(new(definition,row.Text("inventory_curve_code")),new(definition,row.Id("inventory_contract_id")));
                    if(definition!=snapshot.Definitions||row.Text("availability")!="available_at_projected_title_v1")throw Error("gas_projection_source","Select the explicit retained inventory market and projected-title availability convention.");
                    var instrument=new GasCommodityHoldingInstrument(GasQuantityLifecycleProjection.ProjectedStockBinding(position.Owner,id),mark,account.Quantity,account.Currency,null,GasHoldingCarrying.Nominal);
                    receipt=new(account.Account,instrument,position.DueAt!.Value);
                }
                var selectedSupply=supplies.Where(s=>s.Id("position_id")==position.Owner.Owner.Id).Select(s=>
                {
                    var source=known.Positions[s.OptionalId("lot_id")??s.Id("incoming_position_id")];
                    var incoming=profiles[source.AccountId].Selection;
                    var binding=s.OptionalId("lot_id") is not null?source.Owner.BindingId:
                        GasQuantityLifecycleProjection.ProjectedStockBinding(source.Owner,GasQuantityLifecycleProjection.PerformanceIdentity(incoming.ProfileId,incoming.Revision,source.Owner));
                    return new GasProjectionSupply(source.Owner,binding,s.OptionalId("reservation_id"),new(s.Number("quantity"),source.Quantity.Specification));
                }).ToImmutableArray();
                var requires=supplies.Where(s=>s.Id("position_id")==position.Owner.Owner.Id&&s.OptionalId("incoming_position_id") is not null).Select(s=>
                {
                    var source=known.Positions[s.Id("incoming_position_id")];var incoming=profiles[source.AccountId].Selection;return LifecycleProjectionEvent.ScopedId(source.Owner,
                        GasQuantityLifecycleProjection.PerformanceIdentity(incoming.ProfileId,incoming.Revision,source.Owner));
                }).Distinct().ToImmutableArray();
                return new GasQuantityPerformance(id,position.Owner,position.DueAt!.Value,quantity,selectedSupply,receipt,requires);
            }).ToImmutableArray();
            var policies=connected.ToImmutableDictionary(account=>account,account=>new GasQuantityProjectionPolicy(profiles[account].Header.Text("implementation"),profiles[account].Header.Text("quantity_disposition")));
            var component=new GasQuantityLifecycleProjection(policies,snapshot,holdings,instructions);
            _=component.EventsFor(horizon);components.Add(component);owners.UnionWith(component.EconomicOwners);
        }
        if(!owners.ToHashSet().SetEquals(active.Select(p=>p.Position)))throw Error("gas_projection_scope","Every opening independently owned gas position must have exactly one qualified projection member.");
        return new(components.ToImmutable(),owners.ToImmutable(),evidence.ToImmutable());
        void Link(Guid a,Guid b)
        {
            if(!links.ContainsKey(b))throw Error("gas_projection_policy","Every explicitly linked source or inventory destination requires its own selected account profile.");
            links[a].Add(b);links[b].Add(a);
        }
    }
    private static void RequireKnownCoverage(GasOwnedKnownHistory known,DateTimeOffset opening,DateTimeOffset horizon)
    {
        var origins=known.Positions.Values.Where(p=>p.Time.EffectiveAt>opening&&p.Time.EffectiveAt<=horizon).ToArray();
        if(origins.Any(p=>p.OriginPolicy=="opening_owned_stock_v1"||p.OriginPolicy=="operator_balance_claim_v1"))
            throw Error("gas_projection_capital","A source-known future independent gas origin requires its native purchase, capital or transfer counterfact; zero opening capital cannot be inferred.");
        if(origins.Length>0||known.Consumptions.Any(c=>c.At>opening&&c.At<=horizon)||known.Reservations.Any(r=>r.At>opening&&r.At<=horizon)||known.Releases.Any(r=>r.Time.EffectiveAt>opening&&r.Time.EffectiveAt<=horizon)||
            known.Settlements.Any(s=>s.Time.EffectiveAt>opening&&s.Time.EffectiveAt<=horizon)||known.Fees.Any(f=>f.Time.EffectiveAt>opening&&f.Time.EffectiveAt<=horizon)||
            known.MarkBindings.Any(m=>m.Time.EffectiveAt>opening&&m.Time.EffectiveAt<=horizon))
            throw Error("gas_projection_known_transition","Retain the native linked transition, stock commitments and cash counterfacts before projecting source-known future gas events.");
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
