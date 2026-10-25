import {closesOnDate,type MarketDates} from './market-calendar.js';
import {latestFrame} from './context-defaults.js';

type Availability=Pick<MarketDates,'dates'|'maxDate'>;

// What opening on the newest close does (D218, with D217's latest instant). The uniquely latest close of the calendar is
// selected. Several closes at that instant, such as versions of one close, ask, and nothing is selected for them (D162).
// No available date selects nothing.
export type CloseChoice={kind:'single';closeId:string;date:string}|{kind:'choose';date:string}|{kind:'none'};
// What the As-of control tells the person about the close it shows: the latest, an earlier one with nothing newer
// since it was selected, that a newer close has become available, or that a close still has to be chosen.
export type CloseBadge='latest'|'selected'|'newer'|'choose';

const inconsistent=()=>Error('The available market dates are inconsistent about their newest date. Reload the calendar.');

// The backend names the newest date (`maxDate`). A name that lists no close, that is not the latest date listed, or
// that is missing while dates exist is an inconsistent answer: it is refused, never read as an older or empty calendar.
export function newestDate(catalog:Availability):string|null{
 const {maxDate}=catalog;
 if(maxDate===null&&!catalog.dates.length)return null;
 if(maxDate===null||!closesOnDate(catalog,maxDate).length||catalog.dates.some(group=>group.date>maxDate))throw inconsistent();
 return maxDate;
}

// The closes at the latest instant the calendar lists, which are on its newest date, and that instant. A close there
// whose time cannot be read is refused: no close is compared with one whose time is unknown.
function latestCloses(catalog:Availability){
 const date=newestDate(catalog);
 if(date===null)return null;
 const closes=closesOnDate(catalog,date),times=closes.map(close=>Date.parse(close.at));
 if(times.some(Number.isNaN))throw inconsistent();
 const latest=Math.max(...times);
 return {date,at:closes[times.indexOf(latest)].at,closes:closes.filter((_,i)=>times[i]===latest)};
}

// The instant of the calendar's latest close, as the backend states it; null for a calendar without closes.
export function latestInstant(catalog:Availability):string|null{return latestCloses(catalog)?.at??null;}

export function newestClose(catalog:Availability):CloseChoice{
 const latest=latestCloses(catalog);
 if(latest===null)return {kind:'none'};
 const closeId=latestFrame(latest.closes);
 return closeId?{kind:'single',closeId,date:latest.date}:{kind:'choose',date:latest.date};
}

// A close at the latest instant is the latest close. Nothing selected asks for a choice. Otherwise the close is earlier:
// it is Newer when the calendar's latest instant is later than the one it had when this close was selected (null when it
// had none), whoever selected it, and Selected while nothing newer has appeared since.
export function closeBadge(selectedId:string,latestAtSelection:string|null,catalog:Availability):CloseBadge{
 const latest=latestCloses(catalog);
 if(selectedId==='')return 'choose';
 if(latest!==null&&latest.closes.some(close=>close.id===selectedId))return 'latest';
 return latest!==null&&latestAtSelection!==null&&Date.parse(latest.at)>Date.parse(latestAtSelection)?'newer':'selected';
}
