// The Vol surfaces page's colours, read from the theme's tokens where a view draws, each time it draws. A token the theme does not
// define is refused rather than drawn in some other colour.
export type VolTheme=Record<'surface'|'surface2'|'surface3'|'border'|'borderStrong'|'text'|'text2'|'text3'|'accent'|'rich'|'cheap'|'font',string>;
const tokens:Record<keyof VolTheme,string>={surface:'--surface',surface2:'--surface-2',surface3:'--surface-3',border:'--border',borderStrong:'--border-strong',text:'--text',
 text2:'--text-2',text3:'--text-3',accent:'--accent',rich:'--vol-rich',cheap:'--vol-cheap',font:'--font'};

/** The value of the theme token `name` at `node`. */
export function themeToken(node:Element,name:string):string{
 const value=getComputedStyle(node).getPropertyValue(name).trim();
 if(!value)throw Error(`The theme does not define ${name}.`);
 return value;
}
export function volTheme(node:Element):VolTheme{
 return Object.fromEntries(Object.entries(tokens).map(([key,name])=>[key,themeToken(node,name)])) as VolTheme;
}

/** The tone a rich/cheap value is drawn in: rich above the fitted smile, cheap below it, and neutral exactly on it. */
export function richCheapTone(value:string):'rich'|'cheap'|'neutral'{
 const match=/^(-?)(\d+)(?:\.(\d+))?$/.exec(value);
 if(!match)throw Error('The surface returned a value that is not exact decimal text.');
 return !/[1-9]/.test(match[2]+(match[3]??''))?'neutral':match[1]?'cheap':'rich';
}
/** The colour of a rich/cheap value's tone in `theme`; a value exactly on the smile takes the neutral third text colour. */
export function richCheapColour(theme:VolTheme,value:string):string{
 const tone=richCheapTone(value);
 return tone==='neutral'?theme.text3:theme[tone];
}
