import {renderRegularTable} from './regular-table.js';
import {clearChart,renderChart,updateChart,type ChartSpec} from './chart-view.js';
import {IntervalRefresh} from './interval-refresh.js';

export type IntervalWidgetData={scope:string;title:string;caption:string;headers:string[];rows:string[][];numeric:number[];displayRows?:string[][];
 chart:ChartSpec;noColumnFilter?:number[];onRowActivate?:(index:number)=>void};

// Presentation only. Values, units, masks, totals and knowledge context come from
// the caller's authorized backend read. Dashboard hosts may supply a refresh source.
export function mountIntervalDataWidget(o:{table:HTMLElement;chart:HTMLElement;key:string;busy:()=>boolean;
 load?:()=>Promise<IntervalWidgetData>}){
 const status=document.createElement('p');status.className='interval-widget-status';status.setAttribute('role','status');status.setAttribute('aria-live','polite');
 o.chart.before(status);
 let disposed=false,scope:string|undefined;
 function show(data:IntervalWidgetData){
  if(disposed)return;status.textContent=data.caption;o.chart.setAttribute('aria-label',data.title);
  const same=scope===data.scope;scope=data.scope;
  if(same)updateChart(o.chart,data.chart);else renderChart(o.chart,data.chart);
  const scroll=o.table.querySelector('.regular-table-scroll'),top=same?scroll?.scrollTop??0:0,left=same?scroll?.scrollLeft??0:0;
  const focused=same&&o.table.contains(document.activeElement)?document.activeElement as HTMLElement:null;
  const focusLabel=focused?.getAttribute('aria-label');
  const selection=focused instanceof HTMLInputElement?{start:focused.selectionStart,end:focused.selectionEnd}:undefined;
  renderRegularTable(o.table,o.key,data.scope,data.headers,data.rows,data.numeric,o.busy,
   {displayRows:data.displayRows,noColumnFilter:data.noColumnFilter,onRowActivate:data.onRowActivate});
  const next=o.table.querySelector('.regular-table-scroll');if(next){next.scrollTop=top;next.scrollLeft=left;}
  if(focusLabel){const input=[...o.table.querySelectorAll<HTMLElement>('[aria-label]')].find(n=>n.getAttribute('aria-label')===focusLabel);input?.focus({preventScroll:true});if(input instanceof HTMLInputElement&&selection)input.setSelectionRange(selection.start,selection.end);}
 }
 let controls:HTMLElement|undefined,observer:MutationObserver|undefined,refresh:IntervalRefresh<IntervalWidgetData>|undefined;
 const visible=()=>o.table.isConnected&&!document.hidden&&!o.table.closest('[hidden]');
 const sync=()=>refresh?.setActive(!!visible());
 if(o.load){
  refresh=new IntervalRefresh(o.load,show,error=>{status.textContent=`Refresh failed · ${error instanceof Error?error.message:'Data unavailable'}. The previous values remain displayed.`;});
  controls=document.createElement('div');controls.className='interval-widget-controls';
  const button=document.createElement('button');button.type='button';button.className='secondary';button.textContent='Refresh';
  button.onclick=()=>{sync();void refresh!.refresh();};
  const label=document.createElement('label');label.textContent='Automatic refresh ';const frequency=document.createElement('select');
  frequency.append(new Option('Off','0'),new Option('Every 5 seconds','5000'),new Option('Every 30 seconds','30000'));label.append(frequency);
  frequency.onchange=()=>{sync();refresh!.setPeriod(Number(frequency.value));};controls.append(button,label);status.before(controls);
  observer=new MutationObserver(sync);observer.observe(document.body,{subtree:true,attributes:true,attributeFilter:['hidden'],childList:true});document.addEventListener('visibilitychange',sync);sync();
 }
 return {show,
  clear(){scope=undefined;refresh?.invalidate();const select=controls?.querySelector('select');if(select)select.value='0';status.textContent='';clearChart(o.chart);o.table.replaceChildren();},
  dispose(){disposed=true;refresh?.dispose();observer?.disconnect();document.removeEventListener('visibilitychange',sync);controls?.remove();status.remove();clearChart(o.chart);o.table.replaceChildren();},
 };
}

export function intervalDataPoints(rows:readonly {start:string;end:string;value:string}[],labels:readonly string[]){
 const points:{x:string;label:string;y:string|null}[]=[];
 rows.forEach((row,i)=>{
  if(i>0&&rows[i-1].end!==row.start)points.push({x:rows[i-1].end,label:'Missing interval data',y:null});
  points.push({x:row.start,label:labels[i],y:row.value});
 });
 return points;
}
