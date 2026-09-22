var queue : int = 3000; // Unity 中透明物体的默认渲染队列为 3000。
var applyToChildren : boolean = false;

function Awake(){
    SetRenderQueue();
}
   
function SetRenderQueue(){
    if (!GetComponent.<Renderer>() || !GetComponent.<Renderer>().sharedMaterial || applyToChildren){
        if(applyToChildren){
            for(var child : Transform in transform){
                child.GetComponent.<Renderer>().sharedMaterial.renderQueue = queue;
            }
        } else { 
            print("No renderer found on this GameObject. Check the applyToChildren box to apply settings to children"); 
        }    
    } else {GetComponent.<Renderer>().sharedMaterial.renderQueue = queue;}
}
