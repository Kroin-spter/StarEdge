var VertexLitBaselightbase : Color = Color(.3, 0.32, 0.35,1);
private var VertexLitBaselight = VertexLitBaselightbase;
var VertexLitTranslucencyColor : Color = Color(0.73,0.85,0.4,1);
var VertexLitWaveScale = 2;
var VertexLitDetailDistance = 60;

function Start ()
{
	Shader.SetGlobalColor("_VertexLitTranslucencyColor", VertexLitTranslucencyColor);
	Shader.SetGlobalColor("_VertexLitBaselight", VertexLitBaselight);
	
	Shader.SetGlobalFloat("_VertexLitWaveScale", VertexLitWaveScale);
    
    
    // 设置第 8 层在指定细节距离之外剔除。
    var distances = new float[32];
    distances[8] = VertexLitDetailDistance; // 用于地形细节等小型物体。
    GetComponent.<Camera>().main.layerCullDistances = distances;
}


function Update () {
	
	// 历史版本的延迟渲染路径判断。
		var mainlight = GameObject.Find("01 Sun");
		VertexLitBaselight = VertexLitBaselightbase * mainlight.GetComponent.<Light>().intensity;
		Shader.SetGlobalColor("_VertexLitBaselight", VertexLitBaselight);
	//}
	
	// 前向渲染路径。
	if(Input.GetKeyDown("1")){
        Camera.main.renderingPath = RenderingPath.Forward;
	}
	// 延迟渲染路径。
	if(Input.GetKeyDown("2")){
        Camera.main.renderingPath = RenderingPath.DeferredLighting;
	}
	
	
	
	
	
	
	
	
}
