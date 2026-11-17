using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

internal static class GasParkCustodyAdapterChecks
{
    internal static void Run()
    {
        foreach(var perspective in new[]{"customer","provider"})
        {
            var row=Agreement(perspective);var catalog=Guid.NewGuid();
            var state=GasParkCustodyAdapter.Read(catalog,row);
            foreach(var opaqueKey in new[]{string.Concat(Enumerable.Repeat("\U0001F30C",65)),"\u00a0"})
            foreach(var index in new[]{0,1,2})
            {
                var opaque=JsonNode.Parse(row.GetRawText())!.AsObject();
                opaque["evidence"]![index]!["sourceKey"]=opaqueKey;opaque["evidenceHeads"]![index]!["sourceKey"]=opaqueKey;
                if(index<2)opaque["acceptance"]![index==0?"agreementSourceKey":"receiptSourceKey"]=opaqueKey;
                else{opaque["returns"]![0]!["evidenceSourceKey"]=opaqueKey;opaque["returns"]![0]!["businessKey"]=opaqueKey;}
                _=GasParkCustodyAdapter.Read(catalog,JsonSerializer.SerializeToElement(opaque));
            }
            if(state.Balance.InCustody.Amount!=ExactDecimal.Parse("450")||state.Balance.Reserved.Amount!=ExactDecimal.Parse(perspective=="customer"?"450":"0")||
                state.Cash is null||state.Cash.Quantity.Amount!=ExactDecimal.Parse("-12.5")||!GasParkCustodyValuation.Components(state.Terms,state.Source).IsEmpty)
                throw new Exception("The source adapter must retain accepted custody, original customer reservations and one independent signed cash membership.");
            var later=JsonNode.Parse(row.GetRawText())!.AsObject();
            var revisedPayment=later["cash"]!["payments"]![0]!.DeepClone().AsObject();
            revisedPayment["revision"]=2;revisedPayment["paid_at"]="2032-01-18T10:00:00Z";revisedPayment["known_at"]="2032-01-21T00:00:00Z";
            later["cash"]!["payments"]!.AsArray().Add(revisedPayment);
            var revised=GasParkCustodyAdapter.Read(catalog,JsonSerializer.SerializeToElement(later));
            var snapshot=new MarketSnapshot(Guid.NewGuid(),new(catalog,row.GetProperty("terms").Id("definitionRevisionId")),state.Source.At,state.Source.At,
                InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[]).WithKnowledge(row.Id("contextId"),state.Source.KnownAt);
            if(revised.Cash!.State.Outstanding(snapshot).ToExactString()!="-12.5"||state.Cash!.State.Outstanding(snapshot).ToExactString()!="-7.5")
                throw new Exception("Knowledge-first cash revision must supersede an earlier payment even when its corrected paid instant follows this business cut.");
            var simultaneous=JsonNode.Parse(row.GetRawText())!.AsObject();
            simultaneous["cash"]!["payments"]![0]!["amount"]="-12.5";
            var releasedPayment=revisedPayment.DeepClone().AsObject();releasedPayment["amount"]="0";
            var replacement=revisedPayment.DeepClone().AsObject();replacement["allocation_id"]=Guid.NewGuid();replacement["revision"]=1;replacement["amount"]="-12.5";
            foreach(var updates in new[]{new[]{replacement,releasedPayment},new[]{releasedPayment,replacement}})
            {
                var permuted=simultaneous.DeepClone().AsObject();
                foreach(var update in updates)permuted["cash"]!["payments"]!.AsArray().Add(update.DeepClone());
                _=GasParkCustodyAdapter.Read(catalog,JsonSerializer.SerializeToElement(permuted));
            }
            void Reject(Action<JsonObject> change)
            {
                var altered=JsonNode.Parse(row.GetRawText())!.AsObject();change(altered);
                try{GasParkCustodyAdapter.Read(catalog,JsonSerializer.SerializeToElement(altered));}
                catch(MarketDataException e)when(e.Code.StartsWith("gas_custody_",StringComparison.Ordinal)||e.Code=="payment_state"){return;}
                throw new Exception("An incomplete, inconsistent or later native custody source must refuse with a structured error.");
            }
            Reject(x=>x.Remove("knownAt"));Reject(x=>x["asOf"]="2032-01-16T10:00:00.0000001Z");
            Reject(x=>
            {
                var longKey=string.Concat(Enumerable.Repeat("\U0001F30C",129));
                x["evidence"]![0]!["sourceKey"]=longKey;x["evidenceHeads"]![0]!["sourceKey"]=longKey;x["acceptance"]!["agreementSourceKey"]=longKey;
            });
            Reject(x=>x.Remove("ownedAccountId"));
            Reject(x=>x["returns"]![0]!["businessKey"]=string.Concat(Enumerable.Repeat("\U0001F30C",129)));
            Reject(x=>x["evidence"]![0]!.AsObject().Remove("previousId"));
            Reject(x=>x["implementation"]="physical_gas_park_loan_v1");
            Reject(x=>x["terms"]!["usePolicy"]="use_allowed");
            Reject(x=>x["acceptance"]!["knownAt"]="2032-01-25T00:00:00.000001Z");
            Reject(x=>x["acceptance"]!["receiptRevision"]=2);
            Reject(x=>x["evidence"]![1]!["rawQuantity"]="719");
            Reject(x=>x["returns"]![0]!["quantity"]="269");
            Reject(x=>x["returns"]![0]!["evidenceSourceKey"]="different_observation");
            Reject(x=>x["evidenceHeads"]!.AsArray().RemoveAt(2));
            Reject(x=>x["balances"]!["ownedQuantityChange"]="-720");
            Reject(x=>x["cash"]!["amount"]="12.5");
            Reject(x=>x["cash"]!["instrumentId"]=Guid.NewGuid());
            Reject(x=>x["cash"]!["payments"]![0]!["trade_id"]=Guid.NewGuid());
            Reject(x=>x["cash"]!["payments"]![0]!["paid_at"]="2032-01-14T10:00:00Z");
            Reject(x=>x["cash"]!["payments"]![0]!["amount"]="-13");
            Reject(x=>x["cash"]!["payments"]![0]!["amount"]="5");
            Reject(x=>
            {
                x["cash"]!["payments"]![0]!["amount"]="-20";
                x["cash"]!["payments"]!.AsArray().Add(revisedPayment.DeepClone());
            });
            Reject(x=>x["cash"]=null);
            if(perspective=="customer")
            {
                Reject(x=>x["returns"]![0]!["allocations"]![0]!["reservationId"]=Guid.NewGuid());
                Reject(x=>x["returns"]![0]!["allocations"]![0]!["remainderQuantity"]="451");
                Reject(x=>x["reservations"]![0]!["quantity"]="449");
            }
            else Reject(x=>x["ownedAccountId"]=Guid.NewGuid());

            // One logical observed return is not two returns merely because a
            // second revision and accepted-event identity were supplied.
            Reject(x=>
            {
                var observed=x["evidence"]![2]!.DeepClone().AsObject();var id=Guid.NewGuid();
                observed["id"]=id;observed["revision"]=2;observed["previousId"]=x["evidence"]![2]!["id"]!.DeepClone();
                observed["knownAt"]="2032-01-21T00:00:00Z";
                x["evidence"]!.AsArray().Add(observed);x["evidenceHeads"]![2]=observed.DeepClone();
                var returned=x["returns"]![0]!.DeepClone().AsObject();returned["id"]=Guid.NewGuid();returned["evidenceId"]=id;returned["evidenceRevision"]=2;
                x["returns"]!.AsArray().Add(returned);x["reconciliationRequired"]=true;
            });
            var corrected=JsonNode.Parse(row.GetRawText())!.AsObject();var correction=corrected["evidence"]![1]!.DeepClone().AsObject();
            correction["id"]=Guid.NewGuid();correction["revision"]=2;correction["previousId"]=corrected["evidence"]![1]!["id"]!.DeepClone();correction["rawQuantity"]="700";
            correction["knownAt"]="2032-01-21T00:00:00Z";
            corrected["evidence"]!.AsArray().Add(correction);corrected["evidenceHeads"]![1]=correction.DeepClone();
            Reject(x=>{x["evidence"]=corrected["evidence"]!.DeepClone();x["evidenceHeads"]=corrected["evidenceHeads"]!.DeepClone();});
            corrected["reconciliationRequired"]=true;
            var retained=GasParkCustodyAdapter.Read(catalog,JsonSerializer.SerializeToElement(corrected));
            if(!retained.ReconciliationRequired||retained.Cash is null||retained.Cash.Quantity.Amount!=ExactDecimal.Parse("-12.5"))
                throw new Exception("Corrected observations remain inspectable with their accepted cash; they must not rewrite title or cash.");
            try{GasParkCustodyAdapter.Components(retained);throw new Exception("A corrected source must not become a successful complete service value.");}
            catch(MarketDataException e)when(e.Code=="gas_custody_reconciliation"){}
        }
        Console.WriteLine("PASS custody source adapter: exact observation manifests, logical return identity, reservation lineage, independent cash and corrected-source refusal");
    }

    internal static JsonElement Agreement(string perspective)
    {
        var trade=Guid.NewGuid();var instrument=Guid.NewGuid();var book=Guid.NewGuid();var context=Guid.NewGuid();
        var reference=Guid.NewGuid();var revision=Guid.NewGuid();var lot=Guid.NewGuid();var reserved=Guid.NewGuid();var remainder=Guid.NewGuid();
        var agreed=Guid.NewGuid();var received=Guid.NewGuid();var returned=Guid.NewGuid();var acceptance=Guid.NewGuid();var binding=Guid.NewGuid();
        var currency=new ReferenceKey(new(reference,revision),"synthetic_currency");
        var cash=new OpenEtrm.MarketData.Trading.TimedCashPayment(currency,OpenEtrm.MarketData.Trading.TradingInstant.Parse("2032-01-19T10:00:00Z","synthetic"));
        object Evidence(Guid id,string key,string kind,string quantity,string observed)=>new{id,sourceKey=key,revision=1,previousId=(Guid?)null,kind,rawQuantity=quantity,observedAt=observed,knownAt="2032-01-20T00:00:00Z",evidence="Synthetic independently retained observation"};
        var evidence=new[]{Evidence(agreed,"agreement","custody_agreement","720","2032-01-14T10:00:00Z"),Evidence(received,"receipt","custody_receipt","720","2032-01-15T10:00:00Z"),Evidence(returned,"return","custody_return","270","2032-01-16T10:00:00Z")};
        return JsonSerializer.SerializeToElement(new{
            implementation=GasParkCustodyTerms.Version,tradeId=trade,instrumentId=instrument,bookId=book,revision=1,publicationOrder=1,
            quantity="720",ownedAccountId=perspective=="customer"?Guid.NewGuid():(Guid?)null,contextId=context,asOf="2032-01-16T10:00:00Z",knownAt="2032-01-25T00:00:00Z",reconciliationRequired=false,
            terms=new{definitionRevisionId=Guid.NewGuid(),referenceCatalogId=reference,referenceRevisionId=revision,point="synthetic_point",quantity="synthetic_quantity",currency="synthetic_currency",
                serviceKind="park",perspective,parkAt="2032-01-15T10:00:00Z",returnAt="2032-01-17T10:00:00Z",titlePolicy=GasParkCustodyTerms.RetainTitle,
                returnPolicy=GasParkCustodyTerms.SameBasis,returnTiming=GasParkCustodyTerms.ReturnsAfterAcceptance,returnOrder=GasParkCustodyTerms.NondecreasingReturns,
                usePolicy=GasParkCustodyTerms.NoUse,lossPolicy=GasParkCustodyTerms.LossUnqualified,physicalPolicy=GasParkCustodyTerms.SeparatePhysical,planPolicy=GasParkCustodyTerms.UnacceptedPlan,
                fixedFeeAmount="12.5",feeDirection="pay",feeDueAt="2032-01-19T10:00:00Z",feeRecognition=GasParkCustodyTerms.FeeAtAcceptance},
            evidence,evidenceHeads=evidence,
            acceptance=new{id=acceptance,quantity="720",effectiveAt="2032-01-15T10:00:00Z",knownAt="2032-01-20T00:00:00Z",agreementEvidenceId=agreed,agreementSourceKey="agreement",agreementRevision=1,
                receiptEvidenceId=received,receiptSourceKey="receipt",receiptRevision=1,allocations=perspective=="customer"?new[]{new{lotId=lot,reservationId=reserved,quantity="720"}}:[]},
            returns=new[]{new{id=Guid.NewGuid(),acceptanceId=acceptance,businessKey="synthetic_recorded_return",quantity="270",effectiveAt="2032-01-16T10:00:00Z",knownAt="2032-01-20T00:00:00Z",evidenceId=returned,evidenceSourceKey="return",evidenceRevision=1,
                allocations=perspective=="customer"?new[]{new{lotId=lot,reservationId=reserved,quantity="270",remainderReservationId=(Guid?)remainder,remainderQuantity="450"}}:[]}},
            reservations=perspective=="customer"?new[]{new{lotId=lot,reservationId=remainder,quantity="450"}}:[],
            balances=new{inCustody="450",reserved=perspective=="customer"?"450":"0",overdue="0",ownedQuantityChange="0"},
            cash=new{bindingId=binding,instrumentId=cash.Id,amount="-12.5",currency="synthetic_currency",dueAt="2032-01-19T10:00:00Z",effectiveAt="2032-01-15T10:00:00Z",
                payments=new[]{new{allocation_id=Guid.NewGuid(),revision=1,trade_id=trade,binding_id=binding,book_id=book,reference_catalog_id=reference,reference_revision_id=revision,currency_code="synthetic_currency",
                    paid_at="2032-01-16T10:00:00Z",known_at="2032-01-20T00:00:00Z",amount="-5"}}}
        });
    }
}
