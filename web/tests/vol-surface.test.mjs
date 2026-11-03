import {test} from 'node:test';
import assert from 'node:assert/strict';
import {surfaceLink} from '../dist/vol-surface-link.js';
import {decimalText,heatCells} from '../dist/vol-surface-view.js';

test('the shared series is heard once per change, and not after unsubscribing',()=>{
  const link=surfaceLink(),heard=[];
  const stop=link.subscribe(code=>heard.push(code));
  link.set('a');link.set('a');link.set(null);link.set('b');
  assert.deepEqual(heard,['a',null,'b']);assert.equal(link.current(),'b');
  stop();link.set('c');
  assert.deepEqual(heard,['a',null,'b']);assert.equal(link.current(),'c');
});

test('exact decimal fractions are shown as volatility points to two decimals, rounding half to even',()=>{
  const points=value=>decimalText(value,{shift:2,places:2});
  assert.equal(points('0.012'),'1.20');
  assert.equal(points('0.012000000000000000000000000001'),'1.20');
  assert.equal(points('0.0199999'),'2.00');
  assert.equal(points('-0.0085'),'-0.85');
  assert.equal(points('0.00125'),'0.12');
  assert.equal(points('0.00135'),'0.14');
  assert.equal(points('0.001250000000000000000000000001'),'0.13');
  assert.equal(points('-0.00001'),'0.00');
  assert.equal(points('3'),'300.00');
  assert.equal(decimalText('0.012',{shift:2,places:2,signed:true}),'+1.20');
  assert.equal(decimalText('-0.012',{shift:2,places:2,signed:true}),'-1.20');
  assert.equal(decimalText('0',{shift:2,places:2,signed:true}),'0.00');
  assert.equal(decimalText('-0.051293294387550533426196144336',{places:4}),'-0.0513');
  for(const value of ['','1e-3','0.1.2','.5','NaN',' 1'])assert.throws(()=>decimalText(value,{places:2}),/exact decimal/);
});

const series=(code,expiryAt,strike,right,outcome,richCheap=null)=>({series:{code,right,strike,expiryAt,underlyingCode:'u',settlement:outcome==='option_settlement_missing'?null:'0.1',outcome,
  marketVolatility:outcome==='solved'?'0.5':null},logMoneyness:'0',fitted:richCheap===null?null:'0.5',richCheap,fitPoint:richCheap!==null});
const expiry=(expiryAt,status)=>({expiryAt,underlyingCode:'u',futures:'3',status,parameters:null,points:status==='fitted'?5:0,rmsResidual:null,maxAbsResidual:null,grid:[]});

test('each expiry, strike and right is a value, a gap with its reason, or not listed',()=>{
  const surface={modelId:'m',strikeAxis:['1','2'],expiries:[expiry('e1','fitted'),expiry('e2','vol_fit_insufficient_strikes')],series:[
    series('a','e1','1','call','solved','0.01'),series('b','e1','1','put','at_intrinsic'),series('c','e1','2','call','option_settlement_missing'),
    series('d','e2','1','call','solved'),series('e','e2','2','put','option_underlying_missing')]};
  const cells=heatCells(surface).map(c=>({...c,series:c.series?.series.code}));
  const at=(row,strike,right)=>cells.find(c=>c.row===row&&c.strike===strike&&c.right===right);
  assert.equal(cells.length,8);
  assert.deepEqual(at(0,'1','call'),{row:0,strike:'1',right:'call',kind:'value',series:'a',reasons:[]});
  assert.equal(at(0,'1','put').kind,'gap');assert.match(at(0,'1','put').reasons.join(),/intrinsic/i);
  assert.deepEqual(at(0,'1','put').reasons.length,1);
  assert.match(at(0,'2','call').reasons.join(),/Missing option settlement/);
  assert.deepEqual(at(0,'2','put'),{row:0,strike:'2',right:'put',kind:'unlisted',series:undefined,reasons:[]});
  // A solved series of an expiry without a fit is a gap for its expiry's reason; an unsolved one gives both reasons.
  assert.equal(at(1,'1','call').kind,'gap');assert.match(at(1,'1','call').reasons.join(),/vol_fit_insufficient_strikes/);
  assert.deepEqual(at(1,'2','put').reasons.length,2);
  assert.equal(at(1,'1','put').kind,'unlisted');
});

test('an outcome or expiry status the page does not know is refused rather than drawn',()=>{
  assert.throws(()=>heatCells({modelId:'m',strikeAxis:['1'],expiries:[expiry('e1','fitted')],series:[series('a','e1','1','call','option_unheard_of')]}),/unknown/);
  assert.throws(()=>heatCells({modelId:'m',strikeAxis:['1'],expiries:[expiry('e1','vol_fit_unheard_of')],series:[]}),/unknown/);
  assert.throws(()=>heatCells({modelId:'m',strikeAxis:['1'],expiries:[expiry('e1','fitted')],series:[series('a','e9','1','call','solved','0.01')]}),/expiry/);
});
