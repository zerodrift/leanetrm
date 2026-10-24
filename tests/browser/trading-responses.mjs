// Preserve actual request paths while inspecting either supported response shape.
function reports(replies){return replies.filter(r=>r.path==='/api/trading/workspace'||r.path==='/api/trading/blotter').map(r=>r.path==='/api/trading/workspace'?{...r,data:r.data.blotter}:r);}
function catalogs(replies){return replies.filter(r=>r.path==='/api/trading/workspace'||r.path==='/api/futures/catalog').map(r=>r.path==='/api/trading/workspace'?{...r,data:r.data.catalog}:r).filter(r=>r.data?.kind==='priced_contracts');}
export async function setupTradingReplies(evaluate){await evaluate(`window.__tradingReports=${reports.toString()};window.__tradingCatalogs=${catalogs.toString()};`);}
