import {run as lazyReads} from './lazy-reads.mjs';
import {run as monteCarloRisk} from './monte-carlo-risk.mjs';
import {run as facilityQuantities} from './facility-quantities.mjs';
import {run as historicalRisk} from './historical-risk.mjs';
import {run as parametricRisk} from './parametric-risk.mjs';
import {run as operatingSettings} from './operating-settings.mjs';
import {run as factorHistory} from './factor-history.mjs';
import {run as crudeSetup} from './crude-setup.mjs';
import {run as riskWorkspace} from './risk-workspace.mjs';
import {run as blotterStreaming} from './blotter-streaming.mjs';
import {run as unifiedTrading} from './unified-trading.mjs';
import {run as physicalLng} from './physical-lng.mjs';
import {run as physicalCrude} from './physical-crude.mjs';
import {run as physicalGas,runReferenceOnly as gasReferences} from './physical-gas.mjs';
import {run as dealTicket} from './deal-ticket.mjs';
import {run as demoInstruments} from './demo-instruments.mjs';
import {run as positionPopout} from './position-popout.mjs';
import {run as blotterPopout} from './blotter-popout.mjs';
import {run as factorSwaps} from './factor-swaps.mjs';
import {run as temporalBrowsing} from './temporal-browsing.mjs';
import {run as scheduledAuthoring} from './scheduled-authoring.mjs';
import {run as scheduledSwaps} from './scheduled-swaps.mjs';
import {run as swapNetting} from './swap-netting.mjs';
import {run as swapSettlement} from './swap-settlement.mjs';
import {run as ppa} from './ppa.mjs';
import {run as ppaComposed} from './ppa-composed.mjs';
import {withOwnedBrowser} from './owned-browser.mjs';
import {run as profileDialog} from './profile-dialog.mjs';
import {run as administration} from './administration.mjs';
import {run as marketContext} from './market-context.mjs';
import {run as dateContext} from './date-context.mjs';
import {run as scheduleExamples} from './schedule-examples.mjs';
import {run as power} from './power.mjs';
import {checkProfileLifetime} from './profile-lifecycle.mjs';
import {run as futures} from './futures.mjs';
import {run as futuresAdmission} from './futures-admission.mjs';
import {run as futuresLoading} from './futures-loading.mjs';
import {run as tradeEditor} from './trade-editor.mjs';
import {run as swaps} from './swaps.mjs';
import {run as swapObservations} from './swap-observations.mjs';
import {run as tableInteractions} from './table-interactions.mjs';
import {run as blotterPlace} from './blotter-place.mjs';
import {run as blotterShell} from './blotter-shell.mjs';
import {run as blotterScope} from './blotter-scope.mjs';
import {run as blotterDetails} from './blotter-details.mjs';
import {run as blotterCloseOut,refusals as blotterCloseOutRefusals} from './blotter-close-out.mjs';
import {run as units} from './units.mjs';
import {run as parties} from './parties.mjs';
import {run as exchange} from './exchange.mjs';
import {run as futuresOptions} from './futures-options.mjs';
import {run as volSurfaces} from './vol-surfaces.mjs';
import {run as risk} from './risk.mjs';
import {run as riskCloses} from './risk-closes.mjs';
import {run as dashboard} from './dashboard.mjs';
import {run as jobs} from './jobs.mjs';
import {run as startupRequests} from './startup-requests.mjs';
import {run as marketNavigationRead} from './market-navigation.mjs';
import {run as marketsDesk,deliveryDesk} from './markets-desk.mjs';
import {run as startupAccess} from './startup-access.mjs';
import {run as reference} from './reference.mjs';
import {run as navigationCache} from './navigation-cache.mjs';
import {run as referenceEditor} from './reference-editor.mjs';
import {run as liveCalculation} from './live-calculation.mjs';
import {run as customCurves} from './custom-curves.mjs';
import {run as curveActivation} from './curve-activation.mjs';
import {run as curveComposition} from './curve-composition.mjs';
import {run as contractDates} from './contract-dates.mjs';
import {run as curveDefinitions} from './curve-definitions.mjs';
import {run as marketDates} from './market-dates.mjs';
import {run as aiSettings} from './ai-settings.mjs';
import {run as agentSettings} from './agent-settings.mjs';
import {run as usageSettings} from './usage-settings.mjs';
import {run as assistantWorkspace} from './assistant-workspace.mjs';
import {run as assistantPanel} from './assistant-panel.mjs';
import {run as pilotClose} from './pilot-close.mjs';
import {run as notices} from './notices.mjs';
import {run as intervalSeries} from './interval-series.mjs';
import {run as intervalCurves} from './interval-curves.mjs';
import {run as valuationStatus} from './valuation-status.mjs';
async function marketNavigation(browser){await startupRequests(browser);await dateContext(browser);await marketNavigationRead(browser);await marketsDesk(browser);await deliveryDesk(browser);await startupAccess(browser);await reference(browser);await marketContext(browser);await scheduleExamples(browser);await referenceEditor(browser);await navigationCache(browser);await lazyReads(browser);}
async function tradingNavigation(browser){await blotterStreaming(browser);await futuresLoading(browser);await futuresAdmission(browser);await futures(browser);}
// The Dashboard shares the risk suite's browser: each owned browser opens a preview session, and the preview caps them.
async function ppaEntry(browser){await ppa(browser);await ppaComposed(browser);}
async function riskAndDashboard(browser){await risk(browser);await riskWorkspace(browser);await dashboard(browser);}
// The table interactions and the Blotter workbench's scenarios share one browser, and so one preview session, for the same reason.
async function tablesAndBlotter(browser){await tableInteractions(browser);await blotterPlace(browser);await blotterDetails(browser);await blotterScope(browser);await blotterShell(browser);}
// The standard fixture initially has futures only. Book both product families
// through their editor checks before exercising mixed-portfolio risk and the Dashboard's Positions.
async function administrationAndPolicies(browser){await operatingSettings(browser);await administration(browser);}
const standardSuites=[['market navigation',marketNavigation],['profile dialog',profileDialog],['administration and operating settings',administrationAndPolicies],['power TOUs',power],['party editors',parties],['futures',tradingNavigation],['trade editor',tradeEditor],['financial swaps',swaps],['risk and dashboard',riskAndDashboard],['table interactions and the Blotter workbench',tablesAndBlotter],['units and precision',units],['saved-change notices',notices]];
if(process.argv.length!==4&&!(process.argv.length===5&&['--assistant-workspace','--facility-quantities','--monte-carlo-risk','--historical-risk','--parametric-risk','--factor-history','--blotter-loading','--unified-trading','--physical-lng','--physical-crude','--crude-setup','--gas-references','--physical-gas','--deal-ticket','--markets-desk','--demo-instruments','--factor-swaps','--scheduled-swaps','--side-observations','--swaps','--ppa','--assistant','--pilot-close','--contract-dates','--units','--parties-power','--exchange','--futures-options','--risk','--risk-workspace','--jobs','--live-calculation','--custom-curves','--market-dates','--reference-editor','--interval-series','--interval-curves','--valuation-status','--close-out'].includes(process.argv[4])))throw Error('Invoke through tools/run_preview.py --browser-check --browser /path/to/browser');
let total=0;
for(const [name,run] of (process.argv[4]==='--assistant-workspace'?[['assistant workspace',assistantWorkspace]]:process.argv[4]==='--facility-quantities'?[['facility quantity ledgers',facilityQuantities]]:process.argv[4]==='--monte-carlo-risk'?[['Monte Carlo risk',monteCarloRisk]]:process.argv[4]==='--historical-risk'?[['historical risk',historicalRisk]]:process.argv[4]==='--parametric-risk'?[['parametric risk',parametricRisk]]:process.argv[4]==='--factor-history'?[['aligned factor history',factorHistory]]:process.argv[4]==='--blotter-loading'?[['blotter loading',tradingNavigation]]:process.argv[4]==='--unified-trading'?[['unified trading',unifiedTrading]]:process.argv[4]==='--markets-desk'?[['markets desk',marketsDesk],['delivery desk',deliveryDesk],['lazy UI reads',lazyReads]]:process.argv[4]==='--crude-setup'?[['crude delivery setup',crudeSetup]]:process.argv[4]==='--physical-lng'?[['physical LNG',physicalLng]]:process.argv[4]==='--physical-crude'?[['physical crude',physicalCrude]]:process.argv[4]==='--gas-references'?[['gas references without price frames',gasReferences]]:process.argv[4]==='--physical-gas'?[['physical gas',physicalGas]]:process.argv[4]==='--deal-ticket'?[['deal ticket',dealTicket]]:process.argv[4]==='--demo-instruments'?[['mixed demo instruments',demoInstruments],['separate trade windows',positionPopout],['Blotter close-out refusals',blotterCloseOutRefusals]]:process.argv[4]==='--close-out'?[['Blotter close-out',blotterCloseOut]]:process.argv[4]==='--factor-swaps'?[['typed factor swaps',factorSwaps]]:process.argv[4]==='--scheduled-swaps'?[['qualified scheduled authoring',scheduledAuthoring],['scheduled financial swaps',scheduledSwaps],['financial swap settlement',swapSettlement],['intra-trade net settlement',swapNetting]]:process.argv[4]==='--side-observations'?[['side observation schedules',swapObservations],['financial swaps',swaps]]:process.argv[4]==='--swaps'?[['financial swaps',swaps]]:process.argv[4]==='--ppa'?[['PPA booking and inspection',ppaEntry]]:process.argv[4]==='--live-calculation'?[['live calculation',liveCalculation]]:process.argv[4]==='--assistant'?[['page-assistant settings',aiSettings],['agents',agentSettings],['usage',usageSettings],['assistant panel',assistantPanel],['pop-out Blotter',blotterPopout]]:process.argv[4]==='--interval-series'?[['temporal browsing',temporalBrowsing],['interval series',intervalSeries],['market context',marketContext]]:process.argv[4]==='--interval-curves'?[['interval curves',intervalCurves]]:process.argv[4]==='--valuation-status'?[['valuation status',valuationStatus]]:process.argv[4]==='--pilot-close'?[['generated pilot closes',pilotClose]]:process.argv[4]==='--contract-dates'?[['shared contract dates',contractDates],['curve definitions',curveDefinitions]]:process.argv[4]==='--reference-editor'?[['reference editor',referenceEditor]]:process.argv[4]==='--market-dates'?[['market close calendar',marketDates]]:process.argv[4]==='--custom-curves'?[['curve composition',curveComposition],['custom curves',customCurves],['curve activation',curveActivation]]:process.argv[4]==='--jobs'?[['jobs',jobs]]:process.argv[4]==='--risk-workspace'?[['Risk results workspace',riskWorkspace]]:process.argv[4]==='--risk'?[['risk',risk],['Risk results workspace',riskWorkspace],['risk at the closes of one date',riskCloses]]:process.argv[4]==='--exchange'?[['exchange futures',exchange]]:process.argv[4]==='--futures-options'?[['futures options',futuresOptions],['vol surfaces',volSurfaces]]:process.argv[4]==='--units'?[['units and precision',units]]:process.argv[4]==='--parties-power'?[['power TOUs',power],['party editors',parties]]:standardSuites)){
  const passed=await withOwnedBrowser(process.argv[2],process.argv[3],run);total+=passed;console.log(`${passed} ${name} browser checks passed.`);
}
if(process.argv.length===4)total+=await checkProfileLifetime(process.argv[2],process.argv[3]);
console.log(`${total} owned browser checks passed.`);
