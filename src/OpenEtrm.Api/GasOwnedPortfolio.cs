using System.Text.Json.Serialization;
using System.Collections.Immutable;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;
using OpenEtrm.Rates;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasOwnedValuationPolicy(string Implementation,string Scope,string CashDiscounting,string TimingPolicy)
{
    public const string Version="gas_owned_mark_to_market_v1",WholeBooks="all_independent_owners_in_selected_books_v1",
        CurrencyDefault="captured_currency_default_v1",ProfileVersion="gas_owned_mark_to_market_v2",
        SavedPrincipal="saved_delivery_profile_principal_carrying_v1";
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public string? PrincipalCarrying{get;init;}
    public void Validate()
    {
        if(!((Implementation==Version&&PrincipalCarrying is null)||(Implementation==ProfileVersion&&PrincipalCarrying==SavedPrincipal))||
            Scope!=WholeBooks||CashDiscounting!=CurrencyDefault||TimingPolicy!=RateDiscountTiming.Version)
            throw new MarketDataException("gas_owned_policy","Select whole-book independent gas ownership, captured currency discount mapping and exact-instant timing explicitly.");
    }
}

internal sealed record GasOwnedPortfolioInput(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot,ValuationPortfolio Portfolio,
    ImmutableArray<PortfolioPositionEvidence> Evidence,ImmutableArray<PortfolioInstantDiscountEvidence> DiscountInputs)
{internal string Measure{get;init;}=GasOwnedPortfolio.Measure;}

internal static class GasOwnedPortfolio
{
    internal const string Measure="gas_owned_present_value_v1";
    internal const string ProfileMeasure="gas_owned_saved_profile_present_value_v2";
    private sealed record ScopeAdmission(Guid Catalog,Guid Context,DateTimeOffset At,ImmutableArray<Guid> Books,bool Configured,DateTimeOffset Until):ICommitAdmission
    {
        public async Task RequireCommit(NpgsqlConnection connection,CancellationToken ct)
        {
            await using var command=ScheduleStore.Command(connection,"SELECT security.require_gas_owned_valuation_scope($1,$2,$3,$4::uuid[],$5,$6)",
                Catalog,Context,At,Books.ToArray(),Configured,Until);
            await command.ExecuteScalarAsync(ct);
        }
    }
    internal static async Task<bool> RequireScope(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,
        ImmutableArray<Guid> books,bool configured,CancellationToken ct)
    {
        await using var capture=ScheduleStore.Command(connection,configured?
            "SELECT least(settlement_data.futures_margin_preview_authority($1),security.authority_until('ReadGasOwnedHoldings'))":
            "SELECT settlement_data.futures_margin_preview_authority($1)",database.Manifest.CatalogId);
        var until=new DateTimeOffset((DateTime)(await capture.ExecuteScalarAsync(ct))!);
        var admission=new ScopeAdmission(database.Manifest.CatalogId,frame.ContextId!.Value,frame.At,books,configured,until);
        database.RegisterCommitAdmission(connection,admission);
        await using var command=ScheduleStore.Command(connection,"SELECT security.require_gas_owned_valuation_scope($1,$2,$3,$4::uuid[],$5,$6)",
            database.Manifest.CatalogId,frame.ContextId.Value,frame.At,books.ToArray(),configured,until);
        try{return(bool)(await command.ExecuteScalarAsync(ct))!;}
        catch(PostgresException error)when(error.SqlState=="P0001"&&error.MessageText=="gas_owned_policy_required")
        {throw new MarketDataException(error.MessageText,GasOwnedOperations.Refusal(error.MessageText),error);}
    }
    internal static async Task<ImmutableArray<GasOwnedPortfolioInput>> Prepare(NpgsqlConnection connection,PreviewDatabase database,
        GasOwnedPortfolioRead held,GasOwnedValuationPolicy policy,CancellationToken ct)
    {
        policy.Validate();var result=ImmutableArray.CreateBuilder<GasOwnedPortfolioInput>();
        var savedPrincipal=policy.PrincipalCarrying==GasOwnedValuationPolicy.SavedPrincipal;
        var measure=savedPrincipal?ProfileMeasure:Measure;
        if(savedPrincipal?held.PrincipalCarryingImplementation!=GasOwnedValuationPolicy.SavedPrincipal||held.Principal.Any(p=>p.PrincipalCarrying is null):
            held.PrincipalCarryingImplementation is not null||held.Principal.Any(p=>p.PrincipalCarrying is not null))
            throw new MarketDataException("gas_principal_profile_source","The selected cash law requires its exact native saved-profile source projection.");
        foreach(var pair in held.Markets.OrderBy(p=>p.Key))
        {
            var market=pair.Value;var positions=held.Positions.Where(p=>p.DefinitionRevisionId==pair.Key).ToImmutableArray();
            var fees=held.Fees.Concat(held.Principal).Where(p=>p.DefinitionRevisionId==pair.Key).ToImmutableArray();
            if(positions.IsEmpty&&fees.IsEmpty)continue;
            foreach(var cash in fees.Where(c=>c.PrincipalCarrying is not null))
            {
                var selected=cash.PrincipalCarrying!;
                if(!Enum.IsDefined(selected.Carrying)||selected.ProfileId==Guid.Empty||selected.ProfileRevision<1||selected.TermsId==Guid.Empty||
                    selected.PositionId!=cash.Position.Owner.Id||selected.AccountId!=cash.AccountId||selected.Currency!=cash.Instrument.Currency||
                    selected.DueAt!=cash.Instrument.PaymentAt||selected.Mark.Curve.Revision!=market.Snapshot.Definitions||
                    selected.Mark.Contract.Revision!=market.Snapshot.Definitions||selected.PublicationOrder<1||selected.PublicationOrder>cash.PublicationOrder||
                    !TradingInstant.IsSupported(selected.KnownAt)||selected.KnownAt>held.KnownAt||market.Snapshot.Knowledge?.KnownAt!=held.KnownAt||
                    market.Snapshot.Knowledge.ContextId!=held.ContextId||market.Snapshot.ValuationAt!=held.At)
                    throw new MarketDataException("gas_principal_profile_source","Retain this cash membership's exact saved profile, currency and certified source.");
            }
            var snapshots=market.Snapshot;var payments=positions.Select(p=>p.Instrument).OfType<GasCommodityHoldingInstrument>()
                .Where(i=>i.Carrying==GasHoldingCarrying.CurrencyCurve&&i.ReturnAt>snapshots.ValuationAt)
                .Select(i=>new SidePaymentKey(i.Id,0,i.ReturnAt!.Value,i.Currency))
                .Concat(positions.Select(p=>p.Instrument).OfType<GasPhysicalDeliveryObligationInstrument>().SelectMany(i=>i.RequiredDiscounts(snapshots.ValuationAt)))
                .Concat(fees.Where(f=>!Nominal(f)&&!f.Instrument.Due(snapshots.ValuationAt)&&f.State.Outstanding(snapshots).Numerator!="0").Select(f=>f.Instrument.Payment))
                .Distinct().ToImmutableArray();
            var discounts=ImmutableDictionary.CreateBuilder<Guid,ISideDiscounting>();var inputs=ImmutableArray.CreateBuilder<PortfolioInstantDiscountEvidence>();
            foreach(var currency in payments.GroupBy(p=>p.Currency))
            {
                var mapping=await SwapStore.CurrencyDefault(connection,database.Manifest.CatalogId,market.Data,market.Frame,currency.Key,ct)??
                    throw new MarketDataException("discount_curve","Independent gas holdings require the exact selected currency discount mapping.");
                var provider=new InstantCurrencyDiscounting(currency.ToImmutableArray(),snapshots,mapping,policy.TimingPolicy);
                foreach(var instrument in currency.Select(p=>p.SideId).Distinct())discounts.Add(instrument,provider);
                inputs.AddRange(provider.Inputs.Select(p=>new PortfolioInstantDiscountEvidence(p.Key.SideId,p.Value)));
            }
            var states=fees.ToImmutableDictionary(f=>new MeasureBinding(f.Position.Owner,f.Position.BindingId),f=>(IBindingValuationState)f.State);
            var bindingDiscounts=fees.Where(Nominal).ToImmutableDictionary(f=>new MeasureBinding(f.Position.Owner,f.Position.BindingId),
                _=>(ISideDiscounting)new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted));
            var instruments=positions.Select(p=>p.Instrument).Concat(fees.Select(f=>(Instrument)f.Instrument)).DistinctBy(i=>i.Id).ToImmutableArray();
            var holdings=positions.Select(p=>Position(p.Position,p.Instrument,p.Quantity)).Concat(fees.Select(f=>Position(f.Position,f.Instrument,f.Quantity))).ToImmutableArray();
            var setup=new ValuationSetup(ValuationMeasure.PresentValue,ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,null)
                {BindingStates=states,BindingDiscounting=bindingDiscounts,InstrumentDiscounting=discounts.ToImmutable(),AccountingFacts=held.AccountingFacts.Where(p=>states.ContainsKey(p.Key)).ToImmutableDictionary()};
            var evidence=positions.Select(p=>Evidence(p.Position,p.Instrument,p.Quantity,p.Revision,p.PublicationOrder,p.Kind,p.AccountCode,p.SourceReference))
                .Concat(fees.Select(p=>Evidence(p.Position,p.Instrument,p.Quantity,1,p.PublicationOrder,"cash_payment",p.AccountCode,p.SourceReference))).ToImmutableArray();
            result.Add(new(market.Data,market.Frame,snapshots,new(instruments,holdings,setup),evidence,inputs.ToImmutable()){Measure=measure});
            PortfolioPositionEvidence Evidence(AccountingPositionKey owner,Instrument instrument,Quantity quantity,int revision,int publication,string kind,string account,string reference)=>
                new(Guid.Empty,revision,owner.BindingId,instrument.Id,instrument.Id,owner.BookId,measure,pair.Key,publication,quantity)
                {Owner=owner.Owner,InstrumentType=kind,TradeReference=account+" · "+kind.Replace('_',' '),InstrumentReference=reference};
        }
        return result.ToImmutable();
        bool Nominal(GasOwnedCashPosition cash)=>savedPrincipal&&cash.PrincipalCarrying?.Carrying==GasHoldingCarrying.Nominal;
    }
    private static RiskPosition Position(AccountingPositionKey owner,Instrument instrument,Quantity quantity)=>new(owner.Owner,owner.BindingId,owner.BookId,
        instrument.Id,quantity,new(new Money(default,instrument.Currency),quantity.Specification),new(default,instrument.Currency));
}
