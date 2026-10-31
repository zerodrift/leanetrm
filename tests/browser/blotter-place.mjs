// The Blotter's grid is built once and changed in place: the trader's place in it, the first row in view and the selected row, survives
// a change of grouping, columns, sort, filters, a new read, the window's width and the lens. A selection the filters hide is cleared and
// said so, and the grid does not jump to its top. While the Blotter is read again, everything it keeps on screen is marked stale.
export async function run({command,evaluate,wait,click,set,submit,persona,check,idle,url}){
  const frames='new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))';
  const width=1440,height=1050;
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);
  // A grid changed in place stays marked stale until its new rows are in, and an update that fails keeps the mark with its reason.
  await evaluate(`(async()=>{const {renderDataGrid,emptyGridLayout}=await import('/data-grid.js');const host=document.createElement('div');host.id='stale-grid';document.body.append(host);
    const options={keys:['position'],rowKeys:['a','b'],layout:emptyGridLayout(),onLayout(){}};
    window.__staleGrid=renderDataGrid(host,'stale','synthetic',['Position'],[['Synthetic A'],['Synthetic B']],[],()=>false,options);window.__staleOptions=options;})()`);
  await wait('window.__staleGrid.ready()');
  await check('a grid being updated stays marked stale until its new rows are in, and is then current',`(async()=>{const g=window.__staleGrid,grid=document.querySelector('#stale-grid .standard-data-grid');g.markStale('Synthetic read under way');
    const done=g.reconfigure([['Synthetic C']],{...window.__staleOptions,rowKeys:['c']}),during=grid.classList.contains('stale');await done;
    return during&&!grid.classList.contains('stale')&&document.querySelector('#stale-grid .table-stale-note').hidden&&document.querySelector('#stale-grid .tabulator-row').textContent.includes('Synthetic C');})()`);
  await check('an update that fails keeps the rows marked stale and says why',`(async()=>{const g=window.__staleGrid,grid=document.querySelector('#stale-grid .standard-data-grid');g.markStale('Synthetic read under way');
    await g.reconfigure([['Synthetic D']],{...window.__staleOptions,rowKeys:['d'],layout:{get order(){throw Error('Synthetic layout failure');},widths:{},sort:[]}});
    const note=document.querySelector('#stale-grid .table-stale-note');return grid.classList.contains('stale')&&!note.hidden&&note.textContent.includes('Synthetic layout failure');})()`,
    "document.querySelector('#stale-grid .table-stale-note').textContent");
  await evaluate("window.__staleGrid.destroy();document.querySelector('#stale-grid').remove();delete window.__staleGrid;delete window.__staleOptions");
  // The positions of the last read, by row key, and where the grid's rows are drawn within its viewport.
  await evaluate(`(()=>{const send=window.fetch;window.__placeOriginalFetch=send;window.fetch=async(input,init)=>{
    // A held read, released by the scenario, shows what is on screen while the Blotter is read again.
    if(String(input)==='/api/trading/workspace'&&window.__placeHold){window.__placeHold=false;await new Promise(resolve=>{window.__placeRelease=resolve;});}
    const response=await send(input,init);
    if(String(input)==='/api/trading/workspace'&&response.ok){const data=await response.clone().json();window.__placeHoldings=new Map(data.blotter.holdings.map(h=>[h.tradeId+':'+h.bindingId,h]));}
    return response;
  };
  const grid=()=>document.querySelector('#desk-positions .standard-data-grid'),holder=()=>document.querySelector('#desk-positions .tabulator-tableholder');
  window.__place={
    rows(){const h=holder(),top=h.getBoundingClientRect().top;return [...h.querySelectorAll('.tabulator-row[data-row-key]')].map(n=>{const r=n.getBoundingClientRect();return {key:n.dataset.rowKey,top:r.top-top,height:r.height};});},
    anchor(){return this.rows().find(r=>r.height>0&&r.top>=-0.5);},
    at(key){return this.rows().find(r=>r.key===key);},
    state(){return {probe:grid()?.dataset.probe??null,selected:grid()?.dataset.selectedRow??null,scroll:holder()?.scrollTop??null};},
  };
 })()`);
  try{
    // The Risk Analyst can read the Blotter and run risk, whose results turn the lens.
    await persona('risk');
    const frame=await evaluate("(async()=>{const w=await(await fetch('/api/workspace')).json();return w.frames.find(f=>f.at.startsWith('2020-12-18')&&!f.originCloseId).id;})()");
    await set('#snapshot',frame);await wait(idle);await click('#nav-futures');
    await wait(`!!document.querySelector('#desk-positions .tabulator-row[data-row-key]')&&document.querySelector('#futures-page').dataset.pricingState==='complete'&&${idle}`);
    // The rows in display order, read by scrolling the virtual grid through them.
    const order=await evaluate(`(async()=>{const h=document.querySelector('#desk-positions .tabulator-tableholder'),keys=[];
      const visible=window.__place.rows().filter(r=>r.top>=0&&r.top+r.height<=h.clientHeight).length;
      for(let top=0;;top+=Math.floor(h.clientHeight/2)){h.scrollTop=top;await ${frames};for(const r of window.__place.rows())if(!keys.includes(r.key))keys.push(r.key);if(h.scrollTop+h.clientHeight>=h.scrollHeight-1)break;}
      h.scrollTop=0;await ${frames};return {keys,visible};})()`);
    // Row 30, or the deepest row that still has a viewport of rows from it down, which must lie below the first viewport: reaching it scrolls.
    const anchorAt=Math.min(29,order.keys.length-order.visible);
    if(anchorAt<order.visible)throw Error(`The standard fixture shows ${order.keys.length} positions, too few to need scrolling to a row beneath ${order.visible} rows in view.`);
    const anchorKey=order.keys[anchorAt],selectedKey=order.keys[anchorAt+2];
    await evaluate(`(async()=>{const h=document.querySelector('#desk-positions .tabulator-tableholder');for(let i=0;i<4;i++){const r=window.__place.at(${JSON.stringify(anchorKey)});if(!r){h.scrollTop+=h.clientHeight/2;}else if(Math.abs(r.top)<1)break;else h.scrollTop+=r.top;await ${frames};}})()`);
    // Opening the row shows its position in the Details pane below the grid, which shrinks the grid and keeps the row first in view.
    const opened=`document.querySelector('#desk-positions .standard-data-grid').dataset.selectedRow===${JSON.stringify(selectedKey)}&&document.querySelector('#position-details')?.hidden===false&&!!document.querySelector('#desk-positions .open-row')`;
    await click(`#desk-positions .tabulator-row[data-row-key="${selectedKey}"]`);
    await wait(opened+'&&'+idle);await evaluate(frames);
    await check(`scrolling to row ${anchorAt+1} and opening row ${anchorAt+3} leaves the grid scrolled past its first rows, with that row selected and its position in the details`,
      `(()=>{const s=window.__place.state();return !!window.__place.anchor()&&s.scroll>0&&s.selected===${JSON.stringify(selectedKey)}&&!document.querySelector('#desk-positions #position-details');})()`,'({anchor:window.__place.anchor(),state:window.__place.state()})');
    await evaluate("document.querySelector('#desk-positions .standard-data-grid').dataset.probe='kept'");
    // Each change keeps the grid itself, the selection, and the row first in view at its offset within the grid's viewport, which the page above the grid
    // cannot move.
    const kept=async(label,change)=>{
      const before=await evaluate('window.__place.anchor()');
      await change();await wait(idle);await evaluate(frames);
      const held=`(()=>{const p=window.__place,s=p.state(),a=p.at(${JSON.stringify(before.key)});return s.probe==='kept'&&s.selected===${JSON.stringify(selectedKey)}&&!!a&&Math.abs(a.top-${before.top})<=2;})()`;
      await wait(held,3000).catch(()=>undefined);
      await check(label+' keeps the grid, its selected row, and the row first in view where it was',held,`({before:${JSON.stringify(before)},after:window.__place.at(${JSON.stringify(before.key)}),first:window.__place.anchor(),state:window.__place.state()})`);
    };
    // With the details open, a new read and a change of columns keep the trader's place, and the details show the selected position again.
    await kept('reading the Blotter again with the details open',async()=>{await click('#futures-refresh');await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);await wait(opened);});
    await check('the details show the selected position again after the new read',opened);
    await kept('showing the Portfolio column with the details open',()=>click('#blotter-column-choices input[value=portfolio]'));
    await kept('hiding it again with the details open',()=>click('#blotter-column-choices input[value=portfolio]'));
    await check('the details still show the selected position',opened);
    // Collapsing the details gives the grid its room back and focus goes back to the row. The first row in view stays where it was unless
    // the taller grid reaches past the last row: the grid then ends at its last row, and no scroll offset could keep the first one in place.
    const shortBefore=await evaluate('window.__place.anchor()');
    await click('#details-collapse');await wait(`document.querySelector('#blotter-pane-details').hidden&&document.activeElement?.dataset?.rowKey===${JSON.stringify(selectedKey)}&&${idle}`);await evaluate(frames);
    const grown=`(()=>{const p=window.__place,s=p.state(),a=p.at(${JSON.stringify(shortBefore.key)}),h=document.querySelector('#desk-positions .tabulator-tableholder'),end=h.scrollTop+h.clientHeight>=h.scrollHeight-1;return s.probe==='kept'&&s.selected===${JSON.stringify(selectedKey)}&&!!a&&(Math.abs(a.top-${shortBefore.top})<=2||end);})()`;
    await wait(grown,3000).catch(()=>undefined);
    await check('collapsing the details keeps the grid and its selected row, and the row first in view where it was unless the grid now reaches its last row',grown,
      `({before:${JSON.stringify(shortBefore)},after:window.__place.at(${JSON.stringify(shortBefore.key)}),state:window.__place.state(),holder:[document.querySelector('#desk-positions .tabulator-tableholder').scrollTop,document.querySelector('#desk-positions .tabulator-tableholder').clientHeight,document.querySelector('#desk-positions .tabulator-tableholder').scrollHeight]})`);
    await kept('grouping the rows by market',()=>set('#blotter-row-group','market'));
    await kept('showing the Portfolio column',()=>click('#blotter-column-choices input[value=portfolio]'));
    await kept('hiding it again',()=>click('#blotter-column-choices input[value=portfolio]'));
    await kept('sorting by market',()=>click('#desk-positions .tabulator-col[tabulator-field=c1]'));
    // A filter by a column in which the first row in view and the selected row both state a value; `compare` gives the filters to choose
    // from, of which the one that keeps the most rows is used, so the rows still need scrolling.
    const numericFilter=async(compare)=>evaluate(`(()=>{const held=window.__placeHoldings,anchor=held.get(window.__place.anchor().key),chosen=held.get(${JSON.stringify(selectedKey)});
      const column=[['Position','quantity'],['Mark','mark']].find(([,field])=>anchor[field]!==null&&chosen[field]!==null);
      if(!column)throw Error('Neither the first row in view nor the selected row states a quantity or a mark.');
      const values=[...held.values()].map(h=>h[column[1]]).filter(v=>v!==null).map(Number);
      const kept=filter=>{const [op,bound]=filter.split(' '),b=Number(bound);return values.filter(v=>op==='>='?v>=b:op==='<='?v<=b:v!==b).length;};
      const value=(${compare})(anchor[column[1]],chosen[column[1]]).sort((x,y)=>kept(y)-kept(x))[0];
      return {label:column[0],value};})()`);
    const keeping=await numericFilter('(a,b)=>[">= "+(Number(a)<Number(b)?a:b),"<= "+(Number(a)<Number(b)?b:a)]');
    await kept(`filtering ${keeping.label} ${keeping.value}`,()=>set(`#desk-positions input[aria-label="Filter ${keeping.label}"]`,keeping.value));
    // What a read again keeps on screen is marked stale, with the reason, until the new values land.
    const staleParts="['#desk-positions .standard-data-grid','#desk','#desk-charts','#futures-summary'].map(s=>document.querySelector(s)).filter(n=>n&&!n.hidden)";
    await kept('reading the Blotter again',async()=>{
      await evaluate("window.__placeHold=true;document.querySelector('#futures-refresh').click()");await wait("typeof window.__placeRelease==='function'");
      await check('while the Blotter is read again, its rows, summary strip, charts and summary stay on screen marked stale, each pane with the reason',
        `(()=>{const parts=${staleParts},notes=[...document.querySelectorAll('#desk-positions .table-stale-note,#desk-stale-note,#charts-stale-note')];return parts.length===4&&parts.every(n=>n.classList.contains('stale'))&&notes.length===3&&notes.every(n=>!n.hidden&&n.getClientRects().length>0&&n.textContent.includes('previous read'));})()`,
        `({parts:${staleParts}.map(n=>[n.id||n.className,n.classList.contains('stale')]),notes:[...document.querySelectorAll('#desk-positions .table-stale-note,#desk-stale-note,#charts-stale-note')].map(n=>[n.hidden,n.textContent])})`);
      await evaluate('window.__placeRelease();window.__placeRelease=undefined');
      await wait("document.querySelector('#futures-page').dataset.pricingState==='complete'&&"+idle);
      await check('once the new values land, nothing is marked stale',`(()=>${staleParts}.every(n=>!n.classList.contains('stale'))&&[...document.querySelectorAll('#desk-positions .table-stale-note,#desk-stale-note,#charts-stale-note')].every(n=>n.hidden))()`);
    });
    await kept(`narrowing the window to 1200 px`,()=>command('Emulation.setDeviceMetricsOverride',{width:1200,height,deviceScaleFactor:1,mobile:false}));
    await kept(`widening it to ${width} px again`,()=>command('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false}));
    // A risk run's results turn the lens to Risk; Values and Risk then switch in place, the same grid showing the other lens's columns.
    await click('#blotter-risk');await wait("document.querySelector('#position-details').dataset.mode==='risk'&&!document.querySelector('#risk-fields').disabled&&"+idle);
    await click('input[name=risk-choice][value=held]');
    const sizes=await evaluate("document.querySelectorAll('#risk-sizes input').length");
    for(let index=0;index<sizes;index++)await set(`#risk-size-${index}`,'0.05');
    const lens=name=>`(()=>{const bar=document.querySelector('#desk-run');return !bar.hidden&&document.querySelector('#desk-lens [data-lens=${name}]')?.getAttribute('aria-pressed')==='true';})()`;
    await kept('a risk run turning the lens to Risk',async()=>{await submit('#risk-form',60000);await wait(lens('risk')+'&&'+idle,60000);});
    const columnShown=title=>`[...document.querySelectorAll('#desk-positions .tabulator-col')].some(c=>c.style.display!=='none'&&c.textContent.trim()===${JSON.stringify(title)})`;
    await check('the Risk lens shows the changes in place of the values',`${columnShown('Up')}&&${columnShown('Down')}&&!${columnShown('Mark')}&&!${columnShown('Value')}`);
    await kept('switching to the Values lens',async()=>{await click('#desk-lens [data-lens=values]');await wait(lens('values')+'&&'+idle);});
    await check('the Values lens shows the values in place of the changes',`${columnShown('Value')}&&!${columnShown('Up')}&&!${columnShown('Shock')}`);
    await kept('switching back to the Risk lens',async()=>{await click('#desk-lens [data-lens=risk]');await wait(lens('risk')+'&&'+idle);});
    const leaving=await numericFilter('(a,b)=>["!= "+b]');
    await set(`#desk-positions input[aria-label="Filter ${leaving.label}"]`,leaving.value);await wait(idle);await evaluate(frames);
    const cleared="(()=>{const s=window.__place.state(),note=document.querySelector('#desk-positions .table-open-note');return s.probe==='kept'&&s.selected===null&&!note.hidden&&note.textContent.includes('no longer shown')&&s.scroll>0;})()";
    await wait(cleared,3000).catch(()=>undefined);
    await check('filtering the selected row out clears the selection with a notice, and the grid does not jump to its top',cleared,
      "({state:window.__place.state(),note:document.querySelector('#desk-positions .table-open-note')?.textContent,first:window.__place.anchor()})");
  }finally{await evaluate('window.fetch=window.__placeOriginalFetch');}
}
