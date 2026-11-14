using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

internal static partial class GasOwnedSetup
{
    private static ImmutableDictionary<string,GasForm> QuantityForms(LifecycleReadContext context,LifecycleTarget target,JsonElement state,JsonElement known)
    {
        const string action=GasOwnedQuantityReconciliationActions.Action;
        if(state.Text("quantityChoiceProfile")!=GasOwnedQuantityReconciliationActions.ChoiceProfile)
            throw GasOwnedOperations.Error("Read the current source-known ordinary quantity choices.");
        JsonElement[] Rows(JsonElement source,string key)=>[..source.GetProperty(key).EnumerateArray()];
        var accounts=Rows(state,"accounts");var account=accounts.SingleOrDefault(a=>a.Id("id")==target.ScopeId);
        if(account.ValueKind!=JsonValueKind.Object||account.Text("kind")!="owned_inventory")throw GasOwnedOperations.Error("Choose the exact owned gas account for this source.");
        var positions=Rows(state,"positions");var consumptions=Rows(state,"consumptions");
        var allEvidence=Rows(state,"evidence").Concat(Rows(known,"evidence")).DistinctBy(e=>e.Id("id")).ToArray();
        var evidenceHeads=allEvidence.GroupBy(e=>(e.Id("account_id"),e.Text("source_key"))).Select(group=>group.MaxBy(e=>Int(e,"revision"))).ToArray();
        var titleChoices=Rows(state,"quantityPositionChoices");var useChoices=Rows(state,"quantityConsumptionChoices");
        void RequireChoices(JsonElement[] choices,string id,JsonElement[] facts,Func<JsonElement,bool> eligible)
        {
            if(choices.Select(row=>row.Id(id)).Distinct().Count()!=choices.Length||choices.Any(row=>row.Id("accountId")!=target.ScopeId||Int(row,"expectedRevision")<1||
                !facts.Any(fact=>fact.Id("id")==row.Id(id)&&Int(fact,"financial_revision")==Int(row,"expectedRevision")&&eligible(fact))))
                throw GasOwnedOperations.Error("Retain every exact source-known member and its native financial revision.");
        }
        RequireChoices(titleChoices,"positionId",positions,fact=>fact.Id("account_id")==target.ScopeId&&fact.Text("kind")=="owned_stock"&&
            fact.Text("origin_policy")=="opening_owned_stock_v1"&&fact.Instant("effective_at")<=context.AsOf);
        RequireChoices(useChoices,"consumptionId",consumptions,fact=>(fact.Text("kind") is "delivery" or "loss")&&fact.Instant("effective_at")<=context.AsOf&&
            positions.Any(position=>position.Id("id")==fact.Id("lot_id")&&position.Id("account_id")==target.ScopeId));
        JsonElement Document(JsonElement row)=>allEvidence.Single(e=>e.Id("id")==row.Id("evidence_id"));
        string Position(JsonElement position)=>account.Text("code")+" · "+Document(position).Text("source_key");
        string Label(JsonElement row,string kind)=>$"{kind} · quantity {row.Text("quantity")} · financial revision {Int(row,"financial_revision")} · {Document(row).Text("source_key")} · {row.Id("id")}";
        GasFormField Reference(string name,string label,string[] members,ImmutableArray<GasFormChoice> choices)=>new(name,label,"reference",false,choices,[],[..members]);
        GasFormField ChoiceField(string name,string label,ImmutableArray<GasFormChoice> choices)=>new(name,label,"select",false,choices,[],[]);
        GasFormField Quantity()=>new("quantity","Explicit corrected quantity","text",false,[],[],[]);
        GasFormField Retraction()=>ChoiceField("retracted","Quantity disposition",[Choice("false","Keep an active quantity",false),Choice("true","Withdraw the recorded quantity",true)]);
        GasFormField Allocation()=>ChoiceField("sourceAllocation","Source allocation contract",[
            Choice(OpenEtrm.MarketData.Gas.GasSourceAllocation.Preserve,"Preserve unused source quantity",OpenEtrm.MarketData.Gas.GasSourceAllocation.Preserve),
            Choice(OpenEtrm.MarketData.Gas.GasSourceAllocation.Withdraw,"Withdraw all source allocations",OpenEtrm.MarketData.Gas.GasSourceAllocation.Withdraw)]);
        GasFormField Evidence(JsonElement fact,string kind)
        {
            var applied=Document(fact);
            var selected=evidenceHeads.Where(e=>e.Id("account_id")==target.ScopeId&&e.Text("source_key")==applied.Text("source_key")&&e.Text("kind")==kind&&
                e.Instant("occurred_at")==applied.Instant("occurred_at")&&e.Instant("occurred_at")<=context.AsOf);
            return ChoiceField("evidenceId","Exact source revision",[..selected.Select(e=>Choice(e.Id("id").ToString(),
                $"{e.Text("source_key")} · source revision {Int(e,"revision")} · document quantity {e.Text("quantity")}",e.Id("id")))]);
        }
        ImmutableArray<GasFormField> Title(JsonElement selected)
        {
            var fact=positions.Single(p=>p.Id("id")==selected.Id("positionId"));
            return [Reference("observedQuantity","Observed title revision",["positionId","expectedRevision"],
                [Choice(fact.Id("id").ToString(),Label(fact,"Owned title"),new{positionId=fact.Id("id"),expectedRevision=Int(selected,"expectedRevision")})]),
                Quantity(),Retraction(),Evidence(fact,"title_receipt"),Allocation()];
        }
        ImmutableArray<GasFormField> Consumption(JsonElement selected)
        {
            var fact=consumptions.Single(c=>c.Id("id")==selected.Id("consumptionId"));var at=fact.Instant("effective_at");
            var knownPositions=Rows(known,"positions").Concat(positions).DistinctBy(p=>p.Id("id")).ToArray();
            var lots=Rows(known,"lots").Concat(Rows(state,"lots")).DistinctBy(l=>l.Id("id")).ToArray();
            var reservations=Rows(state,"reservations");var releases=Rows(state,"releases");
            var stock=ImmutableArray.CreateBuilder<GasFormChoice>();
            foreach(var lot in lots)
            {
                var position=knownPositions.Single(p=>p.Id("id")==lot.Id("id"));
                if(position.Id("account_id")!=target.ScopeId||position.Text("kind")!="owned_stock"||lot.Instant("available_at")>at||
                    position.Id("id")!=fact.Id("lot_id")&&position.Number("quantity").CompareTo(default)<=0)continue;
                stock.Add(Choice(lot.Id("id")+":none",Position(position)+" · no reservation",new{lotId=lot.Id("id"),reservationId=(Guid?)null}));
                foreach(var reservation in reservations.Where(r=>r.Id("lot_id")==lot.Id("id")&&r.Instant("effective_at")<=at&&
                    !releases.Any(release=>release.Id("reservation_id")==r.Id("id")&&release.Instant("effective_at")<=at)))
                    stock.Add(Choice(lot.Id("id")+":"+reservation.Id("id"),Position(position)+" · reservation "+reservation.Text("business_key"),
                        new{lotId=lot.Id("id"),reservationId=(Guid?)reservation.Id("id")}));
            }
            return [Reference("observedQuantity","Observed use revision",["consumptionId","expectedRevision"],
                [Choice(fact.Id("id").ToString(),Label(fact,Words(fact.Text("kind"))),new{consumptionId=fact.Id("id"),expectedRevision=Int(selected,"expectedRevision")})]),
                Reference("identifiedStock","Identified stock and reservation",["lotId","reservationId"],stock.ToImmutable()),Quantity(),Retraction(),
                Evidence(fact,fact.Text("kind")=="loss"?"physical_loss":"physical_delivery"),Allocation()];
        }
        GasForm Form(string label,ImmutableArray<GasFormField> fields)=>new(label,"RecordGasOwnedHoldings",fields);
        GasForm form;
        if(target.Handler==GasOwnedQuantityReconciliationActions.Handler)
        {
            if(target.BindingId!=target.ScopeId)throw GasOwnedOperations.Error("Select the exact native account for the ordinary correction.");
            form=Form("Correct observed gas title or use",[
                new GasFormField("positions","Owned title corrections","array",false,[],[],[]){MemberLabel="Owned title",Variants=[..titleChoices.Select(row=>
                    new GasFormVariant(row.Id("positionId").ToString(),Label(positions.Single(p=>p.Id("id")==row.Id("positionId")),"Owned title"),
                        new(GasOwnedQuantityReconciliationActions.PositionSourceHandler,target.ScopeId,row.Id("positionId")),""))]},
                new GasFormField("consumptions","Recorded delivery and loss corrections","array",false,[],[],[]){MemberLabel="Recorded use",Variants=[..useChoices.Select(row=>
                    new GasFormVariant(row.Id("consumptionId").ToString(),Label(consumptions.Single(c=>c.Id("id")==row.Id("consumptionId")),"Recorded use"),
                        new(GasOwnedQuantityReconciliationActions.ConsumptionSourceHandler,target.ScopeId,row.Id("consumptionId")),""))]},
                new("evidence","Correction evidence reference","text",false,[],[],[])]);
        }
        else if(target.Handler==GasOwnedQuantityReconciliationActions.PositionSourceHandler)
        {
            var selected=titleChoices.SingleOrDefault(row=>row.Id("positionId")==target.BindingId);
            if(selected.ValueKind!=JsonValueKind.Object)throw GasOwnedOperations.Error("Select one exact source-known owned title.");
            form=Form("Correct observed owned title",Title(selected));
        }
        else if(target.Handler==GasOwnedQuantityReconciliationActions.ConsumptionSourceHandler)
        {
            var selected=useChoices.SingleOrDefault(row=>row.Id("consumptionId")==target.BindingId);
            if(selected.ValueKind!=JsonValueKind.Object)throw GasOwnedOperations.Error("Select one exact source-known delivery or loss.");
            form=Form("Correct observed delivery or loss",Consumption(selected));
        }
        else throw GasOwnedOperations.Error("Select a supported native ordinary quantity source.");
        return ImmutableDictionary<string,GasForm>.Empty.Add(action,form);
    }
}
