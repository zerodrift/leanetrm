// The risk report's presets (blotter-desk.md, Risk report inline): Shock every held contract and Parallel shift. Which targets
// shock what the books hold is the backend's to say, in the catalog's held targets; the browser takes those targets by their id,
// never matching curves or contracts itself, and pairs each with the size the person entered for its price unit.
import {scenarioShift,type ScenarioShift,type ScenarioTarget} from './risk-scenarios.js';

export type Preset='held'|'parallel';
export type PresetCatalog={scenarioTargets:ScenarioTarget[];heldContractTargets:string[];heldCurveTargets:string[]};

const inconsistent='The risk catalog is inconsistent. Reload it.';
// The kind of target each preset shocks: a whole contract, or a whole curve.
const presetKinds:Readonly<Record<Preset,string>>={held:'contract',parallel:'parallel'};

/** The targets a preset shocks, in the order the catalog offers them. A held target the catalog does not offer, or one of another kind, is refused. */
export function presetTargets(catalog:PresetCatalog,kind:Preset):ScenarioTarget[]{
  const ids=kind==='held'?catalog.heldContractTargets:catalog.heldCurveTargets;
  if(!Array.isArray(ids))throw Error(inconsistent);
  const named=new Set(ids),targets=catalog.scenarioTargets.filter(target=>named.has(target.id));
  if(named.size!==ids.length||targets.length!==ids.length||targets.some(target=>target.kind!==presetKinds[kind]))throw Error(inconsistent);
  return targets;
}

/** The price units the targets are quoted in, each once, in the order the targets first name them: one shock size is asked for each. */
export function sizesByUnit(targets:ScenarioTarget[]):string[]{
  return [...new Set(targets.map(target=>target.unit))];
}

/** The bumps of a run in parts of at most `limit`, in order: one holdings request takes at most sixteen. */
export function splitBumps<T>(bumps:T[],limit=16):T[][]{
  if(!Number.isInteger(limit)||limit<1)throw Error('A part holds a positive whole number of bumps.');
  const parts:T[][]=[];
  for(let start=0;start<bumps.length;start+=limit)parts.push(bumps.slice(start,start+limit));
  return parts;
}

/** The bumps of a preset: each target shocked by the size entered for its price unit, which every unit needs. */
export function presetRequest(targets:ScenarioTarget[],sizes:Record<string,string>,convention:string):ScenarioShift[]{
  const entered=new Map(sizesByUnit(targets).map(unit=>{
    const size=Object.hasOwn(sizes,unit)?sizes[unit].trim():'';
    if(!size)throw Error(`Enter a shock size for ${unit}.`);
    return [unit,size] as const;
  }));
  return targets.map(target=>scenarioShift(target,entered.get(target.unit)!,convention));
}
