import {test} from 'node:test';
import assert from 'node:assert/strict';
import {currencyKey,currencyLabels,priceTerms,endChoice,aiChange,segmentLabel} from '../dist/ai-settings.js';

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
  assert.deepEqual(['add_model','rename_model','add_price','withdraw_price','replace_key','record_terms','attest','enable_provider','disable_provider'].map(aiChange),
    ['Model added','Display name changed','Price added','Price withdrawn','Key replaced','Terms recorded','Attested','Enabled','Disabled']);
  assert.throws(()=>aiChange('enable_model'),/inconsistent/);
  const labels=currencyLabels([usd]),prices=[{sequence:1,kind:'rates',currency:{catalogId:'c1',revisionId:'r1',code:'usd'}},{sequence:2,kind:'withdrawal',currency:null}];
  assert.equal(segmentLabel({sequence:1,priced:true},prices,labels),'Revision 1 · USD · US Dollar');
  assert.equal(segmentLabel({sequence:2,priced:false},prices,labels),'No price · revision 2');
  assert.equal(segmentLabel({sequence:null,priced:false},prices,labels),'No price');
  assert.equal(segmentLabel({sequence:1,priced:true},[{...prices[0],currency:{catalogId:'c9',revisionId:'r9',code:'usd'}}],labels),'Revision 1 · currency not readable');
  assert.throws(()=>segmentLabel({sequence:3,priced:true},prices,labels),/inconsistent/);
});
