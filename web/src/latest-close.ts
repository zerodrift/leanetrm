import {closesOnDate,uniqueClose,type MarketDates} from './market-calendar.js';

type Availability=Pick<MarketDates,'dates'|'maxDate'>;

// What opening on the newest close does (D208). A date with one close selects it. A date with several closes or
// correction versions asks, and nothing is selected for it (D162). No available date selects nothing.
export type CloseChoice={kind:'single';closeId:string;date:string}|{kind:'choose';date:string}|{kind:'none'};
// What the As-of control tells the person about the close it shows: the newest, one they picked, a newer one that
// arrived after the app chose, or that a close still has to be chosen.
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

// A close on the newest date is the latest close, whoever chose it. Nothing selected asks for a choice. An earlier
// close is Selected when the person picked it and Newer when the app chose it before a later date appeared.
export function closeBadge(selectedId:string,chosenByUser:boolean,catalog:Availability):CloseBadge{
 const date=newestDate(catalog);
 if(selectedId==='')return 'choose';
 if(date!==null&&closesOnDate(catalog,date).some(close=>close.id===selectedId))return 'latest';
 return chosenByUser?'selected':'newer';
}
