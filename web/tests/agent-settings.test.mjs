import {test} from 'node:test';
import assert from 'node:assert/strict';
import {aiTabs,agentChange,agentState,modelChanged,agentRow,versionRows,modelChoices,agentSaveRequest,agentRefusal,boundsHelp,keptModel,outputHelp,agentMessage,knownAgentRejection} from '../dist/agent-settings.js';

const vendors=[{code:'anthropic',name:'Anthropic'},{code:'openai',name:'OpenAI'}];
const roles=[{code:'market_data_editor',name:'Market data editor',permissions:['ReadMarketData','UseAgents']},
  {code:'risk_analyst',name:'Risk analyst',permissions:['ReadMarketData','ReadTrades','UseAgents']},{code:'trader',name:'Trader',permissions:['ReadMarketData','ReadTrades','UseAgents']}];
const tools=[{code:'navigate',permissions:[]},{code:'read_current_page',permissions:[]},{code:'read_market_data',permissions:['ReadMarketData']},
  {code:'read_reference_data',permissions:['ReadMarketData']},{code:'read_schedules',permissions:['ReadMarketData']},
  {code:'read_trade_parties',permissions:['ReadMarketData','ReadTradeParties']},{code:'read_trades',permissions:['ReadMarketData','ReadTrades']}];
const models=[{id:'m1',version:3,vendor:'anthropic',identifier:'model-one',displayName:'Model one',outputLimit:4096,reasoningEfforts:['low','high']}];
const directory={vendors,roles,tools,models,bounds:{instructions:6000,grantDays:90}};
const version=(changes={})=>({version:2,description:'Answers questions about the page.',instructions:'Quote only values you read.\nSay when something is unavailable.',
  model:{id:'m1',version:3,vendor:'anthropic',identifier:'model-one',displayName:'Model one',current:true},reasoning:'low',caching:false,
  tools:['read_current_page','read_trades'],allowedRoles:['risk_analyst','trader'],limits:{toolSteps:8,inputTokens:200000,outputTokens:2048,elapsedSeconds:120,readSeconds:15,retries:2},
  grantDays:28,savedBy:'Synthetic admin',savedAt:'2026-10-20T12:00:00Z',...changes});
const agent=(changes={})=>({id:'a1',name:'Page guide',version:4,state:'active',publishedVersion:1,newest:version(),published:version({version:1}),...changes});
const instant=at=>`at ${at}`;
const entry=(changes={})=>({description:'Answers questions.',instructions:'Quote only values you read.',modelId:'m1',reasoning:'low',caching:'off',
  tools:['read_trades'],allowedRoles:['trader'],limits:{toolSteps:'8',inputTokens:'200000',outputTokens:'2048',elapsedSeconds:'120',readSeconds:'15',retries:'0'},grantDays:'28',...changes});

test('each Settings › AI tab is offered by its own permission, in the order Usage, Models & prices, Providers, Agents',()=>{
  assert.deepEqual(aiTabs(['ReadModelUsage']),['usage']);
  assert.deepEqual(aiTabs(['ManageModelProviders']),['models','providers']);
  assert.deepEqual(aiTabs(['ManageAgents']),['agents']);
  assert.deepEqual(aiTabs(['ManageAgents','ManageModelProviders','ReadMarketData']),['models','providers','agents']);
  assert.deepEqual(aiTabs(['ManageAgents','ReadModelUsage','ManageModelProviders']),['usage','models','providers','agents']);
  assert.deepEqual(aiTabs(['ReadMarketData','UseAgents']),[]);
});

test('agent changes and states come from closed sets; anything else refuses to display',()=>{
  assert.deepEqual(['create_agent','save_agent','publish_agent','retire_agent'].map(agentChange),['Agent created','Version saved','Version published','Agent retired']);
  assert.throws(()=>agentChange('rename_agent'),/inconsistent/);
  assert.equal(agentState(agent()),'Active');assert.equal(agentState(agent({state:'retired'})),'Retired');
  assert.throws(()=>agentState(agent({state:'paused'})),/inconsistent/);
});

test("an agent is marked only while its published version's model version is no longer current",()=>{
  assert.equal(modelChanged(agent()),false);
  assert.equal(modelChanged(agent({published:version({version:1,model:{...version().model,current:false}})})),true);
  // A draft on an outdated model version does not mark the agent; publishing a newer version clears the mark.
  assert.equal(modelChanged(agent({newest:version({model:{...version().model,current:false}})})),false);
  assert.equal(modelChanged(agent({publishedVersion:null,published:null})),false);
  assert.throws(()=>modelChanged(agent({published:version({model:{...version().model,current:undefined}})})),/inconsistent/);
});

test('the agents table names versions, the published model and when it was last saved',()=>{
  assert.deepEqual(agentRow(agent(),directory,instant),['Page guide','Active','Version 2 · draft','Version 1','Model one · model-one (Anthropic)','Synthetic admin · at 2026-10-20T12:00:00Z']);
  assert.deepEqual(agentRow(agent({version:1,publishedVersion:null,newest:null,published:null}),directory,instant),['Page guide','Active','None saved','Not published','','']);
  const changed=agent({publishedVersion:2,published:version({model:{...version().model,current:false}})});
  assert.deepEqual(agentRow(changed,directory,instant).slice(1,5),['Active · model changed','Version 2','Version 2','Model one · model-one (Anthropic)']);
  // A retired agent can publish nothing more, so it carries no mark.
  assert.equal(agentRow({...changed,state:'retired'},directory,instant)[1],'Retired');
  // A published version newer than the newest one cannot happen.
  assert.throws(()=>agentRow(agent({publishedVersion:3,published:version({version:3})}),directory,instant),/inconsistent/);
});

test("a version's rows state every field it was saved with, by name",()=>{
  const rows=versionRows(version(),directory,instant);
  assert.deepEqual(rows,[['Description','Answers questions about the page.'],['Instructions','Quote only values you read.\nSay when something is unavailable.'],
    ['Model','Model one · model-one (Anthropic) · capability version 3'],['Reasoning effort','low'],['Prompt caching','Off'],['Tools','Read current page, Read trades'],
    ['Allowed roles','Risk analyst, Trader'],['Tool steps per turn','8'],['Input tokens per turn','200,000'],['Output tokens per call','2,048'],['Time per turn','120 seconds'],['Time per read','15 seconds'],
    ['Retries per call','2'],['Grant duration','28 days'],['Saved','Synthetic admin · at 2026-10-20T12:00:00Z']]);
  assert.deepEqual(versionRows(version({tools:[],allowedRoles:[],caching:true,model:{...version().model,current:false}}),directory,instant).slice(2,7).map(r=>r[1]),
    ['Model one · model-one (Anthropic) · capability version 3 · no longer current','low','On','None','None']);
  // A role that no longer uses agents is still named as saved; an unknown tool or vendor cannot be shown.
  assert.equal(versionRows(version({allowedRoles:['viewer','trader']}),directory,instant)[6][1],'viewer (no longer uses agents), Trader');
  assert.equal(versionRows(version({tools:['read_current_page','read_jobs']}),directory,instant)[5][1],'Read current page, Read jobs');
  for(const changes of [{tools:['read_everything']},{model:{...version().model,vendor:'unknown'}}])
    assert.throws(()=>versionRows(version(changes),directory,instant),/inconsistent/);
});

test('a version may name only the models the directory offers, each at its current capability version',()=>{
  assert.deepEqual(modelChoices(directory),[{value:'m1',label:'Model one · model-one (Anthropic) · capability version 3'}]);
});

test('a chosen model holds only at the capability version it was chosen at, never moving to another',()=>{
  assert.deepEqual(keptModel(directory,{id:'m1',version:3}),{id:'m1',version:3});
  assert.equal(keptModel(directory,{id:'m1',version:2}),undefined);
  assert.equal(keptModel(directory,{id:'gone',version:3}),undefined);
  assert.equal(keptModel(directory,undefined),undefined);
  const two={...directory,models:[...models,{...models[0],id:'m2',identifier:'model-two',displayName:'Model two',outputLimit:128000}]};
  assert.equal(outputHelp(two,'m1'),'Up to 4,096 tokens per call for this capability version.');
  assert.equal(outputHelp(two,'m2'),'Up to 128,000 tokens per call for this capability version.');
  assert.equal(outputHelp(two,''),'');
});

test('a save names every field as entered, nothing blank is inferred, and numbers are whole',()=>{
  assert.deepEqual(agentSaveRequest(entry(),directory),{description:'Answers questions.',instructions:'Quote only values you read.',modelId:'m1',modelVersion:3,reasoning:'low',
    caching:false,tools:['read_trades'],allowedRoles:['trader'],limits:{toolSteps:8,inputTokens:200000,outputTokens:2048,elapsedSeconds:120,readSeconds:15,retries:0},grantDays:28});
  assert.equal(agentSaveRequest(entry({caching:'on'}),directory).caching,true);
  // Text travels exactly as entered: the server owns its rules.
  assert.equal(agentSaveRequest(entry({instructions:' Leading space'}),directory).instructions,' Leading space');
  // A version may switch on no tool and allow no role yet.
  assert.deepEqual(agentSaveRequest(entry({tools:[],allowedRoles:[]}),directory).tools,[]);
  assert.throws(()=>agentSaveRequest(entry({description:''}),directory),/^Error: Enter a description\.$/);
  assert.throws(()=>agentSaveRequest(entry({instructions:''}),directory),/^Error: Enter the instructions\.$/);
  assert.throws(()=>agentSaveRequest(entry({modelId:''}),directory),/^Error: Choose a model\.$/);
  assert.throws(()=>agentSaveRequest(entry({modelId:'gone'}),directory),/Choose a model again after refreshing AI settings/);
  assert.throws(()=>agentSaveRequest(entry({reasoning:''}),directory),/^Error: Choose a reasoning effort\.$/);
  assert.throws(()=>agentSaveRequest(entry({caching:''}),directory),/^Error: Choose whether prompt caching is on\.$/);
  for(const value of ['','1.5','-1','2e3',' 8','99999999999999999999'])
    assert.throws(()=>agentSaveRequest(entry({limits:{...entry().limits,toolSteps:value}}),directory),/^Error: Enter the tool steps per turn as a whole number\.$/);
  assert.throws(()=>agentSaveRequest(entry({limits:{...entry().limits,retries:''}}),directory),/retries per call as a whole number/);
  assert.throws(()=>agentSaveRequest(entry({limits:{...entry().limits,readSeconds:''}}),directory),/^Error: Enter the time per read in seconds as a whole number\.$/);
  assert.throws(()=>agentSaveRequest(entry({grantDays:'four'}),directory),/^Error: Enter the grant duration as a whole number of days\.$/);
});

test('the limits the API passes are stated with the fields they bound',()=>{
  assert.deepEqual(boundsHelp(directory),{instructions:'Up to 6,000 characters, in lines.',grantDays:'Up to 90 days.'});
  assert.throws(()=>boundsHelp({...directory,bounds:{instructions:6000}}),/inconsistent/);
});

test('an allowed role lacking a permission is named with the permission, from the structured refusal',()=>{
  assert.equal(agentRefusal({code:'agent_role_lacks_permission',role:'Viewer',permission:'UseAgents'}),
    'The allowed role Viewer lacks UseAgents, which this version needs. Allow only roles that use agents and hold every permission its tools need.');
  // A refusal without its fields is still explained, never with invented names.
  assert.equal(agentRefusal({code:'agent_role_lacks_permission'}),'An allowed role lacks a permission this version needs. Allow only roles that use agents and hold every permission its tools need.');
  assert.equal(agentRefusal({code:'agent_role_lacks_permission',role:7,permission:'UseAgents'}),'An allowed role lacks a permission this version needs. Allow only roles that use agents and hold every permission its tools need.');
  assert.equal(agentRefusal({code:'model_admin_input'}),undefined);
});

test("the codes agents share with the model commands are explained in the agents' terms",()=>{
  const save='settings/ai/agents/save';
  assert.match(agentMessage('model_admin_input',save),/1 to 100 tool steps.*1 to 10,000,000 output tokens per call.*1 to 3,600 seconds per turn; 1 to 30 seconds per read, and no more than per turn; 0 to 10 retries per call.*a grant of at least one day/);
  assert.match(agentMessage('model_admin_conflict',save),/^This agent changed, its name is taken/);
  assert.match(agentMessage('model_unqualified',save),/^The model is not qualified/);
  for(const code of ['agent_retired',undefined])assert.equal(agentMessage(code,save),undefined);
});

test("a request the server cannot read, or one larger than it accepts, wrote nothing: it is released, and the ranges explain it where entries were sent",()=>{
  for(const code of ['request_format','request_too_large']){
    assert.equal(knownAgentRejection(code),true,code);
    // A limit beyond its range, or instructions or a grant past the limit stated with them, can make such a request.
    for(const path of ['settings/ai/agents/create','settings/ai/agents/save'])
      assert.match(agentMessage(code,path),/^Check the entries: .*instructions within the limit stated with them.*1 to 100 tool steps.*a grant of at least one day, within the limit stated with it\.$/,`${code} ${path}`);
    // Publish and Retire send no entries, so such a refusal can only mean the page and the server disagree, which a refresh resolves.
    for(const path of ['settings/ai/agents/publish','settings/ai/agents/retire'])assert.equal(agentMessage(code,path),undefined,`${code} ${path}`);
  }
  // An answer that does not show the request was refused before any write keeps it.
  for(const code of [undefined,'request_failed','response_unknown','model_admin_outcome_unknown'])assert.equal(knownAgentRejection(code),false,String(code));
});
