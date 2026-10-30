import test from 'node:test';
import assert from 'node:assert/strict';
class Element{
 constructor(tag){this.tag=tag;this.attributes={};this.dataset={};this.children=[];}
 setAttribute(key,value){this.attributes[key]=value;}
 append(...values){this.children.push(...values);}
}
globalThis.document={createElement:tag=>new Element(tag),createElementNS:(_,tag)=>new Element(tag)};
const {componentTypeBadge,componentTypeLabel}=await import('../dist/curve-component-types.js');
test('builder types distinguish whole curves, exact contracts, selections, dates and computed prices without a colour cue',()=>{
 const expected={curve:'Curve',contract:'Dated contract',selection:'Contract selection',dates:'Date sequence',panel:'Price panel',curve_result:'Curve output'};
 const drawings=[];
 for(const [type,label] of Object.entries(expected)){
  assert.equal(componentTypeLabel(type),label);
  const badge=componentTypeBadge(type);
  assert.equal(badge.dataset.componentType,type);
  assert.equal(badge.children[1].textContent,label);
  assert.equal(badge.children[0].attributes['aria-hidden'],'true');
  drawings.push(JSON.stringify(badge.children[0].children));
 }
 assert.equal(new Set(drawings).size,Object.keys(expected).length);
});
test('an unrecognised backend type cannot masquerade as a curve or contract',()=>{
 for(const type of ['',undefined,'toString','asset','rate'])assert.throws(()=>componentTypeBadge(type),/Unknown component type/);
});
