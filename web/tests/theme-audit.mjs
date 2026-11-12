// What breaks the dark theme under an element of a drawn page, as a function to evaluate in that page: `grounds` are the opaque
// backgrounds lighter than `ceiling` (relative luminance) on elements at least 32px each way, which a mark such as a badge is not, and
// `text` is what is written under 4.5:1 on the ground it is drawn on, a form control's value included. Dimmed and disabled content,
// text over a background image and generated content are not judged.
export const themeAudit=`((root,ceiling=.2)=>{
 // A computed colour as red, green, blue (0 to 255) and alpha: rgb(), rgba(), or the color(srgb …) a mixed colour computes to.
 const parse=value=>{
  const m=/^rgba?\\(([\\d.]+), ([\\d.]+), ([\\d.]+)(?:, ([\\d.]+))?\\)$/.exec(value),mixed=/^color\\(srgb ([\\d.e-]+) ([\\d.e-]+) ([\\d.e-]+)(?: \\/ ([\\d.e-]+))?\\)$/.exec(value);
  if(m)return [+m[1],+m[2],+m[3],m[4]===undefined?1:+m[4]];
  if(mixed)return [mixed[1]*255,mixed[2]*255,mixed[3]*255,mixed[4]===undefined?1:+mixed[4]];
  throw Error('The audit cannot read the colour '+value);
 };
 const luminance=colour=>{const [r,g,b]=colour.slice(0,3).map(c=>{c/=255;return c<=.03928?c/12.92:((c+.055)/1.055)**2.4;});return .2126*r+.7152*g+.0722*b;};
 const over=(top,under)=>[0,1,2].map(i=>top[i]*top[3]+under[i]*(1-top[3])).concat(1);
 // The colour behind a node: its own and its ancestors' backgrounds, composed down to the first opaque one, on white where none is.
 const groundOf=node=>{const layers=[];for(let n=node;n;n=n.parentElement){const style=getComputedStyle(n);if(style.backgroundImage!=='none')return null;const colour=parse(style.backgroundColor);if(colour[3]>0)layers.push(colour);if(colour[3]===1)break;}
  return layers.reduceRight((under,top)=>over(top,under),[255,255,255,1]);};
 const dimmed=node=>{for(let n=node;n;n=n.parentElement){if(+getComputedStyle(n).opacity<1||n.matches(':disabled,[aria-disabled=true]'))return true;}return false;};
 const name=node=>node.tagName.toLowerCase()+(node.id?'#'+node.id:'')+[...node.classList].map(c=>'.'+c).join('');
 const hex=colour=>'#'+colour.slice(0,3).map(c=>Math.round(c).toString(16).padStart(2,'0')).join('');
 const grounds=[],text=[];
 for(const node of [root,...root.querySelectorAll('*')]){
  const box=node.getBoundingClientRect(),style=getComputedStyle(node);
  if(!box.width||!box.height||style.visibility!=='visible')continue;
  const own=parse(style.backgroundColor);
  if(own[3]>=.5&&box.width>=32&&box.height>=32&&luminance(own)>ceiling)grounds.push(name(node)+' '+hex(own));
  const words=node.matches('input:not([type=hidden]),select,textarea')?node.value||node.placeholder||'value':[...node.childNodes].filter(child=>child.nodeType===3&&child.textContent.trim()).map(child=>child.textContent.trim()).join(' ');
  if(!words||dimmed(node))continue;
  const ground=groundOf(node);if(!ground)continue;
  const ink=over(parse(style.color),ground),[light,dark]=[luminance(ink),luminance(ground)].sort((a,b)=>b-a),ratio=(light+.05)/(dark+.05);
  if(ratio<4.5)text.push(name(node)+' "'+words.slice(0,40)+'" '+hex(ink)+' on '+hex(ground)+' '+ratio.toFixed(2)+':1');
 }
 return {grounds,text};
})`;
