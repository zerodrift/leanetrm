import {node} from './risk-report-elements.js';
import {plotNumber} from './plot-number.js';
import {estimates,isZero,moneyText,sign,type Money,type Step} from './pnl-model.js';

/**
 * The bridge as columns from zero to the result. A column's bar runs from the level before its step to the level after it;
 * the residual, which opening price Greeks do not explain, is drawn hollow, and the result as one bar from zero. A step with
 * no amount has no bar. Each column is as wide as its own figure, so no figure runs over another, and the chart scrolls in
 * its frame when the columns need more room than the page has. Only the bars' geometry uses JavaScript numbers: every
 * figure shown is the backend's text.
 */
export function bridgeChart(steps:Step[],result:Money){
 // An adjustment that is exactly zero is listed in the reconciliation table and not drawn.
 const drawn=steps.filter(s=>s.kind!=='adjustment'||!s.amount||!isZero(s.amount));
 const levels=[0,plotNumber(result.amount),...drawn.flatMap(s=>[plotNumber(s.start.amount),plotNumber(s.end.amount)])];
 const low=Math.min(...levels),span=Math.max(...levels)-low||1,at=(level:number)=>(level-low)/span*100;
 const frame=node('div'),chart=node('div');frame.className='pnl-chart';chart.className='pnl-waterfall';chart.setAttribute('role','img');
 chart.setAttribute('aria-label','P&L bridge from zero to the result. The exact values follow in the reconciliation table.');
 chart.style.setProperty('--pnl-steps',String(drawn.length+1));chart.style.setProperty('--pnl-zero',at(0)+'%');
 function column(label:string,value:string,title:string,from:number,to:number,bar:string|null,connects:boolean){
  const step=node('div'),figure=node('div',value),plot=node('div'),name=node('div',label);
  step.className='pnl-step';step.title=title;figure.className='pnl-bar-value';plot.className='pnl-plot';name.className='pnl-bar-label';
  if(bar){const mark=node('span');mark.className='pnl-bar '+bar;mark.style.bottom=Math.min(at(from),at(to))+'%';mark.style.height=Math.abs(at(to)-at(from))+'%';plot.append(mark);}
  if(connects){const link=node('span');link.className='pnl-connector';link.style.bottom=at(to)+'%';plot.append(link);}
  step.append(figure,plot,name);chart.append(step);
 }
 for(const s of drawn){
  const start=plotNumber(s.start.amount),end=plotNumber(s.end.amount),partial=s.unavailableEstimates>0;
  if(!s.amount){column(s.label,'Unavailable',`${s.label}: unavailable · ${estimates(s.unavailableEstimates)}, in Residual`,start,end,null,true);continue;}
  column(s.label+(partial?' · partial':''),moneyText(s.amount),`${s.label}: ${moneyText(s.amount)} (${s.amount.amount})${partial?` · ${estimates(s.unavailableEstimates)} unavailable, in Residual`:''}`,
   start,end,sign(s.amount)+(s.kind==='residual'?' hollow':''),true);
 }
 column('P&L',moneyText(result),`Economic P&L: ${moneyText(result)} (${result.amount})`,0,plotNumber(result.amount),'total',false);
 frame.append(chart);return frame;
}
