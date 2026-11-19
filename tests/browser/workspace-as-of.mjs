// Drive two full workspaces in the same owned browser. Reprice after every change
// in the other tab so cached DOM values cannot make a shared server selection pass.
import assert from 'node:assert/strict';

const curveButton='.curve-button[data-curve="ng_monthly"]';
const mode="document.querySelector('#knowledge-summary').textContent";
const day="document.querySelector('#date-context-on').value";
const prices="document.querySelectorAll('#contracts tbody tr').length>0";
const healthy="document.querySelector('#error').hidden";

async function openMarket(page,url,idle){
  await page.command('Page.navigate',{url:url+'/'});
  await page.wait('!!document.querySelector('+JSON.stringify(curveButton)+')&&'+idle,30000);
  // Observe the real logical response delivered by the owned browser's SSE probe.
  await page.evaluate(`{
    const send=window.fetch;
    window.curveReads=[];
    window.fetch=async(...args)=>{
      const path=new URL(args[0],location.href).pathname;
      const request=path==='/api/market/dates'?JSON.parse(args[1].body):null;
      const hold=window.holdHistoricalStrip&&request?.curve==='ng_monthly'&&request.maximumDates===10;
      if(hold)window.holdHistoricalStrip=false;
      const response=await send(...args);
      if(hold&&response.ok){
        window.heldStripDays=(await response.clone().json()).dates.map(d=>d.date);
        await new Promise(resolve=>{window.releaseHistoricalStrip=resolve;});
      }
      if(path==='/api/evaluate'&&response.ok){
        const curve=await response.clone().json();
        window.curveReads.push({close:curve.snapshotId,points:curve.points.map(p=>[p.contractId,p.value])});
      }
      return response;
    };
  }`);
}

async function reprice(page,idle){
  const count=await page.evaluate('window.curveReads.length');
  await page.click(curveButton,30000);
  await page.wait(`((window.curveReads.length>${count}&&${prices})||!${healthy})&&${idle}`,30000);
  const failure=await page.evaluate(`${healthy}?null:document.querySelector('#error').textContent`);
  assert.equal(failure,null,'The tab must be able to reprice its own selected close');
  const result=await page.evaluate(`({
    selected:document.querySelector('#snapshot').value,
    date:${day},
    mode:${mode},
    known:document.querySelector('#date-context-view').value,
    curve:window.curveReads.at(-1),
    rows:[...document.querySelectorAll('#contracts tbody tr')].map(tr=>[...tr.cells].map(td=>td.textContent))
  })`);
  assert.equal(result.selected,result.curve.close,'Curve response must price the frame selected in this tab');
  assert.ok(result.curve.points.length>0&&result.curve.points.every(([id,value])=>id&&typeof value==='string'),
    'Expected exact contract identities and price text from the server');
  return result;
}

async function showDetails(page){
  if(!await page.evaluate("document.querySelector('#as-of-context').open"))
    await page.click('#as-of-context>summary');
}

async function applyCurrentKnowledge(page,idle){
  await showDetails(page);
  await page.click('#date-context-latest',30000);
  await page.click('#apply-view-context',30000);
  await page.wait(`${mode}==='Historical · read only'&&${healthy}&&${idle}`,30000);
}

async function selectDay(page,businessDate,idle){
  await page.click('#market-date-open',30000);
  if(await page.evaluate("document.querySelector('#market-date-month').value")!==businessDate.slice(0,7))
    await page.set('#market-date-month',businessDate.slice(0,7));
  const button=`#market-date-grid button[data-date="${businessDate}"]`;
  await page.wait(`!!document.querySelector(${JSON.stringify(button+':not(:disabled)')})`);
  await page.click(button,30000);
  await page.wait(`!document.querySelector('#market-date-dialog').open&&${day}===${JSON.stringify(businessDate)}&&${idle}`,30000);
}

export async function run(main){
  const {url,idle,tab,check}=main;
  await openMarket(main,url,idle);
  const other=await tab();
  try{
    await openMarket(other,url,idle);
    await selectDay(main,'2020-12-17',idle);
    await selectDay(other,'2020-12-18',idle);
    const before=await reprice(main,idle);
    const otherCurrent=await reprice(other,idle);
    assert.equal(before.mode,'Current knowledge');
    assert.equal(otherCurrent.mode,'Current knowledge');
    assert.notEqual(before.curve.close,otherCurrent.curve.close);

    await main.command('Page.bringToFront');
    await applyCurrentKnowledge(main,idle);
    const historical=await reprice(main,idle);
    assert.equal(historical.date,before.date);
    assert.deepEqual(historical.curve.points,before.curve.points,'Applying knowledge must preserve the recorded close prices');
    const stillCurrent=await reprice(other,idle);
    assert.deepEqual(stillCurrent.curve,otherCurrent.curve);
    assert.equal(stillCurrent.mode,'Current knowledge');
    await check('Applying historical knowledge in one tab leaves the other tab pricing its own current close',healthy);

    await applyCurrentKnowledge(other,idle);
    const otherHistorical=await reprice(other,idle);
    const chips="[...document.querySelectorAll('#curve-closes .vol-close-chip')].map(b=>b.dataset.close)";
    // Change curves first so the following monthly date-strip read is a real request.
    await main.click('.curve-button[data-curve="bridge_basis"]',30000);
    await main.wait(`${chips}.length>0&&${idle}`,30000);
    await main.evaluate('window.holdHistoricalStrip=true');
    assert.deepEqual(await reprice(main,idle),historical,'The second tab must not replace the first tab’s historical view');
    await main.wait("typeof window.releaseHistoricalStrip==='function'",30000);
    assert.deepEqual(await main.evaluate('window.heldStripDays'),[before.date]);
    await showDetails(main);
    await main.click('#back-view-context',30000);
    await main.wait(`${mode}==='Current knowledge'&&${healthy}&&${idle}`,30000);
    const restored=await reprice(main,idle);
    assert.equal(restored.date,before.date);
    assert.deepEqual(restored.curve,before.curve);
    assert.deepEqual(restored.rows,before.rows);
    await main.wait(`${chips}.length>1`,30000);
    const restoredStrip=await main.evaluate(chips);
    await main.evaluate('window.releaseHistoricalStrip();new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))');
    assert.deepEqual(await main.evaluate(chips),restoredStrip,'The delayed historical date strip must not replace the restored working strip');
    assert.deepEqual(await reprice(other,idle),otherHistorical,'Back must leave the other tab at its own cutoff');
    await check('Back restores the exact prior selected close, contracts and prices in its own tab',healthy);

    await showDetails(other);
    await other.click('#current-view-context',30000);
    await other.wait(`${mode}==='Current knowledge'&&${healthy}&&${idle}`,30000);
    assert.deepEqual((await reprice(main,idle)).curve,before.curve);
    await check('Returning the other tab to Working data preserves the restored market selection',healthy);
    await showDetails(other);
    await other.click('#back-view-context',30000);
    await other.wait(`${mode}!== 'Current knowledge'&&${healthy}&&${idle}`,30000);
    assert.deepEqual(await reprice(other,idle),otherHistorical);
    await check('Back from Working data restores the previous historical close and prices',healthy);
  }finally{
    await other.close();
  }
}
