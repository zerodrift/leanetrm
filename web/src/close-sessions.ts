// What the As-of control offers under the chosen close session (D334): that session's recorded closes, and every working
// snapshot separately, since an unrecorded snapshot is working data, not a close. No session chosen offers no close; nothing
// chooses one. A historical view offers the closes of its own business day.
export type FrameClose={session:string;sessionVersion:number;businessDate:string;replacedBy:string|null};
type Offered={id:string;at:string;businessDate:string;close?:FrameClose|null};

// An unrecorded snapshot reads as working data wherever it is named, with its instant, since it belongs to no session.
export const workingSnapshot=(instant:string)=>'Working snapshot · '+instant;

export function sessionOffer<T extends Offered>(frames:readonly T[],session:string|null,effectiveOn:string|null):{closes:T[];working:T[]}{
 return {closes:frames.filter(frame=>session!==null&&frame.close?.session===session&&(effectiveOn===null||frame.businessDate===effectiveOn)),
  working:frames.filter(frame=>!frame.close&&(effectiveOn===null||frame.businessDate===effectiveOn))};
}

// The dates P&L explain compares under the chosen close session (D398): that session's recorded closes, each labelled by its
// business day since the session names its time of day, and every working snapshot, labelled by its instant. A working
// snapshot belongs to no session.
export function comparableDates(frames:readonly Offered[],session:string|null,day:(date:string)=>string,instant:(at:string)=>string){
 const offer=sessionOffer(frames,session,null);
 return [...offer.closes,...offer.working].map(frame=>({id:frame.id,at:frame.at,label:frame.close?day(frame.close.businessDate):workingSnapshot(instant(frame.at)),session:frame.close?.session??null}));
}

// The frame a separate window's recorded close id names, while that close is offered and still in force. A replaced close is history (D334).
export function linkedFrame<T extends Offered&{originCloseId?:string|null}>(offered:readonly T[],closeId:string):T|undefined{
 return offered.find(frame=>frame.originCloseId===closeId&&frame.close?.replacedBy==null);
}

// A frame that is a close of another session than the chosen one is not opened: the person is told which session it needs, and
// nothing changes their remembered choice (D334). Null for a frame of the chosen session, a working snapshot or an unknown frame.
export function otherSessionRefusal(frames:readonly Offered[],frameId:string,session:string|null,names:ReadonlyMap<string,string>):string|null{
 const close=frames.find(frame=>frame.id===frameId)?.close;
 if(!close||close.session===session)return null;
 return `That answer used the ${names.get(close.session)??close.session} close session. Choose that session to open its As of date.`;
}
