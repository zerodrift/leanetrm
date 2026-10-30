using System.Collections.Immutable;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

public enum FuturesSettlementState { Unprocessed = 1 }

// Execution P&L only. Daily/final settlement, cash payment and PV require their
// own retained state and conventions; this contract never substitutes zero VM.
public sealed record FuturesMarketBinding
{
    public const string Implementation = "contract_lot_execution_pnl_v2";
    public static bool Supported(string? implementation)=>implementation == Implementation;
    public string Convention { get; }
    public CurveRequest Market { get; }
    public MarketContract Contract { get; }
    public FuturesContract Instrument { get; }
    public FuturesSeriesSpecification Series => Instrument.Series;
    public QuoteSpecification Quote { get; }
    public DateTimeOffset EligibleFrom { get; }
    public string SettlementMethod => Series.SettlementMethod;
    public Guid Id { get; }
    public Quantity UnitQuantity => Instrument.UnitQuantity;
    public ImmutableArray<CurveRequest> Dependencies => [Market];
    public FuturesMarketBinding(Guid id, CurveRequest market, MarketContract contract, FuturesSeriesSpecification series,
        QuoteSpecification quote, string settlementMethod, string implementation, FuturesContract? instrument = null,DateTimeOffset? eligibleFrom = null)
    {
        if (contract is null || series is null || quote is null)
            throw Error("futures_definition", "Explicit dated contract, series and quote terms are required.");
        if (!Supported(implementation) || settlementMethod != series.SettlementMethod)
            throw Error("futures_convention", "Select the current contract-lot execution convention and the exact series settlement method.");
        var from=eligibleFrom??series.FirstTradingAt;
        if(from is not {} resolved||!TradingInstant.IsSupported(resolved)||resolved>=contract.Expiry||series.FirstTradingAt is {} sourceFirst&&resolved<sourceFirst)
            throw Error("futures_definition","Explicit qualified eligibility is required when the source first-trading instant is unknown.");
        EligibleFrom=resolved;
        if (id == Guid.Empty || contract.Key.Id == Guid.Empty || contract.Key.Revision.CatalogId == Guid.Empty ||
            contract.Key.Revision.RevisionId == Guid.Empty || contract.Kind != ContractKind.Future || market.Contract != contract.Key ||
            market.Curve.Revision != contract.Key.Revision || string.IsNullOrWhiteSpace(market.Curve.Code) ||
            contract.FuturesSeries != series.Key || series.Key.Revision != quote.Key.Revision ||
            quote.Kind != (series.BasisSettlement is null?QuoteKind.Outright:QuoteKind.Basis) ||
            series.QuoteQuantity != quote.Quantity || series.Currency != quote.Currency || series.Multiplier != quote.Multiplier ||
            series.TradingTick != quote.TradingTick || series.ContractSize.CompareTo(default) <= 0 ||
            new[] { series.Key, series.Location, series.DeliveryProfile, series.LotQuantity }.Any(k =>
                k.Revision != quote.Key.Revision || string.IsNullOrWhiteSpace(k.Code)) ||
            quote.TradingTick.CompareTo(default) <= 0 || quote.ClearingIncrement.CompareTo(default) <= 0 ||
            quote.Minimum.CompareTo(quote.Maximum) > 0 ||
            series.DeliveryFrom != contract.DeliveryFrom || series.DeliveryTo != contract.DeliveryTo || series.LastTradingAt != contract.Expiry ||
            contract.DeliveryFrom >= contract.DeliveryTo || contract.DeliveryTo >= new DateOnly(9999, 12, 31) ||
            series.FirstTradingAt is {} first&&(!TradingInstant.IsSupported(first)||first>=contract.Expiry) || !TradingInstant.IsSupported(contract.Expiry))
            throw Error("futures_definition", "The exact dated contract, quote, series and trading interval must agree.");
        _ = PriceUnit.FromQuote(quote);
        Id = id; Market = market; Contract = contract; Instrument = instrument ?? new(series);
        if (Instrument.Series != series) throw Error("futures_identity", "The bound series must equal the instrument definition.");
        Quote = quote; Convention=implementation;
    }
    public UnitPrice PriceChange(UnitPrice mark, UnitPrice executionPrice)
    {
        RequirePrices(mark,executionPrice);
        return mark.Subtract(executionPrice);
    }
    internal void RequirePrices(UnitPrice mark,UnitPrice executionPrice)
    {
        if (mark is null || executionPrice is null)
            throw Error("futures_execution", "Explicit mark and execution price are required.");
        mark.RequireQuote(Quote); executionPrice.RequireQuote(Quote);
        if (!executionPrice.Value.IsMultipleOf(Quote.TradingTick))
            throw Error("futures_execution", "Execution price must follow the exact contract trading tick.");
        if (!mark.Value.IsMultipleOf(Quote.ClearingIncrement))
            throw Error("clearing_precision", "Settlement mark must follow the selected quote's clearing increment.");
    }
    public Quantity LotSize => Instrument.LotSize;
    public Money ExecutionPnl(UnitPrice mark, UnitPrice executionPrice, ExactDecimal lots)
    {
        RequirePrices(mark, executionPrice);
        return Instrument.ExecutionPnl(mark, executionPrice, lots);
    }
    public Money UnitExecutionPnl(UnitPrice mark, UnitPrice executionPrice) => ExecutionPnl(mark, executionPrice, ExactDecimal.Parse("1"));
    internal static MarketDataException Error(string code, string message) => new(code, message);
}

// The exact series reference is the economic primary key in ref_data.futures_series.
// Quote source, quote admission policy and curve recipe belong to pricing bindings.
[EconomicDefinition("FuturesContract_v1")]
public sealed record FuturesContract : Instrument
{
    public override bool SupportsQuantityScaling=>true;
    internal override void RequireSameDefinition(Instrument other)
    {
        base.RequireSameDefinition(other);
        if(other is not FuturesContract contract||contract.Series!=Series)
            throw FuturesMarketBinding.Error("instrument_identity","An exact futures series key cannot reinterpret its immutable terms.");
    }
    [IdentityField]
    public ReferenceKey SeriesKey => Series.Key;
    public FuturesSeriesSpecification Series { get; }
    public Quantity LotSize => ResolveLot(Series);
    public override ReferenceKey Currency => Series.Currency;
    public override Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Series.QuoteQuantity);
    public FuturesContract(FuturesSeriesSpecification series)
        : base(ContractIdentity.Futures(series?.Key ?? default))
    {
        if (series is null || series.SettlementMethod is not ("cash" or "physical") || !Enum.IsDefined(series.LotBasis) ||
            series.ContractSize.CompareTo(default) <= 0 || series.Multiplier.CompareTo(default) <= 0 || series.TradingTick.CompareTo(default) <= 0 ||
            series.DeliveryFrom >= series.DeliveryTo ||
            series.FirstTradingAt is {} first&&(!TradingInstant.IsSupported(first)||first>=series.LastTradingAt) || !TradingInstant.IsSupported(series.LastTradingAt))
            throw FuturesMarketBinding.Error("futures_definition", "An exact supported futures series and settlement convention are required.");
        foreach (var key in new[] {series.Key,series.QuoteQuantity,series.Currency,series.Location,series.DeliveryProfile,series.LotQuantity})
            if (key.Revision != series.Key.Revision || string.IsNullOrWhiteSpace(key.Code)) throw FuturesMarketBinding.Error("futures_definition", "Contract terms require one exact reference revision.");
        if(series.BasisSettlement is {} basis && (series.SettlementMethod!="cash" || basis.Implementation!="index_minus_future_v1" ||
            basis.BenchmarkSeries==series.Key || new[]{basis.Index,basis.BenchmarkSeries}.Any(k=>k.Revision!=series.Key.Revision||string.IsNullOrWhiteSpace(k.Code))))
            throw FuturesMarketBinding.Error("futures_definition","Basis settlement requires an explicit exact-revision index and distinct benchmark series.");
        _ = ResolveLot(series);
        Series = series;
    }
    private static Quantity ResolveLot(FuturesSeriesSpecification series)
    {
        if(series.LotBasis==LotBasis.FixedTotal)
        {
            if(series.LotResolution is not null || series.LotQuantity!=series.QuoteQuantity)
                throw FuturesMarketBinding.Error("futures_lot","Fixed lots require identical quote/lot units and no variable-lot resolution.");
            return new(series.ContractSize,series.QuoteQuantity);
        }
        var resolution=series.LotResolution;
        if(resolution is null || resolution.Quantity is null || resolution.Quantity.Specification!=series.QuoteQuantity ||
            resolution.Factor.CompareTo(default)<=0 || resolution.Quantity.Amount.CompareTo(default)<=0 ||
            resolution.Quantity.Amount!=series.ContractSize*resolution.Factor ||
            resolution.Implementation is not ("published_contract_size_v1" or "calendar_delivery_days_v1") ||
            resolution.Implementation=="calendar_delivery_days_v1" && (series.LotBasis!=LotBasis.PerDeliveryDay ||
                resolution.Factor!=ExactDecimal.Parse((series.DeliveryTo.DayNumber-series.DeliveryFrom.DayNumber).ToString(System.Globalization.CultureInfo.InvariantCulture))))
            throw FuturesMarketBinding.Error("futures_lot","A variable lot requires an explicit coherent exact-series quantity and selected factor convention.");
        return resolution.Quantity;
    }
    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        var binding=context.Futures(Id);
        if(binding.Series!=Series)throw FuturesMarketBinding.Error("futures_identity","The selected binding must retain the exact instrument terms.");
        return new(Id,Series.LotBasis==LotBasis.FixedTotal&&SettlementMethod=="cash"?"fixed_total_open_mark_v1":"contract_unit_open_mark_v2",
            binding.Dependencies,UnitQuantity.Specification,Currency);
    }
    private string SettlementMethod=>Series.SettlementMethod;
    public override ExactMoney Value(UnitValuationContext context)
    {
        if(context.Measure!=ValuationMeasure.ExecutionPnl)return base.Value(context);
        var binding=context.Futures(Id);
        context.Market!.Model.RequireFuturesDefinition(binding.Market,binding.Contract,Series,binding.Quote);
        return context.Inputs.Price(binding.Market).ExactTimes(UnitQuantity);
    }
    public override InstrumentValuation PV(SideValuationContext context) =>
        throw FuturesMarketBinding.Error("futures_pv_unsupported", "Futures PV requires retained variation-margin and settlement state; execution P&L is a separate operation.");
    public Money ExecutionPnl(UnitPrice mark, UnitPrice executionPrice, ExactDecimal lots)
    {
        if (mark is null || executionPrice is null || mark.Unit != executionPrice.Unit ||
            mark.Unit.Currency != Currency || mark.Unit.Quantity != UnitQuantity.Specification || mark.Unit.Multiplier != Series.Multiplier)
            throw FuturesMarketBinding.Error("futures_execution", "Prices require the exact contract currency, quantity and quote multiplier.");
        var one=ExactDecimal.Parse("1");
        return ExactMoney.WeightedSum(Currency, new[] {
            ((IEnumerable<(ExactDecimal, ExactDecimal)>)new[] {(mark.Value,one)},new[] {Series.Multiplier,LotSize.Amount,lots}),
            ((IEnumerable<(ExactDecimal, ExactDecimal)>)new[] {(executionPrice.Value,one)},new[] {Series.Multiplier,LotSize.Amount,lots,ExactDecimal.Parse("-1")})}).ToMoney();
    }
    public Money UnitExecutionPnl(UnitPrice mark, UnitPrice executionPrice) => ExecutionPnl(mark, executionPrice, ExactDecimal.Parse("1"));
}

public static class ContractIdentity
{
    // Canonical UTF-8 ASCII encoding, shared with the database; UUID is an alias.
    // The complete exact-series key is always retained and checked on lookup.
    public static Guid Futures(ReferenceKey series)
    {
        Money.RequireKey(series);
        var bytes = System.Text.Encoding.UTF8.GetBytes($"futures_contract_v1:{series.Revision.CatalogId:D}:{series.Revision.RevisionId:D}:{series.Code}");
        return Guid.ParseExact(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..32], "N");
    }
}

// Product-specific execution facts are owned by a generic trade binding. They
// carry no booked quantity; the binding stores the physical quantity once.
public sealed record FuturesExecution:TradeExecution
{
    public FuturesMarketBinding MarketBinding { get; }
    public FuturesContract Instrument => MarketBinding.Instrument;
    public UnitPrice ExecutionPrice { get; }
    public DateTimeOffset ExecutedAt { get; }
    public FuturesSettlementState Settlement { get; }
    public static TradeInstrument Bind(Guid id,FuturesMarketBinding marketBinding,ExactDecimal lots,UnitPrice executionPrice,
        DateTimeOffset executedAt,FuturesSettlementState settlement,Guid? retainedInstrumentId=null)
    {
        if (marketBinding is null || executionPrice is null) throw FuturesMarketBinding.Error("futures_execution", "Execution requires an explicit instrument and price.");
        if (!lots.IsMultipleOf(ExactDecimal.Parse("1")) || lots == default ||
            lots.CompareTo(ExactDecimal.Parse("-100000")) < 0 || lots.CompareTo(ExactDecimal.Parse("100000")) > 0)
            throw FuturesMarketBinding.Error("futures_lots", "Supply nonzero signed integer lots between -100000 and 100000.");
        if(id==Guid.Empty||retainedInstrumentId==Guid.Empty)throw FuturesMarketBinding.Error("futures_execution","Execution requires exact binding and retained identities.");
        return new(id,marketBinding.Instrument,marketBinding.Instrument.LotSize.Scale(lots),
            new FuturesExecution(marketBinding,executionPrice,executedAt,settlement),retainedInstrumentId);
    }
    public FuturesExecution(FuturesMarketBinding marketBinding,UnitPrice executionPrice,DateTimeOffset executedAt,FuturesSettlementState settlement)
    {
        if(marketBinding is null||executionPrice is null)throw FuturesMarketBinding.Error("futures_execution","Execution requires an explicit instrument and price.");
        var side=marketBinding;
        if (!TradingInstant.IsSupported(executedAt) || executedAt < side.EligibleFrom || executedAt >= side.Contract.Expiry ||
            executionPrice.Unit != PriceUnit.FromQuote(side.Quote) || !executionPrice.Value.IsMultipleOf(side.Quote.TradingTick))
            throw FuturesMarketBinding.Error("futures_execution", "Execution requires an exact identity, supported trading instant, price unit and trading tick.");
        if (settlement != FuturesSettlementState.Unprocessed)
            throw FuturesMarketBinding.Error("futures_settlement", "Only explicitly unprocessed settlement state is supported by execution P&L.");
        executionPrice.RequireQuote(side.Quote);
        MarketBinding=marketBinding;ExecutionPrice=executionPrice;ExecutedAt=executedAt;Settlement=settlement;
    }
    public static FuturesExecution Require(TradeInstrument binding)=>binding.Execution as FuturesExecution??
        throw FuturesMarketBinding.Error("futures_execution","The futures operation requires explicit futures execution facts.");
    public ExactDecimal Lots(Quantity quantity)
    {
        if(quantity.Specification!=Instrument.LotSize.Specification||!quantity.Amount.IsMultipleOf(Instrument.LotSize.Amount))
            throw FuturesMarketBinding.Error("futures_lots","The external quantity must represent whole lots in the exact contract unit.");
        var lots=quantity.Amount.DivideExact(Instrument.LotSize.Amount);
        if(lots==default||lots.CompareTo(ExactDecimal.Parse("-100000"))<0||lots.CompareTo(ExactDecimal.Parse("100000"))>0)
            throw FuturesMarketBinding.Error("futures_lots","Supply nonzero signed integer lots between -100000 and 100000.");
        return lots;
    }
    internal override void Validate(Instrument instrument,Quantity quantity)
    {
        if(instrument is not FuturesContract contract||contract.Series!=Instrument.Series)
            throw FuturesMarketBinding.Error("futures_identity","Execution facts must reference the binding's exact futures contract.");
        _=Lots(quantity);
    }
    internal override UnitMoney Basis(Instrument instrument)=>new(ExecutionPrice.ExactTimes(instrument.UnitQuantity),instrument.UnitQuantity.Specification);
}

public sealed record FuturesHolding(Guid TradeId, Guid BookId, Guid BindingId, Guid InstrumentId, Guid PricingBindingId, ContractKey Contract,
    Quantity Quantity, UnitPrice ExecutionPrice, UnitPrice Mark, Money ExecutionPnl, FuturesSettlementState Settlement,
    CurveKey Market,ExactDecimal Lots);

public static class FuturesHoldings
{
    private static bool SameInputs(MarketSnapshot requested, MarketSnapshot retained) => requested.HasSameInputs(retained);
    internal sealed record ResolvedHolding(Guid TradeId,Guid BookId,TradeInstrument Position,Quantity Quantity,UnitPrice Mark);
    public static ImmutableArray<FuturesHolding> Price(ImmutableArray<Trade> trades, MarketSnapshot snapshot,
        CurveBuild market, string implementation)=>Resolve(trades,snapshot,market,implementation).Select(h=>
        {
            var p=h.Position;var execution=FuturesExecution.Require(p);var side=execution.MarketBinding;var lots=execution.Lots(p.Quantity);
            return new FuturesHolding(h.TradeId,h.BookId,p.Id,p.RetainedInstrumentId,side.Id,side.Contract.Key,h.Quantity,
                execution.ExecutionPrice,h.Mark,side.ExecutionPnl(h.Mark,execution.ExecutionPrice,lots),execution.Settlement,side.Market.Curve,lots);
        }).ToImmutableArray();
    internal static ImmutableArray<ResolvedHolding> Resolve(ImmutableArray<Trade> trades,MarketSnapshot snapshot,
        CurveBuild market,string implementation,CancellationToken cancellation=default)
    {
        if (!FuturesMarketBinding.Supported(implementation))
            throw FuturesMarketBinding.Error("futures_convention", "Choose an explicit supported futures pricing convention.");
        if (trades.IsDefault || trades.Length > 100000 || trades.Any(t => t is null) || trades.Select(t => t.Id).Distinct().Count() != trades.Length ||
            !SameInputs(snapshot,market.Snapshot) || !market.Scenario.IsBase || market.NumericalPolicy != CurveModel.NumericalPolicy ||
            !TradingInstant.IsSupported(snapshot.ValuationAt))
            throw FuturesMarketBinding.Error("futures_context", "Use distinct trades and a matching pinned snapshot, definition revision and numerical policy.");
        var sides = new Dictionary<Guid,FuturesMarketBinding>();
        var instruments = new Dictionary<Guid,FuturesContract>();
        var aliases = new Dictionary<Guid,Guid>();
        foreach (var position in trades.SelectMany(t => t.Instruments))
        {
            cancellation.ThrowIfCancellationRequested();
            var execution=FuturesExecution.Require(position);var instrument=execution.Instrument;
            var side = execution.MarketBinding;
            if (sides.TryGetValue(side.Id, out var retained) && retained != side)
                throw FuturesMarketBinding.Error("futures_identity", "A pricing binding identity cannot reinterpret its exact inputs.");
            sides.TryAdd(side.Id, side);
            if (instruments.TryGetValue(instrument.Id, out var prior) && prior != instrument)
                throw FuturesMarketBinding.Error("futures_identity", "An exact series key cannot reinterpret its immutable contract terms.");
            if (aliases.TryGetValue(position.RetainedInstrumentId, out var canonical) && canonical != instrument.Id)
                throw FuturesMarketBinding.Error("futures_identity", "A retained instrument alias cannot identify different contracts.");
            aliases[position.RetainedInstrumentId] = instrument.Id;
            instruments.TryAdd(instrument.Id, instrument);
        }
        var prices = market.Values.ToDictionary(v => v.Request);
        var result = ImmutableArray.CreateBuilder<ResolvedHolding>();
        foreach (var trade in trades)
        foreach (var position in trade.Instruments)
        {
            cancellation.ThrowIfCancellationRequested();
            var execution=FuturesExecution.Require(position);var side=execution.MarketBinding;
            market.Model.RequireFuturesDefinition(side.Market, side.Contract, side.Series, side.Quote);
            if (execution.ExecutedAt > snapshot.ValuationAt)
                throw FuturesMarketBinding.Error("futures_effective", "An execution cannot enter a valuation before its exact effective instant.");
            if (snapshot.ValuationAt < side.EligibleFrom || snapshot.ValuationAt >= side.Contract.Expiry)
                throw FuturesMarketBinding.Error("futures_lifecycle", "Pricing beyond the supported live interval requires explicit lifecycle processing.");
            if (!prices.TryGetValue(side.Market, out var value))
                throw FuturesMarketBinding.Error("missing_observation", "The exact held contract price is required; no prompt contract is substituted.");
            if (value.Quote != side.Quote.Key) throw FuturesMarketBinding.Error("futures_definition", "The exact held quote definition is required.");
            var mark = value.Price;
            var quantity=position.Quantity;
            side.RequirePrices(mark,execution.ExecutionPrice);
            result.Add(new(trade.Id,trade.BookId,position,quantity,mark));
        }
        return result.ToImmutable();
    }
}
