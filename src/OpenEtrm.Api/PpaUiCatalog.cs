using System.Text.Json;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

// Authoring metadata only: no observations, generated schedules or inferred terms.
internal static class PpaUiCatalog
{
    internal static string[] UnsupportedFeatures => [
        "nonlinear_contract_clauses", "custom_contractual_interval_profiles", "continuous_escalation",
        "resampling", "percentage_of_output_normalization", "cross_revision_valuation",
        "partial_month_capacity_pricing", "delivery_services", "fx", "payment_grouping",
        "accepted_settlement", "payment_recording", "runtime_templates", "historical_session_valuation"];
    internal static object References(PreviewData data)=>new {
        currencies=data.References["currencies"],quantitySpecs=data.References["quantity_specifications"],units=data.References["units"],
        clocks=data.References["clock_rules"],calendars=data.References["calendars"],locations=data.References["locations"]};
    internal static object Definitions(PreviewData data)
    {
        var curves=data.Definitions["curves"];
        object Curve(JsonElement row,string code,string name,string kind)
        {
            var quote=data.References["quote_specifications"].Single(q=>q.Text("code")==row.Text("quote_code"));
            return new{code,name,kind,quote,deliveryProfile=row.Text("profile_code"),location=row.OptionalText("location_code")};
        }
        var intervals=data.Definitions["contract_intervals"].Select(c=>c.Id("contract_id")).ToHashSet();
        var marked=data.Definitions["curve_members"].GroupBy(m=>m.Text("curve_code")).Where(g=>g.All(m=>intervals.Contains(m.Id("contract_id")))).Select(g=>g.Key).ToHashSet();
        var supported=curves.Where(c=>marked.Contains(c.Text("code")) ||
            data.References["settlement_targets"].Any(t=>t.Text("code")==c.OptionalText("settlement_target_code") && t.Text("kind")=="index"));
        return new{generationAssets=data.Definitions["generation_assets"],assetVersions=data.Definitions["generation_asset_versions"],volumeSources=data.Definitions["volume_sources"],
            priceCurves=supported.Select(c=>Curve(c,c.Text("code"),c.Text("name"),marked.Contains(c.Text("code"))?"interval":"period"))
                .Concat(data.Definitions["shaped_curves"].Select(s=>{
                    var block=data.Definitions["shaped_curve_blocks"].First(b=>b.Text("shaped_curve_code")==s.Text("code") && b.Text("anchor_role")=="period");
                    return Curve(curves.Single(c=>c.Text("code")==block.Text("block_curve_code")),s.Text("code"),s.Text("name"),"shaped");})).ToArray()};
    }
    private static object Member(string role,int ordinal,string kind,string[] quantities,params PpaSlotInput[] slots)=>new{role,ordinal,sideKind=kind,sideKinds=role is "certificate_payment" or "capacity_payment"?new[]{"fixed","indexed"}:new[]{kind},quantityKinds=quantities,linkedSlots=slots};
    private static object[] CertificateMembers=>[Member("certificate_delivery",0,"certificate",["volume"]),Member("certificate_payment",0,"fixed",["slots"],new PpaSlotInput("certificate_delivery",0))];
    private static object[] PhysicalMembers=>[Member("fixed_charge",0,"fixed",["slots"],new PpaSlotInput("energy_delivery",0)),..CertificateMembers];
    internal static object[] Compositions=>[
        new{id="fixed_physical",label="Fixed physical",optionalMembers=PhysicalMembers,members=new[]{Member("energy_delivery",0,"energy",["volume","profile"]),Member("strike_payment",0,"fixed",["slots"],new PpaSlotInput("energy_delivery",0))}},
        new{id="indexed_physical",label="Indexed physical",optionalMembers=PhysicalMembers,members=new[]{Member("energy_delivery",0,"energy",["volume","profile"]),Member("indexed_payment",0,"indexed",["slots"],new PpaSlotInput("energy_delivery",0))}},
        new{id="virtual_fixed_for_floating",label="Virtual fixed for floating",optionalMembers=CertificateMembers,members=new[]{Member("indexed_payment",0,"indexed",["volume","profile"]),Member("strike_payment",0,"fixed",["volume","profile"])}},
        new{id="basis_cash",label="Basis cash",optionalMembers=Array.Empty<object>(),members=new[]{Member("indexed_payment",0,"indexed",["volume","profile"]),Member("indexed_payment",1,"indexed",["volume","profile"])}},
        new{id="capacity_agreement",label="Capacity agreement",optionalMembers=Array.Empty<object>(),members=new[]{Member("capacity_delivery",0,"capacity",["accredited"]),Member("capacity_payment",0,"fixed",["slots"],new PpaSlotInput("capacity_delivery",0))}}];
    internal static object Policies=>new {
        instrumentImplementations=new[]{"ppa_instrument_v1"},quantityStages=new[]{"generated","delivered"},hourMemberships=new[]{"all","peak","off"},
        escalationBases=new[]{"anniversary_step_v1"},certificateEligibility=new[]{"asset_generation_at_stage_v1"},certificateHolding=new[]{"held_at_current_vintage_mark_v1"},
        certificateDisposition=new[]{"none_recorded_v1"},capacityRules=new[]{"mw_day_per_operating_day_v1"},discounting=new[]{false,true}};
}
