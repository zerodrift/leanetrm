using System.Collections.Immutable;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Risk;

namespace OpenEtrm.MarketData.Trading;

[EconomicDefinition("retained_operating_member_v1")]
public sealed record RetainedOperatingMember:IRetainedProgramMember
{
    public Guid SideId {get;}
    public BookedSideProgram Program {get;}
    [IdentityField]public string ProgramDigest {get;}
    [IdentityField]public ExactDecimal Coefficient {get;}
    ExactDecimal IRetainedProgramMember.ValuationCoefficient=>Coefficient;
    OpenEtrm.Shared.Numerics.ExactRatio? IRetainedProgramMember.ValuationRatio=>null;
    public RetainedOperatingMember(Guid sideId,ExactDecimal coefficient,BookedSideProgram program)
    {
        if(sideId==Guid.Empty||program is null||program.Document.Family!="operating"||
            coefficient!=ExactDecimal.Parse("1")&&coefficient!=ExactDecimal.Parse("-1"))
            throw new MarketDataException("operating_membership","Retain an independently owned operating program and explicit membership sign.");
        SideId=sideId;Coefficient=coefficient;Program=program;ProgramDigest=BookedSideProgram.Digest(program.Write());
    }
}

[EconomicDefinition("retained_operating_right_v1")]
public sealed record RetainedOperatingInstrument:RetainedProgramInstrument<RetainedOperatingMember>
{
    [IdentityField]public override ImmutableArray<RetainedOperatingMember> Members {get;}
    [IdentityField]public AssetKey Resource {get;}
    [IdentityField]public override Quantity UnitQuantity {get;}
    [IdentityField]public override ReferenceKey Currency {get;}
    public override bool SupportsQuantityScaling=>false;
    public override bool SupportsFixedUnitBinding=>true;
    public RetainedOperatingInstrument(Guid id,AssetKey resource,Quantity unit,ReferenceKey currency,ImmutableArray<RetainedOperatingMember> members):base(id,members,unit,currency)
    {
        if(resource is null||resource.Revision.CatalogId==Guid.Empty||resource.Revision.RevisionId==Guid.Empty||string.IsNullOrWhiteSpace(resource.Code)||unit is null||unit.Amount!=ExactDecimal.Parse("1")||members.IsDefaultOrEmpty||members.Length>16||members.Any(m=>m is null)||
            unit.Specification.Revision!=currency.Revision||members.Any(m=>m.Program.Document.Instructions.Any(i=>i.Output.Currency is {} c&&c!=currency)))
            throw new MarketDataException("operating_instrument","Retain one exact complete right, resource version, currency and explicit independently signed program memberships.");
        Money.RequireKey(unit.Specification);Money.RequireKey(currency);
        Resource=resource;UnitQuantity=unit;Currency=currency;Members=members;
    }
    public override void ValidateBinding(Quantity quantity,DeliveryCoverage? coverage)
    {
        if(quantity!=UnitQuantity)throw new MarketDataException("operating_right_quantity","A complete exclusive right is bound exactly once.");
        base.ValidateBinding(quantity,coverage);
    }
    public override ExactMoney Value(UnitValuationContext context)
    {
        RequireUnstarted(context.Snapshot);
        return base.Value(context);
    }
    protected internal override void RequireBeforeValuation(SideValuationContext context) => RequireUnstarted(context.Snapshot);
    private void RequireUnstarted(MarketSnapshot snapshot)
    {
        if(Members.SelectMany(m=>m.Program.Document.Instructions).Where(i=>i.Output.Operation=="output_guard").Any(i=>i.Output.Interval!.Start<=snapshot.ValuationAt))
            throw new MarketDataException("operating_delivery_lifecycle","A started operating plan requires a separately qualified operational lifecycle.");
    }
    public override UnitPricingContract Declare(UnitValuationContext context)=>RetainedDeclaration(new(Id,"retained_supplied_operating_right_side_v2",
        [..Members.SelectMany(m=>m.Program.Document.Instructions).SelectMany(i=>i.Bindings).Where(b=>b.Kind=="contract_price").Select(b=>b.Market!.Value).Distinct()],
        UnitQuantity.Specification,Currency){Time=ValuationTimeDependency.Pinned,ScheduleIdentity=RetainedScheduleIdentity});
    protected override ScheduleMarketSources Sources(CurveBuild basis)=>ScheduleMarketSources.ForOperating(basis);
}
