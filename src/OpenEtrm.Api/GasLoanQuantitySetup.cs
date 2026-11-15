using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.MarketData.Gas;

namespace OpenEtrm.Api;

internal static partial class GasOwnedSetup
{
    private static ImmutableDictionary<string,GasForm> LoanQuantityForms(LifecycleReadContext context,LifecycleTarget target,
        JsonElement state,JsonElement known,JsonElement source)
    {
        const string action=GasLoanQuantityReconciliationActions.Action;
        if(source.Text("quantityChoiceProfile")!="gas_loan_quantity_choices_v1")
            throw GasOwnedOperations.Error("Retain the native loan correction choices at this exact source.");
        JsonElement[] Rows(JsonElement value,string name)=>[..value.GetProperty(name).EnumerateArray()];
        var loan=source.GetProperty("loan");var stock=source.GetProperty("stock");var owed=source.GetProperty("return");
        var accountId=source.Id("accountId");var loanId=source.Id("loanId");
        var account=Rows(state,"accounts").Single(a=>a.Id("id")==accountId);
        if(account.Text("kind")!="owned_inventory"||loan.Id("account_id")!=accountId||loan.Id("id")!=loanId||
            stock.Id("id")!=loan.Id("received_lot_id")||owed.Id("id")!=loan.Id("return_position_id")||
            stock.Id("account_id")!=accountId||owed.Id("account_id")!=accountId||
            Int(stock,"financialRevision")!=Int(loan,"stock_revision")||Int(owed,"financialRevision")!=Int(loan,"return_revision")||
            target.Handler==GasLoanQuantityReconciliationActions.Handler&&(target.ScopeId!=accountId||target.BindingId!=loanId)||
            target.Handler==GasLoanQuantityReconciliationActions.ConsumptionSourceHandler&&target.ScopeId!=loanId)
            throw GasOwnedOperations.Error("Select the native loan and its exact account, stock and return revisions.");
        if(stock.Instant("effective_at")>context.AsOf)
            throw new OpenEtrm.MarketData.MarketDataException("gas_loan_quantity_not_effective",
                "This observed loan title is after the selected operation time. Its source facts remain readable; select a time at or after title to correct its quantity.");
        var evidence=Rows(source,"evidence").Concat(Rows(known,"evidence")).DistinctBy(row=>row.Id("id")).ToArray();
        var heads=evidence.GroupBy(row=>(row.Id("account_id"),row.Text("source_key"))).Select(group=>group.MaxBy(row=>Int(row,"revision"))).ToArray();
        var uses=Rows(source,"consumptions");
        var editable=uses.Where(row=>row.GetProperty("editable").GetBoolean()).ToArray();
        if(uses.Select(row=>row.Id("id")).Distinct().Count()!=uses.Length||uses.Any(row=>Int(row,"financialRevision")<1))
            throw GasOwnedOperations.Error("Retain every selected native consumption and its exact financial revision.");
        JsonElement Document(Guid id)=>evidence.Single(row=>row.Id("id")==id);
        string Source(Guid id)=>Document(id).Text("source_key");
        string Caption(JsonElement use)=>$"{Words(use.Text("kind"))} · {Source(use.Id("evidence_id"))} · quantity {use.Text("quantity")} {source.Text("quantityUnit")} · financial revision {Int(use,"financialRevision")} · {use.Id("id")}";
        GasFormField Reference(string name,string label,string[] members,ImmutableArray<GasFormChoice> choices)=>new(name,label,"reference",false,choices,[],[..members]);
        GasFormField Select(string name,string label,ImmutableArray<GasFormChoice> choices)=>new(name,label,"select",false,choices,[],[]);
        GasFormField Quantity()=>new("quantity","Explicit corrected quantity ("+source.Text("quantityUnit")+")","text",false,[],[],[]);
        GasFormField Allocation()=>Select("sourceAllocation","Source allocation contract",[
            Choice(GasSourceAllocation.Preserve,"Preserve unused source quantity",GasSourceAllocation.Preserve),
            Choice(GasSourceAllocation.Withdraw,"Withdraw all source allocations",GasSourceAllocation.Withdraw)]);
        GasFormField Evidence(string name,string label,Guid appliedId,string kind,DateTimeOffset at)
        {
            var applied=Document(appliedId);
            return Select(name,label,[..heads.Where(row=>row.Id("account_id")==accountId&&row.Text("source_key")==applied.Text("source_key")&&
                row.Text("kind")==kind&&row.Instant("occurred_at")==at).Select(row=>Choice(row.Id("id").ToString(),
                    $"{row.Text("source_key")} · source revision {Int(row,"revision")} · document quantity {row.Text("quantity")} {source.Text("quantityUnit")}",row.Id("id")))]);
        }
        GasForm form;
        if(target.Handler==GasLoanQuantityReconciliationActions.Handler)
        {
            var parent=account.Text("code")+" · "+Source(loan.Id("agreement_evidence_id"))+" · "+loanId;
            form=new("Correct observed commodity loan quantity","RecordGasOwnedHoldings",[
                Reference("observedLoan","Observed loan, stock and return revisions",["loanId","expectedRevision","stockExpectedRevision","returnExpectedRevision"],
                    [Choice(loanId.ToString(),parent+$" · loan revision {Int(loan,"expectedRevision")} · stock revision {Int(stock,"financialRevision")} · return revision {Int(owed,"financialRevision")}",
                        new{loanId,expectedRevision=Int(loan,"expectedRevision"),stockExpectedRevision=Int(stock,"financialRevision"),returnExpectedRevision=Int(owed,"financialRevision")})]),
                Quantity(),Evidence("titleEvidenceId","Exact title source revision",loan.Id("title_evidence_id"),"title_receipt",stock.Instant("effective_at")),
                Evidence("agreementEvidenceId","Exact agreement source revision",loan.Id("agreement_evidence_id"),"loan_agreement",stock.Instant("effective_at")),Allocation(),
                Select("feeDisposition","Independent fixed fee contract",[Choice(GasLoanQuantityReconciliationActions.RetainFees,"Retain fixed fee memberships and bank history",GasLoanQuantityReconciliationActions.RetainFees)]),
                new GasFormField("consumptions","Recorded use and repayment corrections","array",false,[],[],[]){MemberLabel="Recorded use or repayment",Variants=[..editable.Select(row=>
                    new GasFormVariant(row.Id("id").ToString(),Caption(row),new(GasLoanQuantityReconciliationActions.ConsumptionSourceHandler,loanId,row.Id("id")),parent){Action=action})]},
                new("evidence","Correction evidence reference","text",false,[],[],[])]);
        }
        else if(target.Handler==GasLoanQuantityReconciliationActions.ConsumptionSourceHandler)
        {
            var selected=editable.SingleOrDefault(row=>row.Id("id")==target.BindingId);
            if(selected.ValueKind!=JsonValueKind.Object)throw GasOwnedOperations.Error("Select one exact editable use or repayment of this native loan.");
            var at=selected.Instant("effective_at");
            var positions=Rows(known,"positions").ToDictionary(row=>row.Id("id"));
            var reservations=Rows(known,"reservations");var releases=Rows(known,"releases");
            var choices=ImmutableArray.CreateBuilder<GasFormChoice>();
            foreach(var lot in Rows(known,"lots"))
            {
                var position=positions[lot.Id("id")];
                if(position.Id("account_id")!=accountId||position.Text("kind")!="owned_stock"||lot.Instant("available_at")>at||
                    position.Id("id")!=selected.Id("lot_id")&&position.Number("quantity").CompareTo(default)<=0)continue;
                var label=account.Text("code")+" · "+Source(position.Id("evidence_id"))+" · "+lot.Id("id");
                choices.Add(Choice(lot.Id("id")+":none",label+" · no reservation",new{lotId=lot.Id("id"),reservationId=(Guid?)null}));
                foreach(var reservation in reservations.Where(row=>row.Id("lot_id")==lot.Id("id")&&row.Instant("effective_at")<=at&&
                    !releases.Any(release=>release.Id("reservation_id")==row.Id("id")&&release.Instant("effective_at")<=at)))
                    choices.Add(Choice(lot.Id("id")+":"+reservation.Id("id"),label+" · reservation "+reservation.Text("business_key"),
                        new{lotId=lot.Id("id"),reservationId=(Guid?)reservation.Id("id")}));
            }
            var kind=selected.Text("kind") switch{"delivery"=>"physical_delivery","loss"=>"physical_loss","return"=>"loan_return",_=>throw GasOwnedOperations.Error("Retain the native editable resource-use kind.")};
            form=new("Correct observed loan use or repayment","RecordGasOwnedHoldings",[
                Reference("observedQuantity","Observed use revision",["consumptionId","expectedRevision"],
                    [Choice(selected.Id("id").ToString(),Caption(selected),new{consumptionId=selected.Id("id"),expectedRevision=Int(selected,"financialRevision")})]),
                Reference("identifiedStock","Identified stock and reservation",["lotId","reservationId"],choices.ToImmutable()),Quantity(),
                Select("retracted","Quantity disposition",[Choice("false","Keep an active quantity",false),Choice("true","Withdraw the recorded quantity",true)]),
                Evidence("evidenceId","Exact use source revision",selected.Id("evidence_id"),kind,at),Allocation()]);
        }
        else throw GasOwnedOperations.Error("Select one supported native loan correction source.");
        return ImmutableDictionary<string,GasForm>.Empty.Add(action,form);
    }
}
