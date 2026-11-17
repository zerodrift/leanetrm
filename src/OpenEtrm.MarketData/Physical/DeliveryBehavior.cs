using System.Collections.Immutable;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.MarketData.Physical;

// Common immutable assessment output, not another quantity store or an
// authorization token. Native adapters retain the exact scope, source closure,
// business/knowledge context and relational ownership of every supplied input.
public sealed record DeliveryCapability(string Code, string Label);
public sealed record DeliveryMeasure(string Code, string Label, string Value);
public sealed record DeliveryActivity(Guid SourceId, string Stage, string Label,
    DeliveryGeometry Geometry, ReferenceKey Location, string Quantity);
public sealed record DeliveryConstraint(string Code, string Label, string State,
    Guid? SourceId, DeliveryGeometry? Geometry, string? Observed, string? Limit,
    string? Residual, string Basis);
public sealed record DeliveryAssessment(string Profile, ReferenceKey QuantitySpecification,
    ImmutableArray<DeliveryActivity> Activities, ImmutableArray<DeliveryMeasure> Measures,
    ImmutableArray<DeliveryConstraint> Constraints, ImmutableArray<DeliveryCapability> Capabilities);

// Intervals and discrete transfers have distinct geometry. A transfer is never
// represented by a zero-length interval or interpreted as a constant flow.
public abstract record DeliveryGeometry
{
    public abstract string Kind { get; }
    public abstract DateTimeOffset From { get; }
    public abstract DateTimeOffset? Through { get; }
    protected static void RequireInstant(DateTimeOffset at)
    {
        if (!TradingInstant.IsSupported(at))
            throw new MarketDataException("delivery_behavior_input", "Retain supported exact UTC delivery instants.");
    }
}

public sealed record DeliveryIntervalGeometry : DeliveryGeometry
{
    public override string Kind => "interval";
    public override DateTimeOffset From { get; }
    public override DateTimeOffset? Through { get; }
    public DeliveryIntervalGeometry(DateTimeOffset from, DateTimeOffset through)
    {
        RequireInstant(from); RequireInstant(through);
        if (from >= through) throw new MarketDataException("delivery_behavior_input", "A delivery interval requires increasing explicit boundaries.");
        From = from; Through = through;
    }
}

public sealed record DeliveryTransferGeometry : DeliveryGeometry
{
    public override string Kind => "transfer";
    public override DateTimeOffset From { get; }
    public override DateTimeOffset? Through => null;
    public DeliveryTransferGeometry(DateTimeOffset at) { RequireInstant(at); From = at; }
}

// Only pure behavior is specialized. Reads, current authority, expected-head
// fences and idempotent commit operations remain in the established adapters.
public abstract class DeliveryBehavior<TInputs> where TInputs : class
{
    public abstract string Profile { get; }
    public DeliveryAssessment Assess(TInputs inputs)
    {
        if (inputs is null) throw Invalid();
        return AssessResolved(inputs);
    }
    protected abstract DeliveryAssessment AssessResolved(TInputs inputs);
    protected static MarketDataException Invalid() => new("delivery_behavior_input",
        "Supply the complete supported native delivery inputs; missing evidence has no default value.");
}
