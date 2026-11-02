/** The CSS colour of a side colour token: `--side-<token>` in app.css. */
export function sideColour(token:string){
  if(!/^[a-z][a-z0-9_-]*$/.test(token))throw Error(`A side colour must be a colour token name, not '${token}'.`);
  return `var(--side-${token})`;
}
