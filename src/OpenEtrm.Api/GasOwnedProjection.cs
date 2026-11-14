using System.Collections.Immutable;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasOwnedProjectionPrepared(ImmutableArray<ILifecycleProjection> Components,
    ImmutableHashSet<AccountingPositionKey> Owners,ImmutableArray<JsonElement> ProfileEvidence)
{
    internal ImmutableArray<LifecycleEconomicOwnerDescription> AdditionalOwners{get;init;}=[];
    internal ImmutableArray<GasQuantityProjectionPlan> QuantityPlans{get;init;}=[];
}
internal sealed record GasQuantityProjectionPlan(MarketSnapshot Source,ImmutableDictionary<Guid,GasQuantityProjectionPolicy> Policies,
    ImmutableArray<GasProjectionHolding> OpeningHoldings,ImmutableArray<GasQuantityPerformance> Performances,ImmutableArray<GasKnownQuantityTransition> KnownTransitions)
{
    internal GasQuantityLifecycleProjection Build()=>new(Policies,Source,OpeningHoldings,Performances,KnownTransitions);
}
internal sealed record GasOwnedProjectionMarket(MarketSnapshot Snapshot,ImmutableDictionary<ReferenceKey,ISideDiscounting> CurrencyCurves);

internal static partial class GasOwnedProjection
{
    internal static Task<ImmutableArray<GasOwnedProjectionAccountChoice>> ReadChoices(NpgsqlConnection connection,PreviewDatabase database,
        InputFrame frame,ImmutableArray<Guid> books,CancellationToken ct)=>GasOwnedKnownHistoryStore.ReadChoices(connection,database,frame,books,ct);
    private sealed record Profile(GasOwnedProjectionSelection Selection,JsonElement Header,ImmutableArray<JsonElement> Performances,ImmutableArray<JsonElement> Supply,ImmutableArray<JsonElement> Cashouts);
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
        ImmutableArray<GasOwnedProjectionSelection> selected,ImmutableDictionary<DefinitionRevision,GasOwnedProjectionMarket> markets,CancellationToken ct,
        GasPhysicalQuantitySupplement? physical=null)
    {
        if(known.Scope!=GasOwnedKnownScope.WholeBooks)
            throw Error("gas_projection_scope","A linked physical dependency journal cannot replace complete independently selected gas holdings.");
        var knowledge=source.Knowledge??throw Error("gas_projection_source","Retain the certified gas opening knowledge context.");
        if(selected.IsDefault||selected.Any(s=>s is null||s.AccountId==Guid.Empty||s.ProfileId==Guid.Empty||s.Revision<1)||
            selected.Select(s=>s.AccountId).Distinct().Count()!=selected.Length||!known.Source.HasSameInputs(source)||markets is null||
            markets.Any(p=>p.Key!=p.Value.Snapshot.Definitions||physical is null&&p.Value.Snapshot.Id!=source.Id||p.Value.Snapshot.ValuationAt!=source.ValuationAt||
                p.Value.Snapshot.Knowledge!=knowledge||p.Value.Snapshot.MarketCutoff!=source.MarketCutoff||p.Value.Snapshot.SelectionPolicy!=source.SelectionPolicy)||
            !TradingInstant.IsSupported(horizon)||horizon<=source.ValuationAt||opening is not null&&(opening.ContextId!=knowledge.ContextId||opening.At!=source.ValuationAt||opening.KnownAt!=knowledge.KnownAt))
            throw Error("gas_projection_source","Select exact saved gas profiles and retain the certified source and complete independent opening holdings.");
        GasOwnedAppliedSources.RequireClear(known.SourceConflicts,horizon);
        var active=opening?.Positions??[];
        var physicalOwners=physical?.RequireSource(known,opening,markets)??ImmutableHashSet<AccountingPositionKey>.Empty;
        var requiredAccounts=active.Select(p=>p.AccountId).Concat(known.Positions.Values.Where(p=>p.Time.EffectiveAt>source.ValuationAt&&p.Time.EffectiveAt<=horizon&&
            !known.Quantities.Positions[p.Owner.Owner.Id].Retracted).Select(p=>p.AccountId)).ToHashSet();
        requiredAccounts.UnionWith(physicalOwners.Select(o=>known.Positions[o.Owner.Id].AccountId));
        var selectedAccounts=selected.Select(s=>s.AccountId).ToHashSet();
        if(!requiredAccounts.IsSubsetOf(selectedAccounts))
            throw Error("gas_projection_policy","Select exactly one saved projection profile for each account containing opening or source-known horizon ownership.");
        var components=ImmutableArray.CreateBuilder<ILifecycleProjection>();var owners=ImmutableHashSet.CreateBuilder<AccountingPositionKey>();var evidence=ImmutableArray.CreateBuilder<JsonElement>();
        var quantityPlans=ImmutableArray.CreateBuilder<GasQuantityProjectionPlan>();var declaredOwners=new List<AccountingPositionKey>();
        var profiles=ImmutableDictionary.CreateBuilder<Guid,Profile>();
        foreach(var selection in selected)
        {
            if(!known.Accounts.ContainsKey(selection.AccountId))throw Error("gas_projection_policy","A saved projection account is outside the retained source.");
            var native=await LifecycleNative.Read(connection,"SELECT security.read_gas_owned_projection_profile($1,$2,$3,$4,$5)::text",ct,
                database.Manifest.CatalogId,selection.ProfileId,selection.Revision,knowledge.ContextId,source.ValuationAt);
            var header=native.GetProperty("profile");
            if(header.Id("id")!=selection.ProfileId||header.Id("account_id")!=selection.AccountId||header.GetProperty("revision").GetInt32()!=selection.Revision)
                throw Error("gas_projection_policy","The selected gas projection profile belongs to another exact account or revision.");
            if(header.Text("implementation") is not(GasQuantityProjectionPolicy.Version or GasCashoutProjectionTerms.Version or GasQuantityProjectionPolicy.KnownVersion))
                throw Error("gas_projection_policy","Select a qualified exact saved quantity or cashout projection implementation.");
            var admission=new GasOwnedSourceReadAdmission(database.Manifest.CatalogId,knowledge.ContextId,source.ValuationAt,native.Instant("authorityUntil"));database.RegisterCommitAdmission(connection,admission);
            var rows=native.GetProperty("performances").EnumerateArray().ToImmutableArray();var supplies=native.GetProperty("supply").EnumerateArray().ToImmutableArray();
            if(rows.Any(row=>known.Positions[row.Id("position_id")].AccountId!=selection.AccountId))
                throw Error("gas_projection_policy","A saved account profile cannot select another account's quantity-performance policy.");
            var cashouts=native.GetProperty("cashouts").EnumerateArray().ToImmutableArray();
            if(cashouts.Any(row=>!known.Positions.TryGetValue(row.Id("position_id"),out var position)||position.AccountId!=selection.AccountId))
                throw Error("gas_projection_policy","A saved cashout projection must retain exact native positions in its selected account.");
            profiles.Add(selection.AccountId,new(selection,header,rows,supplies,cashouts));
            evidence.Add(JsonSerializer.SerializeToElement(new{profile=header,performances=rows,supply=supplies,cashouts},RiskJobs.Json));
            await admission.RequireCommit(connection,ct);
        }
        if(physicalOwners.Any(o=>!Accepted(known.Positions[o.Owner.Id].AccountId)))
            throw Error("gas_projection_policy","Select the accepted-quantity projection version explicitly for every connected native physical obligation and receipt.");
        RequireKnownCoverage(known,source.ValuationAt,horizon,profiles.ToImmutable(),physicalOwners);
        var links=selectedAccounts.ToDictionary(id=>id,_=>new HashSet<Guid>());
        bool CrossesHorizon(Guid position)=>known.Positions[position].DueAt>source.ValuationAt&&known.Positions[position].DueAt<=horizon;
        foreach(var profile in profiles.Values)
        {
            foreach(var row in profile.Performances.Where(r=>CrossesHorizon(r.Id("position_id"))))if(row.OptionalId("destination_account_id") is {} destination)Link(profile.Selection.AccountId,destination);
            foreach(var row in profile.Supply.Where(r=>CrossesHorizon(r.Id("position_id"))))Link(profile.Selection.AccountId,known.Positions[row.OptionalId("lot_id")??row.Id("incoming_position_id")].AccountId);
        }
        foreach(var settlement in known.Settlements.Where(s=>s.Time.EffectiveAt>source.ValuationAt))
        {
            var parent=known.Positions[settlement.PositionId];
            if(!active.Any(p=>p.Position==parent.Owner)||!Accepted(parent.AccountId))continue;
            if(settlement.ReceiptLotId is {} receipt)Link(parent.AccountId,known.Positions[receipt].AccountId);
            if(settlement.ConsumptionId is {} consumption)Link(parent.AccountId,known.Positions[known.Consumptions.Single(c=>c.Id==consumption).LotId].AccountId);
        }
        foreach(var repayment in known.Repayments.Where(r=>r.At>source.ValuationAt))
        {
            var parent=known.Positions[known.Links.LoanReturnPositions[repayment.LoanId]];
            if(active.Any(p=>p.Position==parent.Owner)&&Accepted(parent.AccountId))Link(parent.AccountId,known.Positions[repayment.LotId].AccountId);
        }
        foreach(var transition in physical?.Transitions??[])
        {
            var accounts=transition.Stock.Select(s=>s.Stock).Concat(transition.Discharges.Select(d=>d.Obligation))
                .Concat(transition.Receipts.Select(r=>r.Stock.Position)).Concat((transition.PhysicalOrigins??[]).Select(o=>o.Holding.Position))
                .Select(o=>known.Positions[o.Owner.Id].AccountId).Distinct().ToArray();
            foreach(var account in accounts.Skip(1))Link(accounts[0],account);
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
            var allPositions=active.Where(p=>connected.Contains(p.AccountId)).ToImmutableArray();
            var physicalEvents=(physical?.Transitions??[]).Where(t=>t.Stock.Select(s=>s.Stock).Concat(t.Discharges.Select(d=>d.Obligation))
                .Concat(t.Receipts.Select(r=>r.Stock.Position)).Concat((t.PhysicalOrigins??[]).Select(o=>o.Holding.Position))
                .Any(o=>connected.Contains(known.Positions[o.Owner.Id].AccountId))).ToImmutableArray();
            var futureHoldings=physicalEvents.SelectMany(t=>(t.PhysicalOrigins??[]).Select(o=>o.Holding).Concat(t.Receipts.Select(r=>r.Stock))).ToArray();
            if(allPositions.IsEmpty&&futureHoldings.Length==0)throw Error("gas_projection_known_transition","This horizon requires a qualified native future-origin gas transition before sampling.");
            foreach(var position in allPositions.Where(p=>p.Kind=="operator_claim"&&p.SettlementMode=="cashout_final_price_v1"))
            {
                components.Add(Cashout(position,profiles[position.AccountId],known,markets));owners.Add(position.Position);declaredOwners.Add(position.Position);
            }
            var positions=allPositions.Where(p=>p.Kind!="operator_claim"||p.SettlementMode!="cashout_final_price_v1").ToImmutableArray();
            if(positions.IsEmpty&&futureHoldings.Length==0)continue;
            var definitions=positions.Select(p=>new DefinitionRevision(database.Manifest.CatalogId,p.DefinitionRevisionId))
                .Concat(futureHoldings.Select(h=>h.Instrument.Mark.Curve.Revision)).Distinct().OrderBy(d=>d.RevisionId).ToArray();
            if(physical is null&&definitions.Length!=1||definitions.Any(d=>!markets.ContainsKey(d)))
                throw Error("gas_projection_source","Explicitly connected physical resources require one retained market graph; independent groups keep their own exact graphs.");
            var market=markets[physical is null?definitions[0]:source.Definitions];
            var snapshot=market.Snapshot;
            var holdings=positions.Select(position=>
            {
                var ownDefinition=new DefinitionRevision(database.Manifest.CatalogId,position.DefinitionRevisionId);var ownMarket=markets[ownDefinition];
                if(opening is null||!opening.Markets.TryGetValue(ownDefinition.RevisionId,out var originalMarket)||!originalMarket.Snapshot.HasSameInputs(ownMarket.Snapshot))
                    throw Error("gas_projection_source","Each gas component must retain the exact authorized opening market inputs for its native holdings.");
                if(position.Kind=="delivery_obligation"&&physical is not null)
                    return physical.OpeningHoldings.Single(h=>h.Position==position.Position);
                if(position.Instrument is not GasCommodityHoldingInstrument instrument||instrument.Mark.Curve.Revision!=ownDefinition)
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
                    ownMarket.CurrencyCurves.GetValueOrDefault(instrument.Currency)??throw Error("projection_cash_policy","Retain this gas return's exact captured currency discount curve."):null;
                return new GasProjectionHolding(position.Position,account,kind,instrument,position.Quantity,available,reservations,discount)
                    {Source=ownDefinition==snapshot.Definitions?null:ownMarket.Snapshot};
            }).ToImmutableArray();
            var instructions=connected.SelectMany(account=>profiles[account].Performances.Where(row=>CrossesHorizon(row.Id("position_id")))
                .Select(row=>(Profile:profiles[account],Row:row))).Select(selectedRow=>
            {
                var row=selectedRow.Row;var selection=selectedRow.Profile.Selection;var supplies=selectedRow.Profile.Supply;
                var position=known.Positions[row.Id("position_id")];
                if(!positions.Any(p=>p.Position==position.Owner)&&!futureHoldings.Any(h=>h.Position==position.Owner))
                    throw Error("gas_projection_stale","A saved performance refers to an obligation absent from the complete opening holdings and exact accepted origins.");
                var id=GasQuantityLifecycleProjection.PerformanceIdentity(selection.ProfileId,selection.Revision,position.Owner);
                var quantity=new Quantity(row.Number("quantity"),position.Quantity.Specification);
                GasProjectedInventoryTerms? receipt=null;
                if(row.OptionalId("destination_account_id") is {} destination)
                {
                    var account=known.Accounts[destination];var definition=new DefinitionRevision(database.Manifest.CatalogId,row.Id("inventory_definition_revision_id"));
                    var mark=new CurveRequest(new(definition,row.Text("inventory_curve_code")),new(definition,row.Id("inventory_contract_id")));
                    if(physical is null&&definition!=snapshot.Definitions||!markets.TryGetValue(definition,out var inventoryMarket)||row.Text("availability")!="available_at_projected_title_v1")
                        throw Error("gas_projection_source","Select the explicit retained inventory market and projected-title availability convention.");
                    var instrument=new GasCommodityHoldingInstrument(GasQuantityLifecycleProjection.ProjectedStockBinding(position.Owner,id),mark,account.Quantity,account.Currency,null,GasHoldingCarrying.Nominal);
                    receipt=new(account.Account,instrument,position.DueAt!.Value){Source=definition==snapshot.Definitions?null:inventoryMarket.Snapshot};
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
            // Version 2 composes the same explicit quantity contract with its
            // separately selected cashout contract; neither policy is inferred.
            var physicalAccepted=physicalEvents.Select(t=>new GasAcceptedQuantityEvent(t,physical!.OriginalPublications[t.Id])).ToImmutableArray();
            var accepted=connected.Any(Accepted)?AcceptedTransitions(known,holdings,snapshot,markets,physicalAccepted):[];
            var policies=connected.ToImmutableDictionary(account=>account,account=>new GasQuantityProjectionPolicy(
                Accepted(account)?GasQuantityProjectionPolicy.KnownVersion:GasQuantityProjectionPolicy.Version,profiles[account].Header.Text("quantity_disposition")));
            var plan=new GasQuantityProjectionPlan(snapshot,policies,holdings,instructions,accepted);quantityPlans.Add(plan);
            var component=plan.Build();
            if(!component.RequiresPhysicalComposition){_=component.EventsFor(horizon);components.Add(component);}
            owners.UnionWith(component.EconomicOwners);declaredOwners.AddRange(component.EconomicOwners);
        }
        var openingOwners=active.Select(p=>p.Position).ToHashSet();var declared=declaredOwners.ToArray();
        if(!openingOwners.IsSubsetOf(owners)||declared.Distinct().Count()!=declared.Length||declared.Any(o=>!openingOwners.Contains(o)&&
            (!known.Positions.TryGetValue(o.Owner.Id,out var future)||future.Owner!=o||future.Time.EffectiveAt<=source.ValuationAt||known.Quantities.Positions[o.Owner.Id].Retracted)))
            throw Error("gas_projection_scope","Preserve every opening gas owner once and declare future native receipt identities without activating their opening value.");
        var additional=declared.Where(o=>!openingOwners.Contains(o)).Select(owner=>
        {
            var position=known.Positions[owner.Owner.Id];
            var title=known.Evidence.SingleOrDefault(e=>e.Id==position.EvidenceId)??
                throw Error("gas_projection_source","A future receipt description requires its exact retained native title evidence.");
            return new LifecycleEconomicOwnerDescription(owner,title.SourceKey,position.Kind);
        }).ToImmutableArray();
        return new(components.ToImmutable(),openingOwners.ToImmutableHashSet(),evidence.ToImmutable()){AdditionalOwners=additional,QuantityPlans=quantityPlans.ToImmutable()};
        bool Accepted(Guid account)=>profiles.TryGetValue(account,out var profile)&&profile.Header.Text("implementation")==GasQuantityProjectionPolicy.KnownVersion;
        void Link(Guid a,Guid b)
        {
            if(!links.ContainsKey(b))throw Error("gas_projection_policy","Every explicitly linked source or inventory destination requires its own selected account profile.");
            links[a].Add(b);links[b].Add(a);
        }
    }
    private static void RequireKnownCoverage(GasOwnedKnownHistory known,DateTimeOffset opening,DateTimeOffset horizon,ImmutableDictionary<Guid,Profile> profiles,
        ImmutableHashSet<AccountingPositionKey> physicalOwners)
    {
        bool Accepted(Guid account)=>profiles.TryGetValue(account,out var profile)&&profile.Header.Text("implementation")==GasQuantityProjectionPolicy.KnownVersion;
        var origins=known.Positions.Values.Where(p=>p.Time.EffectiveAt>opening&&p.Time.EffectiveAt<=horizon&&!known.Quantities.Positions[p.Owner.Owner.Id].Retracted&&
            !physicalOwners.Contains(p.Owner)).ToArray();
        if(origins.Any(p=>p.OriginPolicy is "opening_owned_stock_v1" or "operator_balance_claim_v1" or "title_transfer_loan_v1"))
            throw Error("gas_projection_capital","A future independent title, claim or loan requires its exact represented purchase, capital or loan commitment; zero opening capital cannot be inferred.");
        if(origins.Any(p=>p.OriginPolicy!="operator_balance_transfer_v1"||!Accepted(p.AccountId))||
            known.Consumptions.Any(c=>c.At>opening&&c.At<=horizon&&!Accepted(known.Positions[c.LotId].AccountId))||
            known.Reservations.Any(r=>r.At>opening&&r.At<=horizon&&!Accepted(known.Positions[r.LotId].AccountId))||
            known.Releases.Any(r=>r.Time.EffectiveAt>opening&&r.Time.EffectiveAt<=horizon&&!Accepted(known.Positions[known.Reservations.Single(s=>s.Id==r.ReservationId).LotId].AccountId))||
            known.Settlements.Any(s=>s.Time.EffectiveAt>opening&&s.Time.EffectiveAt<=horizon&&(!Accepted(known.Positions[s.PositionId].AccountId)||s.CashId is not null))||
            known.Fees.Any(f=>f.Time.EffectiveAt>opening&&f.Time.EffectiveAt<=horizon)||known.MarkBindings.Any(m=>m.Time.EffectiveAt>opening&&m.Time.EffectiveAt<=horizon))
            throw Error("gas_projection_known_transition","Select accepted-quantity projection for native physical movements, or retain the exact linked cash, capital and mark-transition adapter required by this source.");
    }
    private static MarketDataException Error(string code,string message)=>new(code,message);
}
