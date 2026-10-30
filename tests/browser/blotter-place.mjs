// The Blotter's grid is built once and changed in place: the trader's place in it, the first row in view and the selected row, survives
// a change of grouping, columns, sort, filters, a new read and the window's width. A selection the filters hide is cleared and said so,
// and the grid does not jump to its top.
export async function run({command,evaluate,wait,click,set,persona,check,idle,url}){
  const frames='new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)))';
  const width=1440,height=1050;
  await command('Page.enable');await command('Runtime.enable');await command('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false});
  await command('Page.navigate',{url});await wait(idle);
  // The positions of the last read, by row key, and where the grid's rows are drawn within its viewport.
  await evaluate(`(()=>{const send=window.fetch;window.__placeOriginalFetch=send;window.fetch=async(input,init)=>{
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
    await persona('trader');
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
    await click(`#desk-positions .tabulator-row[data-row-key="${selectedKey}"]`);
    await wait(`document.querySelector('#desk-positions .standard-data-grid').dataset.selectedRow===${JSON.stringify(selectedKey)}&&!!document.querySelector('#desk-positions .open-row #position-details')`);
    // The row's inline position view closes; the row stays selected. (The inline view is replaced by a details pane.)
    await click('#position-details .position-close');await wait("(document.querySelector('#position-details')?.hidden??true)&&!document.querySelector('#desk-positions .open-row')");
    await evaluate(frames);
    await check(`scrolling to row ${anchorAt+1} and selecting row ${anchorAt+3} leaves the grid scrolled past its first rows, with that row selected`,
      `(()=>{const s=window.__place.state();return !!window.__place.anchor()&&s.scroll>0&&s.selected===${JSON.stringify(selectedKey)};})()`,'({anchor:window.__place.anchor(),state:window.__place.state()})');
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
    await kept('reading the Blotter again',()=>click('#futures-refresh'));
    await kept(`narrowing the window to 1200 px`,()=>command('Emulation.setDeviceMetricsOverride',{width:1200,height,deviceScaleFactor:1,mobile:false}));
    await kept(`widening it to ${width} px again`,()=>command('Emulation.setDeviceMetricsOverride',{width,height,deviceScaleFactor:1,mobile:false}));
    const leaving=await numericFilter('(a,b)=>["!= "+b]');
    await set(`#desk-positions input[aria-label="Filter ${leaving.label}"]`,leaving.value);await wait(idle);await evaluate(frames);
    const cleared="(()=>{const s=window.__place.state(),note=document.querySelector('#desk-positions .table-open-note');return s.probe==='kept'&&s.selected===null&&!note.hidden&&note.textContent.includes('no longer shown')&&s.scroll>0;})()";
    await wait(cleared,3000).catch(()=>undefined);
    await check('filtering the selected row out clears the selection with a notice, and the grid does not jump to its top',cleared,
      "({state:window.__place.state(),note:document.querySelector('#desk-positions .table-open-note')?.textContent,first:window.__place.anchor()})");
  }finally{await evaluate('window.fetch=window.__placeOriginalFetch');}
}
