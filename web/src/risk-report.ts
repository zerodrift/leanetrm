export type RiskAmounts={base:string;up:string;down:string;upPnl:string;downPnl:string;delta:string;gamma:string};
export type RiskValues=RiskAmounts&{display:RiskAmounts;currency:string;currencyReference:unknown;deltaUnit:string;gammaUnit:string};
export type RiskTotal={bookId:string;book:string;measure:string;measureLabel:string;values:RiskValues};

export function riskTotalGroups(rows:RiskTotal[]){
  const groups=new Map<string,RiskTotal[]>();
  for(const row of rows){
    const key=JSON.stringify([row.measure,row.values.currencyReference,row.values.deltaUnit,row.values.gammaUnit]);
    const group=groups.get(key)??[];group.push(row);groups.set(key,group);
  }
  return [...groups.values()];
}

export type RiskWorkReport={heldInstruments:number;work:{positionCount:number;uniqueValuations:number;scenarioCount:number;preparationScenarioCount:number;unitPricerCalls:number;rebuiltCurveNodes:number;reusedCurveNodes:number;repeatedInstrumentReferences:number;quantityApplications:number};connectedScenarios:{definitions:{revisionId:string};work:{evaluatedNodes:number;reusedReads:number;affectedNodes:number;traversedAffectedEdges:number};preparation:{sourceCompilations:number;declaredSourceNodes:number}}[]};
export function riskWorkDetails(report:RiskWorkReport){
  return [
    `Materialization · ${report.work.rebuiltCurveNodes} curve nodes evaluated · ${report.work.reusedCurveNodes} reused.`,
    ...report.connectedScenarios.map(m=>`Market revision ${m.definitions.revisionId} · ${m.work.evaluatedNodes} graph nodes evaluated · ${m.work.reusedReads} reused reads · ${m.work.affectedNodes} affected nodes · ${m.work.traversedAffectedEdges} affected edges traversed · ${m.preparation.sourceCompilations} source compilations · ${m.preparation.declaredSourceNodes} declared source nodes.`),
  ];
}
