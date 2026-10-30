using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using OpenEtrm.Shared.Temporal;

namespace OpenEtrm.MarketData.Trading;

[AttributeUsage(AttributeTargets.Property)]
public sealed class IdentityFieldAttribute : Attribute;

// A new economic contract must opt in with a reviewed canonicalization version.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EconomicDefinitionAttribute(string version) : Attribute
{
    public string Version { get; } = version;
}

public sealed record DefinitionIdentity(string Version, string Digest, string Canonical)
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Fields = new();

    public static DefinitionIdentity For(object definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var contract = definition.GetType().GetCustomAttribute<EconomicDefinitionAttribute>(false)
            ?? throw Error();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) Write(writer, definition);
        var bytes = stream.ToArray();
        return new(ContractVersion(definition, contract), Convert.ToHexStringLower(SHA256.HashData(bytes)), Encoding.UTF8.GetString(bytes));
    }

    internal static string CanonicalTerm(object term)
    {
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream))Write(writer,term);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string ContractVersion(object definition,EconomicDefinitionAttribute contract)=>
        definition switch
        {
            FloatingSidePeriod p when !p.ContractObservations.IsDefaultOrEmpty=>"floating_side_period_v2",
            FloatingProjectionBinding b when !b.ContractObservations.IsDefaultOrEmpty=>"floating_projection_binding_v2",
            _=>contract.Version
        };

    private static void Write(Utf8JsonWriter writer, object? value)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        var type = value.GetType();
        if (type.GetCustomAttribute<EconomicDefinitionAttribute>(false) is {} contract)
        {
            var fields = Fields.GetOrAdd(type, t => t.GetProperties().Where(p => p.IsDefined(typeof(IdentityFieldAttribute)))
                .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray());
            if (fields.Length == 0 || fields.Any(p => p.GetIndexParameters().Length != 0)) throw Error();
            writer.WriteStartObject(); writer.WriteString("contract", ContractVersion(value, contract));
            foreach (var field in fields)
            {
                if(field.Name==nameof(FloatingSidePeriod.ContractObservations)&&
                    (value is FloatingSidePeriod p&&p.ContractObservations.IsDefaultOrEmpty||value is FloatingProjectionBinding b&&b.ContractObservations.IsDefaultOrEmpty))continue;
                var selected=field.GetValue(value);
                writer.WritePropertyName(field.Name);Write(writer,selected);
            }
            writer.WriteEndObject();
        }
        else if (value is SideMembership member)
        {
            writer.WriteStartArray(); Write(writer, member.Side); Write(writer, member.Coefficient); if(member.QuantityRatio is {} ratio) Write(writer,ratio); writer.WriteEndArray();
        }
        else if (value is ExactDecimal number) writer.WriteStringValue(number.ToString());
        else if (value is IEnumerable sequence && value is not string)
        {
            writer.WriteStartArray(); foreach (var item in sequence) Write(writer, item); writer.WriteEndArray();
        }
        else if (value is bool flag) writer.WriteBooleanValue(flag);
        else if (value is string text) writer.WriteStringValue(text);
        else if (value is Guid id) writer.WriteStringValue(id);
        else if (value is int integer) writer.WriteNumberValue(integer);
        else if (value is Enum option) writer.WriteNumberValue(Convert.ToInt32(option, CultureInfo.InvariantCulture));
        else if (value is DateOnly date) writer.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        else if (value is DateTimeOffset instant) writer.WriteStringValue(instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        else WriteTerms(writer, value);
    }

    // Explicit encodings prevent a new convenience property on a value object
    // from silently changing a published canonicalization version.
    private static void WriteTerms(Utf8JsonWriter writer, object value)
    {
        object?[] terms = value switch
        {
            ReferenceRevision r => ["reference_revision", r.CatalogId, r.RevisionId],
            DefinitionRevision r => ["definition_revision", r.CatalogId, r.RevisionId],
            SeriesKey k => ["series", k.Revision, k.Id],
            ReferenceKey k => ["reference", k.Revision, k.Code],
            ContractKey k => ["contract", k.Revision, k.Id],
            CurveKey k => ["curve", k.Revision, k.Code],
            CurveRequest r => ["request", r.Curve, r.Contract],
            MarketContract c => ["market_contract", c.Key, c.Kind, c.DeliveryFrom, c.DeliveryTo, c.Expiry, c.FuturesSeries],
            FuturesSeriesSpecification s => ["futures_series", s.Key, s.QuoteQuantity, s.Currency, s.Multiplier, s.TradingTick,
                s.Location, s.DeliveryProfile, s.DeliveryFrom, s.DeliveryTo, s.LastTradingAt, s.LotQuantity, s.ContractSize, s.LotBasis, s.FirstTradingAt],
            QuoteSpecification q => ["quote", q.Key, q.Kind, q.Currency, q.Quantity, q.Multiplier, q.TradingTick, q.ClearingIncrement, q.Minimum, q.Maximum],
            Quantity q => ["quantity", q.Amount, q.Specification],
            OpenEtrm.Shared.Numerics.ExactRatio r => ["exact_ratio", r.Numerator.ToString(CultureInfo.InvariantCulture), r.Denominator.ToString(CultureInfo.InvariantCulture)],
            UnitPrice p => ["price", p.Value, p.Unit],
            PriceUnit u => ["price_unit", u.Currency, u.Quantity, u.Multiplier],
            FloatingObservation o => ["observation",o.From,o.To,o.AveragingQuantity],
            SwapProjection p => ["projection", p.Market, p.Contract, p.AveragingQuantity],
            CustomCurveProjectionBinding p => ["custom_projection", p.CatalogId, p.CurveId, p.Version,
                p.OutputNode, p.Position, p.Selection, p.Implementation],
            SwapObservationCalendar c => ["observation_calendar", c.Key, c.BusinessDays],
            PaymentCalendar c => ["payment_calendar", c.Key, c.BusinessDays],
            DateSet d => ["date_set", d.From, d.To, d.Dates],
            Power.AssetKey k => ["asset", k.Revision, k.Code],
            Power.VolumeSourceKey k => ["volume_source", k.Revision, k.Code],
            Power.DeliveryCoverage c => ["coverage", c.From, c.To],
            Power.DeliveryHours h => ["delivery_hours", h.Membership, h.Peak, h.Holidays],
            Power.TouPeak p => ["tou_peak", p.StartHour, p.EndHour, p.Saturday],
            PaymentRule r => ["payment_rule", PaymentRule.Implementation, r.Frequency, r.Rule, r.Days, r.DayOfMonth,
                r.DayBasis, r.ShortMonthPolicy, r.Adjustment, r.Calendar],
            _ => throw Error()
        };
        Write(writer, terms);
    }

    private static MarketDataException Error() => new("definition_identity", "Economic identity requires an explicitly versioned contract and supported marked fields.");
}

// Owned by one authorized operation, never a global cache or permission grant.
// TradingCatalogId names its owner namespace; authorized reference/market catalogs
// can differ. The caller resolves all references under current authority first.
// The canonical payload participates in equality, so a digest collision cannot
// merge distinct definitions. Booked quantities and market contexts stay outside.
public sealed class InstrumentIdentityMap
{
    public Guid TradingCatalogId { get; }
    private readonly Dictionary<DefinitionIdentity, Instrument> definitions = [];
    private readonly Dictionary<Guid, DefinitionIdentity> identities = [];
    private readonly Dictionary<Guid, (DefinitionIdentity Economics,DefinitionIdentity? Binding,string? Schedule)> sides = [];
    public int Count => definitions.Count;
    public InstrumentIdentityMap(Guid catalogId)
    {
        if (catalogId == Guid.Empty) throw new MarketDataException("instrument_identity", "An explicit authorized catalog is required.");
        TradingCatalogId = catalogId;
    }
    public T Intern<T>(T instrument) where T : Instrument
    {
        var key = instrument.EconomicIdentity;
        if (identities.TryGetValue(instrument.Id, out var prior) && prior != key)
            throw new MarketDataException("instrument_identity", "An exact instrument identity cannot reinterpret its immutable economics.");
        if (instrument is FuturesContract future && definitions.TryGetValue(key, out var economic) && economic is FuturesContract retainedFuture && retainedFuture.Series != future.Series)
            throw new MarketDataException("instrument_identity", "An exact series key cannot reinterpret immutable contract terms.");
        var members = (instrument is SidedInstrument composed ? composed.Sides : []).Select(m => m.Side)
            .Select(side => (side.Id, Identity: (Economics: DefinitionIdentity.For(side), Binding: side.PricingBindingIdentity,Schedule:side.RetainedSchedule?.Manifest))).ToArray();
        if (members.GroupBy(m => m.Id).Any(g => g.Select(m => m.Identity).Distinct().Count() != 1) ||
            members.Any(m => sides.TryGetValue(m.Id, out var retainedSide) && retainedSide != m.Identity))
            throw new MarketDataException("side_identity", "An exact side identity cannot reinterpret its immutable economics.");
        definitions.TryGetValue(key, out var retained);
        if (retained is not null && (retained.PricingBindingIdentity != instrument.PricingBindingIdentity||retained.RetainedScheduleIdentity!=instrument.RetainedScheduleIdentity))
            throw new MarketDataException("valuation_binding", "Shared contractual terms cannot replace explicitly different pricing bindings.");
        foreach (var member in members) sides[member.Id] = member.Identity;
        identities[instrument.Id] = key;
        if (retained is not null) return (T)retained;
        definitions.Add(key, instrument); return instrument;
    }

    public T Retain<T>(T instrument) where T : Instrument
    {
        if (definitions.TryGetValue(instrument.EconomicIdentity, out var prior) && prior.Id != instrument.Id)
            throw new MarketDataException("instrument_identity", "Equivalent retained economics cannot silently replace an exact published instrument identity.");
        return Intern(instrument);
    }
}
