using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;
using OpenEtrm.MarketData;

namespace OpenEtrm.Api;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasSetupSaveRequest(Guid OperationId,[property:JsonRequired]Guid? BaseSetupId,[property:JsonRequired]JsonElement Configuration);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasSetupValidateRequest(Guid SetupId,string From,string To);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GasSetupPublishRequest(Guid OperationId,Guid SetupId);

public sealed class GasSetupEditor(PreviewDatabase database)
{
 static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
 public async Task<object> Catalog(NpgsqlConnection c,CancellationToken ct,TradingView? view=null,Guid? proposalId=null,bool contextOnly=false)
 {
  var catalog=database.Manifest.CatalogId;var revision=database.Manifest.GasMarket?.RevisionId??database.Manifest.RevisionId;
  async Task<System.Collections.Immutable.ImmutableArray<JsonElement>> Versions()=>await PreviewDatabase.Rows(c,"SELECT row_to_json(v)::text FROM(SELECT id,state,base_setup_id,publication_order,configuration FROM market_data.gas_setup_versions WHERE(catalog_id,definition_revision_id)=($1,$2) ORDER BY publication_order DESC NULLS LAST,id LIMIT 50)v",[catalog,revision],ct);
  var working=view?.Working??true;
  var versions=working&&!contextOnly?await Versions():[];
  JsonElement? proposal=null;
  if(working&&!contextOnly&&proposalId is {} selected)
  {
   var rows=await PreviewDatabase.Rows(c,"SELECT row_to_json(v)::text FROM market_data.gas_setup_versions v WHERE(catalog_id,definition_revision_id,id)=($1,$2,$3)",[catalog,revision,selected],ct);
   proposal=rows.Length==1?rows[0]:throw new MarketDataException("gas_setup_input","The exact saved proposal is unavailable under this context and current access.");
  }
  var proposals=working&&!contextOnly&&proposalId is null?await PreviewDatabase.Rows(c,"SELECT row_to_json(v)::text FROM market_data.gas_setup_versions v WHERE(catalog_id,definition_revision_id)=($1,$2) AND state='draft' ORDER BY id LIMIT 51",[catalog,revision],ct):[];
  if(proposals.Length>50)throw new MarketDataException("gas_setup_input","The unpublished proposal review exceeds fifty entries. Read an exact proposal identity.");
  if(view is not null)await view.Bind(c,catalog,ct);
  if(!working&&!contextOnly)
  {
   versions=await Versions();
   if(proposalId is {} historical)
   {
    var rows=await PreviewDatabase.Rows(c,"SELECT row_to_json(v)::text FROM market_data.gas_setup_versions v WHERE(catalog_id,definition_revision_id,id)=($1,$2,$3)",[catalog,revision,historical],ct);
    proposal=rows.Length==1?rows[0]:throw new MarketDataException("gas_setup_input","The exact setup is unavailable under this context and current access.");
   }
  }
  var data=await database.Read(c,ct,selectedRevision:database.Manifest.GasMarket?.RevisionId,observationIds:[],referenceScope:ReferenceReadScope.AllVisible,observationScope:ObservationReadScope.ExactPins);
  await using var active=ScheduleStore.Command(c,"SELECT security.current_gas_setup($1,$2)",catalog,revision);
  var current=await active.ExecuteScalarAsync(ct);Guid? setup=current is Guid id?id:null;
  JsonElement configuration;
  if(setup is {} published)
  {
   await using var read=ScheduleStore.Command(c,"SELECT configuration::text FROM market_data.gas_setup_versions WHERE(catalog_id,id)=($1,$2)",catalog,published);
   configuration=JsonSerializer.Deserialize<JsonElement>((string)(await read.ExecuteScalarAsync(ct))!);
  }
  else
  {
   var rows=await PreviewDatabase.Rows(c,"""
    SELECT row_to_json(x)::text FROM(SELECT p.*,a.group_code,a.location_adjustment FROM ref_data.gas_delivery_conventions p
    LEFT JOIN market_data.gas_valuation_assignments a ON(a.reference_catalog_id,a.reference_revision_id,a.pipeline_code,a.zone_code,a.point_code,a.product_code)=(p.catalog_id,p.revision_id,p.pipeline_code,p.zone_code,p.point_code,p.product_code)
     AND(a.catalog_id,a.revision_id)=($1,$2) AND a.setup_id IS NULL
    WHERE(p.catalog_id,p.revision_id)=($3,$4) ORDER BY p.pipeline_code,p.zone_code,p.point_code,p.product_code)x
    """,[catalog,revision,data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId],ct);
   var groups=await PreviewDatabase.Rows(c,"SELECT row_to_json(g)::text FROM market_data.gas_valuation_groups g WHERE(catalog_id,revision_id)=($1,$2) AND setup_id IS NULL ORDER BY code",[catalog,revision],ct);
   var meters=await PreviewDatabase.Rows(c,"SELECT row_to_json(m)::text FROM ref_data.gas_meter_assignments m WHERE(catalog_id,revision_id)=($1,$2) ORDER BY code",[data.ReferenceRevision.CatalogId,data.ReferenceRevision.RevisionId],ct);
   var tariffs=await PreviewDatabase.Rows(c,"SELECT row_to_json(t)::text FROM market_data.gas_usage_tariffs t WHERE(catalog_id,revision_id)=($1,$2) AND setup_id IS NULL ORDER BY code",[catalog,revision],ct);
   configuration=JsonSerializer.SerializeToElement(new{
    paths=rows.Select(p=>new{pipeline=p.Text("pipeline_code"),zone=p.Text("zone_code"),point=p.Text("point_code"),product=p.Text("product_code"),clock=p.Text("clock_code"),dayStartMinute=int.Parse(p.Text("day_start_minute")),groupCode=p.OptionalText("group_code"),adjustment=p.OptionalText("location_adjustment")??"0"}),
    groups=groups.Select(g=>new{code=g.Text("code"),pipeline=g.Text("pipeline_code"),product=g.Text("product_code"),curve=g.Text("curve_code"),adjustment=g.Text("adjustment")}),
    meters=meters.Select(m=>new{code=m.Text("code"),pipeline=m.Text("pipeline_code"),zone=m.Text("zone_code"),point=m.Text("point_code"),product=m.Text("product_code"),identifier=m.Text("identifier"),quantity=m.Text("quantity_code"),validFrom=m.Text("valid_from"),validTo=m.Text("valid_to")}),
    tariffs=tariffs.Select(t=>new{code=t.Text("code"),pipeline=t.Text("pipeline_code"),product=t.Text("product_code"),quote=t.Text("quote_code"),rate=t.Text("rate"),validFrom=t.Text("valid_from"),validTo=t.Text("valid_to")})},Json);
  }

  var response=new{activeSetupId=setup,configuration,versions,proposals,proposal,context=view?.Context,fieldDescriptions=DeliverySetupDescriptions.Gas,startingValues=DeliverySetupDefaults.Gas(configuration),canSave=working&&await PreviewDatabase.HasPermission(c,"WriteMarketDefinitions",ct),canPublish=working&&await PreviewDatabase.HasPermission(c,"SealMarketDefinitions",ct),
   locations=data.References["locations"],products=data.References["commodities"],clocks=data.References["clock_rules"].Where(x=>x.Text("kind")=="fixed_offset"),quantities=data.References["quantity_specifications"],quotes=data.References["quote_specifications"].Where(x=>x.Text("kind")=="outright"),curves=data.Definitions["curves"],data.ReferenceRevision,data.DefinitionRevision};
  await using var authority=ScheduleStore.Command(c,"SELECT security.market_access($1,'ReadMarketData') AND security.reference_access($2,'ReadMarketData')",catalog,data.ReferenceRevision.CatalogId);
  if(!Equals(await authority.ExecuteScalarAsync(ct),true))throw new PreviewDeniedException();
  return response;
 }
 public Task<JsonElement> Save(NpgsqlConnection c,GasSetupSaveRequest body,CancellationToken ct)=>Execute(c,
  "SELECT security.save_gas_setup($1,$2,$3,$4,$5::jsonb)::text",[database.Manifest.CatalogId,database.Manifest.GasMarket?.RevisionId??database.Manifest.RevisionId,body.OperationId,(object?)body.BaseSetupId??DBNull.Value,body.Configuration.GetRawText()],ct);
 public Task<JsonElement> Validate(NpgsqlConnection c,GasSetupValidateRequest body,CancellationToken ct)=>Execute(c,
  "SELECT security.validate_gas_setup($1,$2,$3,$4)::text",[database.Manifest.CatalogId,body.SetupId,DateWorkspace.Date(body.From),DateWorkspace.Date(body.To)],ct);
 public Task<JsonElement> Publish(NpgsqlConnection c,GasSetupPublishRequest body,CancellationToken ct)=>Execute(c,
  "SELECT security.publish_gas_setup($1,$2,$3)::text",[database.Manifest.CatalogId,body.SetupId,body.OperationId],ct);
 static async Task<JsonElement> Execute(NpgsqlConnection c,string sql,object[] values,CancellationToken ct)
 {
  try
  {
   await using var q=ScheduleStore.Command(c,sql,values);
   return JsonSerializer.Deserialize<JsonElement>((string)(await q.ExecuteScalarAsync(ct))!);
  }
  catch(PostgresException e)when(e.SqlState is "P0002" or "23502" or "23503" or "23505" or "23514" or "22P02" or "22003")
  {throw new MarketDataException("gas_setup_input","Select a saved setup version and complete compatible references, exact values and date ranges.");}
 }
}
