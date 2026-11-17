using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;

internal static class GasParkLoanBalanceFeeAdapterChecks
{
    internal static void Run()
    {
        var catalog=Guid.NewGuid();var trade=Guid.NewGuid();var reference=Guid.NewGuid();var revision=Guid.NewGuid();
        var agreement=JsonSerializer.SerializeToElement(new{
            implementation=GasParkLoanBalanceFeeTerms.Version,reconciliationRequired=false,tradeId=trade,quantity="720",
            asOf="2032-03-14T11:00:00Z",knownAt="2032-03-16T00:00:00Z",
            terms=new{serviceKind="loan",perspective="customer",principalDirection=1,definitionRevisionId=Guid.NewGuid(),
                referenceCatalogId=reference,referenceRevisionId=revision,quantity="synthetic_quantity",location="synthetic_point",currency="synthetic_currency",
                markCurve="synthetic_mark",markContractId=Guid.NewGuid(),advanceAt="2032-03-14T10:00:00Z",returnAt="2032-03-14T12:00:00Z",
                fixedFeeAmount="0",feeDirection="pay",feeDueAt="2032-03-15T16:00:00Z",feeRecognition=GasParkLoanBalanceFeeTerms.CumulativeDifference,
                titlePolicy=GasCommodityLoan.TitleTransfer,returnPolicy=GasParkAndLoanTerms.EqualQuantity,
                returnTiming=GasParkAndLoanTerms.ReturnsAfterAdvance,carrying="nominal_mark_v1",
                balanceFee=new{rateAmount="0.004",basis=GasParkLoanBalanceFeeTerms.AcceptedOutstanding,timeBasis=GasParkLoanBalanceFeeTerms.ElapsedHour,
                    startPolicy=GasParkLoanBalanceFeeTerms.AcceptedAdvance,endPolicy=GasParkLoanBalanceFeeTerms.ContractualCutoff,
                    overduePolicy=GasParkLoanBalanceFeeTerms.AccrueOverdue,projectionPolicy=GasParkLoanBalanceFeeTerms.ReturnAtCutoff,
                    unadvancedPolicy=GasParkLoanBalanceFeeTerms.UnadvancedAtEvaluation,determinationPolicy=GasParkLoanBalanceFeeTerms.CumulativeDifference,
                    accrualThroughAt="2032-03-14T13:00:00Z",roundingIncrement="0.01",roundingTieRule="to_even",roundingStage="payment_total"}},
            advance=new{id=Guid.NewGuid(),effectiveAt="2032-03-14T10:00:00Z",knownAt="2032-03-15T00:00:00Z"},
            returns=new[]{new{id=Guid.NewGuid(),quantity="270",effectiveAt="2032-03-14T11:00:00Z",knownAt="2032-03-15T00:00:00Z"}},
            balances=new{acceptedPrincipal="720",acceptedReturn="270",outstandingPrincipal="450"},
            fee=new{cumulativeDeterminedTotal="-2.88",amount="-2.88",unpaid="-2.88",dueAt="2032-03-15T16:00:00Z",
                determinations=new[]{new{id=Guid.NewGuid(),through="2032-03-14T11:00:00Z",effectiveAt="2032-03-14T11:00:00Z",roundedTotal="-2.88",contribution="-2.88",feeId=Guid.NewGuid(),knownAt="2032-03-15T00:00:00Z"}}}
        });
        var principal=GasParkLoanPortfolio.Terms(catalog,agreement);
        var state=GasParkLoanBalanceFeeAdapter.Read(principal,agreement);
        if(state.Source.AgreementId!=trade||state.Source.Returns.Length!=1||state.CumulativeDeterminedTotal.Amount!=ExactDecimal.Parse("-2.88")||
            GasParkLoanBalanceFee.Plan(state.Terms,state.Source,state.CumulativeDeterminedTotal).RemainingFee.Amount!=ExactDecimal.Parse("-3.6"))
            throw new Exception("The managed consumer must retain exact accepted events and cumulative determined cash before composing remaining fee.");
        void Reject(Action<JsonObject> change)
        {
            var altered=JsonNode.Parse(agreement.GetRawText())!.AsObject();change(altered);
            try{var p=GasParkLoanPortfolio.Terms(catalog,JsonSerializer.SerializeToElement(altered));GasParkLoanBalanceFeeAdapter.Read(p,JsonSerializer.SerializeToElement(altered));}
            catch(MarketDataException e)when(e.Code is "gas_park_loan_context" or "gas_balance_fee_source" or "gas_balance_fee_terms" or "gas_balance_fee_checkpoint"){return;}
            throw new Exception("A malformed, inconsistent or later source must not become successful remaining fee cash.");
        }
        Reject(x=>x["terms"]!["fixedFeeAmount"]="12");
        Reject(x=>x["terms"]!.AsObject().Remove("balanceFee"));
        Reject(x=>x["terms"]!["balanceFee"]!["timeBasis"]="calendar_day");
        Reject(x=>x["advance"]!.AsObject().Remove("knownAt"));
        Reject(x=>x["returns"]![0]!["knownAt"]="2032-03-17T00:00:00Z");
        Reject(x=>x["balances"]!["acceptedReturn"]="269");
        Reject(x=>x["fee"]!["cumulativeDeterminedTotal"]="-2");
        Reject(x=>x["fee"]!["determinations"]![0]!["contribution"]="-2");
        Reject(x=>x["fee"]!.AsObject().Remove("determinations"));
        Reject(x=>x["fee"]!["determinations"]![0]!["knownAt"]="2032-03-17T00:00:00Z");
        Reject(x=>x["fee"]!["determinations"]![0]!["effectiveAt"]="2032-03-14T12:00:00Z");
        Reject(x=>x["fee"]!["determinations"]![0]!.AsObject().Remove("effectiveAt"));
        Reject(x=>x["asOf"]="2032-03-14T11:00:00.0000001Z");
        var old=JsonNode.Parse(agreement.GetRawText())!.AsObject();
        old["returns"]=new JsonArray();old["balances"]!["acceptedReturn"]="0";old["balances"]!["outstandingPrincipal"]="720";
        var oldState=GasParkLoanBalanceFeeAdapter.Read(principal,JsonSerializer.SerializeToElement(old));
        if(GasParkLoanBalanceFee.Plan(oldState.Terms,oldState.Source,oldState.CumulativeDeterminedTotal).RemainingFee.Amount!=ExactDecimal.Parse("-5.76"))
            throw new Exception("A retained pre-return prefix must not inherit a later accepted partial return.");
        var before=old.DeepClone().AsObject();before["asOf"]="2032-03-14T10:30:00Z";
        before["fee"]!["determinations"]=new JsonArray();before["fee"]!["cumulativeDeterminedTotal"]="0";before["fee"]!["amount"]="0";
        var pre=GasParkLoanBalanceFeeAdapter.Read(principal,JsonSerializer.SerializeToElement(before));
        if(GasParkLoanBalanceFee.Plan(pre.Terms,pre.Source,pre.CumulativeDeterminedTotal).RemainingFee.Amount!=ExactDecimal.Parse("-8.64"))
            throw new Exception("A source before fee determination must retain service fee until independent cash actually exists.");
        var request=new LifecycleActionRequest(Guid.NewGuid(),new(Guid.NewGuid(),state.Source.At,new(2032,3,14)),
            new(GasParkLoanActions.Handler,trade,Guid.NewGuid()),GasParkLoanActions.Determine,Guid.NewGuid(),1,false,new('0',64),
            JsonSerializer.SerializeToElement(new{expectedVersion=1,previousDeterminationId=state.Checkpoint!.Id,
                through="2032-03-14T11:00:00Z",effectiveAt="2032-03-14T11:00:00Z",businessKey="synthetic_fee",evidence="Synthetic determination"}));
        var outcome=JsonSerializer.SerializeToElement(new{exactNumerator="-72",exactDenominator="25",roundedTotal="-2.88",contribution="0"});
        var review=JsonSerializer.SerializeToElement(GasParkLoanActions.Effects(catalog,agreement,request,outcome),GasOwnedOperations.Json);
        if(!review.TryGetProperty("previousCumulativeRoundedFee",out var previous)||previous.GetString()!="-2.88"||
            review.Text("accruedThrough")!="2032-03-14T11:00:00Z"||review.Text("cumulativeRoundedFee")!="-2.88"||review.Text("independentCash")!="0"||
            review.GetProperty("dueAt").GetDateTimeOffset()!=DateTimeOffset.Parse("2032-03-15T16:00:00Z"))
            throw new Exception("A fee review must state its selected through time, previous and new rounded cumulative totals, signed contribution and exact due time.");
        var later=JsonNode.Parse(agreement.GetRawText())!.AsObject();later["asOf"]="2032-03-14T12:00:00Z";
        var laterRequest=request with{Selection=JsonSerializer.SerializeToElement(new{expectedVersion=1,previousDeterminationId=state.Checkpoint!.Id,
            through="2032-03-14T12:00:00Z",effectiveAt="2032-03-14T12:00:00Z",businessKey="synthetic_later_fee",evidence="Synthetic later determination"})};
        var laterReview=JsonSerializer.SerializeToElement(GasParkLoanActions.Effects(catalog,JsonSerializer.SerializeToElement(later),laterRequest,
            JsonSerializer.SerializeToElement(new{exactNumerator="-117",exactDenominator="25",roundedTotal="-4.68",contribution="-1.8"})),GasOwnedOperations.Json);
        if(laterReview.Text("previousCumulativeRoundedFee")!="-2.88"||laterReview.Text("cumulativeRoundedFee")!="-4.68"||laterReview.Text("independentCash")!="-1.8")
            throw new Exception("Review the new signed fee contribution separately from both cumulative amounts.");
        try
        {
            GasParkLoanActions.Effects(catalog,agreement,request,JsonSerializer.SerializeToElement(new{exactNumerator="-72",exactDenominator="25",roundedTotal="-2.88",contribution="999"}));
            throw new Exception("A fee review must independently compare native and managed exact cumulative cash.");
        }
        catch(MarketDataException error)when(error.Code=="gas_park_loan_context"){}
        var context=Guid.NewGuid();var frame=new InputFrame(Guid.NewGuid(),"Synthetic fee preview",state.Source.PlannedAdvanceAt.AddHours(-1),new(2032,3,14),[],ImmutableDictionary<string,ImmutableArray<Guid>>.Empty,context);
        var b=new GasParkLoanBooking(Guid.NewGuid(),frame.Id,context,principal.Mark.Curve.Revision.RevisionId,PhysicalGasStore.Convention,"current",GasParkLoanBalanceFeeTerms.Version,
            Guid.NewGuid(),Guid.NewGuid(),1,Guid.NewGuid(),1,Guid.NewGuid(),null,"loan","customer","720",principal.AdvanceAt,principal.ReturnAt,
            principal.TitlePolicy,principal.ReturnRule,principal.ReturnTiming,"nominal_mark_v1","0","pay",state.Terms.DueAt,
            agreement.GetProperty("terms").GetProperty("balanceFee").Deserialize<GasParkLoanBalanceFeeBooking>(GasOwnedOperations.Json));
        var account=JsonSerializer.SerializeToElement(new{reference_catalog_id=reference,reference_revision_id=revision,quantity_code="synthetic_quantity",location_code="synthetic_point",currency_code="synthetic_currency",mark_curve_code=principal.Mark.Curve.Code,mark_contract_id=principal.Mark.Contract.Id});
        var snapshot=new MarketSnapshot(frame.Id,principal.Mark.Curve.Revision,frame.At,frame.At,InputSelectionPolicy.ExactPinnedFinalObservationsNoAgeFiltering,[]).WithKnowledge(context,state.Source.KnownAt);
        var draft=GasParkLoanTicketStore.Draft(b,account,frame,snapshot);
        var draftState=GasParkLoanBalanceFeeAdapter.Read(GasParkLoanPortfolio.Terms(catalog,draft),draft);
        if(draftState.Source.Advance is not null||!draftState.Source.Returns.IsEmpty||draftState.CumulativeDeterminedTotal.Amount!=default||
            GasParkLoanBalanceFee.Plan(draftState.Terms,draftState.Source,draftState.CumulativeDeterminedTotal).RemainingFee.Amount!=ExactDecimal.Parse("-8.64"))
            throw new Exception("The ordinary booking preview must materialize the selected fee law without inventing an accepted advance or determined cash.");
        Console.WriteLine("PASS balance fee adapter: explicit law, exact accepted-event source, historical determination/cash handoff and strict malformed/source refusals");
    }
}
