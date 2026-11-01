// The exact decimal or rational text the backend writes for amounts ("-1234.5", "-10/3"), read by one grammar into its digits. The text stays
// the value; the browser never rounds it.
export type ExactNumber={sign:''|'-';integer:string;fraction:string|null;denominator:string|null};
export function exactNumber(value:string):ExactNumber|null{
 const m=/^(-?)(\d+)(?:\.(\d+)|\/([1-9]\d*))?$/.exec(value);
 return m?{sign:m[1] as ''|'-',integer:m[2],fraction:m[3]??null,denominator:m[4]??null}:null;
}
