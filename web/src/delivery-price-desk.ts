// The Delivery prices desk's models (markets-desk.md, Delivery prices): what its chart and tiles show of a period read and the catalog.
// Every count, price and instant is the backend's; nothing is computed here, and a missing price stays missing.
import type {TileSpec} from './tiles.js';
import type {TimePoint} from './time-chart.js';

export type DeliveryRow={start:string;end:string;formattedPrice:string;knownAt:string};
export type DeliveryExtreme={price:string;start:string};
export type DeliverySummary={intervals:number;expectedIntervals:number;missingIntervals:number;low:DeliveryExtreme|null;high:DeliveryExtreme|null;
  average:string|null;latestKnownAt:string|null;policy:string};
export type DeliveryCollection={jobs:number;enabledJobs:number;latestAttempt:{state:string;finishedAt:string|null;fromDate:string;throughDate:string;errorCode:string|null}|null};

/**
 * One point per published interval, at its start, with the exact price string. Where the next published interval does not start
 * as the previous one ends, a point without a price at that end keeps the chart from drawing a line across the unpublished time.
 */
export function deliveryChartPoints(rows:readonly Pick<DeliveryRow,'start'|'end'|'formattedPrice'>[]):TimePoint[]{
  return rows.flatMap((row,i)=>{
    const previous=rows[i-1],gap=previous!==undefined&&Date.parse(previous.end)!==Date.parse(row.start);
    return [...(gap?[{at:previous.end,value:null}]:[]),{at:row.start,value:row.formattedPrice}];
  });
}

/**
 * The period summary's tiles: the intervals published of those expected, the low and the high with the interval each starts, the
 * time-weighted average and the latest knowledge used. `start` names an interval's start as the table does; `instant` any instant.
 */
export function summaryTiles(summary:DeliverySummary,unit:string,labels:{start:(at:string)=>string;instant:(at:string)=>string}):TileSpec[]{
  const none=(label:string,figure:string):TileSpec=>({label,rows:[{figure}],words:true});
  const extreme=(label:string,value:DeliveryExtreme|null):TileSpec=>value?{label,aside:labels.start(value.start),rows:[{figure:value.price,caption:unit}]}:none(label,'No price');
  return [
    {label:'Intervals',rows:[{figure:String(summary.intervals),caption:`of ${summary.expectedIntervals}`}],...(summary.missingIntervals?{note:`${summary.missingIntervals} missing at this cutoff`}:{})},
    extreme('Low',summary.low),extreme('High',summary.high),
    summary.average===null?none('Average','No price'):{label:'Average',aside:'Time-weighted',rows:[{figure:summary.average,caption:unit}]},
    none('Latest knowledge',summary.latestKnownAt===null?'Nothing published':labels.instant(summary.latestKnownAt)),
  ];
}

/** The collection tile: the jobs, how many are enabled, and the newest attempt's outcome and delivery dates, as the catalog states them. */
export function collectionTile(collection:DeliveryCollection,date:(day:string)=>string):TileSpec{
  const aside=`${collection.jobs} ${collection.jobs===1?'job':'jobs'} · ${collection.enabledJobs} enabled`,attempt=collection.latestAttempt;
  if(!attempt)return {label:'Collection',aside,rows:[{figure:'No attempts yet'}],words:true};
  const range=attempt.fromDate===attempt.throughDate?date(attempt.fromDate):`${date(attempt.fromDate)} – ${date(attempt.throughDate)}`;
  return {label:'Collection',aside,rows:[{figure:attempt.state,caption:`newest attempt · ${range}`}],...(attempt.errorCode?{note:attempt.errorCode}:{}),words:true};
}
