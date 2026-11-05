// Preserve an offered value; fill a missing choice only when it is unambiguous.
export function linkedValue(values:readonly string[],current=''){return values.includes(current)?current:values.length===1?values[0]:'';}
