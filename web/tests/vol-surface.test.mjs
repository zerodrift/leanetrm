import {test} from 'node:test';
import assert from 'node:assert/strict';
import {surfaceLink} from '../dist/vol-surface-link.js';
import {decimalText,heatCells,liveSurface,richCheapText} from '../dist/vol-surface-view.js';
import {richCheapTone} from '../dist/vol-surface-theme.js';
import {surfaceMesh} from '../dist/vol-surface-3d.js';

test('the shared series is heard once per change, and not after unsubscribing',()=>{
  const link=surfaceLink(),heard=[];
  const stop=link.subscribe(code=>heard.push(code));
  link.set('a');link.set('a');link.set(null);link.set('b');
  assert.deepEqual(heard,['a',null,'b']);assert.equal(link.current(),'b');
  stop();link.set('c');
  assert.deepEqual(heard,['a',null,'b']);assert.equal(link.current(),'c');
});

test('a rich/cheap of exactly zero is on the smile, neither rich nor cheap, and drawn in the neutral tone',()=>{
  assert.equal(richCheapTone('0.0012'),'rich');assert.equal(richCheapText('0.0012'),'Rich +0.12 vol pts');
  assert.equal(richCheapTone('-0.0012'),'cheap');assert.equal(richCheapText('-0.0012'),'Cheap -0.12 vol pts');
  for(const zero of ['0','0.000000000000000000000000000000','-0.000'])
    {assert.equal(richCheapTone(zero),'neutral',zero);assert.equal(richCheapText(zero),'On the smile',zero);}
  assert.equal(richCheapTone('0.000000000000000000000000000001'),'rich');
  for(const value of ['','1e-3','.5'])assert.throws(()=>richCheapTone(value),/exact decimal/);
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

test('expired expiries and their series are left out of the drawn surface, and named',()=>{
  const surface={modelId:'m',strikeAxis:['2'],expiries:[expiry('e0','option_expired'),expiry('e1','fitted')],series:[
    series('x','e0','1','call','option_expired'),series('a','e1','2','call','solved','0.01')]};
  const {surface:live,expired}=liveSurface(surface);
  assert.deepEqual(live.expiries.map(e=>e.expiryAt),['e1']);assert.deepEqual(live.series.map(s=>s.series.code),['a']);
  assert.deepEqual(expired.map(e=>e.expiryAt),['e0']);assert.deepEqual(live.strikeAxis,['2']);
  assert.equal(heatCells(live).length,2);
  assert.throws(()=>liveSurface({...surface,series:[...surface.series,series('y','e0','2','put','solved','0.01')]}),/expired/);
});

const fittedAt=(expiryAt,strikes,status='fitted')=>({...expiry(expiryAt,status),grid:strikes.map(strike=>({strike,logMoneyness:'0',fitted:'0.4'+strike.replace('.','')}))});
const surfaceOf=expiries=>({modelId:'m',strikeAxis:[],expiries,series:[]});

test('the 3D mesh draws a row per fitted expiry and joins neighbouring rows only where both list the same consecutive strikes',()=>{
  const mesh=surfaceMesh(surfaceOf([fittedAt('2026-11-01T00:00:00Z',['1','2','3']),fittedAt('2026-12-01T00:00:00Z',['2','3','4']),
    expiry('2027-01-01T00:00:00Z','vol_fit_insufficient_strikes'),fittedAt('2027-02-01T00:00:00Z',['1','2'])]));
  assert.deepEqual(mesh.rows.map(r=>[r.expiry,r.points.map(p=>p.strike)]),[[0,['1','2','3']],[1,['2','3','4']],[3,['1','2']]]);
  assert.deepEqual(mesh.rows[0].points[1],{strike:'2',fitted:'0.42'});
  // The gap expiry has no row, and no join reaches across it to the next fitted expiry.
  assert.deepEqual(mesh.joins,[{from:0,to:1,strikes:['2','3']}]);
});

test('a join never spans a strike that one of its rows lists between the two',()=>{
  const mesh=surfaceMesh(surfaceOf([fittedAt('2026-11-01T00:00:00Z',['1','2','2.5']),fittedAt('2026-12-01T00:00:00Z',['1','2','3'])]));
  assert.deepEqual(mesh.joins,[{from:0,to:1,strikes:['1','2']}]);
});

test('a surface whose fitted grids disagree with their expiry status or order is refused rather than drawn',()=>{
  assert.throws(()=>surfaceMesh(surfaceOf([fittedAt('2026-11-01T00:00:00Z',[])])),/grid/);
  assert.throws(()=>surfaceMesh(surfaceOf([fittedAt('2026-11-01T00:00:00Z',['1'],'vol_fit_nonpositive')])),/grid/);
  assert.throws(()=>surfaceMesh(surfaceOf([fittedAt('2026-12-01T00:00:00Z',['1']),fittedAt('2026-11-01T00:00:00Z',['1'])])),/order/);
});

test('the page reads each colour from the theme, and a token the theme does not define is refused rather than left empty',async()=>{
  const {volTheme}=await import('../dist/vol-surface-theme.js');
  const styled=values=>{globalThis.getComputedStyle=()=>({getPropertyValue:name=>values[name]??''});return {};};
  const all=Object.fromEntries(['--surface','--surface-2','--surface-3','--border','--border-strong','--text','--text-2','--text-3','--accent','--vol-rich','--vol-cheap','--font'].map(n=>[n,` ${n}-value `]));
  try{
    assert.equal(volTheme(styled(all)).rich,'--vol-rich-value');
    assert.throws(()=>volTheme(styled({...all,'--vol-cheap':' '})),/does not define --vol-cheap/);
  }finally{delete globalThis.getComputedStyle;}
});
