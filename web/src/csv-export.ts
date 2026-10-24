// Spreadsheet exports: every cell quoted, and text a spreadsheet could read as a formula prefixed with an apostrophe.
// Only an exact decimal in a column the caller names numeric is kept as it is, so amounts stay exact (D114).
export function guardedCsv(rows:string[][],numeric:number[]=[]):string{
 return rows.map((row,r)=>row.map((v,i)=>{const value=r>0&&numeric.includes(i)&&/^-?\d+(\.\d+)?$/.test(v)?v:/^[\s=+\-@]/.test(v)?"'"+v:v;return '"'+value.replaceAll('"','""')+'"';}).join(',')).join('\r\n')+'\r\n';
}
// Hands the browser a CSV file to save.
export function downloadCsv(name:string,csv:string){
 const url=URL.createObjectURL(new Blob([csv],{type:'text/csv;charset=utf-8'})),link=document.createElement('a');
 link.href=url;link.download=name;link.click();setTimeout(()=>URL.revokeObjectURL(url),0);
}
