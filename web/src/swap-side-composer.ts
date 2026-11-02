// Selection against the server's admitted native structures. No side economics
// or extra composition is supplied by the browser.
export type SideStructure={type:string;label?:string;sides:string[]};
const contained=(chosen:readonly string[],available:readonly string[])=>chosen.every(kind=>chosen.filter(k=>k===kind).length<=available.filter(k=>k===kind).length);
export const sideStructure=(structures:readonly SideStructure[],chosen:readonly string[])=>structures.find(s=>s.sides.length===chosen.length&&contained(chosen,s.sides));
export const sideOffers=(structures:readonly SideStructure[],chosen:readonly string[])=>[...new Set(structures.flatMap(s=>s.sides))].filter(kind=>structures.some(s=>contained([...chosen,kind],s.sides)));
