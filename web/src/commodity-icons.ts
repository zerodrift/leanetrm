// Commodity icons (D425). Reference data names each commodity's icon and the browser only draws it: one path in a 20×20 box
// with round strokes and no fill, in the commodity's colour. An unknown name means the contract is broken: it throws, never
// becomes a fallback drawing, and nothing here reads a commodity's code or name.
const icons={
 flame:'M10 2c1 4 5 5 5 10a5 5 0 0 1-10 0c0-3 2-5 3-6 0 3 1 4 2 4 2-2 1-5 0-8z',
 bolt:'M11 2 4 11h5l-1 7 8-10h-5z',
 drop:'M10 2.5c3 4 5.5 6.6 5.5 9.5a5.5 5.5 0 0 1-11 0c0-2.9 2.5-5.5 5.5-9.5z',
 tanker:'M2 12h16l-2 4H4zM5 12V8h7v4M12 9.5h3l1.5 2.5',
 cylinder:'M6 8a4 4 0 0 1 8 0v7.5a1.5 1.5 0 0 1-1.5 1.5h-5A1.5 1.5 0 0 1 6 15.5zM8.5 2.5h3M10 2.5V4M6 11h8',
 anchor:'M10 6.5a2 2 0 1 0 0-4 2 2 0 0 0 0 4zM10 6.5V17M7 9.5h6M4 11.5a6 6 0 0 0 12 0',
 leaf:'M4 16c0-8 5-12 12-12 0 7-4 12-12 12zM4 16l7-7',
 cube:'M10 2 18 6v8l-8 4-8-4V6zM2 6l8 4 8-4M10 10v8',
} as const;
export type CommodityIcon=keyof typeof icons;
export const isCommodityIcon=(value:unknown):value is CommodityIcon=>typeof value==='string'&&Object.hasOwn(icons,value);
export function commodityIconPath(icon:CommodityIcon){if(!isCommodityIcon(icon))throw Error('Unknown commodity icon');return icons[icon];}
