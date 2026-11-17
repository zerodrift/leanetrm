using System.Collections.Immutable;
using System.Text.Json;
using OpenEtrm.Api;

internal static class GasParkLoanSetupChecks
{
    internal static void Run()
    {
        var trade=Guid.NewGuid();var instrument=Guid.NewGuid();var owned=Guid.NewGuid();var claim=Guid.NewGuid();var lot=Guid.NewGuid();
        var receipt=Guid.NewGuid();var agreement=Guid.NewGuid();var dispatch=Guid.NewGuid();var returned=Guid.NewGuid();var statement=Guid.NewGuid();
        var future=Guid.NewGuid();var fee=Guid.NewGuid();var reservation=Guid.NewGuid();
        var context=new LifecycleReadContext(Guid.NewGuid(),DateTimeOffset.Parse("2041-02-02T12:00:00Z"),new(2041,2,2));
        var target=new LifecycleTarget("gas_park_loan",trade,instrument);
        var definitions=new[]{(owned,"gas.loan.borrow"),(owned,"gas.loan.repay"),(owned,"gas.owned.dispatch"),(claim,"gas.operator.record"),(claim,"gas.operator.settle"),(owned,"gas.cash.record_payment"),(claim,"gas.cash.record_payment"),(owned,"gas.park_loan.fee_determine"),(claim,"gas.park_loan.fee_determine")}
            .Select(pair=>new{account_id=pair.Item1,action=pair.Item2,policy_id=Guid.NewGuid(),policy_revision=2,revision=3}).ToArray();
        var policies=definitions.Select(binding=>JsonSerializer.SerializeToElement(new{id=binding.policy_id,revision=2,action=binding.action,mode="review"})).ToImmutableArray();
        var ownedState=JsonSerializer.SerializeToElement(new{
            accounts=new[]{new{id=owned,code="synthetic_inventory",kind="owned_inventory"}},
            evidence=new[]{
                new{id=receipt,account_id=owned,source_key="synthetic_receipt",revision=1,kind="title_receipt",occurred_at="2041-02-01T12:00:00Z"},
                new{id=agreement,account_id=owned,source_key="synthetic_agreement",revision=1,kind="loan_agreement",occurred_at="2041-02-01T12:00:00Z"},
                new{id=dispatch,account_id=owned,source_key="synthetic_dispatch",revision=1,kind="physical_delivery",occurred_at="2041-02-01T12:00:00Z"},
                new{id=returned,account_id=owned,source_key="synthetic_return",revision=1,kind="loan_return",occurred_at="2041-02-01T12:00:00Z"},
                new{id=future,account_id=owned,source_key="synthetic_receipt",revision=2,kind="title_receipt",occurred_at="2041-02-03T12:00:00Z"}},
            positions=new[]{new{id=lot,account_id=owned,kind="owned_stock",evidence_id=dispatch,effective_at="2041-02-01T12:00:00Z",quantity="720"}},
            lots=new[]{new{id=lot,available_at="2041-02-01T12:00:00Z"}},
            reservations=new[]{new{id=reservation,lot_id=lot,business_key="synthetic_reservation",effective_at="2041-02-01T12:00:00Z"}},releases=Array.Empty<object>()});
        var claimState=JsonSerializer.SerializeToElement(new{
            accounts=new[]{new{id=claim,code="synthetic_claims",kind="operator_balance"}},
            evidence=new[]{new{id=statement,account_id=claim,source_key="synthetic_statement",revision=1,kind="operator_statement",occurred_at="2041-02-01T12:00:00Z"}},
            positions=Array.Empty<object>(),lots=Array.Empty<object>(),reservations=Array.Empty<object>(),releases=Array.Empty<object>()});
        JsonElement Source(int direction)=>JsonSerializer.SerializeToElement(new{implementation="physical_gas_park_loan_v1",tradeId=trade,instrumentId=instrument,
            contextId=context.ContextId,asOf=context.AsOf,stateVersion=7,ownedAccountId=owned,claimAccountId=direction==1?(Guid?)null:claim,
            terms=new{principalDirection=direction},advance=new{feeId=fee},policies=definitions});
        foreach(var direction in new[]{1,-1})
        {
            var setup=GasOwnedSetup.BuildParkLoanSetup(context,target,Source(direction),ownedState,direction==1?null:claimState,policies);
            var advance=setup.Forms["gas.park_loan.advance"].Fields;var repayment=setup.Forms["gas.park_loan.return"].Fields;
            var payment=setup.Forms["gas.park_loan.fee_payment"].Fields;
            if(setup.Context!=context||setup.Target!=target||setup.Forms.Values.Any(form=>form.Permission!="RecordGasOwnedHoldings"))
                throw new Exception("Service forms must retain the exact native source, target and execution authority.");
            foreach(var form in setup.Forms.Values)
            {
                var version=form.Fields.Single(field=>field.Name=="serviceVersion");
                if(version.Members is not ["expectedVersion"]||JsonSerializer.SerializeToElement(version.Choices.Single().Value).GetProperty("expectedVersion").GetInt32()!=7)
                    throw new Exception("Every service action must explicitly select the captured agreement version.");
            }
            var evidence=advance.Single(field=>field.Name=="evidenceId").Choices.Select(choice=>choice.Id).ToHashSet();
            if(!evidence.SetEquals(direction==1?[]:[dispatch.ToString()]))
                throw new Exception("Advance evidence must retain its source kind; a future-dated title head blocks the older receipt.");
            if(!advance.Single(field=>field.Name=="agreementEvidenceId").Choices.Select(choice=>choice.Id).SequenceEqual([agreement.ToString()]))
                throw new Exception("An advance requires an explicit accepted agreement evidence reference.");
            var stock=advance.Single(field=>field.Name=="identifiedStock");
            if(stock.Members is not ["lotId","reservationId"]||stock.Nullable!=(direction==1)||stock.Choices.Length!=(direction==1?0:2))
                throw new Exception("Advance stock must use exact lot/reservation tuples or explicit Not applicable for the receiving party.");
            if(advance.Single(field=>field.Name=="claimEvidenceId").Nullable!=(direction==1)||
                direction==-1&&advance.Single(field=>field.Name=="claimEvidenceId").Choices.Single().Id!=statement.ToString())
                throw new Exception("The principal giver must explicitly select the paired claim-account evidence.");
            if(repayment.Single(field=>field.Name=="evidenceId").Choices.Any(choice=>choice.Id==dispatch.ToString())||
                repayment.Single(field=>field.Name=="identifiedStock").Nullable!=(direction==-1))
                throw new Exception("Return evidence and identified stock must follow the saved native return direction.");
            if(payment.Single(field=>field.Name=="feeId").Choices.Single().Id!=fee.ToString()||
                payment.Single(field=>field.Name=="expectedRevision").Kind!="integer"||payment.Single(field=>field.Name=="amount").Kind!="text")
                throw new Exception("Fee payment must select only its independent owner and preserve manual fact revision and signed amount text.");
            if(setup.Policies.Length!=3||setup.Policies.Any(policy=>policy.AccountId!=trade.ToString()||!setup.Forms.ContainsKey(policy.Action)||policy.Revision!=2))
                throw new Exception("The common selector must retain each exact native policy under the agreement target.");
        }
        foreach(var direction in new[]{1,-1})
        {
            var refund=Guid.NewGuid();var opposite=Guid.NewGuid();var previous=Guid.NewGuid();
            var balance=JsonSerializer.SerializeToElement(new{implementation="physical_gas_park_loan_balance_time_v1",tradeId=trade,instrumentId=instrument,
                contextId=context.ContextId,asOf=context.AsOf,stateVersion=9,ownedAccountId=owned,claimAccountId=direction==1?(Guid?)null:claim,
                terms=new{principalDirection=direction,currency="synthetic_currency"},advance=new{feeId=(Guid?)null},policies=definitions,
                fee=new{unpaid="0",determinations=new[]{
                    new{id=Guid.NewGuid(),feeId=fee,roundedTotal="-2.88",contribution="-2.88",unpaid="0",through="2041-02-01T10:00:00Z"},
                    new{id=Guid.NewGuid(),feeId=refund,roundedTotal="-2.34",contribution="0.54",unpaid="0.54",through="2041-02-01T11:00:00Z"},
                    new{id=previous,feeId=opposite,roundedTotal="-2.88",contribution="-0.54",unpaid="-0.54",through="2041-02-01T12:00:00Z"}}}});
            var setup=GasOwnedSetup.BuildParkLoanSetup(context,target,balance,ownedState,direction==1?null:claimState,policies);
            var determine=setup.Forms["gas.park_loan.fee_determine"].Fields;
            var prior=determine.Single(f=>f.Name=="previousDetermination");
            if(prior.Nullable||prior.Members is not ["previousDeterminationId"]||JsonSerializer.SerializeToElement(prior.Choices.Single().Value).Id("previousDeterminationId")!=previous||
                determine.Any(field=>field.Name=="amount")||setup.Policies.Single(p=>p.Action=="gas.park_loan.fee_determine").Revision!=2)
                throw new Exception("Fee determination must select its exact previous cumulative head and distinct action policy without a trusted amount.");
            if(prior.Choices.Single().Label!="Rounded cumulative fee -2.88 synthetic_currency")
                throw new Exception("The previous determination choice must name its retained rounded cumulative total and exact currency, not its contribution or raw timestamp.");
            var fees=setup.Forms["gas.park_loan.fee_payment"].Fields.Single(f=>f.Name=="feeId").Choices.Select(c=>c.Id).ToHashSet();
            if(!fees.SetEquals([refund.ToString(),opposite.ToString()]))
                throw new Exception("Independent refund and charge owners remain selectable when aggregate unpaid cash nets to zero; paid owners stay excluded.");
        }
        Console.WriteLine("PASS Park and Loan typed lifecycle form references, explicit null choices, source heads and independent fee payments");
    }
}
