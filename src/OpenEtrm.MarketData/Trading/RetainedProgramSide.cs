using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenEtrm.Shared.Numerics;

namespace OpenEtrm.MarketData.Trading;

// Source admission remains explicit and commodity-owned. No adapter calls an
// Instrument PV or reconstructs its authored Side from current reference data.
internal abstract record RetainedProgramSources
{
    internal abstract ScheduleMarketSources Resolve(CurveBuild basis);
    internal abstract ImmutableArray<CurveRequest> Dependencies(BookedSideProgram program,MarketSnapshot? snapshot);
    // Existing admitted source readers retain their exact family-specific
    // bindings. These functions select inputs only; Side PV owns execution.
    internal sealed record Admitted(Func<CurveBuild,ScheduleMarketSources> Reader):RetainedProgramSources
    {
        internal override ScheduleMarketSources Resolve(CurveBuild basis)=>Reader(basis);
        internal override ImmutableArray<CurveRequest> Dependencies(BookedSideProgram program,MarketSnapshot? snapshot)=>
            [..program.Document.Instructions.SelectMany(i=>i.Bindings).Select(b=>b.Market).OfType<CurveRequest>().Distinct()];
    }
    public sealed record Operating:RetainedProgramSources
    {
        internal override ScheduleMarketSources Resolve(CurveBuild basis)=>ScheduleMarketSources.ForOperating(basis);
        internal override ImmutableArray<CurveRequest> Dependencies(BookedSideProgram program,MarketSnapshot? snapshot)=>
            [..program.Document.Instructions.SelectMany(i=>i.Bindings).Where(b=>b.Kind=="contract_price").Select(b=>b.Market!.Value).Distinct()];
    }
}

[EconomicDefinition("retained_program_side_v1")]
public sealed record RetainedProgramSide:Side
{
    public BookedSideProgram Program {get;}
    [IdentityField]public string ProgramDigest=>Program.Identity.ContentDigest;
    [IdentityField]public string EconomicTerms=>Program.Document.EconomicIdentity;
    // The program retains calendar definitions, purpose bindings and resolved
    // grids. It deliberately has no replacement authoring calendar object.
    public new SideCalendarPolicy CalendarPolicy=>base.CalendarPolicy;
    public Quantity UnitQuantity {get;}
    public ReferenceKey Currency {get;}
    internal RetainedProgramSources Sources {get;}
    public override DefinitionIdentity? PricingBindingIdentity=>string.IsNullOrEmpty(Program.Document.PricingBindingIdentity)?null:
        new("retained_program_pricing_v1",Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Program.Document.PricingBindingIdentity))),Program.Document.PricingBindingIdentity);
    public override ImmutableArray<CurveRequest> Dependencies=>Sources.Dependencies(Program,null);
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot)=>Sources.Dependencies(Program,snapshot);
    private readonly RetainedProgramSchedule schedule;
    internal RetainedProgramSide(Guid id,BookedSideProgram program,Quantity unit,ReferenceKey currency,RetainedProgramSources sources):base(id)
    {
        if(program is null||unit is null||unit.Amount!=ExactDecimal.Parse("1")||unit.Specification.Revision!=currency.Revision||sources is null||
            program.Document.Instructions.Any(i=>i.Output.Currency is {} c&&c!=currency))
            throw new MarketDataException("retained_side","Select one admitted executable program with its exact unit, currency and authorized source bindings.");
        Money.RequireKey(unit.Specification);Money.RequireKey(currency);
        Program=program;UnitQuantity=unit;Currency=currency;Sources=sources;schedule=new(this);
    }
    public static RetainedProgramSide ForOperating(Guid id,BookedSideProgram program,Quantity unit,ReferenceKey currency)
    {
        if(program is null||program.Document.Family!="operating"||unit is null)
            throw new MarketDataException("retained_side","Select the admitted operating program and its exact declared unit.");
        try
        {
            using var economic=JsonDocument.Parse(program.Document.EconomicIdentity);
            if(!economic.RootElement.TryGetProperty(nameof(FixedSide.OperatingDefinition),out var terms)||terms.ValueKind!=JsonValueKind.Object||
                !terms.TryGetProperty(nameof(OperatingSideTerms.UnitQuantity),out var declared)||declared.GetRawText()!=DefinitionIdentity.CanonicalTerm(unit))
                throw new MarketDataException("retained_side","The operating Side must retain the exact one-right or certificate unit sealed in its program.");
        }
        catch(JsonException){throw new MarketDataException("retained_side","The admitted operating program requires its sealed economic terms.");}
        return new(id,program,unit,currency,new RetainedProgramSources.Operating());
    }
    public override RetainedProgramSchedule GenerateSchedule()=>schedule;
    public override RetainedProgramSideValuation PV(SideValuationContext context)=>GenerateSchedule().Evaluate(context);
}

public sealed record RetainedProgramSchedule:SideSchedule
{
    public RetainedProgramSide Side {get;}
    public BookedSideProgram Program=>Side.Program;
    public override ImmutableArray<CurveRequest> Dependencies=>Side.Dependencies;
    public override ImmutableArray<CurveRequest> DependenciesAt(MarketSnapshot snapshot)=>Side.DependenciesAt(snapshot);
    public override ImmutableArray<SchedulePriceTerm> PriceTerms=>[];
    // Native block columns are the schedule. Materializing replacement scalar
    // rows would discard its geometry, source policies and retained addresses.
    public override IReadOnlyList<ScheduleRow> Rows=>throw new MarketDataException("retained_schedule_rows","Inspect the retained program's native schedule columns or evaluated schedule snapshot.");
    internal RetainedProgramSchedule(RetainedProgramSide side):base(side.Id,side.Currency,side.UnitQuantity,BookedSideProgram.CurrentVersion){Side=side;}
    public override RetainedProgramSideValuation Evaluate(SideValuationContext context)
    {
        var execution=context?.RetainedExecution??throw new MarketDataException("native_program_context","Retained Side valuation requires its exact base/scenario inputs.");
        return new(this,context!,Side.Sources.Resolve(execution.Basis).Evaluate(Program,SideId,execution.Scenario,context!.Discounting,execution.Cancellation));
    }
}

public sealed record RetainedProgramSideValuation:SideValuation
{
    public BookedSideEvaluation Execution {get;}
    internal RetainedProgramSideValuation(RetainedProgramSchedule schedule,SideValuationContext context,BookedSideEvaluation execution):base(schedule,context,execution.PresentValue){Execution=execution;}
    public override SideScheduleSnapshot InspectSchedule(string manifest,ExactDecimal scale,SignedRatio coefficient,Func<DateOnly,DateTimeOffset> boundary)=>
        SideScheduleSnapshot.FromProgramValue(this,Execution,((RetainedProgramSchedule)Schedule).Program,manifest,scale,coefficient,boundary);
}

public sealed partial class SideValuationContext
{
    internal RetainedProgramExecutionContext? RetainedExecution {get;private set;}
    public static SideValuationContext ForRetainedPrograms(CurveBuild basis,CurveBuild scenario,ISideDiscounting discounting,CancellationToken cancellation=default)=>
        new SideValuationContext(scenario,discounting).WithRetainedPrograms(basis,scenario,cancellation);
    internal SideValuationContext WithRetainedPrograms(CurveBuild basis,CurveBuild scenario,CancellationToken cancellation)
    {
        if(basis is null||scenario is null||!scenario.Snapshot.HasSameInputs(Snapshot)||!basis.Snapshot.HasSameInputs(Snapshot))
            throw new MarketDataException("native_program_context","Retain the exact source snapshot and declared base/scenario market inputs.");
        var copy=(SideValuationContext)MemberwiseClone();copy.RetainedExecution=new(basis,scenario,cancellation);return copy;
    }
}
internal sealed record RetainedProgramExecutionContext(CurveBuild Basis,CurveBuild Scenario,CancellationToken Cancellation);
