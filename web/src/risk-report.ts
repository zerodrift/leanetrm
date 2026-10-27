import type {ValuationReason} from './valuation-status.js';

export type RiskAmounts={base:string;up:string;down:string;upPnl:string;downPnl:string;delta:string;gamma:string};
export type RiskValues=RiskAmounts&{display:RiskAmounts;currency:string;currencyReference:unknown;deltaUnit:string;gammaUnit:string};
export type RiskTotal={bookId:string;book:string;measure:string;measureLabel:string;values:RiskValues};
/**
 * A shocked factor as a report states it: the catalog's target, by its id and label, in its own price unit, with the curves it belongs to
 * and, for a contract quote, the delivery period it covers.
 */
export type RiskFactor={id:string;label:string;coordinate:string;kind:string;unit:string;curves:string[];deliveryFrom:string|null;deliveryTo:string|null};
/** A position a run leaves out because its base close cannot value it (D220), with the book it is held in. */
export type ExcludedPosition={tradeId:string;bindingId:string;bookId:string;tradeReference:string;instrumentType:string;reason:ValuationReason};
/** A holding's results under one factor: a position is named by its trade and binding. */
export type RiskPosition={tradeId:string;bindingId:string;bookId:string;book:string;tradeReference:string;instrumentReference:string;instrumentType:string;
  measure:string;measureLabel:string;values:RiskValues};

export type RiskWorkReport={heldInstruments:number;work:{positionCount:number;uniqueValuations:number;scenarioCount:number;preparationScenarioCount:number;unitPricerCalls:number;rebuiltCurveNodes:number;reusedCurveNodes:number;repeatedInstrumentReferences:number;quantityApplications:number};connectedScenarios:{definitions:{revisionId:string};work:{evaluatedNodes:number;reusedReads:number;affectedNodes:number;traversedAffectedEdges:number};preparation:{sourceCompilations:number;declaredSourceNodes:number}}[]};
/**
 * A portfolio risk report: each factor's results by book and by holding, and the positions the run leaves out. An earlier job's
 * result can lack its list of those positions, which is refused when it is shown, never read as leaving out none.
 */
export type RiskReport=RiskWorkReport&{displayPolicy:unknown;scenario:{depth:number;isBase:boolean};context:{contextId:string};snapshotId:string;
  excludedPositions?:ExcludedPosition[];factors:{factor:RiskFactor;bump:string;totals:RiskTotal[];positions:RiskPosition[]}[]};
export function riskWorkDetails(report:RiskWorkReport){
  return [
    `Materialization · ${report.work.rebuiltCurveNodes} curve nodes evaluated · ${report.work.reusedCurveNodes} reused.`,
    ...report.connectedScenarios.map(m=>`Market revision ${m.definitions.revisionId} · ${m.work.evaluatedNodes} graph nodes evaluated · ${m.work.reusedReads} reused reads · ${m.work.affectedNodes} affected nodes · ${m.work.traversedAffectedEdges} affected edges traversed · ${m.preparation.sourceCompilations} source compilations · ${m.preparation.declaredSourceNodes} declared source nodes.`),
  ];
}
