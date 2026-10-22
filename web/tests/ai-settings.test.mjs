import {test} from 'node:test';
import assert from 'node:assert/strict';
import {priceTerms,endChoice,aiChange,segmentLabel,capabilityRequest,termsRequest,stateLabel,attestationStatement,capabilityRows,
  testOutcome,testRows,lastTestLabel,lastTestSummary,endpointLabel,testableModels,testRequest,knownAiRejection,aiRefusal} from '../dist/ai-settings.js';
import {currencyKey,currencyLabels} from '../dist/currency-labels.js';

const usd={catalogId:'c1',revisionId:'r1',code:'usd',displayCode:'USD',name:'US Dollar',catalogCode:'synthetic_reference',revisionKey:'initial_v1'};
const entry=(changes={})=>({currency:{catalogId:'c1',revisionId:'r1',code:'usd'},cacheReadRequired:false,
  rates:{uncachedInput:{value:'1.234',notOffered:false},cacheRead:{value:'0.0123',notOffered:false},cacheWrite:{value:'',notOffered:true},output:{value:'9.87',notOffered:false}},
  tier:'none',threshold:'',tierRates:{uncachedInput:'',cacheRead:'',cacheWrite:'',output:''},...changes});

test('currency choices are labelled from their rows and name the revision only when a label repeats',()=>{
  const repeated={...usd,revisionId:'r2',revisionKey:'display_2'},eur={...usd,code:'eur',displayCode:'EUR',name:'Euro'};
  const labels=currencyLabels([usd,repeated,eur]);
  assert.equal(labels.get(currencyKey(eur)),'EUR · Euro');
  assert.equal(labels.get(currencyKey(usd)),'USD · US Dollar (synthetic_reference initial_v1 usd)');
  assert.equal(labels.get(currencyKey(repeated)),'USD · US Dollar (synthetic_reference display_2 usd)');
  assert.equal(new Set(labels.values()).size,3);
});

test('a price request names every rate and choice, with null only where it was chosen',()=>{
  assert.deepEqual(priceTerms(entry()),{currency:{catalogId:'c1',revisionId:'r1',code:'usd'},rates:{uncachedInput:'1.234',cacheRead:'0.0123',cacheWrite:null,output:'9.87'},tier:null});
  // The tier offers exactly the base set's dimensions, so a dimension not offered there is null here too.
  const tiered=priceTerms(entry({tier:'tier',threshold:'123456',tierRates:{uncachedInput:'2.468',cacheRead:'0.0246',cacheWrite:'',output:'19.74'}}));
  assert.deepEqual(tiered.tier,{threshold:123456,rates:{uncachedInput:'2.468',cacheRead:'0.0246',cacheWrite:null,output:'19.74'}});
  // Rates are sent exactly as entered: the server owns their format.
  assert.equal(priceTerms(entry({rates:{...entry().rates,output:{value:' 9.87',notOffered:false}}})).rates.output,' 9.87');
});

test('nothing blank is inferred: a currency, each rate, the tier and its threshold are required',()=>{
  assert.throws(()=>priceTerms(entry({currency:undefined})),/Choose a currency/);
  assert.throws(()=>priceTerms(entry({rates:{...entry().rates,uncachedInput:{value:'',notOffered:false}}})),/^Error: Enter the uncached input rate\.$/);
  assert.throws(()=>priceTerms(entry({rates:{...entry().rates,cacheWrite:{value:'',notOffered:false}}})),/Enter the cache write rate or mark it not offered/);
  assert.throws(()=>priceTerms(entry({tier:''})),/long-prompt tier/);
  for(const threshold of ['','2e5','1.5','-1','99999999999999999999'])
    assert.throws(()=>priceTerms(entry({tier:'tier',threshold,tierRates:{uncachedInput:'2.468',cacheRead:'0.0246',cacheWrite:'',output:'19.74'}})),/whole number of tokens/);
  assert.throws(()=>priceTerms(entry({tier:'tier',threshold:'123456',tierRates:{uncachedInput:'2.468',cacheRead:'0.0246',cacheWrite:'',output:''}})),/tier's output rate/);
});

test('cache read cannot be left unpriced where the directory requires it',()=>{
  const unpriced=entry({cacheReadRequired:true,rates:{...entry().rates,cacheRead:{value:'',notOffered:true}}});
  assert.throws(()=>priceTerms(unpriced),/^Error: Enter the cache read rate\.$/);
  assert.equal(priceTerms(entry({cacheReadRequired:true})).rates.cacheRead,'0.0123');
});

test('an interval end is chosen explicitly',()=>{
  assert.equal(endChoice('open','price'),true);
  assert.equal(endChoice('ends','price'),false);
  assert.throws(()=>endChoice('','withdrawal'),/withdrawal is open-ended or ends/);
});

test('audit and schedule labels come only from known actions and revisions',()=>{
  assert.deepEqual(['add_model','rename_model','add_price','withdraw_price','replace_key','record_terms','attest','enable_provider','disable_provider',
    'edit_model','enable_model','disable_model'].map(aiChange),
    ['Model added','Display name changed','Price added','Price withdrawn','Key replaced','Terms recorded','Attested','Enabled','Disabled',
     'Capabilities recorded','Enabled','Disabled']);
  assert.throws(()=>aiChange('delete_model'),/inconsistent/);
  const labels=currencyLabels([usd]),prices=[{sequence:1,kind:'rates',currency:{catalogId:'c1',revisionId:'r1',code:'usd'}},{sequence:2,kind:'withdrawal',currency:null}];
  assert.equal(segmentLabel({sequence:1,priced:true},prices,labels),'Revision 1 · USD · US Dollar');
  assert.equal(segmentLabel({sequence:2,priced:false},prices,labels),'No price · revision 2');
  assert.equal(segmentLabel({sequence:null,priced:false},prices,labels),'No price');
  assert.equal(segmentLabel({sequence:1,priced:true},[{...prices[0],currency:{catalogId:'c9',revisionId:'r9',code:'usd'}}],labels),'Revision 1 · currency not readable');
  assert.throws(()=>segmentLabel({sequence:3,priced:true},prices,labels),/inconsistent/);
});

const facts=['outputLimit','reasoning','identifierStability','acceptedServed','tools','streaming'];
const capability=(changes={})=>({outputLimit:'64000',efforts:['low','high'],served:'',onlyOwn:true,
  sources:Object.fromEntries(facts.map(f=>[f,'https://example.invalid/'+f])),checkedOn:Object.fromEntries(facts.map(f=>[f,'2026-10-01'])),...changes});

test('a capability request names every fact with its source and date, exactly as entered',()=>{
  const request=capabilityRequest(capability());
  assert.equal(request.outputLimit,64000);
  assert.deepEqual(request.reasoningEfforts,['low','high']);
  assert.deepEqual(request.acceptedServed,[]);
  for(const f of facts){assert.equal(request[f+'Source'],'https://example.invalid/'+f);assert.equal(request[f+'CheckedOn'],'2026-10-01');}
  assert.equal(Object.keys(request).length,3+2*facts.length);
  // Identifiers hold no spaces or commas, so either separates them; the server owns their form.
  assert.deepEqual(capabilityRequest(capability({onlyOwn:false,served:' model-a,\nmodel-b  model-a '})).acceptedServed,['model-a','model-b','model-a']);
  assert.equal(capabilityRequest(capability({sources:{...capability().sources,tools:' https://example.invalid/tools'}})).toolsSource,' https://example.invalid/tools');
});

test('nothing blank is inferred: a whole-number limit, chosen efforts, an explicit served choice and every source and date',()=>{
  for(const outputLimit of ['','6.4e4','64000.0','-1','99999999999999999999'])assert.throws(()=>capabilityRequest(capability({outputLimit})),/whole number of tokens/);
  assert.throws(()=>capabilityRequest(capability({efforts:[]})),/reasoning efforts/);
  assert.throws(()=>capabilityRequest(capability({onlyOwn:false,served:' \n '})),/reports only its own/);
  assert.throws(()=>capabilityRequest(capability({onlyOwn:true,served:'model-a'})),/^Error: Either list/);
  assert.throws(()=>capabilityRequest(capability({sources:{...capability().sources,streaming:''}})),/source for streaming/);
  assert.throws(()=>capabilityRequest(capability({checkedOn:{...capability().checkedOn,identifierStability:''}})),/date the source for the identifier's stability was checked/);
});

test('a terms request names every term, source and date exactly as entered',()=>{
  const entry={accountType:'Synthetic account',retention:'Synthetic retention',retentionSource:'https://example.invalid/r',retentionCheckedOn:'2026-10-01',
    training:'Synthetic training',trainingSource:'https://example.invalid/t',trainingCheckedOn:'2026-10-01',
    ownerAccess:'Synthetic owner access',ownerAccessSource:'https://example.invalid/o',ownerAccessCheckedOn:'2026-10-01'};
  assert.deepEqual(termsRequest(entry),entry);
  assert.deepEqual(termsRequest({...entry,retention:' Padded '}).retention,' Padded ');
  assert.throws(()=>termsRequest({...entry,accountType:''}),/^Error: Enter the account type/);
  assert.throws(()=>termsRequest({...entry,trainingSource:''}),/source of the training-use terms/);
  assert.throws(()=>termsRequest({...entry,ownerAccessCheckedOn:''}),/date the owner-access terms were checked/);
});

test('a state names whether it is enabled and, when it is not, whether it qualifies',()=>{
  assert.equal(stateLabel({enabled:true,qualified:true}),'Enabled');
  assert.equal(stateLabel({enabled:true,qualified:false}),'Enabled · no longer qualified');
  assert.equal(stateLabel({enabled:false,qualified:true}),'Disabled · qualified');
  assert.equal(stateLabel({enabled:false,qualified:false}),'Disabled · not qualified');
});

test('the attestation statement names the key and terms version it binds, or says what is missing',()=>{
  const provider={name:'Anthropic',credential:{version:2,lastFour:'0002'},terms:{version:3,accountType:'Synthetic API organization'}};
  const statement=attestationStatement(provider);
  // Two keys can share their last four characters, so the statement names the key's version too.
  for(const part of ['Anthropic','key, version 2, ends in 0002','“Synthetic API organization”','terms version 3','does not permit training on submitted content','no data-sharing opt-in'])
    assert.ok(statement.includes(part),part);
  assert.ok(!statement.includes('are attested'));
  // An attested pair offers no second attestation, and the statement says why.
  assert.match(attestationStatement({...provider,attestation:{attestedBy:'Synthetic admin'}}),/This key and these terms are attested; a new key or new terms need a new attestation\.$/);
  assert.match(attestationStatement({...provider,credential:null}),/^Attest once Anthropic has a key and recorded terms\.$/);
  assert.match(attestationStatement({...provider,terms:null}),/has a key and recorded terms/);
});

test('capability rows show each fact with its value, source and checked date',()=>{
  const sourced=Object.fromEntries(facts.flatMap(f=>[[f+'Source','https://example.invalid/'+f],[f+'CheckedOn','2026-10-01']]));
  const rows=capabilityRows({outputLimit:64000,reasoningEfforts:['low','max'],acceptedServed:[],...sourced});
  assert.deepEqual(rows.map(r=>r.slice(0,2)),[['Output limit','64,000 tokens'],['Reasoning efforts','low, max'],['Identifier','Not repointed'],
    ['Served identifiers','Only its own'],['Tool calling','Confirmed'],['Streaming','Confirmed']]);
  assert.deepEqual(rows[3].slice(2),['https://example.invalid/acceptedServed','2026-10-01']);
  assert.equal(capabilityRows({outputLimit:1,reasoningEfforts:['low'],acceptedServed:['model-a','model-b'],...sourced})[3][1],'model-a, model-b');
});

const reported=count=>({count,source:'reported'});
const finished={id:'t1',operationId:'o1',vendor:'anthropic',modelId:'m1',identifier:'model-synthetic',capabilityVersion:2,mapping:'messages_v1',reasoning:'low',
  maxOutputTokens:256,credentialVersion:3,termsVersion:4,priceSequence:5,testedBy:'Synthetic admin',startedAt:'2026-10-20T10:00:00Z',closedAt:'2026-10-20T10:00:01Z',
  outcome:'succeeded',served:'model-synthetic',usage:{status:'recognized',uncachedInput:reported(1234567),cacheRead:reported(0),cacheWrite:{count:0,source:'not_billed'},
    output:reported(9),reasoning:{count:null,source:'unknown'}},
  cost:{method:'token_cost_v1',status:'priced',amount:'0.000114744',currency:{catalogId:'c1',revisionId:'r1',code:'usd'}}};
const label=c=>`${c.code.toUpperCase()} · Synthetic`,at=instant=>'at '+instant;

test('a test outcome reads from the closed set, and an open test has none recorded yet',()=>{
  assert.equal(testOutcome('succeeded'),'Succeeded');
  assert.equal(testOutcome('model_mismatch'),'Served by another model');
  assert.equal(testOutcome('limit_elapsed'),'Time limit elapsed');
  assert.equal(testOutcome('interrupted'),'Interrupted');
  assert.equal(testOutcome(null),'No outcome recorded yet');
  assert.throws(()=>testOutcome('model_overjoyed'),/inconsistent/);
});

test('a finished test shows what it pinned, its usage by dimension with each source, and its exact cost',()=>{
  assert.deepEqual(testRows(finished,label,at),[['Outcome','Succeeded'],['Model','model-synthetic'],['Capability version','Version 2'],['Reasoning effort','low'],
    ['Key and terms','Key version 3 · terms version 4'],['Price revision','Revision 5'],['Tested by','Synthetic admin'],['Started','at 2026-10-20T10:00:00Z'],
    ['Closed','at 2026-10-20T10:00:01Z'],['Served as','model-synthetic'],['Uncached input','1,234,567 tokens'],['Cache read','0 tokens'],['Cache write','Not billed'],
    ['Output','9 tokens'],['Reasoning','Unknown'],['Cost','0.000114744 USD · Synthetic']]);
});

test('a cost the method gives no amount names why, and an open test shows only what it pinned',()=>{
  const cost=status=>testRows({...finished,cost:{...finished.cost,status,amount:null}},label,at).at(-1)[1];
  assert.equal(cost('usage_unknown'),'No cost: usage is unknown');
  assert.equal(cost('usage_unrecognized'),'No cost: usage was not recognized');
  assert.equal(cost('cost_unpriced'),'No cost: tokens were used in a dimension the pinned price does not offer');
  assert.throws(()=>cost('discounted'),/inconsistent/);
  const open=testRows({...finished,closedAt:null,outcome:null,served:null,usage:null,cost:null},label,at);
  assert.deepEqual(open.map(r=>r[0]),['Outcome','Model','Capability version','Reasoning effort','Key and terms','Price revision','Tested by','Started']);
  assert.equal(open[0][1],'No outcome recorded yet');
  assert.equal(testRows({...finished,served:null},label,at).find(r=>r[0]==='Served as')[1],'Not reported');
  // What the server always states is never filled in here.
  assert.throws(()=>testRows({...finished,reasoning:null},label,at),/inconsistent/);
  assert.throws(()=>testRows({...finished,usage:{...finished.usage,output:{count:9,source:'estimated'}}},label,at),/inconsistent/);
  assert.throws(()=>testRows({...finished,usage:{...finished.usage,output:{count:null,source:'reported'}}},label,at),/inconsistent/);
});

test('a last test reads its outcome and time, and says when it ran under a key or terms no longer attested',()=>{
  assert.equal(lastTestLabel(null,at),'Not tested');
  assert.equal(lastTestLabel({...finished,underCurrentAttestation:true},at),'Succeeded · at 2026-10-20T10:00:01Z');
  assert.equal(lastTestLabel({...finished,underCurrentAttestation:false},at),'Succeeded · at 2026-10-20T10:00:01Z · earlier key or terms');
  assert.equal(lastTestLabel({...finished,closedAt:null,outcome:null,served:null,usage:null,cost:null,underCurrentAttestation:true},at),
    'No outcome recorded yet · started at 2026-10-20T10:00:00Z');
  // Whether the test ran under the attested key and terms is the server's to state.
  assert.throws(()=>lastTestLabel(finished,at),/inconsistent/);
  assert.throws(()=>lastTestLabel({...finished,underCurrentAttestation:'true'},at),/inconsistent/);
});

test("the last test's summary says what a test under an earlier key or terms needs before it can run again",()=>{
  assert.equal(lastTestSummary(null,true),'This provider has not been tested.');
  assert.equal(lastTestSummary({...finished,underCurrentAttestation:true},true),'');
  assert.equal(lastTestSummary({...finished,underCurrentAttestation:false},true),'This test ran under an earlier key or terms. Test again to check the current ones.');
  assert.equal(lastTestSummary({...finished,underCurrentAttestation:false},false),
    'This test ran under an earlier key or terms. Attest the current key and terms, then test again to check them.');
  assert.throws(()=>lastTestSummary({...finished,underCurrentAttestation:null},true),/inconsistent/);
});

test("a provider's test offers only its own models with recorded capabilities",()=>{
  const models=[{id:'a1',vendor:'anthropic',capability:{version:1}},{id:'a2',vendor:'anthropic',capability:null},{id:'o1',vendor:'openai',capability:{version:1}}];
  assert.deepEqual(testableModels(models,'anthropic').map(m=>m.id),['a1']);
  assert.deepEqual(testableModels(models,'openai').map(m=>m.id),['o1']);
  assert.deepEqual(testableModels(models,'google'),[]);
});

test('an endpoint says whether it is a check endpoint, and a vendor without an adapter has none',()=>{
  assert.equal(endpointLabel({url:'https://api.example.invalid/v1/messages',check:false}),'https://api.example.invalid/v1/messages');
  assert.equal(endpointLabel({url:'http://127.0.0.1:9/v1/messages',check:true}),'http://127.0.0.1:9/v1/messages (check endpoint)');
  assert.equal(endpointLabel(null),'No adapter in this release');
});

test('a test request names its model and reasoning effort; nothing blank is inferred',()=>{
  assert.deepEqual(testRequest({modelId:'m1',reasoning:'high'}),{modelId:'m1',reasoning:'high'});
  assert.throws(()=>testRequest({modelId:'',reasoning:'high'}),/^Error: Choose the model to test\.$/);
  assert.throws(()=>testRequest({modelId:'m1',reasoning:''}),/^Error: Choose the reasoning effort\.$/);
});

test("a request the server cannot read, or one larger than it accepts, wrote nothing: it is released, and the ranges explain it where entries were sent",()=>{
  for(const code of ['request_format','request_too_large']){
    assert.equal(knownAiRejection(code),true,code);
    const refused=()=>Object.assign(Error('The app could not complete that request. Please refresh and try again.'),{code});
    for(const path of ['settings/ai/models/rename','settings/ai/models/edit','settings/ai/providers/terms','settings/ai/providers/key']){
      const shown=aiRefusal(refused(),path);
      assert.equal(shown.code,code);
      for(const range of ['a display name has 1 to 80 characters','terms text 1 to 1,000','an output limit is 1 to 10,000,000 tokens','a key has 8 to 512 visible ASCII characters'])
        assert.ok(shown.message.includes(range),`${path}: ${range}`);
    }
    // A command without entries is refused this way only when the page and the server disagree, which a refresh resolves.
    for(const path of ['settings/ai/models/enable','settings/ai/providers/attest','settings/ai/providers/test']){const error=refused();assert.equal(aiRefusal(error,path),error,path);}
  }
  // An answer that does not show the request was refused before any write keeps it, and other refusals keep their messages.
  for(const code of [undefined,'request_failed','response_unknown','model_admin_outcome_unknown'])assert.equal(knownAiRejection(code),false,code);
  const conflict=Object.assign(Error('Synthetic conflict message'),{code:'model_admin_conflict'});
  assert.equal(aiRefusal(conflict,'settings/ai/models/rename'),conflict);
});
