import test from 'node:test';
import assert from 'node:assert/strict';
import {presetRequest,presetTargets,sizesByUnit,splitBumps} from '../dist/risk-presets.js';

const gas='USD/MMBtu',power='USD/MWh';
const target=(id,kind,unit)=>({id,label:`Synthetic ${id}`,coordinate:'market_quote',kind,unit,factor:{coordinate:'market_quote',target:{kind,curve:{code:id}}}});
// A catalog as the backend states it: the targets it offers, in its own order, and which of them shock what the books hold.
const contracts=Array.from({length:12},(_,n)=>target(`contract-${n}`,'contract',n<7?gas:power));
const catalog={scenarioTargets:[target('curve-gas','parallel',gas),...contracts.slice().reverse(),target('curve-power','parallel',power),target('points','points',gas)],
 heldContractTargets:contracts.map(t=>t.id),heldCurveTargets:['curve-power','curve-gas']};

test('Shock every held contract takes the held contract targets the catalog names, in the order the catalog offers them',()=>{
 const held=presetTargets(catalog,'held');
 assert.equal(held.length,12);
 assert.deepEqual(held.map(t=>t.id),catalog.scenarioTargets.filter(t=>t.kind==='contract').map(t=>t.id),'catalog order, not the order the ids were listed in');
 assert.ok(held.every(t=>catalog.scenarioTargets.includes(t)),'the targets are the catalog\'s own, never rebuilt');
});

test('Parallel shift takes the held whole-curve targets the catalog names',()=>{
 assert.deepEqual(presetTargets(catalog,'parallel').map(t=>t.id),['curve-gas','curve-power']);
});

test('a held target the catalog does not offer, or of another kind than its preset shocks, is an inconsistent catalog',()=>{
 const message={message:'The risk catalog is inconsistent. Reload it.'};
 assert.throws(()=>presetTargets({...catalog,heldContractTargets:[...catalog.heldContractTargets,'unknown']},'held'),message);
 assert.throws(()=>presetTargets({...catalog,heldCurveTargets:['unknown']},'parallel'),message);
 assert.throws(()=>presetTargets({...catalog,heldContractTargets:['points']},'held'),message);
 assert.throws(()=>presetTargets({...catalog,heldCurveTargets:['contract-0']},'parallel'),message);
 assert.throws(()=>presetTargets({...catalog,heldContractTargets:['contract-0','contract-0']},'held'),message);
});

test('sizes are asked for once for each unit the targets are quoted in, in the order the targets first name them',()=>{
 assert.deepEqual(sizesByUnit(presetTargets(catalog,'held')),[power,gas]);
 assert.deepEqual(sizesByUnit([]),[]);
});

test('bumps are split into parts of at most sixteen, in order, none of them empty',()=>{
 const bumps=Array.from({length:17},(_,n)=>n);
 assert.deepEqual(splitBumps(bumps).map(part=>part.length),[16,1]);
 assert.deepEqual(splitBumps(bumps).flat(),bumps);
 assert.deepEqual(splitBumps(bumps.slice(0,16)),[bumps.slice(0,16)]);
 assert.deepEqual(splitBumps(bumps.concat(bumps),16).map(part=>part.length),[16,16,2]);
 assert.deepEqual(splitBumps([]),[]);
 assert.deepEqual(splitBumps(bumps,5).map(part=>part.length),[5,5,5,2]);
 for(const limit of [0,-1,1.5,Number.NaN])assert.throws(()=>splitBumps(bumps,limit),/positive whole number/);
});

test('a preset shocks each target by the size entered for its unit, under the convention the catalog names',()=>{
 const targets=[contracts[0],contracts[8]];
 assert.deepEqual(presetRequest(targets,{[gas]:'0.10',[power]:'1'},'synthetic-convention'),[
  {factor:contracts[0].factor,size:'0.10',convention:'synthetic-convention'},
  {factor:contracts[8].factor,size:'1',convention:'synthetic-convention'},
 ]);
 const [shift]=presetRequest([contracts[0]],{[gas]:' 0.25 '},'synthetic-convention');
 assert.equal(shift.size,'0.25','the size is sent as entered, without the spaces around it');
 shift.factor.target.curve.code='changed';assert.equal(contracts[0].factor.target.curve.code,'contract-0','the factor is the catalog\'s, copied');
});

test('a unit without a size is refused by name, and so is the first unit when no size is entered at all',()=>{
 assert.throws(()=>presetRequest([contracts[0],contracts[8]],{[gas]:'0.10'},'c'),{message:'Enter a shock size for USD/MWh.'});
 assert.throws(()=>presetRequest([contracts[0],contracts[8]],{[gas]:'0.10',[power]:'  '},'c'),{message:'Enter a shock size for USD/MWh.'});
 assert.throws(()=>presetRequest([contracts[8],contracts[0]],{},'c'),{message:'Enter a shock size for USD/MWh.'});
 assert.throws(()=>presetRequest([contracts[0],contracts[8]],{},'c'),{message:'Enter a shock size for USD/MMBtu.'});
});
