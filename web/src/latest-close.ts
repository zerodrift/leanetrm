import {closesOnDate,uniqueClose,type MarketDates} from './market-calendar.js';

type Availability=Pick<MarketDates,'dates'|'maxDate'>;

// What opening on the newest close does (D218). A date with one close selects it. A date with several closes or
// correction versions asks, and nothing is selected for it (D162). No available date selects nothing.
export type CloseChoice={kind:'single';closeId:string;date:string}|{kind:'choose';date:string}|{kind:'none'};
// What the As-of control tells the person about the close it shows: the latest, an earlier one with nothing newer
// since it was selected, that a newer close has become available, or that a close still has to be chosen.
export type CloseBadge='latest'|'selected'|'newer'|'choose';

// The backend names the newest date (`maxDate`). A name that lists no close, that is not the latest date listed, or
// that is missing while dates exist is an inconsistent answer: it is refused, never read as an older or empty calendar.
export function newestDate(catalog:Availability):string|null{
 const {maxDate}=catalog;
 if(maxDate===null&&!catalog.dates.length)return null;
 if(maxDate===null||!closesOnDate(catalog,maxDate).length||catalog.dates.some(group=>group.date>maxDate))
  throw Error('The available market dates are inconsistent about their newest date. Reload the calendar.');
 return maxDate;
}

export function newestClose(catalog:Availability):CloseChoice{
 const date=newestDate(catalog);
 if(date===null)return {kind:'none'};
 const closeId=uniqueClose(catalog,date);
 return closeId===undefined?{kind:'choose',date}:{kind:'single',closeId,date};
}

// A close on the newest date is the latest close. Nothing selected asks for a choice. Otherwise the close is earlier:
// it is Newer when the calendar's newest date is later than the one it had when this close was selected (null when it
// had none), whoever selected it, and Selected while nothing newer has appeared since.
export function closeBadge(selectedId:string,newestAtSelection:string|null,catalog:Availability):CloseBadge{
 const date=newestDate(catalog);
 if(selectedId==='')return 'choose';
 if(date!==null&&closesOnDate(catalog,date).some(close=>close.id===selectedId))return 'latest';
 return date!==null&&newestAtSelection!==null&&date>newestAtSelection?'newer':'selected';
}
