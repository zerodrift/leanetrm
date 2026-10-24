import test from 'node:test';
import assert from 'node:assert/strict';
import {sparklineGeometry,divergingBar} from '../dist/mini-charts.js';
const subpaths=path=>(path.match(/M/g)||[]).length;
const empty={path:'',mark:null,tips:[]};
test('sparkline breaks the line at missing months and marks the delivery month',()=>{
 const g=sparklineGeometry([{x:0,y:'1',label:'Nov'},{x:1,y:null,label:'Dec'},{x:2,y:'3',label:'Jan'},{x:3,y:'2',label:'Feb'}],100,20,0);
 assert.equal(subpaths(g.path),2);
 assert.deepEqual(g.mark&&[Math.round(g.mark.x)],[Math.round(g.tips[0].x)]);
 assert.equal(g.tips.length,3);assert.equal(g.tips[1].label,'Jan');
});
test('sparkline of a flat or single-point curve stays inside its box',()=>{
 const g=sparklineGeometry([{x:0,y:'5',label:'Nov'}],80,20,0);
 assert.ok(g.mark&&g.mark.y>=0&&g.mark.y<=20);
});
test('a month left out of the list breaks the line like a null month',()=>{
 assert.equal(subpaths(sparklineGeometry([{x:0,y:'1',label:'Nov'},{x:2,y:'3',label:'Jan'}],100,20).path),2);
 assert.equal(subpaths(sparklineGeometry([{x:0,y:'1',label:'Nov'},{x:1,y:'3',label:'Dec'}],100,20).path),1);
});
test('a null month is never bridged, however close its neighbours are',()=>{
 const g=sparklineGeometry([{x:0,y:'1',label:'a'},{x:0.5,y:null,label:'b'},{x:1,y:'3',label:'c'}],100,20);
 assert.equal(subpaths(g.path),2);
});
test('higher prices are drawn higher, left to right, and every valued month lies on the line inside the box',()=>{
 const g=sparklineGeometry([{x:0,y:'1',label:'Nov'},{x:1,y:'3',label:'Dec'},{x:2,y:'2',label:'Jan'}],100,20);
 const [nov,dec,jan]=g.tips;
 assert.ok(dec.y<jan.y&&jan.y<nov.y);assert.ok(nov.x<dec.x&&dec.x<jan.x);
 for(const tip of g.tips){assert.ok(tip.x>=0&&tip.x<=100&&tip.y>=0&&tip.y<=20);assert.ok(g.path.includes(`${tip.x} ${tip.y}`));}
});
test('an isolated month between gaps is still on the path',()=>{
 const g=sparklineGeometry([{x:0,y:'1',label:'Nov'},{x:1,y:null,label:'Dec'},{x:2,y:'3',label:'Jan'},{x:3,y:null,label:'Feb'}],100,20);
 assert.equal(subpaths(g.path),2);
 for(const tip of g.tips)assert.ok(g.path.includes(`${tip.x} ${tip.y}`));
});
test('no mark is drawn where the delivery month has no value or is not on the curve',()=>{
 const points=[{x:0,y:'1',label:'Nov'},{x:1,y:null,label:'Dec'},{x:2,y:'3',label:'Jan'}];
 assert.equal(sparklineGeometry(points,100,20,1).mark,null);
 assert.equal(sparklineGeometry(points,100,20,7).mark,null);
 assert.equal(sparklineGeometry(points,100,20).mark,null);
});
test('a curve without values draws nothing',()=>{
 assert.deepEqual(sparklineGeometry([],100,20),empty);
 assert.deepEqual(sparklineGeometry([{x:0,y:null,label:'Nov'},{x:1,y:null,label:'Dec'}],100,20,0),empty);
});
test('values that are not exact decimal strings raise instead of drawing a guess',()=>{
 for(const y of ['','abc','1e3','NaN','Infinity',' 1'])assert.throws(()=>sparklineGeometry([{x:0,y,label:'Nov'}],100,20),/exact/);
 for(const bad of ['','abc','1e3'])for(const args of [[bad,'10'],['5',bad]])assert.throws(()=>divergingBar(...args,100),/exact/);
});
test('diverging bars grow right for gains, left for losses and vanish at zero',()=>{
 assert.equal(divergingBar('271','284',100).direction,'gain');
 assert.equal(divergingBar('-284','284',100).direction,'loss');
 assert.equal(divergingBar('0','284',100).direction,'none');
 const loss=divergingBar('-284','284',100);assert.equal(loss.x+loss.width,50);
});
test('bar length is proportional to the shared scale and never leaves its track',()=>{
 const pairs=({x,width})=>[x,width];
 assert.deepEqual(pairs(divergingBar('142','284',100)),[50,25]);
 assert.deepEqual(pairs(divergingBar('-71','284',100)),[37.5,12.5]);
 assert.deepEqual(pairs(divergingBar('568','284',100)),[50,50]);
 assert.deepEqual(pairs(divergingBar('-568','284',100)),[0,50]);
});
test('a zero scale or a zero value draws no bar',()=>{
 assert.deepEqual(divergingBar('5','0',100),{x:50,width:0,direction:'none'});
 assert.deepEqual(divergingBar('0.000','284',100),{x:50,width:0,direction:'none'});
 assert.deepEqual(divergingBar('-0','284',100),{x:50,width:0,direction:'none'});
});
test('an exact rational amount places its bar by its ratio, as an exact decimal would',()=>{
 assert.deepEqual(divergingBar('1/4','1/2',100),divergingBar('0.25','0.5',100));
 assert.deepEqual(divergingBar('-1/3','1',90),{x:30,width:15,direction:'loss'});
 for(const bad of ['1/0','1/-2','1/','/2','1/2/3'])assert.throws(()=>divergingBar(bad,'1',100),/exact/);
});
