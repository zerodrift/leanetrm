import {test} from 'node:test';
import assert from 'node:assert/strict';
import {chainRows,chainExpiries,significantText,measureReason} from '../dist/options-chain.js';

const expiry='2027-01-26T19:30:00+00:00',later='2027-02-23T19:30:00+00:00';
const measure=(value,missing=null)=>({value,missing});
const analytics=(delta)=>({value:'0.167',priceBump:'0.012',delta:measure(delta),gamma:measure('1.987955334753591876'),vega:measure('0.001634163476493991'),theta:measure('-0.00412')});
const series=(code,right,strike,extra={},at=expiry)=>({series:{code,right,strike,expiryAt:at,underlyingCode:'ng_f27',settlement:'0.1670',outcome:'solved',marketVolatility:'0.4512'},
 logMoneyness:'0',fitted:'0.45',richCheap:'0.0012',fitPoint:true,inTheMoney:false,analytics:analytics(right==='call'?'0.523':'-0.477'),...extra});
const surface={modelId:'m',strikeAxis:['2.75','3','3.25','10'],sensitivities:null,
 expiries:[{expiryAt:expiry,underlyingCode:'ng_f27',futures:'3.01',status:'fitted',parameters:{a:'0.45',b:'-0.1',c:'0.8'},points:5,rmsResidual:'0.001',maxAbsResidual:'0.002',grid:[],atTheMoneyStrike:'3',daysToExpiry:'30.5'},
  {expiryAt:later,underlyingCode:'ng_g27',futures:'3.1',status:'fitted',parameters:null,points:5,rmsResidual:null,maxAbsResidual:null,grid:[],atTheMoneyStrike:'3.25',daysToExpiry:'58.5'},
  {expiryAt:'2026-12-01T19:30:00+00:00',underlyingCode:'ng_z26',futures:null,status:'option_expired',parameters:null,points:0,rmsResidual:null,maxAbsResidual:null,grid:[],atTheMoneyStrike:null,daysToExpiry:null}],
 series:[series('c3','call','3'),series('p3','put','3',{inTheMoney:null}),series('p275','put','2.75',{inTheMoney:false}),
  series('c325','call','3.25',{analytics:{...analytics('0.3'),vega:measure(null,'sensitivity_unstable')}}),
  series('p325','put','3.25',{inTheMoney:true,analytics:null,series:{code:'p325',right:'put',strike:'3.25',expiryAt:expiry,underlyingCode:'ng_f27',settlement:'0.31',outcome:'option_settlement_below_intrinsic',marketVolatility:null}}),
  series('g10','call','10',{},later)]};

test('a chain lists the live expiries a person can choose, in the surface\'s order',()=>{
 assert.deepEqual(chainExpiries(surface).map(e=>e.expiryAt),[expiry,later]);
});

test('a chain has one row per strike its expiry lists, in the backend\'s strike order, with the at-the-money row marked',()=>{
 const rows=chainRows(surface,expiry,new Map());
 assert.deepEqual(rows.map(r=>[r.strike,r.atTheMoney,r.call?.code??null,r.put?.code??null]),[['2.75',false,null,'p275'],['3',true,'c3','p3'],['3.25',false,'c325','p325']]);
 assert.deepEqual(chainRows(surface,later,new Map()).map(r=>[r.strike,r.atTheMoney]),[['10',false]]);
});

test('a side shows the stored figures as display text and never computes one',()=>{
 const side=chainRows(surface,expiry,new Map()).find(r=>r.strike==='3').call;
 assert.deepEqual([side.settlement,side.impliedVolatility.text,side.delta.text,side.gamma.text,side.vega.text,side.theta.text,side.inTheMoney],
  ['0.1670','45.1%','0.52','1.988','0.001634','-0.004120',false]);
 assert.equal(chainRows(surface,expiry,new Map()).find(r=>r.strike==='3').put.delta.text,'-0.48');
});

test('a missing measure is a dash with its code, and a series without analytics has its settlement and no figures',()=>{
 const rows=chainRows(surface,expiry,new Map()),unstable=rows.find(r=>r.strike==='3.25').call,failed=rows.find(r=>r.strike==='3.25').put;
 assert.deepEqual(unstable.vega,{text:'—',missing:'sensitivity_unstable'});
 assert.equal(unstable.delta.text,'0.30');
 assert.equal(failed.settlement,'0.31');
 for(const cell of [failed.impliedVolatility,failed.delta,failed.gamma,failed.vega,failed.theta])assert.deepEqual(cell,{text:'—',missing:'option_settlement_below_intrinsic'});
});

test('held lots appear only beside a held series, as the backend states them',()=>{
 const rows=chainRows(surface,expiry,new Map([['c3','2'],['p275','-3']]));
 assert.deepEqual(rows.flatMap(r=>[r.call,r.put]).filter(Boolean).map(s=>[s.code,s.heldLots]),[['p275','-3'],['c3','2'],['p3',null],['c325',null],['p325',null]]);
});

test('an unknown expiry or a duplicate series is refused, not drawn',()=>{
 assert.throws(()=>chainRows(surface,'2030-01-01T00:00:00+00:00',new Map()),/expiry/);
 assert.throws(()=>chainRows({...surface,series:[...surface.series,series('c3b','call','3')]},expiry,new Map()),/twice/);
});

test('significant figures are kept by half-even rounding of the exact text, for display only',()=>{
 assert.deepEqual(['1.987955','0.0016341634','-0.00412','123456.7','0','0.99995','12.345','-0.000012345'].map(v=>significantText(v,4)),
  ['1.988','0.001634','-0.004120','123457','0','1.000','12.34','-0.00001234']);
});

test('a missing measure is explained in words, and an unknown code is shown as it is',()=>{
 assert.match(measureReason('option_at_intrinsic'),/intrinsic/);
 assert.match(measureReason('sensitivity_unstable'),/agree/);
 assert.equal(measureReason('some_new_code'),'some_new_code');
});

test('contracts sharing an expiry keep their own call, put and at-the-money strike',()=>{
 const base=surface.series[0],other={...base,series:{...base.series,underlyingCode:'other',code:'other-call'}};
 const together={...surface,expiries:[...surface.expiries,{...surface.expiries[0],underlyingCode:'other',atTheMoneyStrike:null}],series:[...surface.series,other]};
 assert.deepEqual(chainRows(together,expiry,new Map(),'other').map(row=>[row.call?.code,row.put,row.atTheMoney]),[['other-call',null,false]]);
});

test('every product page uses the same exact definition and resolves its own underlying and commodity',async()=>{
 const {readOptionProducts}=await import('../dist/option-directory.js');
 const first={definitionRevisionId:'d1',offset:0,pageSize:1,hasMore:true,rows:[{code:'on',name:'Gas option',underlying_product_code:'ng',exchange_code:'n'}],
  references:{futures_products:[{code:'ng',name:'Gas future',commodity_code:'gas'}],commodities:[{code:'gas',name:'Natural gas'}],exchanges:[{code:'n',name:'North'}]}};
 const second={definitionRevisionId:'d1',offset:1,pageSize:1,hasMore:false,rows:[{code:'lo',name:'Oil option',underlying_product_code:'cl'}],
  references:{futures_products:[{code:'cl',name:'Oil future',commodity_code:'oil'}],commodities:[{code:'oil',name:'Crude oil'}]}};
 const seen=[],signal=new AbortController().signal;
 const directory=await readOptionProducts(async(path,body,bound)=>{
  const q=new URL(path,'http://localhost/').searchParams;assert.equal(bound,signal);
  seen.push([q.get('snapshotId'),q.get('offset'),q.get('definitionRevisionId')]);return q.get('offset')==='0'?first:second;
 },'snap',signal);
 assert.deepEqual(seen,[['snap','0',null],['snap','1','d1']]);
 assert.deepEqual(directory.products.map(p=>[p.name,p.market,p.underlying,p.exchange]),[['Gas option','Natural gas','Gas future','North'],['Oil option','Crude oil','Oil future','']]);
 await assert.rejects(readOptionProducts(async()=>second,'snap',signal,'other-definition'),/changed definition/);
});
