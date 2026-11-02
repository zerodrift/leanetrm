// Add each supported native side through the visible ticket controls.
export async function chooseSwapSides({click,wait,idle},type='financial_fixed_floating'){
 for(const kind of type==='financial_fixed_floating'?['fixed','floating']:['floating','floating']){
  await click(`[data-add-side="${kind}"]`);if(idle)await wait(idle);
 }
}
