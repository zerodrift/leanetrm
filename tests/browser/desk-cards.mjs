// What the desk's cards and group headers are checked with, as expressions run in the page. Each takes the page expression for the
// market summary groups of the read on screen. A scenario that holds swaps asks for panels, so a card that came out empty cannot
// pass; a scenario that may hold none gets the quiet note checked instead.
const swapGroups=groups=>`${groups}.filter(g=>g.instrumentType==='financial_swap')`;

/** The common Value by market card: one heading, a captioned panel per currency with bars about 20px tall, and each bar listed as text. */
export const swapPvCard=(groups,{requireSwaps=false}={})=>`(()=>{
 const swaps=${swapGroups(groups)},card=document.querySelector('#desk-chart-pv'),panels=[...card.querySelectorAll('.chart')];
 if(${requireSwaps}&&!swaps.length)return false;
 if(!swaps.length)return card.querySelectorAll('h3').length===1&&panels.length===0&&card.querySelector('.desk-chart-empty')?.textContent==='No swaps in the selected books';
 const items=panels.flatMap(p=>[...p.querySelectorAll('ul.sr-only li')]);
 return card.querySelectorAll('h3').length===1&&card.querySelector('h3').textContent==='Value by market'&&panels.length===new Set(swaps.map(g=>g.currency)).size&&panels.every(p=>{
  const caption=p.querySelector('.chart-toolbar strong').textContent,bars=swaps.filter(g=>caption.startsWith(g.currency+' · ')).length,band=(p.querySelector('.standard-chart-plot').getBoundingClientRect().height-16)/bars;
  return caption.endsWith(' · bar right = gain')&&bars>0&&band>=18&&band<=24;})&&items.length===swaps.length&&items.every(li=>/: \\S/.test(li.textContent));
})()`;

/** Every group header reads apart: no word runs into the next. With `twoSubtotals`, some header must state two, so the check is not empty. */
export const groupHeadersReadApart=({twoSubtotals=false}={})=>`(()=>{
 const headers=[...document.querySelectorAll('#desk-positions .tabulator-group')];
 return headers.length>0&&(!${twoSubtotals}||headers.some(h=>h.querySelectorAll('.desk-subtotal').length>1))&&headers.every(h=>!/[A-Za-z0-9](?:PV|P&L) /.test(h.textContent)&&/ positions? (?:PV|P&L) /.test(h.textContent));
})()`;
