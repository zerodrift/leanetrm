import test from 'node:test';
import assert from 'node:assert/strict';

// A minimal DOM: elements with a namespace, class, attributes, dataset and children.
class Element{
 constructor(tag,namespace=null){this.tag=tag;this.namespace=namespace;this.className='';this.dataset={};this.attributes={};this.children=[];}
 setAttribute(k,v){this.attributes[k]=String(v);}
 getAttribute(k){return this.attributes[k]??null;}
 append(...items){this.children.push(...items);}
}
globalThis.document={createElement:tag=>new Element(tag),createElementNS:(namespace,tag)=>new Element(tag,namespace)};
const {kindBadge,kindLabel,kindGroups}=await import('../dist/curve-kinds.js');

// The backend's curve_kinds_v1 codes and the labels the design names for them.
const labels={market_settlement:'Market settlement',trader_mark:'Trader mark',interest_rate:'Interest rate',basis_spread:'Basis / spread',scaled:'Scaled',
 weighted_blend:'Weighted blend',rolling_average:'Rolling history average',balance_of_month:'Balance-of-month average',prompt_contract:'Prompt / rolling contract',custom:'Custom graph'};

test('every kind has its label, and its badge is an image named by that label with a decorative drawing',()=>{
 for(const [kind,label] of Object.entries(labels)){
  assert.equal(kindLabel(kind),label);
  const badge=kindBadge(kind);
  assert.equal(badge.getAttribute('role'),'img');assert.equal(badge.getAttribute('aria-label'),label);assert.equal(badge.getAttribute('title'),label);
  assert.equal(badge.dataset.kind,kind,'the kind code selects its colour');
  const [svg]=badge.children;
  assert.equal(badge.children.length,1);assert.equal(svg.tag,'svg');assert.equal(svg.namespace,'http://www.w3.org/2000/svg');
  assert.equal(svg.getAttribute('aria-hidden'),'true');assert.ok(svg.children.length>0,`${kind} draws its icon`);
 }
});

test('each kind draws its own icon',()=>{
 const drawings=Object.keys(labels).map(kind=>JSON.stringify(kindBadge(kind).children[0].children.map(s=>[s.tag,s.attributes])));
 assert.equal(new Set(drawings).size,drawings.length);
});

test('a badge comes in a small and a medium size, small unless asked',()=>{
 assert.match(kindBadge('scaled').className,/\bcurve-kind-sm\b/);
 assert.match(kindBadge('scaled','md').className,/\bcurve-kind-md\b/);
});

test('an unknown kind is a broken contract, never a fallback badge',()=>{
 for(const kind of ['shaped','','Custom',undefined,'toString','__proto__']){
  assert.throws(()=>kindBadge(kind),{message:'Unknown curve kind'});
  assert.throws(()=>kindLabel(kind),{message:'Unknown curve kind'});
 }
});

test('the groups name source curves, relationships and synthetic curves, and hold each kind exactly once',()=>{
 assert.deepEqual(kindGroups.map(g=>g.label),['Source curves','Relationships','Synthetic']);
 const listed=kindGroups.flatMap(g=>g.kinds);
 assert.deepEqual([...listed].sort(),Object.keys(labels).sort());
 assert.equal(new Set(listed).size,listed.length);
 assert.deepEqual(kindGroups[0].kinds,['market_settlement','trader_mark','interest_rate']);
 assert.deepEqual(kindGroups[1].kinds,['basis_spread','scaled']);
});
