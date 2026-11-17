using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

internal sealed record GasParkLoanPortfolioScope(Guid Catalog,Guid Context,DateTimeOffset At,DateTimeOffset KnownAt,
    DateOnly BusinessDate,ImmutableArray<JsonElement> Agreements,GasOwnedSourceReadAdmission Admission);

// Native gas replaces service principal at accepted advance. Fixed cash or
// determined balance fees have independent native cash owners; the balance-fee
// service retains only its still-undetermined fee under the selected plan.
internal static class GasParkLoanPortfolio
{
    internal static GasParkAndLoanTerms Terms(Guid catalog,JsonElement agreement)
    {
        var balance=agreement.Text("implementation")==GasParkLoanBalanceFeeTerms.Version;
        if(catalog==Guid.Empty||agreement.Text("implementation") is not(GasParkAndLoanTerms.Version or GasParkLoanBalanceFeeTerms.Version))
            throw Invalid("Retain the exact installed service profile and native catalog.");
        if(!agreement.TryGetProperty("reconciliationRequired",out var reconciliation)||reconciliation.ValueKind is not(JsonValueKind.True or JsonValueKind.False))
            throw Invalid("Retain the native agreement's explicit reconciliation status at the selected knowledge cutoff.");
        if(reconciliation.GetBoolean())
            throw new MarketDataException("gas_park_loan_reconciliation","Accepted service performance references corrected evidence. Reconcile the native facts before a complete scheduled-plan valuation.");
        var t=agreement.GetProperty("terms");
        if(!t.TryGetProperty("feeRecognition",out var recognition)||recognition.ValueKind!=JsonValueKind.String||
            recognition.GetString()!=(balance?GasParkLoanBalanceFeeTerms.CumulativeDifference:GasParkAndLoanTerms.FeeAtAdvance))
            throw Invalid("Retain the selected profile's explicit fee determination law.");
        var reference=new ReferenceRevision(t.Id("referenceCatalogId"),t.Id("referenceRevisionId"));
        var definition=new DefinitionRevision(catalog,t.Id("definitionRevisionId"));
        var fee=t.Number("fixedFeeAmount");
        if(balance&&fee!=default)throw Invalid("The selected balance-time profile has its own fee law and cannot also carry a fixed fee.");
        var direction=t.Text("feeDirection") switch{"pay"=>ExactDecimal.Parse("-1"),"receive"=>ExactDecimal.Parse("1"),
            _=>throw Invalid("Retain the independently selected fee direction.")};
        if(fee!=default&&fee.CompareTo(default)!=direction.CompareTo(default))
            throw Invalid("The native signed fee conflicts with its saved pay or receive direction.");
        var terms=new GasParkAndLoanTerms(GasParkAndLoanTerms.Version,t.Text("serviceKind"),t.Text("perspective"),
            t.Text("titlePolicy"),t.Text("returnPolicy"),new(reference,t.Text("location")),new(reference,t.Text("quantity")),
            new(reference,t.Text("currency")),new(new(definition,t.Text("markCurve")),new(definition,t.Id("markContractId"))),
            t.Instant("advanceAt"),t.Instant("returnAt"),t.Text("carrying")=="nominal_mark_v1"?GasHoldingCarrying.Nominal:
                throw Invalid("The installed service profile requires its explicit nominal principal carrying law."),
            new(fee.CompareTo(default)<0?-fee:fee,new(reference,t.Text("currency"))),direction,
            balance?null:t.OptionalText("feeDueAt") is {} due?TradingInstant.Parse(due,"gas_park_loan_context"):null,t.Text("returnTiming"));
        if(Integer(t,"principalDirection")!=terms.AdvanceDirection.CompareTo(default))
            throw Invalid("The saved principal direction conflicts with the explicit service and commercial perspective.");
        if(balance)_=GasParkLoanBalanceFeeAdapter.FeeTerms(terms,agreement);
        return terms;
    }

    internal static async Task<GasParkLoanPortfolioScope> Scope(NpgsqlConnection connection,PreviewDatabase database,
        InputFrame frame,MarketSnapshot snapshot,ImmutableArray<Guid> books,CancellationToken ct,ImmutableArray<Guid>? selectedTrades=null)
    {
        RequireSource(frame,snapshot);
        if(books.IsDefault||books.Any(id=>id==Guid.Empty)||books.Distinct().Count()!=books.Length||
            selectedTrades is {} selected&&(selected.IsDefault||selected.Any(id=>id==Guid.Empty)||selected.Distinct().Count()!=selected.Length))
            throw Invalid("Select an explicit distinct book scope and exact optional trade scope.");
        var catalog=database.Manifest.CatalogId;var context=frame.ContextId!.Value;
        await using var capture=ScheduleStore.Command(connection,"SELECT security.gas_park_loan_authority($1,false)",catalog);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        var directory=books.IsEmpty||selectedTrades is {} empty&&empty.IsEmpty?JsonSerializer.SerializeToElement(Array.Empty<object>()):
            await LifecycleNative.Read(connection,"SELECT security.gas_park_loan_directory($1,$2,$3,$4::uuid[],$5::uuid[],$6::uuid[])::text",ct,catalog,context,frame.At,books.ToArray(),Array.Empty<Guid>(),selectedTrades?.ToArray()??Array.Empty<Guid>());
        if(directory.ValueKind!=JsonValueKind.Array)throw Invalid("Read the complete native service directory at this exact source.");
        var all=directory.EnumerateArray().ToImmutableArray();
        if(all.Select(a=>a.Id("tradeId")).Distinct().Count()!=all.Length)throw Invalid("Each booked agreement must occur once in the selected native service directory.");
        foreach(var agreement in all)RequireAgreement(agreement,frame,snapshot,false);
        var agreements=all.Where(a=>books.Contains(a.Id("bookId"))&&(selectedTrades is null||selectedTrades.Value.Contains(a.Id("tradeId")))).ToImmutableArray();
        foreach(var agreement in agreements)
        {
            var nativeUntil=agreement.Instant("authorityUntil");
            if(nativeUntil<until)until=nativeUntil;
        }
        var admission=new GasOwnedSourceReadAdmission(catalog,context,frame.At,until);
        database.RegisterCommitAdmission(connection,admission);
        await admission.RequireCommit(connection,ct);
        return new(catalog,context,frame.At,snapshot.Knowledge!.KnownAt,frame.BusinessDate,agreements,admission);
    }

    internal static async Task<ImmutableArray<GasOwnedPortfolioInput>> Prepare(NpgsqlConnection connection,PreviewDatabase database,
        GasParkLoanPortfolioScope scope,InputFrame frame,MarketSnapshot snapshot,ImmutableArray<(PreviewData Data,InputFrame Frame)> supplied,
        bool requirePinnedSources,CancellationToken ct)
    {
        RequireSource(frame,snapshot);
        if(scope.Catalog!=database.Manifest.CatalogId||scope.Context!=frame.ContextId||scope.At!=frame.At||
            scope.KnownAt!=snapshot.Knowledge!.KnownAt||scope.BusinessDate!=frame.BusinessDate)
            throw Invalid("The captured service scope cannot be reused under another business or knowledge context.");
        var result=await PrepareAgreements(connection,database,frame,snapshot,scope.Agreements,supplied,requirePinnedSources,false,ct);
        await scope.Admission.RequireCommit(connection,ct);return result;
    }

    // The caller supplies either a certified native read or a reviewed booking
    // draft with no accepted effects. Authorization remains the caller's native
    // admission; this adapter never creates a financial fact or paid allocation.
    internal static async Task<ImmutableArray<GasOwnedPortfolioInput>> PrepareAgreements(NpgsqlConnection connection,PreviewDatabase database,
        InputFrame frame,MarketSnapshot snapshot,ImmutableArray<JsonElement> agreements,ImmutableArray<(PreviewData Data,InputFrame Frame)> supplied,
        bool requirePinnedSources,bool draft,CancellationToken ct)
    {
        RequireSource(frame,snapshot);
        if(agreements.IsDefault||supplied.IsDefault||agreements.Select(a=>a.Id("tradeId")).Distinct().Count()!=agreements.Length)
            throw Invalid("Supply the exact distinct agreements and explicitly selected market input sources.");
        var catalog=database.Manifest.CatalogId;var context=frame.ContextId!.Value;
        var remaining=new List<(JsonElement Agreement,GasParkAndLoanTerms Terms,ImmutableArray<GasParkAndLoanComponent> Components)>();
        foreach(var agreement in agreements)
        {
            RequireAgreement(agreement,frame,snapshot,draft);var terms=Terms(catalog,agreement);
            var quantity=new Quantity(agreement.Number("quantity"),terms.Quantity);var balances=agreement.GetProperty("balances");
            var advanced=new Quantity(balances.Number("acceptedPrincipal"),terms.Quantity);var returned=new Quantity(balances.Number("acceptedReturn"),terms.Quantity);
            _=GasParkAndLoanPrincipal.Plan(terms,quantity,advanced,returned,frame.At);
            var components=agreement.Text("implementation")==GasParkLoanBalanceFeeTerms.Version?
                BalanceComponents(terms,agreement):GasParkAndLoanValuation.Components(terms,quantity,advanced,frame.At);
            if(!components.IsEmpty)remaining.Add((agreement,terms,components));
        }
        var result=ImmutableArray.CreateBuilder<GasOwnedPortfolioInput>();
        foreach(var group in remaining.GroupBy(a=>a.Terms.Mark.Curve.Revision.RevisionId).OrderBy(g=>g.Key))
        {
            var sources=group.Select(a=>new CatalogueSource(group.Key,a.Terms.Quantity.Revision.CatalogId,a.Terms.Quantity.Revision.RevisionId)).Distinct().ToArray();
            if(sources.Length!=1)throw Invalid("Each service mark source requires one exact definition and reference bundle.");
            var source=await MarketInputSelection.Resolve(database,connection,frame,sources[0],supplied,requirePinnedSources,ct);
            var selected=source.Data.Snapshot(source.Frame).WithKnowledge(context,snapshot.Knowledge!.KnownAt);
            var trades=ImmutableArray.CreateBuilder<Trade>();var states=ImmutableDictionary.CreateBuilder<MeasureBinding,IBindingValuationState>();
            var evidence=ImmutableArray.CreateBuilder<PortfolioPositionEvidence>();var payments=ImmutableArray.CreateBuilder<SidePaymentKey>();
            foreach(var entry in group)
            {
                var agreement=entry.Agreement;var trade=agreement.Id("tradeId");var book=agreement.Id("bookId");var service=agreement.Id("instrumentId");
                var bindings=entry.Components.Select(c=>new TradeInstrument(GasParkAndLoanValuation.ComponentBinding(service,c.Role),c.Instrument,c.Quantity)).ToImmutableArray();
                trades.Add(new(trade,book,bindings));
                foreach(var component in entry.Components)
                {
                    var binding=GasParkAndLoanValuation.ComponentBinding(service,component.Role);
                    if(component.Instrument is TimedCashPayment cash)
                    {
                        var position=new AccountingPositionKey(trade,binding,book);
                        states.Add(new(trade,binding),new RecordedCashValuation(position,cash,component.Quantity,[],RecordedCashValuation.Policy,context,selected.Knowledge!.KnownAt));
                        if(!cash.Due(selected.ValuationAt))payments.Add(cash.Payment);
                    }
                    evidence.Add(new(trade,Integer(agreement,"revision"),binding,service,component.Instrument.Id,book,GasParkLoanTicket.Measure,group.Key,
                        Integer(agreement,"publicationOrder"),component.Quantity){InstrumentType=GasParkLoanTicket.Product,
                        TradeReference=draft?"":TradingReference.Trade(agreement.GetProperty("tradeReference").GetInt32()),
                        InstrumentReference=draft?"":TradingReference.Instrument(agreement,"instrumentReference")});
                }
            }
            var providers=ImmutableDictionary.CreateBuilder<Guid,ISideDiscounting>();var discounts=ImmutableArray.CreateBuilder<PortfolioInstantDiscountEvidence>();
            foreach(var currency in payments.Distinct().GroupBy(p=>p.Currency))
            {
                var mapping=await SwapStore.CurrencyDefault(connection,catalog,source.Data,source.Frame,currency.Key,ct)??
                    throw new MarketDataException("discount_curve","The independent service fee requires its captured currency discount mapping.");
                var provider=new InstantCurrencyDiscounting(currency.ToImmutableArray(),selected,mapping,RateDiscountTiming.Version);
                foreach(var id in currency.Select(p=>p.SideId).Distinct())providers.Add(id,provider);
                discounts.AddRange(provider.Inputs.Select(p=>new PortfolioInstantDiscountEvidence(p.Key.SideId,p.Value)));
            }
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null){BindingStates=states.ToImmutable(),InstrumentDiscounting=providers.ToImmutable()};
            result.Add(new(source.Data,source.Frame,selected,ValuationPortfolio.FromTrades(trades.ToImmutable(),setup),evidence.ToImmutable(),discounts.ToImmutable()){Measure=GasParkLoanTicket.Measure});
        }
        return result.ToImmutable();
    }

    private static void RequireSource(InputFrame frame,MarketSnapshot snapshot)
    {
        if(frame.ContextId is null||snapshot.Knowledge?.ContextId!=frame.ContextId||snapshot.ValuationAt!=frame.At)
            throw Invalid("Select an exact certified service source with explicit business and knowledge coordinates.");
    }
    private static ImmutableArray<GasParkAndLoanComponent> BalanceComponents(GasParkAndLoanTerms principal,JsonElement agreement)
    {
        var state=GasParkLoanBalanceFeeAdapter.Read(principal,agreement);
        return GasParkLoanBalanceFeeValuation.Components(principal,state.Terms,state.Source,state.CumulativeDeterminedTotal);
    }
    private static void RequireAgreement(JsonElement agreement,InputFrame frame,MarketSnapshot snapshot,bool draft)
    {
        if(agreement.Id("contextId")!=frame.ContextId||agreement.Instant("asOf")!=frame.At||
            agreement.Instant("knownAt")!=snapshot.Knowledge!.KnownAt||DateWorkspace.Date(agreement.Text("effectiveOn"))!=frame.BusinessDate||
            agreement.Id("tradeId")==Guid.Empty||agreement.Id("instrumentId")==Guid.Empty||agreement.Id("bookId")==Guid.Empty||Integer(agreement,"revision")!=1||
            (draft?Integer(agreement,"publicationOrder")!=0:Integer(agreement,"publicationOrder")<1)||agreement.Instant("effectiveAt")>frame.At||
            draft&&agreement.GetProperty("advance").ValueKind!=JsonValueKind.Null)
            throw Invalid("Retain the agreement's exact native owner, revision, source context and explicit draft or accepted state.");
    }
    private static int Integer(JsonElement row,string key)=>int.Parse(row.Text(key),CultureInfo.InvariantCulture);
    private static MarketDataException Invalid(string message)=>new("gas_park_loan_context",message);
}
