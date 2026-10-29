using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PpaBreakEvenRequest([property:JsonRequired]Guid SnapshotId,[property:JsonRequired]PpaTermsInput Terms,
    [property:JsonRequired]string Quantity,[property:JsonRequired]string CoverageFrom,[property:JsonRequired]string CoverageTo,
    [property:JsonRequired]string KnownAt,[property:JsonRequired]string PendingActuals,[property:JsonRequired]PpaSlotInput SelectedSide,
    [property:JsonRequired]string Measure,[property:JsonRequired]int QuoteDecimalPlaces,[property:JsonRequired]string QuoteRounding);

internal sealed class PpaBreakEvenStore(PreviewDatabase database,PpaStore trades)
{
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){Converters={new ExactDecimalJsonConverter()}};
    internal static object Capabilities=>new{implementation=PpaBreakEvenPlan.Implementation,operation="ppa/break-even",label="Model break-even price",
        scope="unbooked_draft",permission="ReadTrades",compositions=new[]{"fixed_physical","virtual_fixed_for_floating"},selectedRole="strike_payment",priceTerm="flat",
        measures=new[]{"ValueExcludingOverdue","CompletePv"},quoteDecimalPlaces=new{min=0,max=18},quoteRounding=new[]{"half_even_v1"},basisEvaluations=2,
        limits=new{evaluatedRows=PpaCompiler.MaximumRequestRows,responseBytes=PpaCompiler.MaximumResponseBytes,deadlineSeconds=20},
        commercialRiskPremium=false,tradeMutation=false};
    internal async Task<object> Solve(NpgsqlConnection connection,InputFrame frame,PpaBreakEvenRequest request,CancellationToken ct)
    {
        PpaCompiler.Size(request.Terms);
        if(request.SelectedSide is null)throw PpaCompiler.Error("ppa_break_even_side","Select the exact flat strike payment membership.");
        var mw=PpaCompiler.Mw(request.Quantity);
        PpaCompiler.PreflightWork(new[]{"zero","one"}.Select(id=>(request.Terms,request.CoverageFrom,request.CoverageTo,id)));
        var known=await PpaQuantitiesStore.KnownAt(connection,request.KnownAt,ct);var pending=PpaQuantitiesStore.Policy(request.PendingActuals);
        var context=await trades.Bind(connection,frame,request.KnownAt,ct);var bundle=trades.SelectedManifest(frame);
        await PpaReads.RequireMarket(connection,database.Manifest.CatalogId,request.Terms.DefinitionRevisionId,request.Terms.ReferenceCatalogId,request.Terms.ReferenceRevisionId,frame,context,ct);
        var data=await trades.Definitions(connection,frame,false,ct);
        var tou=await PpaReads.Tou(connection,data,frame,context,[request.Terms],ct);
        var instrument=PpaCompiler.Compile(data,request.Terms,tou,await PpaCompiler.ReadQuantityTypes(connection,data,request.Terms,ct));await PpaCompiler.ValidateDefinitions(connection,data,instrument,ct);
        var coverage=PpaCompiler.Coverage(instrument,PpaQuantitiesStore.Clock(data,instrument.Delivery.Clock.Code),request.CoverageFrom,request.CoverageTo);
        var plan=PpaBreakEvenPlan.Create(instrument,new(PpaCompiler.Role(request.SelectedSide.Role),request.SelectedSide.Ordinal),coverage,mw,request.Measure,request.QuoteDecimalPlaces,request.QuoteRounding);
        PpaCompiler.Work(plan.Basis.Select(i=>(i,coverage)),[],i=>PpaQuantitiesStore.Clock(data,i.Delivery.Clock.Code));
        data=new PreviewData(bundle,data.Definitions,data.References,await PreviewDatabase.ReadObservationPins(connection,bundle,frame.ObservationIds,ct)){CalendarDaysRead=true};
        var bindings=plan.Basis.Select(i=>new PpaBinding(null,null,Guid.NewGuid(),i,coverage,mw)).ToImmutableArray();
        var prepared=await new PpaReads().Prepare(connection,data,frame,bindings,known,new(context,known),IntervalKnowledgePolicy.ResolvedCutoff,pending,[],ct);
        var session=prepared.Inputs.Session(prepared.Market);
        var result=plan.Solve(session,prepared.Discounting[plan.Basis[0].Id],ct);
        await trades.Recheck(connection,frame,ct);
        object Exclusions(ValueMeasureSet m)=>new{m.HasOverdue,m.OverdueKnownBalance,m.OverdueEstimated,m.RealisedDeliveryValue,m.EstimatedHoldingValue,m.ModeledFromFrozenAnchor};
        return JsonSerializer.SerializeToElement(new{implementation=PpaBreakEvenPlan.Implementation,label="Model break-even price",scope="unbooked_draft",
            measure=plan.Measure,measureDefinition=ValueMeasureSet.Label,coverage,quantity=mw.ToString(),contractQuantitySpecification=instrument.UnitQuantity.Specification,
            selectedSide=request.SelectedSide,price=result.Price,sensitivity=result.Sensitivity,sensitivityDefinition="Selected contract value change per +1 in the retained strike quote unit, at the supplied contract MW.",
            zeroStrikeValue=result.ZeroValue,oneStrikeValue=result.OneValue,inputValue=result.InputValue,
            quote=result.Quote,quoteDecimalPlaces=plan.QuoteDecimalPlaces,quoteRounding=plan.QuoteRounding,quoteIsExact=result.QuoteIsExact,
            exactPriceEnterable=result.ExactPriceEnterable,quoteResidual=result.QuoteResidual,
            assumptions=new{otherTermsAndInputs="held_fixed",discounting=instrument.Discounting,pendingActuals=request.PendingActuals,commercialRiskPremium=false,
                deliveredPhysicalValue="excluded_from_selected_measure",overdueCash="excluded; CompletePv unavailable when present"},
            exclusions=new{atZero=Exclusions(result.ZeroMeasures),atOne=Exclusions(result.OneMeasures)},
            receipt=new{request,termsIdentity=DefinitionIdentity.For(instrument).Digest,
                basis=plan.Basis.Select((i,n)=>new{strike=n.ToString(),instrumentId=i.Id,identity=DefinitionIdentity.For(i).Digest,memberships=i.Memberships.Select(m=>new{role=PpaCompiler.Role(m.Slot.Role),m.Slot.Ordinal,sideId=m.Side.Id,identity=DefinitionIdentity.For(m.Side).Digest})}),
                sideIdentityScope="request_local_basis_variants; not persisted side definitions",
                inputs=PpaStore.InputReceipt(prepared,context,request.PendingActuals),discountFactors=result.Factors.Select(f=>new{f.Payment,f.DiscountFactor,f.Source,snapshot=f.Snapshot.Id}),
                discountInputs=PpaReads.DiscountInputs(prepared.Discounting),solver=PpaBreakEvenPlan.Implementation,quotePolicy=plan.QuoteRounding},
            profiles=PpaPricingProfile.Create(plan,result),
            profileDefinition="Operating-month groups per side, quantity state, economic class, coverage and due date. Effective prices are quantity-weighted gross value / quantity / coefficient, including declared adders and multipliers, in currency per quantity unit. Zero quantity has no implied price. Physical value is not cash. No volumes are summed across sides.",
            work=new{session.RowEvaluations,session.UniqueSideCalculations,session.UnitValuations,prepared.Inputs.SeriesValidations,prepared.Inputs.SeriesSelections}},Json);
    }
}
