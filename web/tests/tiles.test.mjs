import test from 'node:test';
import assert from 'node:assert/strict';

// A minimal DOM: elements with a class, text, dataset, style properties and children.
class Text{constructor(text){this.textContent=text;}}
class Element{
  constructor(tag){this.tag=tag;this.className='';this.dataset={};this.children=[];this.style={setProperty:(k,v)=>{this.style[k]=v;}};this._text='';}
  get textContent(){return this._text+this.children.map(c=>c.textContent).join('');}
  set textContent(value){this._text=value;this.children=[];}
  append(...items){this.children.push(...items);}
}
globalThis.document={createElement:tag=>new Element(tag),createTextNode:text=>new Text(text)};
const {renderTile}=await import('../dist/tiles.js');
const tree=e=>({tag:e.tag,class:e.className,text:e.textContent,sign:e.dataset.sign,kids:e.children.filter(c=>c instanceof Element).map(tree)});

test('a tile is a label, one block per row with its figure and caption, and an optional note',()=>{
  const tile=renderTile({label:'Front price',rows:[{figure:'2.345',caption:'USD/MMBtu',sign:'negative'},{figure:'7'}],note:'Settles later'});
  assert.equal(tile.className,'desk-tile');
  const [label,first,second,note]=tree(tile).kids;
  assert.equal(label.class,'desk-tile-label');assert.equal(label.text,'Front price');
  assert.equal(first.class,'desk-tile-row');
  const [value]=first.kids;
  assert.equal(value.class,'kpi-value');assert.equal(value.text,'2.345 USD/MMBtu');assert.equal(value.sign,'negative');
  assert.equal(value.kids[0].class,'kpi-figure');assert.equal(value.kids[0].text,'2.345');
  assert.equal(second.kids[0].text,'7','a row without a caption shows only its figure');
  assert.equal(second.kids[0].sign,undefined,'no stated sign sets no sign');
  assert.equal(note.class,'desk-tile-note');assert.equal(note.text,'Settles later');
});

test('a tile without a note has none',()=>{
  const tile=renderTile({label:'Contracts',rows:[{figure:'12'}]});
  assert.equal(tile.children.length,2);
});
