// What the As-of control offers under the chosen close session (D328): that session's recorded closes, and every working
// snapshot separately, since an unrecorded snapshot is working data, not a close. No session chosen offers no close; nothing
// chooses one. A historical view offers the closes of its own business day.
export type FrameClose={session:string;sessionVersion:number;businessDate:string;replacedBy:string|null};
type Offered={id:string;at:string;close?:FrameClose|null};

export function sessionOffer<T extends Offered>(frames:readonly T[],session:string|null,effectiveOn:string|null):{closes:T[];working:T[]}{
 return {closes:frames.filter(frame=>session!==null&&frame.close?.session===session&&(effectiveOn===null||frame.close.businessDate===effectiveOn)),
  working:frames.filter(frame=>!frame.close&&(effectiveOn===null||frame.at.slice(0,10)===effectiveOn))};
}
