// The theme's tokens as the built stylesheet states them, and the WCAG 2.x contrast ratio of two of its colours.
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
/** A built file's text. */
export const built=name=>readFileSync(new URL('../dist/'+name,import.meta.url),'utf8');
/** A built stylesheet's rules, without its comments. */
export const sheet=name=>built(name).replace(/\/\*[\s\S]*?\*\//g,'');
export const css=sheet('app.css');
/** The `:root` block, which alone states colours. */
export const rootBlock=/:root\{([^}]*)\}/.exec(css)[0];
const declared=new Map(rootBlock.slice(6,-1).split(';').map(d=>d.trim()).filter(d=>d.startsWith('--')).map(d=>[d.slice(0,d.indexOf(':')),d.slice(d.indexOf(':')+1).trim()]));
export const tokenNames=[...declared.keys()];
/** The value of a token, through the tokens it names. */
export function token(name){
 const value=declared.get(name);assert.ok(value,`the theme does not define ${name}`);
 const alias=/^var\((--[\w-]+)\)$/.exec(value);return alias?token(alias[1]):value;
}
/** A declared value as a colour: a token reference resolved, anything else as written. */
export const resolved=value=>value.startsWith('var(')?token(value.slice(4,-1)):value;
const channels=hex=>(hex.length===4?[...hex.slice(1)].map(c=>c+c):hex.slice(1).match(/../g)).map(c=>parseInt(c,16)/255);
const luminance=hex=>{const [r,g,b]=channels(hex).map(c=>c<=0.03928?c/12.92:((c+0.055)/1.055)**2.4);return 0.2126*r+0.7152*g+0.0722*b;};
export function contrast(a,b){
 for(const colour of [a,b])assert.match(colour,/^#(?:[0-9a-f]{3}|[0-9a-f]{6})$/i,`${colour} is not a hex colour`);
 const [light,dark]=[luminance(a),luminance(b)].sort((x,y)=>y-x);return (light+0.05)/(dark+0.05);
}
