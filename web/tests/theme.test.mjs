import test from 'node:test';
import assert from 'node:assert/strict';
import {readdirSync,readFileSync} from 'node:fs';
import {built,contrast,css,rootBlock,sheet,token,tokenNames} from './theme-tokens.mjs';

// The app has one theme (D383): a dark palette stated once, as tokens in app.css's :root block. Everything else names a token, so no
// rule can keep a colour of its own that the palette does not know.
const sheets={'app.css':css.replace(rootBlock,''),'position-window.css':sheet('position-window.css')};
// A colour written out: hex, a colour function, or one of the common colour names as a whole word of a declaration's value.
const written=/#[0-9a-f]{3,8}\b|\b(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch)\(|(?<![\w-])(?:white|black|red|green|blue|gr[ae]y|silver|navy|orange|yellow|purple|teal|pink|brown|gold|maroon|olive|lime|aqua|cyan|magenta|violet|indigo|crimson|coral|salmon|khaki|beige|ivory)(?![\w-])/i;
test('every colour in the stylesheets is a theme token',()=>{
 for(const [name,text] of Object.entries(sheets)){
  const literals=[...text.matchAll(/[{;]\s*([\w-]+)\s*:([^{};]*)/g)].filter(([,property,value])=>!property.startsWith('font')&&property!=='content'&&written.test(value)).map(m=>m[0].slice(1).trim());
  assert.deepEqual(literals,[],`${name} states colours outside the theme`);
 }
 // The check sees what it is for: a written colour in each form is one.
 for(const value of ['#fff','rgb(0 0 0/.4)','solid red','white'])assert.match(value,written);
 for(const value of ['var(--surface)','pre-wrap','transparent','currentColor','nowrap'])assert.doesNotMatch(value,written);
});
test('every custom property the stylesheets read is defined there or set by a view',()=>{
 const all=Object.values(sheets).join('')+rootBlock;
 const defined=new Set([...all.matchAll(/(--[\w-]+)\s*:/g)].map(m=>m[1]));
 const dist=new URL('../dist/',import.meta.url);
 for(const file of readdirSync(dist).filter(name=>name.endsWith('.js')))for(const m of readFileSync(new URL(file,dist),'utf8').matchAll(/setProperty\(\s*['"`](--[\w-]+)/g))defined.add(m[1]);
 const missing=[...new Set([...all.matchAll(/var\(\s*(--[\w-]+)/g)].map(m=>m[1]))].filter(name=>!defined.has(name));
 assert.deepEqual(missing,[]);
});
test('the browser draws its own controls and the page before its stylesheet in the dark scheme',()=>{
 assert.match(rootBlock,/[{;]\s*color-scheme:dark\s*[;}]/);
 for(const page of ['index.html','blotter.html','position.html'])assert.match(built(page),/<meta name="color-scheme" content="dark">/,page);
});

const surfaces=['--bg','--surface','--surface-2','--surface-3','--accent-soft'];
const least=(colour,grounds)=>Math.min(...grounds.map(ground=>contrast(token(colour),token(ground))));
const holds=(colour,grounds,ratio)=>{const found=least(colour,grounds);assert.ok(found>=ratio,`${colour} is ${found.toFixed(2)}:1 on its faintest ground, under ${ratio}:1`);};
test('text meets AA contrast on every surface it is written on',()=>{
 for(const text of ['--text','--text-2','--text-3','--accent-text','--warn','--danger'])holds(text,surfaces,4.5);
 holds('--warn',['--warn-soft'],4.5);holds('--danger',['--danger-soft'],4.5);holds('--gain-ink',['--gain-soft'],4.5);
 holds('--on-accent',['--accent','--accent-hover'],4.5);
 holds('--sidebar-text',['--sidebar','--sidebar-2','--sidebar-active'],4.5);holds('--text',['--sidebar','--sidebar-2','--sidebar-active'],4.5);
 holds('--sidebar-muted',['--sidebar','--sidebar-2'],4.5);
});
test('text that carries a gain or a loss is above 6:1 on every surface',()=>{
 for(const ink of ['--gain-ink','--loss-ink'])holds(ink,surfaces,6);
});
test('marks, focus rings and the text of unavailable choices are 3:1 or better on every surface',()=>{
 for(const mark of ['--accent','--focus','--gas','--power','--gain','--loss','--spread','--side-cash','--side-fee','--side-neutral','--text-disabled'])holds(mark,surfaces,3);
 holds('--focus',['--sidebar','--sidebar-2','--sidebar-active'],3);
});
test('volatility rich and cheap are 4.5:1 or better on every surface',()=>{
 for(const tone of ['--vol-rich','--vol-cheap'])holds(tone,surfaces,4.5);
});
test('each curve kind is a fill of 6:1 or better on every surface under an icon of 7:1 or better',()=>{
 const kinds=tokenNames.filter(name=>name.startsWith('--kind-'));
 assert.equal(kinds.length,10);
 for(const kind of kinds){holds(kind,surfaces,6);holds('--on-kind',[kind],7);}
});

// The sign-in page is served before the application's stylesheet may be read, so it states the theme's colours itself.
test('the sign-in page is drawn in the theme\'s colours',()=>{
 const source=readFileSync(new URL('../../src/OpenEtrm.Api/HostedAuthentication.cs',import.meta.url),'utf8');
 const style=/MapGet\("\/style\.css",\(\)=>Results\.Content\(\"\"\"([\s\S]*?)\"\"\"/.exec(source)?.[1];
 assert.ok(style,'the sign-in stylesheet was not found');assert.match(style,/:root\{color-scheme:dark\}/);
 const theme=new Set(tokenNames.filter(name=>/^#[0-9a-f]{3,6}$/i.test(token(name))).map(token));
 const stated=[...new Set(style.replace(/\/\*[\s\S]*?\*\//g,'').match(/#[0-9a-f]{3,8}\b/gi))];
 assert.ok(stated.length>=8,'the sign-in stylesheet states its colours');
 assert.deepEqual(stated.filter(colour=>!theme.has(colour)),[]);
});
