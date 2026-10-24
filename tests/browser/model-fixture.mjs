// A qualified provider and priced models for the page-assistant browser checks, made through an administrator's
// independent session as Settings › AI would make them. Every term is synthetic, and identifiers carry each run's stamp.
const sourced=(fact,checkedOn='2026-10-01')=>[[fact+'Source','https://example.invalid/synthetic/'+fact],[fact+'CheckedOn',checkedOn]];

// The vendor's key, terms and attestation and, unless `enable` is false, its Enable.
export async function qualifyProvider(side,key,{enable=true}={}){
  const vendor=async()=>({operationId:crypto.randomUUID(),vendor:'anthropic',
    expectedVersion:(await side.call('settings/ai/providers')).providers.find(p=>p.vendor==='anthropic').version});
  await side.call('settings/ai/providers/key',{...await vendor(),key});
  await side.call('settings/ai/providers/terms',{...await vendor(),accountType:'Synthetic commercial API organization',
    retention:'Synthetic: inputs and outputs are deleted within 30 days.',retentionSource:'https://example.invalid/synthetic/retention',retentionCheckedOn:'2026-10-01',
    training:'Synthetic: submitted content is not used for training.',trainingSource:'https://example.invalid/synthetic/training',trainingCheckedOn:'2026-10-01',
    ownerAccess:'Synthetic: organization owners can view request logs.',ownerAccessSource:'https://example.invalid/synthetic/owner-access',ownerAccessCheckedOn:'2026-10-01'});
  await side.call('settings/ai/providers/attest',await vendor());
  if(enable)await side.call('settings/ai/providers/enable',await vendor());
}

// A model with its capabilities recorded under the given reasoning efforts and a rate set in USD in force since 2020;
// `capabilities` records them again, as a new capability version.
export async function pricedModel(side,{identifier,displayName,efforts}){
  const added=await side.call('settings/ai/models/add',{operationId:crypto.randomUUID(),vendor:'anthropic',identifier,displayName});
  const listed=async()=>(await side.call('settings/ai/models')).models.find(m=>m.id===added.id);
  const capabilities=async reasoningEfforts=>side.call('settings/ai/models/edit',{operationId:crypto.randomUUID(),modelId:added.id,expectedVersion:(await listed()).version,
    identifierPolicy:'fixed',outputLimit:4096,reasoningEfforts,acceptedServed:[],...Object.fromEntries(['outputLimit','reasoning','identifierStability','acceptedServed','tools','streaming'].flatMap(f=>sourced(f)))});
  await capabilities(efforts);
  const usd=(await side.call('settings/ai/models')).currencies.find(c=>c.code==='usd');
  await side.call('settings/ai/models/price',{operationId:crypto.randomUUID(),modelId:added.id,expectedSequence:(await listed()).priceSequence,
    currency:{catalogId:usd.catalogId,revisionId:usd.revisionId,code:usd.code},rates:{uncachedInput:'1.234',cacheRead:'0.0123',cacheWrite:'2.5',output:'9.87'},
    tier:null,effectiveFrom:'2020-01-01T00:00:00Z',effectiveTo:null});
  return {id:added.id,listed,capabilities,usd,enable:async()=>side.call('settings/ai/models/enable',{operationId:crypto.randomUUID(),modelId:added.id,expectedVersion:(await listed()).version})};
}
