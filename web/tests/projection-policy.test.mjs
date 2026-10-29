import test from 'node:test';
import assert from 'node:assert/strict';
import {setCurveNames} from '../dist/market-view.js';
setCurveNames([]);
const ref=code=>({revision:{catalogId:'synthetic-reference',revisionId:'one'},code});
const receipt=(mode=2)=>({instrumentId:'synthetic-instrument',sideId:'synthetic-side',curve:{revision:{catalogId:'synthetic-market',revisionId:'one'},code:'synthetic_curve'},settlement:{mode,curveTargetIndex:ref('synthetic_target'),selectedIndex:ref(mode===2?'synthetic_selected':'synthetic_target'),projectionPolicy:mode===2?'forward_curve_no_basis_v1':null},label:mode===2?'Curve risk with unmodelled basis to synthetic_selected':'Curve risk settling to synthetic_target'});
test('risk labels preserve server wording without a current curve directory and collapse duplicate memberships',async()=>{
 const {projectionPolicyLabels}=await import('../dist/projection-policy.js');
 assert.deepEqual(projectionPolicyLabels([]),[]);
 assert.deepEqual(projectionPolicyLabels([receipt(1)]),[]);
 assert.deepEqual(projectionPolicyLabels([receipt(),structuredClone(receipt())]),['Curve risk with unmodelled basis to synthetic_selected']);
});
test('missing, inconsistent or unsupported risk projection evidence is refused rather than presented without a basis label',async()=>{
 const {projectionPolicyLabels}=await import('../dist/projection-policy.js');
 for(const value of [undefined,null,{},[{}],[{...receipt(),settlement:{...receipt().settlement,projectionPolicy:null}}],[{...receipt(),settlement:{...receipt().settlement,selectedIndex:ref('synthetic_target')}}],[{...receipt(),settlement:{...receipt().settlement,mode:3}}]])assert.throws(()=>projectionPolicyLabels(value),/projection polic/);
});
