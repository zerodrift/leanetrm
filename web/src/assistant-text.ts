// How the panel draws an answer (docs/ai-assistant.md §10.1, D191, D247): paragraphs, lists, tables, emphasis and code,
// built from text nodes only. Links, images and markup stay literal text, and URLs are never linked, so text injected
// into an answer cannot carry data out.
export type Inline={kind:'text'|'emphasis'|'strong'|'code';text:string};
export type Block={kind:'paragraph';lines:Inline[][]}|{kind:'list';ordered:boolean;items:Inline[][]}|{kind:'code';text:string}
  |{kind:'table';header:Inline[][];rows:Inline[][][]};

const bullet=/^ {0,3}[-*+][ \t]+(.*)$/,numbered=/^ {0,3}\d{1,9}[.)][ \t]+(.*)$/,fence=/^ {0,3}```/;
const piped=/^ {0,3}\|.*\|[ \t]*$/,separator=/^:?-+:?$/;
// A pipe-table line's cells, trimmed; the pipes at either end open and close the row.
const cells=(line:string)=>line.trim().slice(1,-1).split('|').map(cell=>cell.trim());

// An answer's blocks: paragraphs split on blank lines, keeping their single line breaks; runs of bulleted or numbered
// items; and fenced code, kept exactly and running to the end while its fence is still open, as it is mid-stream.
export function answerBlocks(text:string):Block[]{
  const lines=text.replace(/\r\n?/g,'\n').split('\n'),blocks:Block[]=[];
  let paragraph:Inline[][]|undefined,list:{ordered:boolean;items:Inline[][]}|undefined;
  const close=()=>{if(paragraph)blocks.push({kind:'paragraph',lines:paragraph});if(list)blocks.push({kind:'list',...list});paragraph=undefined;list=undefined;};
  for(let i=0;i<lines.length;i++){
    const line=lines[i];
    if(fence.test(line)){
      close();const code:string[]=[];
      for(i++;i<lines.length&&lines[i].trim()!=='```';i++)code.push(lines[i]);
      blocks.push({kind:'code',text:code.join('\n')});continue;
    }
    if(!line.trim()){close();continue;}
    // A table is a header row, a separator row of dashes for each column, and rows of the header's width; anything
    // else stays text.
    if(piped.test(line)&&piped.test(lines[i+1]??'')&&cells(lines[i+1]).every(cell=>separator.test(cell))&&cells(lines[i+1]).length===cells(line).length){
      const width=cells(line).length;let end=i+2;
      while(end<lines.length&&piped.test(lines[end]))end++;
      if(lines.slice(i+2,end).every(row=>cells(row).length===width)){
        close();
        blocks.push({kind:'table',header:cells(line).map(inlines),rows:lines.slice(i+2,end).map(row=>cells(row).map(inlines))});
        i=end-1;continue;
      }
    }
    const item=bullet.exec(line)??numbered.exec(line);
    if(item){
      const ordered=!bullet.test(line);
      if(paragraph||list&&list.ordered!==ordered)close();
      list??={ordered,items:[]};list.items.push(inlines(item[1]));continue;
    }
    if(list)close();
    (paragraph??=[]).push(inlines(line));
  }
  close();
  return blocks;
}

// A line's spans: code spans first, whose text is literal, then **strong** and *emphasis*, each only when closed and
// not opening or closing on a space; anything else is text. A line longer than 2,000 characters stays text, which keeps
// drawing a long answer, again with every piece that streams, within time.
export function inlines(line:string):Inline[]{
  if(line.length>2000)return [{kind:'text',text:line}];
  const spans:Inline[]=[];
  const push=(kind:Inline['kind'],text:string)=>{
    if(!text)return;const last=spans.at(-1);
    if(kind==='text'&&last?.kind==='text')last.text+=text;else spans.push({kind,text});
  };
  const emphasis=(text:string)=>{
    let from=0;
    for(const m of text.matchAll(/\*\*(?=\S)(.+?)(?<=\S)\*\*|\*(?=[^\s*])(.+?)(?<=[^\s*])\*/g)){
      push('text',text.slice(from,m.index));
      if(m[1]!==undefined)push('strong',m[1]);else push('emphasis',m[2]);
      from=m.index+m[0].length;
    }
    push('text',text.slice(from));
  };
  let at=0;
  for(const m of line.matchAll(/`([^`]+)`/g)){emphasis(line.slice(at,m.index));push('code',m[1]);at=m.index+m[0].length;}
  emphasis(line.slice(at));
  return spans;
}

// The blocks as nodes: each element holds text nodes only.
export function renderBlocks(blocks:Block[],document:Document){
  const fragment=document.createDocumentFragment();
  const spans=(parent:HTMLElement,line:Inline[])=>{
    for(const span of line){
      if(span.kind==='text'){parent.append(document.createTextNode(span.text));continue;}
      const node=document.createElement(span.kind==='emphasis'?'em':span.kind);node.textContent=span.text;parent.append(node);
    }
  };
  for(const block of blocks){
    if(block.kind==='code'){const pre=document.createElement('pre'),code=document.createElement('code');code.textContent=block.text;pre.append(code);fragment.append(pre);continue;}
    if(block.kind==='table'){
      const table=document.createElement('table'),head=document.createElement('thead'),body=document.createElement('tbody');
      const row=(cellsOf:Inline[][],tag:'th'|'td')=>{const tr=document.createElement('tr');for(const cell of cellsOf){const node=document.createElement(tag);spans(node,cell);tr.append(node);}return tr;};
      head.append(row(block.header,'th'));for(const cellsOf of block.rows)body.append(row(cellsOf,'td'));
      table.append(head,body);fragment.append(table);continue;
    }
    if(block.kind==='list'){
      const list=document.createElement(block.ordered?'ol':'ul');
      for(const item of block.items){const li=document.createElement('li');spans(li,item);list.append(li);}
      fragment.append(list);continue;
    }
    const p=document.createElement('p');
    block.lines.forEach((line,index)=>{if(index)p.append(document.createElement('br'));spans(p,line);});
    fragment.append(p);
  }
  return fragment;
}
