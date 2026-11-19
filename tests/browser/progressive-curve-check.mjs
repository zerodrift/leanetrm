import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {mkdtemp,rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join,resolve} from 'node:path';
import {createInterface} from 'node:readline';
import {once} from 'node:events';
import {curveLatencyRelay} from './curve-latency-relay.mjs';
import {connect,waitForBrowserPage,stopOwnedBrowser,stopOwnedBrowserGroup} from './owned-browser.mjs';

// The benchmark owns both endpoints and accepts no URL or database configuration.
const [dotnet,chromePath]=process.argv.slice(2);
assert(dotnet&&chromePath,'Pass the dotnet and Chromium executable paths.');
const directory=await mkdtemp(join(tmpdir(),'curve-paint-'));
const host=spawn(dotnet,[resolve('tests/OpenEtrm.Api.Checks/bin/Release/net10.0/OpenEtrm.Api.Checks.dll'),'--progressive-curve-preview'],{stdio:['pipe','pipe','inherit']});
let chrome,socket,relay;
try{
  const lines=createInterface({input:host.stdout});
  const ready=once(lines,'line',{signal:AbortSignal.timeout(30000)});
  const stopped=once(host,'exit').then(([code])=>{throw Error('Preview exited with '+code);});
  const [address]=await Promise.race([ready,stopped]);
  assert.match(address,/^http:\/\/127\.0\.0\.1:\d+$/);
  relay=await curveLatencyRelay(new URL(address));
  chrome=spawn(chromePath,['--headless=new','--no-first-run','--disable-background-networking','--disable-extensions',
    '--disable-component-update','--disable-sync','--remote-debugging-port=0','--user-data-dir='+directory,'about:blank'],{stdio:'ignore',detached:true});
  socket=new WebSocket(await waitForBrowserPage(directory,chrome));
  const errors=[];
  const {command,evaluate,wait}=await connect(socket,errors);
  await command('Runtime.enable');
  await command('Page.navigate',{url:relay.address});
  await wait("typeof window.selectCurve==='function'");
  // Capture real event sources so an old event already queued by the browser can be delivered explicitly.
  await evaluate(`window.EventSource=class extends EventSource {
    constructor(url){super(url);window.lastSource=this;this.addEventListener('points',e=>this.lastPoints=e.data);}
  };true`);
  const start=(mode,count)=>evaluate('window.selectCurve('+JSON.stringify(mode)+','+count+')');
  const baseline=await start('whole',76);
  assert.equal(baseline.rows.length,76);
  assert.equal(baseline.curve,'ercot_houston_zone_rt_peak');assert.equal(baseline.unit,'USD/MWh');
  assert.equal(baseline.rows[0].price,'49.4');
  const held=start('hold',76);
  await wait("document.querySelector('#status').textContent==='8 of 76 prices' && document.querySelector('#chart canvas') && document.querySelectorAll('tbody tr').length===8");
  assert.equal(await evaluate("document.querySelector('tbody td:last-child').textContent"),baseline.rows[0].price);
  await evaluate("document.querySelector('#release').click()");
  const progressive=await held;
  assert.equal(progressive.firstCount,8);
  assert.deepEqual(progressive.rows,baseline.rows);assert.equal(progressive.snapshot,baseline.snapshot);
  assert.equal(progressive.unit,baseline.unit);assert.equal(progressive.curve,baseline.curve);
  const truncated=await start('cut',76);
  assert.equal(truncated.state,'failed');
  assert.equal(await evaluate("document.querySelectorAll('tbody tr, #chart canvas').length"),0);
  const stale=start('hold',384);
  await wait("document.querySelector('#status').textContent==='8 of 384 prices'");
  const staleId=await evaluate("window.staleSource=window.lastSource;window.staleSource.url.split('/').at(-1)");
  const replacement=start('whole',76);
  assert.equal((await stale).state,'cancelled');
  assert.deepEqual((await replacement).rows,baseline.rows);
  await evaluate("window.staleSource.dispatchEvent(new MessageEvent('points',{data:window.staleSource.lastPoints}));window.staleSource.dispatchEvent(new Event('error'))");
  assert.equal(await evaluate("document.querySelector('#status').textContent"),'Complete');
  assert.equal(await evaluate("document.querySelectorAll('tbody tr').length"),76);
  await wait(`(async()=>(await fetch('/held/${staleId}')).status===404)()`);
  // A replayed range on the live stream invalidates the partially drawn curve.
  const replay=start('hold',76);
  await wait("document.querySelector('#status').textContent==='8 of 76 prices'");
  await evaluate("window.lastSource.dispatchEvent(new MessageEvent('points',{data:window.lastSource.lastPoints}))");
  assert.equal((await replay).state,'failed');
  assert.equal(await evaluate("document.querySelectorAll('tbody tr, #chart canvas').length"),0);
  assert.deepEqual(errors,[]);
  console.log('PASS partial chart before remaining calculation, exact final rows, truncated stream and cancelled selection');
  for(const latencyMs of [0,330]){
    relay.setRoundTrip(latencyMs);
    const pings=await evaluate(`(async()=>{
      const times=[];
      for(let n=0;n<5;n++){
        const begin=performance.now();const response=await fetch('/ping',{cache:'no-store'});if(!response.ok)throw Error('Ping failed');
        await response.text();times.push(performance.now()-begin);
      }
      return times;
    })()`);
    const median=pings.toSorted((a,b)=>a-b)[2];
    assert(Math.abs(median-latencyMs)<35,'Measured ping outside the requested latency profile: '+median);
    console.log(JSON.stringify({latencyMs,pingMs:median}));
    for(const count of [76,384]){
      const oracle=(await start('whole',count)).rows;
      for(const [month,row] of oracle.entries()){
        // Integer cents from the fixture's three independent marks, each rising by one cent per month modulo twelve.
        const cents=4940+3*(month%12);
        assert.equal(row.price,(cents/100).toFixed(2).replace(/0$/,''));
        assert.equal(row.id,'41000000-0000-0000-0000-'+String(10007+month*9).padStart(12,'0'));
        assert.equal(row.date,new Date(Date.UTC(2041,month,1)).toISOString().slice(0,10));
      }
      await start('progressive',count);
      for(let sample=0;sample<8;sample++){
        for(const mode of (sample%2?['progressive','whole']:['whole','progressive'])){
          const {rows,state,snapshot,curve,unit,...timing}=await start(mode,count);
          assert.equal(state,'complete');assert.deepEqual(rows,oracle);
          assert.equal(snapshot,baseline.snapshot);assert.equal(curve,baseline.curve);assert.equal(unit,baseline.unit);
          console.log(JSON.stringify({latencyMs,count,mode,sample,...timing}));
        }
      }
    }
  }
  relay.setRoundTrip(0);
  for(const count of [76,384]){
    for(const mode of ['whole','progressive']){
      const sizes=await evaluate(`(async()=>{
        const url=new URL('/curve/${mode}/${count}/'+crypto.randomUUID(),location.origin);
        const response=await fetch(url);
        const body=await response.text();
        if(response.headers.get('Content-Encoding')!=='gzip'||!body.includes('event: complete'))throw Error('Expected complete gzip stream');
        const entry=performance.getEntriesByName(url.href).at(-1);
        return {gzipBytes:entry.encodedBodySize,jsonEventBytes:new TextEncoder().encode(body).length};
      })()`);
      assert(sizes.gzipBytes>0&&sizes.jsonEventBytes>sizes.gzipBytes);console.log(JSON.stringify({count,mode,...sizes}));
    }
  }
  assert.deepEqual(errors,[]);
}finally{
  socket?.close();
  host.stdin.end();
  try{
    if(chrome?.pid)await stopOwnedBrowserGroup(chrome);
    await rm(directory,{recursive:true,force:true,maxRetries:3});
  }finally{
    try{await relay?.close();}finally{await stopOwnedBrowser(host);}
  }
}
