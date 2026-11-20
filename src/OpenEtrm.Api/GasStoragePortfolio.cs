using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Gas;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

internal sealed record GasStoragePortfolioInput(PreviewData Data,InputFrame Frame,MarketSnapshot Snapshot,
    ValuationPortfolio Portfolio,ImmutableArray<PortfolioPositionEvidence> Evidence);
// The retained contract as its managed terms: everything a plan is checked against and valued with.
internal sealed record GasStorageContract(GasStorageEntitlement Entitlement,GasStorageState Opening,GasStorageTerminal Terminal,QuoteSpecification Quote,
    PaymentRule Payment,GasStorageCharges Charges,ImmutableArray<GasStorageContractDay> Days);

// A booked contract becomes a GasStorageInstrument from its retained rows and the plan in force under the selected
// context. Every retained physical term is used here: the entitlement admits the plan before any Side values it.
internal static class GasStoragePortfolio
{
    // A contract with no plan, with a plan its ratchets no longer admit, or already flowing is listed with that reason and no value.
    internal static bool Unpriced(MarketDataException error)=>error.Code is "gas_storage_plan" or "gas_storage_started" or "gas_storage_ratchet"
        or "gas_storage_inventory" or "gas_storage_terminal" or "gas_storage_direction";
    internal static GasStorageContract Contract(PreviewData data,GasStoragePosition position)
    {
        // The contract's curves, contracts and quote are those of the revision it was booked on: another revision's data cannot stand in for it.
        if(data.DefinitionRevision.RevisionId!=position.Definition||data.ReferenceRevision!=position.Reference)
            throw Invalid("Read the storage contract with its own market definition and reference revision.");
        var terms=position.Terms;var revision=data.DefinitionRevision;var unit=position.Quantity;
        Quantity Amount(JsonElement row,string field)=>new(ExactDecimal.Parse(row.Text(field)),unit);
        var quote=data.QuoteRows(terms.Text("quote_code")).SingleOrDefault()??throw Invalid("A held contract needs its exact quote.");
        var entitlement=new GasStorageEntitlement(revision,position.Binding.ToString("D"),unit,Amount(terms,"minimum_inventory"),Amount(terms,"maximum_inventory"),
            [..position.Ratchets.EnumerateArray().Select(b=>new GasStorageRatchet(Amount(b,"lower"),Amount(b,"upper"),Amount(b,"maximum_daily_injection"),Amount(b,"maximum_daily_withdrawal")))],
            ExactDecimal.Parse(terms.Text("injection_fuel")),ExactDecimal.Parse(terms.Text("withdrawal_fuel")),terms.Text("ratchet_basis"),terms.Text("flow_policy"),terms.Text("fuel_basis"));
        var pay=terms.GetProperty("payment_rule");
        var payment=StructuredPowerPreview.OperatingPayment(new(pay.Text("frequency"),pay.Text("rule"),Whole(pay,"days"),Whole(pay,"day_of_month"),pay.OptionalText("day_basis"),
            pay.OptionalText("short_month"),pay.Text("adjustment"),pay.OptionalText("calendar_code"),pay.Text("implementation")));
        var minute=int.Parse(terms.Text("day_start_minute"),CultureInfo.InvariantCulture);
        var days=position.Days.EnumerateArray().Select(row=>
        {
            var day=DateWorkspace.Date(row.Text("gas_day"));var month=new DateOnly(day.Year,day.Month,1);
            return new GasStorageContractDay(new(row.Instant("starts_at"),row.Instant("ends_at"),day,minute/60,minute%60,1),month,month.AddMonths(1),
                new(new(revision,position.InjectionCurve),new(revision,row.Id("injection_contract_id"))),new(new(revision,position.WithdrawalCurve),new(revision,row.Id("withdrawal_contract_id"))));
        }).ToImmutableArray();
        UnitPrice Price(string field)=>new(ExactDecimal.Parse(terms.Text(field)),quote);
        return new(entitlement,new(days[0].GasDay.Start,ExactQuantity.FromQuantity(Amount(terms,"opening_inventory"))),new(Amount(terms,"terminal_minimum"),Amount(terms,"terminal_maximum")),
            quote,payment,new(Price("injection_charge"),Price("withdrawal_charge"),Price("monthly_capacity_charge")),days);
    }
    internal static ImmutableArray<GasStorageDay> Plan(GasStorageContract contract,IEnumerable<(string Injection,string Withdrawal)> flows)
    {
        var unit=contract.Entitlement.Quantity;var rows=flows.ToArray();
        if(rows.Length!=contract.Days.Length)throw new MarketDataException("gas_storage_plan","A plan states one row for each gas day of the term.");
        return [..rows.Select((flow,i)=>new GasStorageDay(contract.Days[i].GasDay,new(ExactDecimal.Parse(flow.Injection),unit),new(ExactDecimal.Parse(flow.Withdrawal),unit)))];
    }
    internal static GasStorageInstrument Instrument(PreviewData data,GasStoragePosition position)
    {
        if(!position.Planned)throw new MarketDataException("gas_storage_plan","This storage contract has no plan in force under the selected context.");
        var contract=Contract(data,position);
        var plan=Plan(contract,position.Plan.GetProperty("flows").EnumerateArray().Select(f=>(f[0].GetString()!,f[1].GetString()!)));
        return GasStorageInstrument.Compose(position.Binding,contract.Entitlement,contract.Opening,contract.Terminal,contract.Quote,contract.Payment,contract.Charges,contract.Days,plan);
    }
    internal static async Task<ImmutableArray<GasStoragePortfolioInput>> Prepare(NpgsqlConnection connection,
        PreviewDatabase database,InputFrame frame,ImmutableArray<GasStoragePosition> positions,
        ImmutableArray<(PreviewData Data,InputFrame Frame)> supplied,bool requirePinnedSources,CancellationToken ct)
    {
        if(positions.IsDefault||supplied.IsDefault||frame.ContextId is not {} contextId)
            throw Invalid("Select exact storage holdings and admitted sources under a certified context.");
        if(positions.IsEmpty)return [];
        var context=PinnedContext.Exact((await new KnowledgeResolver(database.Manifest.CatalogId).Read(connection,contextId,ct)).Context);
        var result=ImmutableArray.CreateBuilder<GasStoragePortfolioInput>();
        foreach(var group in positions.GroupBy(p=>p.Definition).OrderBy(g=>g.Key))
        {
            var references=group.Select(p=>p.Reference).Distinct().ToArray();if(references.Length!=1)throw Invalid("One held definition requires one exact reference revision.");
            var source=await MarketInputSelection.Resolve(database,connection,frame,new(group.Key,references[0].CatalogId,references[0].RevisionId),supplied,requirePinnedSources,ct);
            MarketInputSelection.Require(source,frame,new(group.Key,references[0].CatalogId,references[0].RevisionId));
            var snapshot=MarketSnapshotBinding.Bind(source.Data.Snapshot(source.Frame),contextId,SchedulePreview.ParseAt(context.KnownAt));
            var trades=ImmutableArray.CreateBuilder<Trade>();var evidence=ImmutableArray.CreateBuilder<PortfolioPositionEvidence>();
            foreach(var position in group)
            {
                var instrument=Instrument(source.Data,position);var quantity=instrument.UnitQuantity;var book=position.Book.Id("id");
                trades.Add(new(position.Trade,book,[new(position.Binding,instrument,quantity)]));
                evidence.Add(new(position.Trade,int.Parse(position.Head.Text("revision"),CultureInfo.InvariantCulture),position.Binding,position.Binding,position.Binding,book,
                    GasStoragePositions.Measure,group.Key,int.Parse(position.Head.Text("publication_order"),CultureInfo.InvariantCulture),quantity)
                    {TradeReference=TradingReference.Trade(position.Head),InstrumentReference=TradingReference.Instrument(position.Head),InstrumentType=GasStoragePositions.Type});
            }
            // No discount binding is retained with the contract yet: cash is valued undiscounted, stated here rather than assumed downstream.
            var portfolio=ValuationPortfolio.FromTrades(trades.ToImmutable(),new(ValuationMeasure.PresentValue,
                ImmutableDictionary<Guid,FuturesMarketBinding>.Empty,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted)));
            result.Add(new(source.Data,source.Frame,snapshot,portfolio,evidence.ToImmutable()));
        }
        return result.ToImmutable();
    }
    internal static Task<CustomCurves.PreparedMarket> PrepareMarket(NpgsqlConnection connection,PreviewDatabase database,
        GasStoragePortfolioInput group,ScheduleContext context,CancellationToken ct)
    {
        var dependencies=group.Portfolio.Declare(group.Snapshot).SelectMany(c=>c.Dependencies).Distinct().ToImmutableArray();
        return CustomCurves.PrepareMarket(connection,database,group.Data,group.Frame,group.Snapshot,context,
            group.Portfolio.Instruments,dependencies,CustomCurves.MarketPreparation.ValuationDependencies,ct,activePrices:[]);
    }
    internal static async Task<ExactMoney> Value(NpgsqlConnection connection,PreviewDatabase database,InputFrame frame,GasStoragePosition position,CancellationToken ct)
    {
        var group=(await Prepare(connection,database,frame,[position],[],false,ct)).Single();
        var context=PinnedContext.Exact((await new KnowledgeResolver(database.Manifest.CatalogId).Read(connection,frame.ContextId!.Value,ct)).Context);
        var market=await PrepareMarket(connection,database,group,context,ct);
        var prepared=RiskEngine.PrepareConnected(market.Valuation,market.Bindings,[group.Portfolio],market.Authority,ct);
        return RiskEngine.CalculateExact(prepared,[],RiskEngine.ExactNumericalPolicy,ct,RiskDetail.Holdings).Groups.Single().BasePositions.Single().Value;
    }
    private static int? Whole(JsonElement row,string field)=>row.OptionalText(field) is {} text?int.Parse(text,CultureInfo.InvariantCulture):null;
    private static MarketDataException Invalid(string message)=>new("gas_storage_market_context",message);
}
