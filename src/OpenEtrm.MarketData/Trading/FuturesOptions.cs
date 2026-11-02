using System.Collections.Immutable;
using OpenEtrm.MarketData.Options;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

// A listed American option on an exact futures series, valued per option contract.
// As for FuturesContract, the exact series key is the economic identity; the
// observation role, calibration and discounting belong to the pricing binding.
[EconomicDefinition("FuturesOptionInstrument_v1")]
public sealed record FuturesOptionInstrument : Instrument
{
    public const string PremiumPaidUpFront = "premium_paid_up_front";
    public override bool SupportsQuantityScaling => true;
    [IdentityField]
    public ReferenceKey SeriesKey => Series.Key;
    public FuturesOptionSeriesSpecification Series { get; }
    public override ReferenceKey Currency => Series.Currency;
    public override Quantity UnitQuantity => new(ExactDecimal.Parse("1"), Series.LotQuantity);

    public FuturesOptionInstrument(FuturesOptionSeriesSpecification series)
        : base(ContractIdentity.FuturesOption(series?.Key ?? default))
    {
        if (series is null || series.Terms is null || series.Multiplier.CompareTo(default) <= 0 ||
            series.ClearingIncrement.CompareTo(default) <= 0 || series.Terms.Strike.CompareTo(default) <= 0 ||
            !TradingInstant.IsSupported(series.Terms.ExpiryAt) ||
            series.InstructionCutoffAt is { } cutoff && (!TradingInstant.IsSupported(cutoff) || cutoff > series.Terms.ExpiryAt) ||
            series.ContraryInstructionsAllowed && series.InstructionCutoffAt is null ||
            string.IsNullOrWhiteSpace(series.AutomaticExerciseRule) ||
            new[] { series.Product, series.UnderlyingSeries, series.Currency, series.LotQuantity }.Any(k =>
                k.Revision != series.Key.Revision || string.IsNullOrWhiteSpace(k.Code)))
            throw Error("option_definition", "An exact option series needs positive strike, multiplier and increment, " +
                "a supported expiry, any stated cutoff before it (required where contrary instructions are accepted), an exercise rule " +
                "and references in its own revision.");
        if (series.PremiumStyle != PremiumPaidUpFront)
            throw Error("unsupported", $"Premium style '{series.PremiumStyle}' is not supported; only {PremiumPaidUpFront} is bookable.");
        Series = series;
    }

    internal override void RequireSameDefinition(Instrument other)
    {
        base.RequireSameDefinition(other);
        if (other is not FuturesOptionInstrument option || option.Series != Series)
            throw Error("instrument_identity", "An exact option series key cannot reinterpret its immutable terms.");
    }

    // Holdings are signed whole option contracts.
    public override void ValidateBinding(Quantity quantity, DeliveryCoverage? coverage)
    {
        base.ValidateBinding(quantity, coverage);
        if (quantity.Specification != Series.LotQuantity || quantity.Amount == default || !quantity.Amount.IsMultipleOf(ExactDecimal.Parse("1")))
            throw Error("option_lots", "Supply a nonzero signed whole number of option contracts.");
    }

    public override UnitPricingContract Declare(UnitValuationContext context)
    {
        var binding = Binding(context);
        return new(Id, AmericanFuturesOptionModel.Id, [binding.Underlying], UnitQuantity.Specification, Currency);
    }

    // The unit price at the scenario futures price is rounded once, half-even, to
    // the 18-digit working scale before the multiplier is applied.
    public override UnitValuation PV(UnitValuationContext context)
    {
        if (context.Measure != ValuationMeasure.PresentValue)
            throw new MarketDataException("valuation_measure", "Futures options implement present value only.");
        var binding = Binding(context);
        var inputs = context.Inputs;
        if (inputs.SnapshotId != binding.SnapshotId)
            throw Error("option_context", $"Option series {Series.Key.Code} was calibrated on another snapshot.");
        context.Market!.Model.RequireFuturesOptionDefinition(Series, binding.Underlying, binding.Settlement);
        var futures = LatticeDecimal.From(inputs.ExactPrice(binding.Underlying).ToUnitPriceExact().Value);
        var unit = binding.UnitPrice(futures, inputs.ValuationAt, context.Cancellation).ToExact();
        return new(context, ExactMoney.FromMoney(new Money(unit, Currency)).Scale(Series.Multiplier), UnitQuantity.Specification);
    }

    private FuturesOptionMarketBinding Binding(UnitValuationContext context)
    {
        var binding = context.FuturesOption(Id);
        if (binding.Instrument.Series != Series)
            throw Error("valuation_binding", "The selected binding must retain the exact option terms.");
        return binding;
    }

    internal static MarketDataException Error(string code, string message) => new(code, message);
}

// Calibration results from one pinned base snapshot. Scenarios reprice at the held
// calibration and the scenario futures price; they never re-solve.
// SnapshotId is the pinned base snapshot the calibration came from.
public sealed record FuturesOptionMarketBinding(Guid Id, FuturesOptionInstrument Instrument, CurveRequest Underlying,
    CurveRequest Settlement, string ObservationRole, OptionCalibration Calibration, LatticeDecimal BaseFutures,
    LatticeDecimal BaseSettlement, LatticeDecimal DiscountToExpiry, OptionNumericalPolicy Policy, Guid SnapshotId)
{
    public Guid Id { get; } = Id != Guid.Empty ? Id : throw FuturesOptionInstrument.Error("valuation_binding", "A binding needs an exact identity.");
    public FuturesOptionInstrument Instrument { get; } = Instrument ?? throw FuturesOptionInstrument.Error("valuation_binding", "A binding needs its option.");
    public string ObservationRole { get; } = ObservationRole == FuturesOptionPricing.DailySettlement ? ObservationRole
        : throw FuturesOptionInstrument.Error("option_observation_role", $"Observation role '{ObservationRole}' is not supported.");
    public OptionCalibration Calibration { get; } = Calibration ?? throw FuturesOptionInstrument.Error("valuation_binding", "A binding needs its calibration.");
    public OptionNumericalPolicy Policy { get; } = Policy ?? throw FuturesOptionInstrument.Error("valuation_binding", "A binding needs its numerical policy.");
    public Guid SnapshotId { get; } = SnapshotId != Guid.Empty ? SnapshotId
        : throw FuturesOptionInstrument.Error("valuation_binding", "A binding needs its calibration snapshot.");

    // Solved: the tree at the held volatility. Fallback: treated as exercised, so
    // intrinsic at the scenario price plus the base residual, floored at zero.
    public LatticeDecimal UnitPrice(LatticeDecimal futures, DateTimeOffset valuationAt, CancellationToken ct)
    {
        switch (Calibration)
        {
            case SolvedVolatility solved:
                Interlocked.Increment(ref trees.Count);
                return AmericanFuturesOptionModel.UnitPrice(Instrument.Series.Terms, futures, solved.Volatility, DiscountToExpiry, valuationAt, Policy, ct);
            case IntrinsicFallback fallback:
                return LatticeDecimal.Max(Intrinsic(futures) + fallback.Residual, LatticeDecimal.Parse("0"));
            default:
                throw FuturesOptionInstrument.Error("valuation_binding", "Unsupported calibration mode.");
        }
    }

    // Work only: how many tree prices this binding's valuations have evaluated since it was prepared.
    public int TreePrices => Volatile.Read(ref trees.Count);
    private readonly TreeCount trees = new();
    private sealed class TreeCount { public int Count; }

    private LatticeDecimal Intrinsic(LatticeDecimal futures)
    {
        var strike = LatticeDecimal.From(Instrument.Series.Terms.Strike);
        return LatticeDecimal.Max(Instrument.Series.Terms.Right == OptionRight.Call ? futures - strike : strike - futures, LatticeDecimal.Parse("0"));
    }

    public FuturesOptionCalibrationReceipt Receipt => Calibration switch
    {
        SolvedVolatility s => Record(FuturesOptionPricing.SolvedMode, s.Volatility.ToString(), s.Residual, s.Iterations, s.UpperBound.ToString(), null),
        IntrinsicFallback f => Record(ImpliedVolatility.FallbackMode, null, f.Residual, null, null, f.Intrinsic.ToString()),
        _ => throw FuturesOptionInstrument.Error("valuation_binding", "Unsupported calibration mode."),
    };

    private FuturesOptionCalibrationReceipt Record(string mode, string? volatility, LatticeDecimal residual, int? iterations,
        string? upperBound, string? intrinsic) =>
        new(Instrument.Id, Instrument.SeriesKey, Policy.Id, FuturesOptionPricing.ScenarioPolicy, BaseFutures.ToString(), BaseSettlement.ToString(),
            DiscountToExpiry.ToString(), mode, volatility, residual.ToString(), iterations, upperBound, intrinsic);
}

// What a run records per option series: its calibration inputs and policies, the
// mode, and the solved volatility or the fallback intrinsic, with lattice values as
// exact decimal strings.
public sealed record FuturesOptionCalibrationReceipt(Guid InstrumentId, ReferenceKey Series, string NumericalPolicy, string ScenarioPolicy,
    string BaseFutures, string Settlement, string DiscountToExpiry, string Mode,
    string? Volatility, string Residual, int? Iterations, string? UpperBound, string? Intrinsic);

public static class FuturesOptionPricing
{
    public const string ScenarioPolicy = "option_implied_volatility_held_v1";
    public const string SolvedMode = "solved";
    public const string DailySettlement = "daily_settlement";

    // Calibrates one option from the base build: the option's and its underlying's
    // prices under the same observation role, and the discount to expiry.
    public static FuturesOptionMarketBinding Prepare(FuturesOptionInstrument option, CurveBuild basis, MarketSnapshot snapshot,
        ISideDiscounting discounting, string observationRole, OptionNumericalPolicy policy, bool fallbackDeclared, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (option is null || basis is null || snapshot is null || discounting is null || policy is null)
            throw FuturesOptionInstrument.Error("option_context", "Supply the option, its base build, snapshot, discounting and numerical policy.");
        if (!basis.Snapshot.HasSameInputs(snapshot) || !basis.Scenario.IsBase)
            throw FuturesOptionInstrument.Error("option_context", "Calibrate from the unshocked base build of this exact snapshot.");
        var role = Role(observationRole);
        var series = option.Series;
        var label = $"Option series {series.Key.Code}";
        RequireLive(series, snapshot, label);
        var model = basis.Model;
        var built = basis.Values.ToDictionary(v => v.Request);
        var (underlying, settlement) = Requests(model, built.Keys, series, role, label);
        model.RequireFuturesOptionDefinition(series, underlying, settlement);
        var discount = DiscountToExpiry(option, discounting, snapshot, label);
        var futures = LatticeDecimal.From(built[underlying].Price.Value);
        var optionPrice = LatticeDecimal.From(built[settlement].Price.Value);
        OptionCalibration calibration;
        try
        {
            calibration = ImpliedVolatility.Solve(series.Terms, optionPrice, futures, discount,
                LatticeDecimal.From(series.ClearingIncrement), snapshot.ValuationAt, policy, fallbackDeclared, ct);
        }
        catch (MarketDataException error) { throw new MarketDataException(error.Code, $"{label}: {error.Message}", error); }
        var id = ContractIdentity.FromCanonical($"futures_option_binding_v1:{option.Id:D}:{snapshot.Id:D}:{underlying.Curve.Code}:{underlying.Contract.Id:D}:" +
            $"{settlement.Curve.Code}:{settlement.Contract.Id:D}:{observationRole}:{policy.Id}:{fallbackDeclared}:{discount}");
        return new(id, option, underlying, settlement, observationRole, calibration, futures, optionPrice, discount, policy, snapshot.Id);
    }

    // FuturesRisk.Calculate's contract plus the option inputs. Each distinct option
    // series is calibrated once from the unstressed base; stress layers and the
    // base/up/down futures scenarios then reprice at the held calibration.
    public static RiskRun Calculate(ImmutableArray<Trade> trades, MarketSnapshot snapshot, CurveModel model,
        ImmutableArray<QuoteBump> bumps, string implementation, string numericalPolicy, OptionNumericalPolicy policy, bool fallbackDeclared,
        ISideDiscounting discounting, string observationRole, CancellationToken cancellation = default, RiskDetail detail = RiskDetail.Holdings,
        ImmutableArray<QuoteShift> stress = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (implementation != ScenarioPolicy)
            throw new MarketDataException("risk_convention", $"Select the {ScenarioPolicy} scenario policy.");
        if (model is null || snapshot is null)
            throw FuturesOptionInstrument.Error("option_context", "Supply an explicit market model and snapshot.");
        if (trades.IsDefaultOrEmpty || trades.Any(t => t is null))
            throw new MarketDataException("risk_position", "Select explicit live holdings before running risk.");
        Role(observationRole);
        var options = trades.SelectMany(t => t.Instruments).Select(p => p.Instrument as FuturesOptionInstrument ??
            throw new MarketDataException("risk_position", "This operation values futures-option holdings only.")).GroupBy(o => o.Id).Select(g =>
        {
            if (g.Any(o => o.Series != g.First().Series))
                throw FuturesOptionInstrument.Error("instrument_identity", "An exact option series key cannot reinterpret its immutable terms.");
            return g.First();
        }).OrderBy(o => o.Id).ToArray();
        var requests = options.SelectMany(o =>
        {
            RequireLive(o.Series, snapshot, $"Option series {o.Series.Key.Code}");
            var (underlying, settlement) = Requests(model, o.Series, observationRole);
            return new[] { underlying, settlement };
        }).Distinct().ToImmutableArray();
        var basis = model.Build(snapshot, requests, cancellation);
        var bindings = options.ToImmutableDictionary(o => o.Id,
            o => Prepare(o, basis, snapshot, discounting, observationRole, policy, fallbackDeclared, cancellation));
        var portfolio = ValuationPortfolio.FromTrades(trades,
            new(ValuationMeasure.PresentValue, ImmutableDictionary<Guid, FuturesMarketBinding>.Empty, null) { OptionBindings = bindings });
        var result = FuturesRisk.Calculate(basis, portfolio, snapshot, model, bumps, numericalPolicy, cancellation, detail, stress);
        return result with { OptionCalibrations = [.. options.Select(o => bindings[o.Id].Receipt)], ScenarioPolicy = ScenarioPolicy };
    }

    // The two curve requests an option series is calibrated from: its underlying future's and its own settlement, each
    // the one curve in the model observing that contract under the selected role.
    public static (CurveRequest Underlying, CurveRequest Settlement) Requests(CurveModel model, FuturesOptionSeriesSpecification series, string observationRole)
    {
        if (model is null || series is null)
            throw FuturesOptionInstrument.Error("option_context", "Supply an explicit market model and option series.");
        return Requests(model, model.ListedRequests(ContractKind.Future, series.UnderlyingSeries)
            .Concat(model.ListedRequests(ContractKind.FutureOption, series.Key)), series, Role(observationRole), $"Option series {series.Key.Code}");
    }

    private static ObservationRole Role(string observationRole) => observationRole == DailySettlement ? ObservationRole.DailySettlement
        : throw FuturesOptionInstrument.Error("option_observation_role", $"Observation role '{observationRole}' is not supported; release 1 values options at {DailySettlement}.");

    private static void RequireLive(FuturesOptionSeriesSpecification series, MarketSnapshot snapshot, string label)
    {
        if (snapshot.ValuationAt >= series.Terms.ExpiryAt)
            throw FuturesOptionInstrument.Error("option_expired", $"{label}: valuation at {snapshot.ValuationAt:O} is not before expiry at {series.Terms.ExpiryAt:O}.");
    }

    private static (CurveRequest Underlying, CurveRequest Settlement) Requests(CurveModel model, IEnumerable<CurveRequest> available,
        FuturesOptionSeriesSpecification series, ObservationRole role, string label)
    {
        var candidates = available.ToArray();
        return (Select(model, candidates, ContractKind.Future, series.UnderlyingSeries, role, $"{label}: underlying futures", "option_underlying_missing"),
            Select(model, candidates, ContractKind.FutureOption, series.Key, role, $"{label}: settlement", "option_settlement_missing"));
    }

    // The one curve observing the series' contract under the selected role. Another
    // role is never a stand-in.
    private static CurveRequest Select(CurveModel model, CurveRequest[] available, ContractKind kind, ReferenceKey series,
        ObservationRole role, string label, string missingCode)
    {
        var listed = available.Where(r => model.ContractDefinition(r.Contract) is var c && c.Kind == kind && c.FuturesSeries == series).ToArray();
        var matching = listed.Where(r => model.AssetDefinition(r.Curve).DirectRole == role).ToArray();
        return matching.Length switch
        {
            1 => matching[0],
            > 1 => throw FuturesOptionInstrument.Error("valuation_binding", $"{label} is observed by more than one curve under {role}."),
            _ => throw (listed.Length > 0
                ? FuturesOptionInstrument.Error("option_observation_role", $"{label} is not observed under {role}; another role is not a stand-in.")
                : FuturesOptionInstrument.Error(missingCode, $"{label} has no curve in the market.")),
        };
    }

    private static LatticeDecimal DiscountToExpiry(FuturesOptionInstrument option, ISideDiscounting discounting, MarketSnapshot snapshot, string label)
    {
        var expiry = new SidePaymentKey(option.Id, 0, option.Series.Terms.ExpiryAt, option.Series.Currency);
        var factors = discounting.Resolve([expiry], snapshot);
        if (factors.IsDefault || factors.Length != 1 || factors[0] is null || factors[0].Payment != expiry)
            throw FuturesOptionInstrument.Error("option_discount", $"{label}: the selected discounting supplies no factor to expiry.");
        return LatticeDecimal.From(factors[0].DiscountFactor);
    }
}
