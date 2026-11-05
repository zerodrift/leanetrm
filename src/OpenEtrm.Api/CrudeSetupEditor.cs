using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;
using OpenEtrm.MarketData.Crude;
using OpenEtrm.MarketData.Trading;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CrudeSetupSaveRequest(Guid OperationId,[property:JsonRequired]Guid? BaseSetupId,[property:JsonRequired]JsonElement Configuration);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CrudeSetupVersionRequest(Guid SetupId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CrudeSetupPublishRequest(Guid OperationId,Guid SetupId);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CrudeSetupPreviewRequest(Guid SnapshotId,Guid SetupId,string KnowledgeMode,string DeliveryFrom,string DeliveryTo,string Quantity);

// Delivery masters and pricing sources have independent exact revisions. A setup
// selects an admitted source; it never creates curves, observations or defaults.
public sealed class CrudeSetupEditor(PreviewDatabase database)
{
 private Guid Catalog=>database.Manifest.CatalogId;
 private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 private static MarketDataException Invalid(string message)=>new("crude_setup_input",message);
 private static JsonElement One(ImmutableArray<JsonElement> rows)=>rows.Length==1?rows[0]:throw Invalid("Select one visible exact delivery setup.");
 private IEnumerable<Guid> Sources()=>new[]{database.Manifest.RevisionId}
  .Concat((database.Manifest.SwapMarkets??[]).Select(m=>m.RevisionId))
  .Concat(database.Manifest.CrudeMarket is {} c?[c.RevisionId]:[])
  .Concat((database.Manifest.RetainedCrudeMarkets??[]).Select(m=>m.RevisionId)).Distinct();
 internal static async Task<Guid[]> CurrentSetups(NpgsqlConnection c,Guid catalog,CancellationToken ct)
 {
  await using var q=ScheduleStore.Command(c,"SELECT security.current_crude_setups($1)",catalog);
  return await q.ExecuteScalarAsync(ct) is Guid[] ids?ids:throw Invalid("The current setup scope is unavailable.");
 }
 internal static InputFrame SourceFrame(PreviewManifest manifest,InputFrame frame,Guid revision)
 {
  if(revision==manifest.RevisionId)return frame;
  var candidates=(manifest.SwapMarkets??[]).Where(m=>m.RevisionId==revision).Select(m=>m.Frames)
   .Concat(manifest.CrudeMarket is {} c&&c.RevisionId==revision?[c.Frames]:[])
   .Concat((manifest.RetainedCrudeMarkets??[]).Where(m=>m.RevisionId==revision).Select(m=>m.Frames)).ToArray();
  if(candidates.Length!=1)throw Invalid("Select one explicitly admitted original source bundle.");
  var frames=candidates[0].Where(f=>f.At==frame.At).ToArray();
  if(frames.Length!=1||frames[0].ObservationIds is null||frames[0].CurveContracts is null)throw Invalid("The source requires exact price inputs at the selected market date.");
  var selected=frames[0];var pins=frame.SwapMarketSelections?.GetValueOrDefault(revision)??[..selected.ObservationIds];
  if(pins.Distinct().Count()!=pins.Length)throw Invalid("Source price inputs must be distinct.");
  var contracts=selected.CurveContracts.ToImmutableDictionary(p=>p.Key,p=>p.Value.ToImmutableArray());
  if((manifest.SwapMarkets??[]).Any(m=>m.RevisionId==revision))
   return frame.ForSwapMarket(pins,contracts) with{SwapMarketSelections=null,PpaBundle=null};
  return frame with{ObservationIds=pins,CurveContracts=contracts,
   FrozenObservationIds=selected.FrozenObservationIds is {} frozen?[..frozen]:[],OriginCloseId=null,SwapMarketSelections=null,PpaBundle=null};
 }
 internal async Task<ReferenceRevision> DeliveryRevision(NpgsqlConnection c,CancellationToken ct)
 {
  var definition=database.Manifest.ReferenceCatalog?.DefinitionRevisionId??database.Manifest.RevisionId;
  var row=One(await PreviewDatabase.Rows(c,"SELECT row_to_json(r)::text FROM market_data.revisions r WHERE(catalog_id,id)=($1,$2)",[Catalog,definition],ct));
  if(row.Text("state")!="sealed")throw Invalid("Delivery masters require a sealed declared reference bundle.");
  var revision=new ReferenceRevision(row.Id("reference_catalog_id"),row.Id("reference_revision_id"));
  // A separately pinned definition may own a different reference catalog from
  // the pricing bundle. Its immutable FK supplies that catalog identity.
  var expected=database.Manifest.ReferenceCatalog?.ReferenceRevisionId??database.Manifest.ReferenceRevisionId;
  if(revision.RevisionId!=expected)throw Invalid("The delivery master bundle does not match its declared exact reference revision.");
  return revision;
 }
 // The delivery clocks the crude profile admits: an explicit UTC clock, as PhysicalCrudeStore requires of every booking.
 internal static Task<ImmutableArray<JsonElement>> ProfileClocks(NpgsqlConnection c,ReferenceRevision revision,CancellationToken ct)=>
  PreviewDatabase.Rows(c,"SELECT row_to_json(k)::text FROM ref_data.clock_rules k WHERE(catalog_id,revision_id)=($1,$2) AND kind='fixed_offset' AND fixed_offset_seconds=0 ORDER BY code",[revision.CatalogId,revision.RevisionId],ct);
 public async Task<object> CatalogRead(NpgsqlConnection c,CancellationToken ct,TradingView? view=null,Guid? proposalId=null)
 {
  async Task<ImmutableArray<JsonElement>> Versions()=>await PreviewDatabase.Rows(c,"SELECT row_to_json(x)::text FROM(SELECT v.*,r.reference_catalog_id source_reference_catalog_id FROM market_data.crude_setup_versions v JOIN market_data.revisions r ON(r.catalog_id,r.id)=(v.catalog_id,v.definition_revision_id) WHERE v.catalog_id=$1 ORDER BY v.publication_order DESC NULLS LAST,v.id LIMIT 100)x",[Catalog],ct);
  async Task<JsonElement?> Proposal()
  {
   if(proposalId is not {} selected)return null;
   var rows=await PreviewDatabase.Rows(c,"SELECT row_to_json(x)::text FROM(SELECT v.*,r.reference_catalog_id source_reference_catalog_id FROM market_data.crude_setup_versions v JOIN market_data.revisions r ON(r.catalog_id,r.id)=(v.catalog_id,v.definition_revision_id) WHERE(v.catalog_id,v.id)=($1,$2))x",[Catalog,selected],ct);
   return rows.Length==1?rows[0]:throw Invalid("The exact saved proposal is unavailable under this context and current access.");
  }
  var working=view?.Working??true;var versions=working?await Versions():[];var proposal=working?await Proposal():null;
  var proposals=working&&proposalId is null?await PreviewDatabase.Rows(c,"SELECT row_to_json(x)::text FROM(SELECT v.*,r.reference_catalog_id source_reference_catalog_id FROM market_data.crude_setup_versions v JOIN market_data.revisions r ON(r.catalog_id,r.id)=(v.catalog_id,v.definition_revision_id) WHERE v.catalog_id=$1 AND v.state='draft' ORDER BY v.id LIMIT 101)x",[Catalog],ct):[];
  if(proposals.Length>100)throw Invalid("The unpublished proposal review exceeds one hundred entries. Read an exact proposal identity.");
  if(view is not null)await view.Bind(c,Catalog,ct);
  if(!working){versions=await Versions();proposal=await Proposal();}
  var heads=await CurrentSetups(c,Catalog,ct);
  var contextSetups=await PreviewDatabase.Rows(c,"SELECT row_to_json(x)::text FROM(SELECT v.*,r.reference_catalog_id source_reference_catalog_id FROM market_data.crude_setup_versions v JOIN market_data.revisions r ON(r.catalog_id,r.id)=(v.catalog_id,v.definition_revision_id) WHERE v.catalog_id=$1 AND v.id=ANY($2::uuid[]) ORDER BY v.code)x",[Catalog,heads],ct);
  if(contextSetups.Length!=heads.Length)throw new PreviewDeniedException();
  var revision=await DeliveryRevision(c,ct);
  var points=await PreviewDatabase.Rows(c,"""
   SELECT row_to_json(x)::text FROM(SELECT p.*,g.name grade_name,f.name facility_name,l.name custody_point_name,g.quantity_code
   FROM ref_data.crude_delivery_points p
   JOIN ref_data.crude_grades g ON(g.catalog_id,g.revision_id,g.code)=(p.catalog_id,p.revision_id,p.grade_code)
   JOIN ref_data.locations f ON(f.catalog_id,f.revision_id,f.code)=(p.catalog_id,p.revision_id,p.facility_code)
   JOIN ref_data.locations l ON(l.catalog_id,l.revision_id,l.code)=(p.catalog_id,p.revision_id,p.custody_point_code)
   WHERE(p.catalog_id,p.revision_id)=($1,$2) ORDER BY p.grade_code,p.facility_code,p.custody_point_code)x
   """,[revision.CatalogId,revision.RevisionId],ct);
  var sources=await PreviewDatabase.Rows(c,"""
   SELECT json_build_object('definitionRevisionId',m.id,'referenceCatalogId',m.reference_catalog_id,'referenceRevisionId',m.reference_revision_id,
    'curve',c.code,'name',c.name,'quantity',q.quantity_code,'multiplier',q.multiplier::text,
    'deliveryPeriods',(SELECT coalesce(jsonb_agg(jsonb_build_object('from',k.delivery_from,'to',k.delivery_to,'kind',k.kind,
      'hasInterval',EXISTS(SELECT 1 FROM market_data.contract_intervals i WHERE(i.catalog_id,i.revision_id,i.contract_id)=(k.catalog_id,k.revision_id,k.id)))),'[]'::jsonb)
      FROM market_data.curve_members b JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(b.catalog_id,b.revision_id,b.contract_id)
      WHERE(b.catalog_id,b.revision_id,b.curve_code)=(c.catalog_id,c.revision_id,c.code)))::text
   FROM market_data.revisions m JOIN market_data.curves c ON(c.catalog_id,c.revision_id)=(m.catalog_id,m.id)
   JOIN ref_data.quote_specifications q ON(q.catalog_id,q.revision_id,q.code)=(m.reference_catalog_id,m.reference_revision_id,c.quote_code)
   JOIN ref_data.quantity_specifications n ON(n.catalog_id,n.revision_id,n.code)=(q.catalog_id,q.revision_id,q.quantity_code)
   JOIN ref_data.settlement_targets t ON(t.catalog_id,t.revision_id,t.code)=(q.catalog_id,q.revision_id,c.settlement_target_code)
   WHERE m.catalog_id=$1 AND m.id=ANY($2::uuid[]) AND m.state='sealed' AND q.kind='outright' AND n.dimension_code='volume' AND t.kind='index' AND t.index_b_code IS NULL
   ORDER BY c.name,m.id,c.code
   """,[Catalog,Sources().ToArray()],ct);

  var authoringSources=sources.Select(s=>new{definitionRevisionId=s.Id("definitionRevisionId"),referenceCatalogId=s.Id("referenceCatalogId"),referenceRevisionId=s.Id("referenceRevisionId"),
   curve=s.Text("curve"),name=s.Text("name"),quantity=s.Text("quantity"),multiplier=s.Text("multiplier"),
   suggestedDeliveryPeriod=view is null?null:DeliverySetupDefaults.FuturePeriod(s.GetProperty("deliveryPeriods").EnumerateArray().Select(p=>new DeliverySetupContract(
    p.Text("kind") switch{"forward"=>ContractKind.Forward,"future"=>ContractKind.Future,"future_option"=>ContractKind.FutureOption,_=>throw Invalid("The source contract kind is unsupported.")},
    DateWorkspace.Date(p.Text("from")),DateWorkspace.Date(p.Text("to")),p.GetProperty("hasInterval").GetBoolean())),view.Frame.At)}).ToArray();
  var response=new{deliveryReferences=await CrudeDeliveryReferences.Read(c,revision,ct),deliveryPoints=points,sources=authoringSources,clocks=await ProfileClocks(c,revision,ct),versions,contextSetups,proposals,proposal,context=view?.Context,fieldDescriptions=DeliverySetupDescriptions.Crude,quantityPolicies=new[]{new{code=CrudeQuantityEquivalence.Policy,label="Same standard volume at 60°F · no conversion"}},
   canSave=working&&await PreviewDatabase.HasPermission(c,"WriteMarketDefinitions",ct),canPublish=working&&await PreviewDatabase.HasPermission(c,"SealMarketDefinitions",ct),
   scope="Maintain exact delivery-to-price bindings for pre-delivery crude booking and valuation. Booked contracts retain their exact setup and original pricing source. Delivery-to-cash for separate delivery masters remains unsupported."};
  var references=versions.Concat(contextSetups).Concat(proposals).Concat(proposal is {} exact?[exact]:[]).SelectMany(v=>new[]{v.Id("delivery_reference_catalog_id"),v.Id("source_reference_catalog_id")})
   .Concat(sources.Select(v=>v.Id("referenceCatalogId"))).Append(revision.CatalogId).Distinct().ToArray();
  await using var authority=ScheduleStore.Command(c,"SELECT security.market_access($1,'ReadMarketData') AND NOT EXISTS(SELECT 1 FROM unnest($2::uuid[]) r WHERE NOT security.reference_access(r,'ReadMarketData'))",Catalog,references);
  if(!Equals(await authority.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
  return response;
 }
 public async Task<JsonElement> Save(NpgsqlConnection c,CrudeSetupSaveRequest body,CancellationToken ct)
 {
  if(body.Configuration.ValueKind!=JsonValueKind.Object||!body.Configuration.TryGetProperty("definitionRevisionId",out var definition)||
   !definition.TryGetGuid(out var revision)||!Sources().Contains(revision))throw Invalid("Choose an explicitly admitted original pricing source.");
  return await Execute(c,"SELECT security.save_crude_setup($1,$2,$3,$4::jsonb)::text",[Catalog,body.OperationId,(object?)body.BaseSetupId??DBNull.Value,body.Configuration.GetRawText()],ct);
 }
 internal async Task<JsonElement> Version(NpgsqlConnection c,Guid id,CancellationToken ct)=>One(await PreviewDatabase.Rows(c,
  "SELECT row_to_json(v)::text FROM market_data.crude_setup_versions v WHERE(catalog_id,id)=($1,$2)",[Catalog,id],ct));
 internal async Task<CrudeValuationAssignment> Assignment(NpgsqlConnection c,PreviewData data,JsonElement version,CrudeDeliveryWindow window,CancellationToken ct)
 {
  if(!Sources().Contains(version.Id("definition_revision_id"))||version.Id("definition_revision_id")!=data.DefinitionRevision.RevisionId)
   throw Invalid("The setup requires its exact admitted pricing revision.");
  if(window.From.UtcDateTime.Date<DateWorkspaceDate(version.Text("valid_from"))||window.To.UtcDateTime>DateWorkspaceDate(version.Text("valid_to")))
   throw Invalid("The setup does not cover the complete delivery interval.");
  var row=One(await PreviewDatabase.Rows(c,"""
   SELECT row_to_json(x)::text FROM(SELECT g.*,q.basis_code,q.unit_code,q.dimension_code,
    ARRAY[d.mass_power,d.length_power,d.time_power,d.temperature_power,d.amount_power,d.current_power,d.luminous_power] powers
   FROM ref_data.crude_grades g JOIN ref_data.quantity_specifications q ON(q.catalog_id,q.revision_id,q.code)=(g.catalog_id,g.revision_id,g.quantity_code)
   JOIN ref_data.dimensions d ON(d.catalog_id,d.revision_id,d.code)=(q.catalog_id,q.revision_id,q.dimension_code)
   WHERE(g.catalog_id,g.revision_id,g.code)=($1,$2,$3))x
   """,[version.Id("delivery_reference_catalog_id"),version.Id("delivery_reference_revision_id"),version.Text("grade_code")],ct));
  ReferenceKey D(string code)=>new(new(version.Id("delivery_reference_catalog_id"),version.Id("delivery_reference_revision_id")),code);
  ReferenceKey P(string code)=>new(data.ReferenceRevision,code);
  var curve=data.Definitions["curves"].Single(x=>x.Text("code")==version.Text("curve_code"));
  var quote=data.Quotes.Single(q=>q.Key.Code==curve.Text("quote_code"));
  var q=data.References["quantity_specifications"].Single(x=>x.Text("code")==quote.Quantity.Code);
  var quantity=new QuantitySpecification(quote.Quantity,P(q.Text("commodity_code")),P(q.Text("basis_code")),P(q.Text("unit_code")),P(q.Text("dimension_code")));
  var dimension=data.References["dimensions"].Single(x=>x.Text("code")==q.Text("dimension_code"));
  var powers=new[]{"mass_power","length_power","time_power","temperature_power","amount_power","current_power","luminous_power"}.Select(n=>int.Parse(dimension.Text(n),System.Globalization.CultureInfo.InvariantCulture)).ToImmutableArray();
  var delivery=new QuantitySpecification(D(row.Text("quantity_code")),D(row.Text("commodity_code")),D(row.Text("basis_code")),D(row.Text("unit_code")),D(row.Text("dimension_code")));
  var proof=new CrudeQuantityEquivalence(delivery,quantity,[..row.GetProperty("powers").EnumerateArray().Select(x=>x.GetInt32())],powers,version.Text("quantity_policy"),
   await DeliveryUnits(c,delivery.Key.Revision,ct),UnitOperations.Converter(new(data.ReferenceRevision,data.References)));
  var target=data.References["settlement_targets"].Single(x=>x.Text("code")==curve.Text("settlement_target_code"));
  if(target.Text("kind")!="index"||target.OptionalText("index_b_code") is not null)throw Invalid("Choose one exact outright index source.");
  var selection=PriceSettlementSelection.CurveTarget(P(target.Text("index_a_code")));
  var members=data.Definitions["curve_members"].Where(x=>x.Text("curve_code")==curve.Text("code")).Select(x=>x.Id("contract_id")).ToHashSet();
  var contracts=data.Contracts.Where(x=>members.Contains(x.Key.Id)&&window.IsCoveredBy(x)).ToArray();
  if(contracts.Length!=1)throw Invalid("The delivery interval requires exactly one monthly source contract.");
  var contract=contracts[0];
  return new(P("cs_"+version.Id("id").ToString("N")),D(row.Text("commodity_code")),D(row.Text("code")),
   new(D(version.Text("facility_code")),D(version.Text("custody_point_code"))),quantity,D(version.Text("clock_code")),
   ExactDecimal.Parse(row.Text("standard_temperature")),row.Text("temperature_scale"),selection,quote,
   new(new(new(data.DefinitionRevision,curve.Text("code")),contract.Key),contract),PriceSettlementInputs.Fixings(data,selection,quote,[contract]).Single(),proof);
 }
 private static async Task<UnitConverter> DeliveryUnits(NpgsqlConnection c,ReferenceRevision revision,CancellationToken ct)
 {
  var references=ImmutableDictionary.CreateBuilder<string,ImmutableArray<JsonElement>>();
  foreach(var table in new[]{"units","unit_conversions","quantity_specifications"})
   references[table]=await PreviewDatabase.Rows(c,$"SELECT row_to_json(r)::text FROM ref_data.{table} r WHERE(catalog_id,revision_id)=($1,$2)",[revision.CatalogId,revision.RevisionId],ct);
  return UnitOperations.Converter(new(revision,references.ToImmutable()));
 }
 private static DateTime DateWorkspaceDate(string value)=>DateWorkspace.Date(value).ToDateTime(TimeOnly.MinValue);
 public async Task<object> Validate(NpgsqlConnection c,Guid id,CancellationToken ct)
 {
  await using(var command=ScheduleStore.Command(c,"SELECT security.require_crude_setup($1,$2)",Catalog,id))await command.ExecuteNonQueryAsync(ct);
  var version=await Version(c,id,ct);
  var data=await database.Read(c,ct,selectedRevision:version.Id("definition_revision_id"),observationIds:[],referenceScope:ReferenceReadScope.AllVisible,observationScope:ObservationReadScope.ExactPins);
  var start=DateWorkspaceDate(version.Text("valid_from"));var end=DateWorkspaceDate(version.Text("valid_to"));
  // Validate each intersecting monthly contract, independently of price availability.
  var curve=version.Text("curve_code");
  var members=data.Definitions["curve_members"].Where(x=>x.Text("curve_code")==curve).Select(x=>x.Id("contract_id")).ToHashSet();
  var eligible=data.Contracts.Where(x=>members.Contains(x.Key.Id)).Where(x=>x.DeliveryFrom.ToDateTime(TimeOnly.MinValue)<end&&start<x.DeliveryTo.ToDateTime(TimeOnly.MinValue)&&CrudeDeliveryWindow.IsMonthlyForward(x)).ToArray();
  var assignments=new List<CrudeValuationAssignment>();
  foreach(var contract in eligible)
  {
   var from=new DateTimeOffset(start>contract.DeliveryFrom.ToDateTime(TimeOnly.MinValue)?start:contract.DeliveryFrom.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero);
   var to=new DateTimeOffset(end<contract.DeliveryTo.ToDateTime(TimeOnly.MinValue)?end:contract.DeliveryTo.ToDateTime(TimeOnly.MinValue),TimeSpan.Zero);
   assignments.Add(await Assignment(c,data,version,new(from,to),ct));
  }
  if(assignments.Count==0)throw Invalid("The setup requires covered monthly source contracts.");
  return new{valid=true,setupId=id,pricingQuantity=new Quantity(ExactDecimal.Parse("1"),assignments[0].Quantity.Key),deliveryQuantity=assignments[0].QuantityEquivalence!.Delivery,
   sourceContracts=assignments.Select(a=>a.Projection.Contract.Key),priceAvailability="Checked separately at the selected market date."};
 }
 public Task<JsonElement> Publish(NpgsqlConnection c,CrudeSetupPublishRequest body,CancellationToken ct)=>Execute(c,
  "SELECT security.publish_crude_setup($1,$2,$3)::text",[Catalog,body.SetupId,body.OperationId],ct);
 public async Task<object> Preview(NpgsqlConnection c,InputFrame frame,CrudeSetupPreviewRequest body,CancellationToken ct)
 {
  var version=await Version(c,body.SetupId,ct);
  if(version.Text("state")!="published")throw Invalid("Preview a published setup version.");
  await using(var check=ScheduleStore.Command(c,"SELECT security.require_crude_setup($1,$2)",Catalog,body.SetupId))await check.ExecuteNonQueryAsync(ct);
  var source=SourceFrame(database.Manifest,frame,version.Id("definition_revision_id"));
  var corrections=source.ResolvesLatestCorrection&&version.Id("definition_revision_id")!=database.Manifest.RevisionId;
  var data=await database.Read(c,ct,selectedRevision:version.Id("definition_revision_id"),observationIds:source.ObservationIds,
   observationScope:corrections?ObservationReadScope.FactHistory:ObservationReadScope.ExactPins);
  if(corrections)source=data.SelectPinned(source);
  var from=TradingInstant.Parse(body.DeliveryFrom,"crude_setup_input");var to=TradingInstant.Parse(body.DeliveryTo,"crude_setup_input");
  if(frame.At>=from)throw new MarketDataException("crude_lifecycle","Delivery-mark preview applies only before delivery starts.");
  var assignment=await Assignment(c,data,version,new(from,to),ct);
  var quantity=ExactDecimal.Parse(body.Quantity);
  if(quantity.CompareTo(default)<=0)throw Invalid("Enter a positive quantity below 10^24.");
  if(frame.ContextId is not {} contextId)throw Invalid("Preview requires a certified knowledge context.");
  var context=(await new KnowledgeStore(Catalog).Read(c,contextId,ct)).Context;
  var snapshot=MarketSnapshotBinding.Bind(data.Snapshot(source),contextId,SchedulePreview.ParseAt(context.KnownAt));
  var side=new PhysicalCrudeSide(Guid.NewGuid(),assignment,from,to,default);
  var market=data.Model.Build(snapshot,side.DependenciesAt(snapshot),ct);
  var result=side.PV(SideValuationContext.ForPhysicalCrude(market,market,new ExplicitDiscounting(ExactDecimal.Parse("1"),DiscountFactorSource.Undiscounted),[assignment]));
  await using(var authority=ScheduleStore.Command(c,"SELECT security.crude_setup_visible($1,$2)",Catalog,body.SetupId))
   if(!Equals(await authority.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
  return new{setupId=body.SetupId,presentValue=result.PresentValue.Scale(quantity).ToExactString(),quantity=quantity.ToString(),
   currency=assignment.Quote.Currency,pricingQuantity=assignment.Quantity.Key,deliveryQuantity=assignment.QuantityEquivalence!.Delivery.Key,
   curve=assignment.Projection.Market.Curve.Code,definitionRevisionId=data.DefinitionRevision.RevisionId,contextId,sourceContract=assignment.Projection.Contract.Key,
   scope="Undiscounted physical delivery mark before delivery; cash consideration and fees are separate obligations."};
 }
 private static async Task<JsonElement> Execute(NpgsqlConnection c,string sql,object[] values,CancellationToken ct)
 {
  try{await using var q=ScheduleStore.Command(c,sql,values);return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);}
  catch(PostgresException e)when(e.SqlState is "P0002" or "23502" or "23503" or "23505" or "23514" or "22P02" or "22003" or "22007" or "22008")
  {throw Invalid("Choose complete compatible exact references, values and delivery dates.");}
 }
}
