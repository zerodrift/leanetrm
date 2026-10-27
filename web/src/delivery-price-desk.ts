// The Delivery prices desk's models (markets-desk.md, Delivery prices): what its chart and tiles show of a period read and the catalog.
// Every count, price and instant is the backend's; nothing is computed here, and a missing price stays missing.
import {publicText} from './market-view.js';
import type {TileSpec} from './tiles.js';
import type {TimePoint} from './time-chart.js';

export type DeliveryRow={start:string;end:string;formattedPrice:string;knownAt:string};
export type DeliveryExtreme={price:string;start:string};
export type DeliverySummary={intervals:number;expectedIntervals:number;missingIntervals:number;low:DeliveryExtreme|null;high:DeliveryExtreme|null;
  average:string|null;latestKnownAt:string|null;policy:string};
export type DeliveryCollection={jobs:number;enabledJobs:number;latestAttempt:{state:string;finishedAt:string|null;fromDate:string;throughDate:string;errorCode:string|null}|null};

export type PeriodChart={points:{at:string;value:string|null;unavailableReason:string|null}[]};
const statedPoint=(p:{at?:unknown;value?:unknown;unavailableReason?:unknown})=>typeof p?.at==='string'&&
  (typeof p.value==='string'&&p.unavailableReason===null||p.value===null&&typeof p.unavailableReason==='string');
/**
 * What the chart draws: the series the period read states, one point per expected interval, an unpublished one without a price.
 * A read that states no such series is refused; the chart is never rebuilt from the rows.
 */
export function periodChart(read:{chart?:unknown}):TimePoint[]{
  const points=(read.chart as Partial<PeriodChart>|undefined)?.points;
  if(!Array.isArray(points)||!points.every(statedPoint))throw Error('The delivery prices do not state the series to chart. Read them again.');
  return points.map(({at,value})=>({at,value}));
}

/**
 * The period summary's tiles: the intervals published of those expected, the low and the high with the interval each starts, the
 * time-weighted average and the latest knowledge used. `start` names an interval's start as the table does; `instant` any instant.
 */
export function summaryTiles(summary:DeliverySummary,unit:string,labels:{start:(at:string)=>string;instant:(at:string)=>string}):TileSpec[]{
  const none=(kind:string,label:string,figure:string):TileSpec=>({kind,label,rows:[{figure}],words:true});
  const extreme=(kind:string,label:string,value:DeliveryExtreme|null):TileSpec=>value?{kind,label,aside:labels.start(value.start),rows:[{figure:value.price,caption:unit}]}:none(kind,label,'No price');
  return [
    {kind:'intervals',label:'Intervals',rows:[{figure:String(summary.intervals),caption:`of ${summary.expectedIntervals}`}],...(summary.missingIntervals?{note:`${summary.missingIntervals} missing at this cutoff`}:{})},
    extreme('low','Low',summary.low),extreme('high','High',summary.high),
    summary.average===null?none('average','Average','No price'):{kind:'average',label:'Average',aside:'Time-weighted',rows:[{figure:summary.average,caption:unit}]},
    none('known','Latest knowledge',summary.latestKnownAt===null?'Nothing published':labels.instant(summary.latestKnownAt)),
  ];
}

/** The collection tile: the jobs, how many are enabled, and the newest attempt's outcome and delivery dates, as the catalog states them. */
export function collectionTile(collection:DeliveryCollection,date:(day:string)=>string):TileSpec{
  const aside=`${collection.jobs} ${collection.jobs===1?'job':'jobs'} · ${collection.enabledJobs} enabled`,attempt=collection.latestAttempt;
  if(!attempt)return {kind:'collection',label:'Collection',aside,rows:[{figure:'No attempts yet'}],words:true};
  const range=attempt.fromDate===attempt.throughDate?date(attempt.fromDate):`${date(attempt.fromDate)} – ${date(attempt.throughDate)}`;
  return {kind:'collection',label:'Collection',aside,rows:[{figure:publicText(attempt.state),caption:`newest attempt · ${range}`}],...(attempt.errorCode?{note:publicText(attempt.errorCode)}:{}),words:true};
}
