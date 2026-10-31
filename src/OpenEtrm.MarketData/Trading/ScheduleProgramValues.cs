using System.Collections.Immutable;
using System.Numerics;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Calculation;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// Untyped exact arithmetic is unbounded. The typed operation enforces its
// existing policy: in particular, cash intermediates retain ExactMoney's range.
public readonly record struct ScheduleNumber
{
    private readonly SignedRatio value;
    public BigInteger Numerator=>value.Numerator;
    public BigInteger Denominator=>value.Denominator;
    public ScheduleNumber(BigInteger numerator,BigInteger denominator)
    {
        if(denominator<=0)throw new MarketDataException("numeric_division","A schedule cell requires a positive denominator.");
        value=new(numerator,denominator);
    }
    private ScheduleNumber(SignedRatio value)=>this.value=value;
    public static ScheduleNumber Parse(string text)
    {
        if(text is null)throw new MarketDataException("numeric_format","An explicit exact amount is required.");
        var(n,d)=ExactNumberText.Parse(text);return new(n,d);
    }
    internal ScheduleNumber Add(ScheduleNumber other)=>new(value+other.value);
    internal ScheduleNumber Multiply(ScheduleNumber other)=>new(value*other.value);
    internal ScheduleNumber Divide(ScheduleNumber other)
    {
        if(other.Numerator<=0||other.Denominator<=0)throw new MarketDataException("numeric_division","The stored division requires a positive resolved denominator.");
        return new(Numerator*other.Denominator,Denominator*other.Numerator);
    }
    internal ExactDecimal Decimal()
    {
        var scaled=BigInteger.DivRem(Numerator*ExactDecimal.WorkingScale,Denominator,out var remainder);
        if(!remainder.IsZero)throw new MarketDataException("precision_loss","This input requires an exact working decimal without inferred rounding.");
        return ExactDecimal.FromScaled(scaled);
    }
    internal bool IsValid=>Denominator>0;
}

[Flags]
public enum SchedulePriceFacts:byte { None=0,Fixed=1,Projected=2,Actual=4,Modeled=8,PublishedFixing=16,Estimated=32 }
public readonly record struct ScheduleCellState(VolumeState? Quantity,SchedulePriceFacts Prices)
{
    public QuantityStage? Stage { get; init; }
    public static ScheduleCellState From(VolumeState? quantity,RowPriceState? price)=>new(quantity,price switch
    {
        null=>SchedulePriceFacts.None,RowPriceState.Fixed=>SchedulePriceFacts.Fixed,RowPriceState.Projected=>SchedulePriceFacts.Projected,
        RowPriceState.Actual=>SchedulePriceFacts.Actual,RowPriceState.Modeled=>SchedulePriceFacts.Modeled,
        RowPriceState.PublishedFixing=>SchedulePriceFacts.PublishedFixing,RowPriceState.Estimated=>SchedulePriceFacts.Estimated,
        _=>throw new MarketDataException("side_input_column","An input requires a supported price state.")
    });
}
public enum ScheduleCellPresence:byte { Resolved=1,NotDemanded,OutsideMeasure }
public readonly record struct ScheduleInputAddress(int Instruction,string ScenarioId);

public sealed record ScheduleValueColumn(string Kind,int? Grid,ReferenceKey? Unit,ReferenceKey? Currency,
    string? QuoteMultiplier,ImmutableArray<ScheduleNumber> Values,ImmutableArray<ScheduleCellState> States,ImmutableArray<ScheduleCellPresence> Presence)
{
    public static ScheduleValueColumn Input(ScheduleDefinitionRow output,ImmutableArray<ScheduleNumber> values,ImmutableArray<ScheduleCellState> states)=>
        new(output.OutputKind,output.Grid,output.Unit,output.Currency,output.QuoteMultiplier,values,states,[..Enumerable.Repeat(ScheduleCellPresence.Resolved,values.Length)]);

    internal void Require(ScheduleDefinitionRow output,int count,ImmutableArray<ScheduleInputLanes> uses,ScheduleInputLanes required,CancellationToken cancellation)
    {
        if(Kind!=output.OutputKind||Grid!=output.Grid||Unit!=output.Unit||Currency!=output.Currency||QuoteMultiplier!=output.QuoteMultiplier||
            Values.IsDefault||States.IsDefault||Presence.IsDefault||Values.Length!=count||States.Length!=count||Presence.Length!=count)
            throw new MarketDataException("side_input_column","A resolved column must match its complete stored type, unit, grid and cell count.");
        for(var cell=0;cell<count;cell++)
        {
            cancellation.ThrowIfCancellationRequested();
            var state=States[cell];var presence=Presence[cell];
            if(presence is not(ScheduleCellPresence.Resolved or ScheduleCellPresence.NotDemanded)||
                ((uses[cell]&required)!=0)!=(presence==ScheduleCellPresence.Resolved))
                throw new MarketDataException("side_input_column","Resolve exactly the demanded source cells; only a lifecycle guard can exclude PV.");
            if(presence==ScheduleCellPresence.NotDemanded)continue;
            if(!Values[cell].IsValid||state.Stage is {} stage&&!Enum.IsDefined(stage)||state.Quantity is {} quantity&&!Enum.IsDefined(quantity)||((int)state.Prices&~63)!=0||
                Kind=="price"&&(state.Prices==SchedulePriceFacts.None||state.Quantity is not null)||
                Kind=="quantity"&&(state.Quantity is null||state.Prices!=SchedulePriceFacts.None)||
                Kind=="factor"&&(state.Quantity is not null||state.Prices!=SchedulePriceFacts.None))
                throw new MarketDataException("side_input_column","Resolved values require explicit valid numbers and cell states.");
            if(Kind=="quantity"&&state.Quantity!=VolumeState.Contractual&&state.Stage is null||Kind!="quantity"&&state.Stage is not null)
                throw new MarketDataException("side_input_column","Resolved volume inputs require their explicit quantity stage.");
            if(Kind=="quantity"&&output.Operation=="quantity_input"&&output.Parameters["stage"]!="declared_port_v1"&&
                (!Enum.TryParse<QuantityStage>(output.Parameters["stage"],out var declared)||state.Stage!=declared))
                throw new MarketDataException("side_input_column","A quantity source must retain the stored requested stage.");
            if(Kind=="quantity")ExactQuantity.RequireRange(Values[cell].Numerator,Values[cell].Denominator);
            if(Kind=="factor"&&Values[cell].Decimal().CompareTo(default)<=0)
                throw new MarketDataException("side_payment_factors","A discount input must be a strictly positive exact working decimal.");
        }
    }
}

public readonly record struct ScheduleContributionState(ComponentKind Kind,EconomicClass? Class,PaymentLifecycle? Lifecycle,
    ValueCoverage Coverage,bool Modeled,string? UnavailableReason);
public sealed record ScheduleContribution(int Instruction,ScheduleValueColumn Amount,ScheduleValueColumn PresentValue,
    ImmutableArray<ScheduleContributionState> States);
public sealed record ScheduleLaneColumns(ScheduleValueColumn Base,ScheduleValueColumn Scenario);
public sealed record BookedSideEvaluation(ResolvedCalculationContext Context,string BaseScenarioId,string ScenarioId,string ScheduleManifest,
    ImmutableArray<ScheduleLaneColumns> Values,ImmutableArray<ScheduleDefinitionRoot> Roots,ImmutableArray<ScheduleContribution> Contributions)
{
    // Pure interpreter calls have no source evidence. Market-source execution supplies it.
    public ScheduleSourceReceipt? SourceReceipt { get; init; }
    public DeliveryCoverage? Coverage { get; init; }
    public ImmutableArray<string> RequestedOutputs { get; init; }=["pv"];
    public ImmutableArray<ScheduleQuantityDemand> QuantityOutputs { get; init; }=[];
    public const string NumericalPolicy="exact_schedule_program_v1";
    public ImmutableArray<ExactQuantity> QuantityValues(string output,ExactDecimal scale)
    {
        var selected=Roots.Where(root=>root.Name==output&&root.Measure=="schedule_quantity").ToArray();
        if(selected.Length!=1)throw new MarketDataException("side_schedule_program","Select one retained schedule quantity output.");
        var column=Values[selected[0].Row].Scenario;
        if(column.Kind!="quantity"||column.Unit is not {} unit||column.Presence.Any(p=>p!=ScheduleCellPresence.Resolved))
            throw new MarketDataException("side_schedule_program","Every exported quantity must have an exact unit and a resolved value.");
        return [..column.Values.Select(value=>new ExactQuantity(value.Numerator,value.Denominator,unit).Scale(scale))];
    }
    public ImmutableArray<ExactQuantity> QuantityColumn(int row,ExactDecimal scale)
    {
        if(row<0||row>=Values.Length)throw new MarketDataException("side_schedule_program","Select a retained quantity column.");
        var value=Values[row];
        if(value.Scenario.Kind!="quantity"||value.Scenario.Unit is not {} unit||value.Scenario.Presence.Any(p=>p!=ScheduleCellPresence.Resolved))
            throw new MarketDataException("side_schedule_program","Select one resolved retained quantity column with its exact native unit.");
        return [..value.Scenario.Values.Select(number=>new ExactQuantity(number.Numerator,number.Denominator,unit).Scale(scale))];
    }
    public bool HasOverdue=>Contributions.Any(contribution=>contribution.States.Any(state=>state.Coverage is ValueCoverage.OverdueBalance or ValueCoverage.OverdueEstimate));
    public ExactMoney? CompletePresentValue=>HasOverdue?null:PresentValue;
    public ExactMoney PresentValue
    {
        get
        {
            if(!RequestedOutputs.Contains("pv"))throw new MarketDataException("side_schedule_program","This execution did not request present value.");
            var value=Values[Roots.Single(root=>root.Name=="pv").Row].Scenario;
            if(value.Kind!="money"||value.Grid is not null||value.Values.Length!=1||value.Currency is not {} currency||value.Presence[0]!=ScheduleCellPresence.Resolved)
                throw new MarketDataException("side_schedule_program","The booked PV root must resolve to one exact cash amount.");
            var amount=value.Values[0];return ExactMoney.FromFraction(amount.Numerator,amount.Denominator,currency);
        }
    }
}
