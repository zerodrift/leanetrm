// The notices one action of the page says are kept beside each other, so a later one does not replace an earlier one unseen, and one
// already said is not said twice.
export const withNotice=(shown:string,message:string)=>!shown?message:shown.includes(message)?shown:`${shown} ${message}`;
