using System.Collections.Immutable;
using OpenEtrm.Api;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

internal static class GasLoanFeePvChecks
{
    internal static async Task Verify(PreviewDatabase database,Guid accountId,Guid loanId,Guid markId,DateTimeOffset at,int revision,int grossCount,
        string nominal,string unpaid,string expectedPv,CancellationToken ct=default)
    {
        await database.WithIdentity("trader","ReadGasOwnedHoldings",async(connection,token)=>
        {
            var catalog=database.Manifest.CatalogId;var day=DateOnly.FromDateTime(at.UtcDateTime);
            await using var capture=ScheduleStore.Command(connection,"SELECT security.capture_date_context($1,$2,NULL)",catalog,day);
            var contextId=(Guid)(await capture.ExecuteScalarAsync(token))!;
            var knowledge=(await new KnowledgeStore(catalog).Read(connection,contextId,token)).Context;
            await PinnedContext.BindExact(connection,catalog,knowledge,token);
            await TradingContext.Bind(connection,catalog,knowledge,at,token);
            var context=new LifecycleReadContext(contextId,at,day);
            var source=await GasLoanFeeActions.ReadSource(connection,database,context,loanId,token);
            if(source.Id("accountId")!=accountId||source.GetProperty("terms").GetProperty("revision").GetInt32()!=revision)
                throw new Exception("Fee PV requires the exact amended loan and native terms revision.");
            var native=await GasOwnedOperations.Read(connection,database,context,accountId,token);
            var account=native.GetProperty("accounts").EnumerateArray().Single(p=>p.Id("id")==accountId);
            var owner=source.Id("positionId");var position=native.GetProperty("positions").EnumerateArray().Single(p=>p.Id("id")==owner);
            if(account.Id("definition_revision_id")!=database.Manifest.RevisionId)
                throw new Exception("The synthetic fee PV fixture requires its explicitly selected original market source.");
            var frame=new InputFrame(Guid.NewGuid(),"Synthetic exact native loan fee cash",at,day,[markId],
                ImmutableDictionary<string,ImmutableArray<Guid>>.Empty.Add(position.Text("mark_curve_code"),[position.Id("mark_contract_id")]),contextId);
            var data=await MarketInputSelection.Root(database,connection,frame,token);
            var snapshot=data.Snapshot(frame).WithKnowledge(contextId,SchedulePreview.ParseAt(knowledge.KnownAt));
            var held=await GasOwnedHoldings.Read(connection,database,frame,snapshot,[account.Id("book_id")],token,[(data,frame)],true);
            var nativeCash=await LifecycleNative.Read(connection,"SELECT security.read_gas_owned_holdings($1,$2,$3,$4::uuid[])::text",token,
                catalog,contextId,at,new[]{account.Id("book_id")});
            database.RegisterCommitAdmission(connection,new GasOwnedSourceReadAdmission(catalog,contextId,at,nativeCash.Instant("authorityUntil")));
            if(nativeCash.Id("contextId")!=contextId||nativeCash.Instant("asOf")!=at||nativeCash.Instant("knownAt")!=snapshot.Knowledge!.KnownAt)
                throw new Exception("Fee bank evidence must retain the exact original source and knowledge cutoff.");
            var cash=held.Fees.Where(p=>p.LoanId==loanId).ToImmutableArray();
            var rows=source.GetProperty("cash").EnumerateArray().ToArray();var ids=rows.Select(p=>p.Id("id")).ToHashSet();
            if(cash.IsEmpty||cash.Length!=grossCount||cash.Length!=rows.Length||!cash.Select(p=>p.Position.BindingId).ToHashSet().SetEquals(ids)||
                cash.Any(p=>p.AccountId!=accountId||p.Position.Owner!=new AccountingOwnerKey(AccountingOwnerKind.NativeHolding,owner)||
                    p.Position.BookId!=account.Id("book_id")||p.State.Position!=p.Position||p.State.SourceContextId!=contextId||p.State.KnownThrough!=snapshot.Knowledge!.KnownAt))
                throw new Exception("Fee PV lost an actual gross cash membership, exact owner/binding or recorded bank-state source.");
            foreach(var item in cash)
            {
                var row=rows.Single(p=>p.Id("id")==item.Position.BindingId);
                if(item.Instrument.PaymentAt!=row.Instant("due_at")||item.Quantity.Amount!=row.Number("amount"))
                    throw new Exception("Ordinary fee holdings changed an actual amount or contractual cash date.");
            }
            var payments=source.GetProperty("payments").EnumerateArray().ToArray();
            var evidence=nativeCash.GetProperty("payments").EnumerateArray().ToDictionary(p=>p.Id("id"));
            var expected=payments.Select(row=>
            {
                var fee=row.Id("fee_id");var item=cash.Single(p=>p.Position.BindingId==fee);
                var first=payments.Where(p=>p.Id("fee_id")==fee&&p.Text("business_key")==row.Text("business_key"))
                    .MinBy(p=>p.GetProperty("revision").GetInt32());
                if(!evidence.TryGetValue(row.Id("id"),out var nativePayment)||nativePayment.Id("fee_id")!=fee||
                    nativePayment.Id("allocation_id")!=first.Id("id")||nativePayment.GetProperty("revision").GetInt32()!=row.GetProperty("revision").GetInt32()||
                    nativePayment.Instant("paid_at")!=row.Instant("paid_at")||
                    ExactMoney.Parse(nativePayment.Text("amount"),item.Instrument.Currency)!=ExactMoney.Parse(row.Text("amount"),item.Instrument.Currency))
                    throw new Exception("Fee bank evidence lost its exact native payment row and first-revision allocation identity.");
                return new AccountingFlow(first.Id("id"),row.GetProperty("revision").GetInt32(),item.Position,row.Instant("paid_at"),
                    nativePayment.Instant("known_at"),ExactMoney.Parse(row.Text("amount"),item.Instrument.Currency),AccountingFlowKind.EconomicCash);
            }).ToImmutableArray();
            var actual=cash.SelectMany(p=>p.State.Allocations).ToImmutableArray();
            if(actual.Length!=expected.Length||!actual.ToHashSet().SetEquals(expected))
                throw new Exception("Ordinary fee cash must retain the complete exact native allocation identity, revision, owner, effective/known times, amount and flow kind.");
            var currency=cash[0].Instrument.Currency;
            var total=cash.Select(p=>ExactMoney.FromMoney(new(p.Quantity.Amount,currency))).Aggregate((a,b)=>a.Add(b));
            var remaining=cash.Select(p=>p.State.Outstanding(snapshot)).Aggregate((a,b)=>a.Add(b));
            var paid=cash.Select(p=>p.State.Paid(snapshot)).Aggregate((a,b)=>a.Add(b));
            if(total!=ExactMoney.Parse(nominal,currency)||remaining!=ExactMoney.Parse(unpaid,currency)||paid!=ExactMoney.Parse("-0.5",currency)||
                source.Text("currentPaid")!="-0.5")
                throw new Exception("Loan fee PV must retain the independently stated gross contract, unpaid balance and original bank payment.");
            var states=cash.ToImmutableDictionary(p=>new MeasureBinding(p.Position.Owner,p.Position.BindingId),p=>(IBindingValuationState)p.State);
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,
                new ExplicitDiscounting(ExactDecimal.Parse("0.95"),DiscountFactorSource.Explicit)){BindingStates=states};
            var portfolio=new ValuationPortfolio([..cash.Select(p=>(Instrument)p.Instrument).DistinctBy(p=>p.Id)],
                [..cash.Select(p=>new RiskPosition(p.Position.Owner,p.Position.BindingId,p.Position.BookId,p.Instrument.Id,p.Quantity,
                    new(new Money(default,currency),currency),new(default,currency)))],setup);
            var value=RiskEngine.CalculateExact(RiskEngine.Prepare(data.Model,snapshot,[portfolio]),[],RiskEngine.ExactNumericalPolicy)
                .Groups.Single().BaseValues.Single().Value;
            if(value!=ExactMoney.Parse(expectedPv,currency))
                throw new Exception($"Expected exact native amended-fee PV {expectedPv}; got {value.ToExactString()}.");
            Console.WriteLine($"PASS native loan fee cash PV: revision {revision}, all {cash.Length} exact gross memberships, original bank -0.5, unpaid {unpaid}, selected DF 0.95, PV {expectedPv}");
            return true;
        },ct);
    }
}
