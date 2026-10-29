using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Power;
using OpenEtrm.MarketData.Trading;

internal static class RetainedScheduleAdmissionChecks
{
    internal static void Run()
    {
        var fixture=new PpaAcceptanceFixture();
        static ScheduleDefinition Rewrite(ScheduleDefinition definition,
            IReadOnlyList<ScheduleDefinitionRow>? rows=null,IReadOnlyList<ScheduleDefinitionEdge>? edges=null,
            IReadOnlyList<ScheduleDefinitionRoot>? roots=null,IReadOnlyList<ScheduleDefinitionGrid>? grids=null)=>new(definition.SideId,definition.Family,
                definition.EconomicIdentity,definition.PricingBindingIdentity,definition.SchemaVersion,
                definition.GeneratorVersion,rows??definition.Rows,edges??definition.Edges,definition.Bindings,
                roots??definition.Roots,definition.Ports,grids??definition.Grids);
        static ScheduleDefinition MoveInputGrid(ScheduleDefinition definition,ScheduleDefinitionRow row,TimeSpan shift)
        {
            var grid=definition.Grids[row.Grid!.Value] with{Ordinal=definition.Grids.Count,Key="changed/input-grid",Origin=definition.Grids[row.Grid.Value].Origin+shift};
            return Rewrite(definition,rows:definition.Rows.Select(value=>value.Ordinal==row.Ordinal?value with{Grid=grid.Ordinal}:value).ToArray(),grids:[..definition.Grids,grid]);
        }
        var admitted=new List<string>();
        void Refuse(PpaInstrument instrument,Guid side,Func<ScheduleDefinition,ScheduleDefinition> change,string behavior)
        {
            try
            {
                var definitions=instrument.Sides.ToDictionary(member=>member.Side.Id,member=>ScheduleDefinitionCompiler.Generate(member.Side));
                definitions[side]=change(definitions[side]);
                _=RetainedSideSchedules.Attach(instrument,definitions);
            }
            catch(MarketDataException error)when(error.Code=="side_schedule_definition"){return;}
            admitted.Add(behavior);
        }
        var profile=fixture.Physical();var payment=profile.Memberships[1].Side.Id;
        Refuse(profile,profile.Memberships[0].Side.Id,definition=>Rewrite(definition,rows:definition.Rows.Select(row=>row.Operation=="price_lookup"
            ?row with{Parameters=row.Parameters.SetItem("selection","unsupported_physical_price_v1")}:row).ToArray()),"an unrecognized physical price lookup selection");
        Refuse(profile,profile.Memberships[0].Side.Id,definition=>new(definition.SideId,definition.Family,definition.EconomicIdentity,
            definition.PricingBindingIdentity,definition.SchemaVersion,definition.GeneratorVersion,definition.Rows,definition.Edges,
            definition.Bindings.Select(binding=>binding.Port=="location"?binding with{Coordinate="capacity"}:binding).ToArray(),definition.Roots,definition.Ports,definition.Grids),
            "an energy price lookup that silently substitutes the capacity attribute");
        try
        {
            var outsideTerm=profile.Sides.ToDictionary(member=>member.Side.Id,member=>
            {
                var definition=ScheduleDefinitionCompiler.Generate(member.Side);
                return Rewrite(definition,grids:definition.Grids.Select(grid=>grid with{Origin=grid.Origin.AddYears(1),Month=grid.Month.AddYears(1),
                    OperatingDays=[..grid.OperatingDays.Select(day=>DateOnly.FromDayNumber(day).AddYears(1).DayNumber)]}).ToArray());
            });
            _=RetainedSideSchedules.Attach(profile,outsideTerm);
            admitted.Add("an obligation and its sibling input moved outside the explicit side term");
        }
        catch(MarketDataException error)when(error.Code=="side_schedule_definition"){}
        Refuse(profile,payment,definition=>
        {
            var pv=definition.Roots.Single(root=>root.Name=="pv").Row;
            return Rewrite(definition,edges:definition.Edges.Where(edge=>edge.Target!=pv||edge.Ordinal!=0)
                .Select(edge=>edge.Target==pv?edge with{Ordinal=edge.Ordinal-1}:edge).ToArray());
        },"a side PV that omits its first declared contribution");
        Refuse(profile,payment,definition=>Rewrite(definition,rows:definition.Rows.Select(row=>row.Operation=="output_guard"
            ?row with{Parameters=row.Parameters.SetItem("lifecycle","unsupported_lifecycle_v1")}:row).ToArray()),"an unrecognized lifecycle policy");
        Refuse(profile,payment,definition=>Rewrite(definition,rows:definition.Rows.Select(row=>row.Operation=="output_guard"
            ?row with{Parameters=row.Parameters.SetItem("pricePolicy","unsupported_price_policy_v1")}:row).ToArray()),"an unrecognized price policy");
        Refuse(profile,payment,definition=>Rewrite(definition,rows:definition.Rows.Select(row=>row.Operation=="discount"
            ?row with{Parameters=row.Parameters.SetItem("policy","unsupported_discount_v1")}:row).ToArray()),"an unrecognized discount policy");
        Refuse(profile,payment,definition=>
        {
            var guard=definition.Roots.First(root=>root.Measure=="contribution_pv").Row;
            var otherCash=definition.Edges.Single(edge=>edge.Target==guard&&edge.Port=="discounted").Source;
            return Rewrite(definition,edges:definition.Edges.Select(edge=>edge.Target==guard&&edge.Port=="amount"?edge with{Source=otherCash}:edge).ToArray());
        },"a guard whose cash and discounted branches refer to different obligations");
        var volume=fixture.Physical(volume:true);var physical=volume.Memberships[0].Side.Id;
        Refuse(volume,physical,definition=>
        {
            var basis=definition.Rows.First(row=>row.Operation=="asset_capacity"&&row.Parameters["measure"]=="capacity_basis_v1");
            return MoveInputGrid(definition,basis,TimeSpan.FromHours(1));
        },"a normalization source window outside its retained obligation");
        Refuse(volume,physical,definition=>
        {
            var source=definition.Rows.First(row=>row.Operation=="quantity_input");
            return MoveInputGrid(definition,source,TimeSpan.FromHours(1));
        },"a quantity source window different from its retained obligation");
        Refuse(profile,payment,definition=>
        {
            var source=definition.Rows.First(row=>row.Operation=="quantity_input");
            return MoveInputGrid(definition,source,TimeSpan.FromHours(1));
        },"a sibling input that silently reads another interval");
        var capacity=fixture.CapacityAgreement();var capacitySide=capacity.Memberships[0].Side.Id;
        Refuse(capacity,capacitySide,definition=>
        {
            var source=definition.Rows.First(row=>row.Operation=="asset_capacity"&&row.Parameters["measure"]=="accredited_capacity_v1");
            return MoveInputGrid(definition,source,TimeSpan.FromDays(1));
        },"an accredited asset evaluated on a different retained operating day");
        Refuse(capacity,capacitySide,definition=>
        {
            var daily=definition.Grids.Single(grid=>grid.FirstRowOrdinal is null);
            return Rewrite(definition,grids:definition.Grids.Select(grid=>grid.Ordinal==daily.Ordinal?grid with{
                Selection=grid.Selection[1..],Lengths=grid.Lengths[1..],OperatingDays=grid.OperatingDays[1..],LocalMinutes=grid.LocalMinutes[1..],Occurrences=grid.Occurrences[1..]}:grid).ToArray());
        },"an accredited asset that omits an operating day");
        var secondAsset=new AssetKey(fixture.Definitions,"synthetic_second_generator");
        var normalization=fixture.Normalization([fixture.Asset.Key,secondAsset]);
        var secondSource=new VolumeSourceKey(fixture.Definitions,"synthetic_second_generation");
        var twoSourceQuantity=new VolumeQuantity([fixture.Source.Key,secondSource],QuantityStage.Generated,fixture.Energy.Key);
        var twoSourceSide=new PowerPhysicalSide(Guid.NewGuid(),fixture.Terms(twoSourceQuantity,normalization:normalization),fixture.Asset.Location);
        var twoSourceCash=new FixedSide(Guid.NewGuid(),fixture.Terms(new SlotQuantity([new(PpaRole.EnergyDelivery)]),normalization:normalization),
            new FlatPrice(new(ExactDecimal.Parse("40"),fixture.EnergyPrice)));
        var twoSources=new PpaInstrument(Guid.NewGuid(),fixture.Definitions,fixture.Reference,
            [new(new(PpaRole.EnergyDelivery),twoSourceSide,ExactDecimal.Parse("1")),new(new(PpaRole.StrikePayment),twoSourceCash,ExactDecimal.Parse("-1"))],false,PpaInstrument.Implementation);
        Refuse(twoSources,twoSourceSide.Id,original=>
        {
            // Two legitimate source quantities are each priced once. The compact
            // power evaluator supports a single price on their total, so it must
            // refuse this different topology instead of pricing the total twice.
            var partitioned=new CertificateDeliverySide(twoSourceSide.Id,
                fixture.Terms(twoSourceQuantity,normalization:normalization,scheduleQuantity:fixture.Energy.Key),fixture.Energy.Key,
                ExactDecimal.Parse("1"),"asset_generation_at_stage_v1","held_at_current_vintage_mark_v1","none_recorded_v1");
            var template=ScheduleDefinitionCompiler.Generate(partitioned);
            var transforms=template.Rows.Where(row=>row.Operation=="quantity_transform").ToDictionary(row=>row.Ordinal,
                row=>template.Edges.Single(edge=>edge.Target==row.Ordinal&&edge.Port=="input").Source);
            var kept=template.Rows.Where(row=>!transforms.ContainsKey(row.Ordinal)).ToArray();
            var ordinals=kept.Select((row,n)=>(row.Ordinal,n)).ToDictionary(pair=>pair.Ordinal,pair=>pair.n);
            int Remap(int node)=>ordinals[transforms.GetValueOrDefault(node,node)];
            var rows=kept.Select(row=>row with{Ordinal=Remap(row.Ordinal),Parameters=row.Operation=="output_guard"
                ?row.Parameters.SetItem("lifecycle","interval_delivery_v1"):row.Parameters}).ToArray();
            var edges=template.Edges.Where(edge=>!transforms.ContainsKey(edge.Target))
                .Select(edge=>edge with{Target=Remap(edge.Target),Source=Remap(edge.Source)}).ToArray();
            var bindings=template.Bindings.Select(binding=>binding.Port=="location"
                ?new ScheduleDefinitionBinding(Remap(binding.Row),"location","location",fixture.Asset.Location,null,null,null,null,"energy",null)
                :binding with{Row=Remap(binding.Row)}).ToArray();
            return new(original.SideId,original.Family,original.EconomicIdentity,original.PricingBindingIdentity,original.SchemaVersion,
                original.GeneratorVersion,rows,edges,bindings,template.Roots.Select(root=>root with{Row=Remap(root.Row)}).ToArray(),[],template.Grids);
        },"a power delivery split into amounts that would each be evaluated using its total quantity");
        var twoSourceCertificate=new CertificateDeliverySide(Guid.NewGuid(),fixture.Terms(twoSourceQuantity,normalization:normalization,
            scheduleQuantity:fixture.Certificates.Key),fixture.Certificates.Key,ExactDecimal.Parse("1"),"asset_generation_at_stage_v1",
            "held_at_current_vintage_mark_v1","none_recorded_v1");
        var certificates=new PpaInstrument(Guid.NewGuid(),fixture.Definitions,fixture.Reference,
            [..twoSources.Memberships,new(new(PpaRole.CertificateDelivery),twoSourceCertificate,ExactDecimal.Parse("1"))],false,PpaInstrument.Implementation);
        Refuse(certificates,twoSourceCertificate.Id,definition=>
        {
            var prices=definition.Bindings.Where(binding=>binding.Port=="location"&&binding.Kind=="quantity_source").Take(2).ToArray();
            return new(definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
                definition.SchemaVersion,definition.GeneratorVersion,definition.Rows,definition.Edges,
                definition.Bindings.Select(binding=>binding==prices[0]?binding with{Code=prices[1].Code}:binding==prices[1]?binding with{Code=prices[0].Code}:binding).ToArray(),definition.Roots,definition.Ports,definition.Grids);
        },"certificate amounts whose retained price sources differ from their quantity sources");
        Refuse(twoSources,twoSourceSide.Id,definition=>
        {
            var sum=definition.Rows.First(row=>row.Operation=="quantity_add"&&row.Key.EndsWith("/source-total",StringComparison.Ordinal));
            var omitted=definition.Edges.Single(edge=>edge.Target==sum.Ordinal&&edge.Ordinal==0).Source;
            return Rewrite(definition,edges:definition.Edges.Where(edge=>edge.Target!=sum.Ordinal||edge.Ordinal!=0)
                .Select(edge=>edge.Target==sum.Ordinal?edge with{Ordinal=edge.Ordinal-1}:edge).ToArray(),
                roots:[..definition.Roots,new("omitted_source",omitted,"unused_quantity")]);
        },"a volume source omitted from one obligation but retained elsewhere under an unrelated root");
        var twoAssetSide=new CapacityDeliverySide(Guid.NewGuid(),fixture.Terms(new AccreditedQuantity([fixture.Asset.Key,secondAsset]),normalization:normalization),
            fixture.CapacityDays.Key,"mw_day_per_operating_day_v1");
        var twoAssetCash=new FixedSide(Guid.NewGuid(),fixture.Terms(new SlotQuantity([new(PpaRole.CapacityDelivery)]),normalization:normalization),
            new FlatPrice(new(ExactDecimal.Parse("0"),fixture.CapacityPrice)));
        var twoAssets=new PpaInstrument(Guid.NewGuid(),fixture.Definitions,fixture.Reference,
            [new(new(PpaRole.CapacityDelivery),twoAssetSide,ExactDecimal.Parse("1")),new(new(PpaRole.CapacityPayment),twoAssetCash,ExactDecimal.Parse("-1"))],false,PpaInstrument.Implementation);
        Refuse(twoAssets,twoAssetSide.Id,definition=>
        {
            var prices=definition.Bindings.Where(binding=>binding.Port=="location"&&binding.Kind=="asset").Take(2).ToArray();
            return new(definition.SideId,definition.Family,definition.EconomicIdentity,definition.PricingBindingIdentity,
                definition.SchemaVersion,definition.GeneratorVersion,definition.Rows,definition.Edges,
                definition.Bindings.Select(binding=>binding==prices[0]?binding with{Code=prices[1].Code}:binding==prices[1]?binding with{Code=prices[0].Code}:binding).ToArray(),definition.Roots,definition.Ports,definition.Grids);
        },"capacity amounts whose retained price assets differ from their quantity assets");
        Refuse(twoAssets,twoAssetSide.Id,definition=>
        {
            var accredited=definition.Rows.Where(row=>row.Operation=="asset_capacity"&&row.Parameters["measure"]=="accredited_capacity_v1").ToArray();
            var selected=accredited.First(row=>definition.Bindings.Single(binding=>binding.Row==row.Ordinal).Code==secondAsset.Code);
            return MoveInputGrid(definition,selected,TimeSpan.FromDays(1));
        },"one asset reading a different day while another preserves the side-wide daily grid");
        if(admitted.Count>0)throw new Exception("Retained admission accepted unsupported graph semantics: "+string.Join("; ",admitted));
    }
}
