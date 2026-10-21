export type CsvSegment={start:string;values:string[]};

// Turns a two-column file into the segments the managed backend expands onto the series' grid.
// This is layout only: no decimal or local-time interpretation happens here. Values travel as
// written, starts must say Z, and the backend rejects anything it cannot read. Rows share a
// segment when each starts one resolution after the one before, measured in UTC milliseconds
// so a repeated local hour stays consecutive. Only a segment's first start reaches the backend.
export function csvSegments(text:string,intervalMinutes:number):CsvSegment[]{
  const lines=text.split(/\r?\n/);
  if(lines[lines.length-1]==='')lines.pop();
  if(lines[0]!=='start,value')throw Error('Row 1: The header must be exactly start,value.');
  const segments:CsvSegment[]=[],step=intervalMinutes*60000;
  let expected=Number.NaN;
  lines.slice(1).forEach((line,index)=>{
    const row=index+2,cells=line.split(',');
    if(cells.length!==2)throw Error(`Row ${row}: Expected two columns, start and value, but found ${cells.length}.`);
    const start=cells[0].trim(),value=cells[1].trim();
    if(!start.endsWith('Z'))throw Error(`Row ${row}: Write the start as a UTC instant ending in Z.`);
    if(value==='')throw Error(`Row ${row}: The value is empty. An import never withdraws an interval; use Withdraw on a value instead.`);
    const at=Date.parse(start),last=segments[segments.length-1];
    if(last&&at===expected)last.values.push(value);
    else segments.push({start,values:[value]});
    expected=at+step;
  });
  return segments;
}
