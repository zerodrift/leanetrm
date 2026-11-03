// A scrolling conversation keeps showing its newest content while the person is at its end, and stays where they
// scrolled to read otherwise. `always` brings the end into view, as after the person asks.
const slack=24;
export function follow(box:HTMLElement,change:()=>void,always=false){
 const atEnd=always||box.scrollHeight-box.scrollTop-box.clientHeight<=slack;
 change();
 if(atEnd)box.scrollTop=box.scrollHeight;
}
