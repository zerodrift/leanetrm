using System.Text.Json;
using System.Text.Json.Nodes;
using OpenEtrm.Api;
using OpenEtrm.MarketData;

internal static class GasParkCustodyEffectChecks
{
    internal static void Run()
    {
        foreach(var perspective in new[]{"customer","provider"})
        {
            var current=GasParkCustodyAdapterChecks.Agreement(perspective);var customer=perspective=="customer";var catalog=Guid.NewGuid();
            var nextEvidence=Guid.NewGuid();var withObservation=JsonNode.Parse(current.GetRawText())!.AsObject();
            var observation=JsonSerializer.SerializeToNode(new{id=nextEvidence,sourceKey="next_return",revision=1,previousId=(Guid?)null,
                kind="custody_return",rawQuantity="100",observedAt="2032-01-16T10:00:00Z",knownAt="2032-01-21T00:00:00Z"})!;
            withObservation["evidence"]!.AsArray().Add(observation);withObservation["evidenceHeads"]!.AsArray().Add(observation.DeepClone());
            current=JsonSerializer.SerializeToElement(withObservation);
            var lot=current.GetProperty("acceptance").GetProperty("allocations").EnumerateArray().Select(r=>r.Id("lotId")).FirstOrDefault();
            var target=new LifecycleTarget("gas_park_custody",current.Id("tradeId"),current.Id("instrumentId"));
            var context=new LifecycleReadContext(current.Id("contextId"),current.Instant("asOf"),new(2032,1,16));
            LifecycleActionRequest Request(string action,object selection)=>new(Guid.NewGuid(),context,target,action,Guid.NewGuid(),1,false,new('0',64),JsonSerializer.SerializeToElement(selection));
            JsonElement Outcome(string action,string quantity,string before,string after,string reservedBefore,string reservedAfter,string fee,object[] allocations,Guid operation,Guid? id=null,bool changed=true)=>
                JsonSerializer.SerializeToElement(new{operationId=operation,tradeId=target.ScopeId,instrumentId=target.BindingId,id=id??Guid.NewGuid(),action,changed,quantity,
                    custodyBefore=before,custodyAfter=after,reservedBefore,reservedAfter,ownedQuantityChange="0",feeAmount=fee,
                    cashBindingId=fee=="0"?(Guid?)null:Guid.NewGuid(),returnAt="2032-01-17T10:00:00Z",allocations});
            JsonElement Review(JsonElement source,LifecycleActionRequest request,JsonElement outcome)=>
                JsonSerializer.SerializeToElement(GasParkCustodyActions.Effects(catalog,source,request,outcome),GasOwnedOperations.Json);
            void Refuse(JsonElement source,LifecycleActionRequest request,JsonElement outcome)
            {
                try{Review(source,request,outcome);}
                catch(MarketDataException error)when(error.Code.StartsWith("gas_custody_",StringComparison.Ordinal)){return;}
                throw new Exception("The custody review must independently refuse inconsistent native quantity, title, cash or reservation effects.");
            }

            var unaccepted=JsonNode.Parse(current.GetRawText())!.AsObject();
            unaccepted["acceptance"]=null;unaccepted["returns"]=new JsonArray();unaccepted["reservations"]=new JsonArray();unaccepted["cash"]=null;
            unaccepted["balances"]=JsonSerializer.SerializeToNode(new{inCustody="0",reserved="0",overdue="0",ownedQuantityChange="0"});
            var before=JsonSerializer.SerializeToElement(unaccepted);
            var accept=Request("gas.park_custody.accept",new{expectedVersion=1,agreementEvidenceId=current.GetProperty("acceptance").Id("agreementEvidenceId"),
                receiptEvidenceId=current.GetProperty("acceptance").Id("receiptEvidenceId"),effectiveAt="2032-01-15T10:00:00Z",
                allocations=customer?new[]{new{lotId=lot,quantity="720"}}:[]});
            var accepted=Outcome("accept","720","0","720","0",customer?"720":"0","-12.5",customer?
                [new{lotId=lot,reservationId=Guid.NewGuid(),quantity="720",remainderReservationId=(Guid?)null,remainderQuantity="0"}]:[],accept.OperationId);
            var acceptedReview=Review(before,accept,accepted);
            if(acceptedReview.Text("custodyBefore")!="0"||acceptedReview.Text("custodyAfter")!="720"||acceptedReview.Text("ownedQuantityChange")!="0"||
                acceptedReview.Text("feeAmount")!="-12.5"||acceptedReview.Text("currency")!="synthetic_currency"||
                !acceptedReview.GetProperty("titleRetainedByCustomer").GetBoolean()||!acceptedReview.GetProperty("physicalActivityUnchanged").GetBoolean())
                throw new Exception("Accepting custody must review exact held quantity and signed independent cash without manufacturing title or flow.");
            var wrongFee=JsonNode.Parse(accepted.GetRawText())!.AsObject();wrongFee["feeAmount"]="-13";Refuse(before,accept,JsonSerializer.SerializeToElement(wrongFee));
            var wrongTitle=JsonNode.Parse(accepted.GetRawText())!.AsObject();wrongTitle["ownedQuantityChange"]="720";Refuse(before,accept,JsonSerializer.SerializeToElement(wrongTitle));
            var wrongOperation=JsonNode.Parse(accepted.GetRawText())!.AsObject();wrongOperation["operationId"]=Guid.NewGuid().ToString();Refuse(before,accept,JsonSerializer.SerializeToElement(wrongOperation));

            // Native command receipts can authenticate an existing fact beyond
            // this knowledge cut. Acknowledgement never promotes that cut.
            var outsideAccept=JsonNode.Parse(accepted.GetRawText())!.AsObject();outsideAccept["changed"]=false;
            var outsideAcceptedReview=Review(before,accept,JsonSerializer.SerializeToElement(outsideAccept));
            if(!outsideAcceptedReview.GetProperty("acknowledgementOutsideSource").GetBoolean()||
                outsideAcceptedReview.Text("custodyBefore")!="0"||outsideAcceptedReview.Text("custodyAfter")!="0"||
                outsideAcceptedReview.Text("performedQuantity")!="0"||outsideAcceptedReview.Text("acknowledgedQuantity")!="720"||
                outsideAcceptedReview.Text("feeAmount")!="0"||outsideAcceptedReview.GetProperty("allocations").GetArrayLength()!=0)
                throw new Exception("A sealed acceptance acknowledgement beyond the selected knowledge cut must preserve that cut and show no new custody, reservation or cash.");
            var invalidOutside=JsonNode.Parse(outsideAccept.ToJsonString())!.AsObject();invalidOutside["custodyAfter"]="721";
            Refuse(before,accept,JsonSerializer.SerializeToElement(invalidOutside));

            var active=customer?current.GetProperty("reservations")[0].Id("reservationId"):Guid.NewGuid();
            var returning=Request("gas.park_custody.return",new{expectedVersion=3,businessKey="synthetic_next_return",evidenceId=nextEvidence,quantity="100",
                effectiveAt="2032-01-16T10:00:00Z",allocations=customer?new[]{new{reservationId=active,quantity="100"}}:[]});
            var returned=Outcome("return","100","450","350",customer?"450":"0",customer?"350":"0","0",customer?
                [new{lotId=lot,reservationId=active,quantity="100",remainderReservationId=(Guid?)Guid.NewGuid(),remainderQuantity="350"}]:[],returning.OperationId);
            var returnReview=Review(current,returning,returned);
            if(returnReview.Text("custodyBefore")!="450"||returnReview.Text("custodyAfter")!="350"||returnReview.Text("feeAmount")!="0"||
                returnReview.Text("ownedQuantityChange")!="0"||!returnReview.GetProperty("feeCashUnchanged").GetBoolean())
                throw new Exception("A partial return releases explicit custody quantities while leaving owned stock and existing fee cash unchanged.");
            var wrongHeld=JsonNode.Parse(returned.GetRawText())!.AsObject();wrongHeld["custodyAfter"]="349";Refuse(current,returning,JsonSerializer.SerializeToElement(wrongHeld));
            if(customer)
            {
                var wrongRemainder=JsonNode.Parse(returned.GetRawText())!.AsObject();wrongRemainder["allocations"]![0]!["remainderQuantity"]="351";
                Refuse(current,returning,JsonSerializer.SerializeToElement(wrongRemainder));
            }

            var original=current.GetProperty("returns")[0];
            var acknowledge=Request("gas.park_custody.return",new{expectedVersion=3,businessKey="synthetic_recorded_return",evidenceId=original.Id("evidenceId"),quantity="270",
                effectiveAt="2032-01-16T10:00:00Z",allocations=customer?new[]{new{reservationId=original.GetProperty("allocations")[0].Id("reservationId"),quantity="270"}}:[]});
            var acknowledged=Outcome("return","270","720","450",customer?"720":"0",customer?"450":"0","0",
                [..original.GetProperty("allocations").EnumerateArray().Select(r=>(object)r.Clone())],acknowledge.OperationId,original.Id("id"),false);
            var acknowledgedReview=Review(current,acknowledge,acknowledged);
            if(acknowledgedReview.Text("custodyBefore")!="450"||acknowledgedReview.Text("custodyAfter")!="450"||
                acknowledgedReview.Text("performedQuantity")!="0"||acknowledgedReview.Text("acknowledgedQuantity")!="270"||
                acknowledgedReview.GetProperty("allocations").GetArrayLength()!=0||acknowledgedReview.GetProperty("acknowledgementOutsideSource").GetBoolean())
                throw new Exception("Acknowledging an existing return must show current balances unchanged, without another reservation or performance.");
            var outsideReturnedReview=Review(before,acknowledge,acknowledged);
            if(!outsideReturnedReview.GetProperty("acknowledgementOutsideSource").GetBoolean()||
                outsideReturnedReview.Text("custodyBefore")!="0"||outsideReturnedReview.Text("custodyAfter")!="0"||
                outsideReturnedReview.Text("reservedAfter")!="0"||outsideReturnedReview.Text("performedQuantity")!="0"||
                outsideReturnedReview.Text("acknowledgedQuantity")!="270")
                throw new Exception("A sealed return acknowledgement outside the knowledge cut cannot introduce acceptance, allocations or the original birth balance into that view.");
            var invalidOutsideReturn=JsonNode.Parse(acknowledged.GetRawText())!.AsObject();invalidOutsideReturn["custodyBefore"]="800";invalidOutsideReturn["custodyAfter"]="530";
            Refuse(before,acknowledge,JsonSerializer.SerializeToElement(invalidOutsideReturn));
        }
        Console.WriteLine("PASS custody review effects: independent quantities, retained title, exact release/remainder, fee cash and unchanged acknowledgement");
    }
}
