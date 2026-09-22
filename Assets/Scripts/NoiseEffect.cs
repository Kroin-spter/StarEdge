using UnityEngine;

[ExecuteInEditMode]
[RequireComponent (typeof(Camera))]
[AddComponentMenu("Image Effects/Noise")]
public class NoiseEffect : MonoBehaviour
{
	/// 单色噪声仅增加颗粒；非单色噪声
	/// 在 YUV 色彩空间加入噪声，更接近录像机效果，
	/// 并会引入洋红色和绿色。
	public bool monochrome = true;
	private bool rgbFallback = false;

	// 噪声颗粒强度在最小值与最大值之间随机取值。
	public float grainIntensityMin = 0.1f;
	public float grainIntensityMax = 0.2f;
	
	/// 噪声颗粒尺寸，1 表示一个像素。
	public float grainSize = 2.0f;

	// 划痕强度在最小值与最大值之间随机取值。
	public float scratchIntensityMin = 0.05f;
	public float scratchIntensityMax = 0.25f;
	
	/// 划痕每秒更换位置的次数。
	public float scratchFPS = 10.0f;
	/// 划痕停留在同一区域时会轻微抖动。
	public float scratchJitter = 0.01f;

	public Texture grainTexture;
	public Texture scratchTexture;
	public Shader   shaderRGB;
	public Shader   shaderYUV;
	private Material m_MaterialRGB;
	private Material m_MaterialYUV;
	
	private float scratchTimeLeft = 0.0f;
	private float scratchX, scratchY;
	
	protected void Start ()
	{
		// 不支持图像效果时禁用组件。
		if (!SystemInfo.supportsImageEffects) {
			enabled = false;
			return;
		}
		
		if( shaderRGB == null || shaderYUV == null )
		{
			Debug.Log( "Noise shaders are not set up! Disabling noise effect." );
			enabled = false;
		}
		else
		{
			if( !shaderRGB.isSupported ) // 不支持 RGB 着色器时禁用效果。
				enabled = false;
			else if( !shaderYUV.isSupported ) // 不支持 YUV 着色器时回退到 RGB。
				rgbFallback = true;
		}
	}
	
	protected Material material {
		get {
			if( m_MaterialRGB == null ) {
				m_MaterialRGB = new Material( shaderRGB );
				m_MaterialRGB.hideFlags = HideFlags.HideAndDontSave;
			}
			if( m_MaterialYUV == null && !rgbFallback ) {
				m_MaterialYUV = new Material( shaderYUV );
				m_MaterialYUV.hideFlags = HideFlags.HideAndDontSave;
			}
			return (!rgbFallback && !monochrome) ? m_MaterialYUV : m_MaterialRGB;
		}
	}
	
	protected void OnDisable() {
		if( m_MaterialRGB )
			DestroyImmediate( m_MaterialRGB );
		if( m_MaterialYUV )
			DestroyImmediate( m_MaterialYUV );
	}
	
	private void SanitizeParameters()
	{
		grainIntensityMin = Mathf.Clamp( grainIntensityMin, 0.0f, 5.0f );
		grainIntensityMax = Mathf.Clamp( grainIntensityMax, 0.0f, 5.0f );
		scratchIntensityMin = Mathf.Clamp( scratchIntensityMin, 0.0f, 5.0f );
		scratchIntensityMax = Mathf.Clamp( scratchIntensityMax, 0.0f, 5.0f );
		scratchFPS = Mathf.Clamp( scratchFPS, 1, 30 );
		scratchJitter = Mathf.Clamp( scratchJitter, 0.0f, 1.0f );
		grainSize = Mathf.Clamp( grainSize, 0.1f, 50.0f );
	}

	// 由相机调用以应用图像效果。
	void OnRenderImage (RenderTexture source, RenderTexture destination)
	{
		SanitizeParameters();
		
		if( scratchTimeLeft <= 0.0f )
		{
			scratchTimeLeft = Random.value * 2 / scratchFPS; // 参数已提前校验，除数不会为零。
			scratchX = Random.value;
			scratchY = Random.value;
		}
		scratchTimeLeft -= Time.deltaTime;

		Material mat = material;

		mat.SetTexture("_GrainTex", grainTexture);
		mat.SetTexture("_ScratchTex", scratchTexture);
		float grainScale = 1.0f / grainSize; // 参数已提前校验，除数不会为零。
		mat.SetVector("_GrainOffsetScale", new Vector4(
			Random.value,
			Random.value,
			(float)Screen.width / (float)grainTexture.width * grainScale,
			(float)Screen.height / (float)grainTexture.height * grainScale
		));
		mat.SetVector("_ScratchOffsetScale", new Vector4(
			scratchX + Random.value*scratchJitter,
			scratchY + Random.value*scratchJitter,
			(float)Screen.width / (float) scratchTexture.width,
			(float)Screen.height / (float) scratchTexture.height
		));
		mat.SetVector("_Intensity", new Vector4(
			Random.Range(grainIntensityMin, grainIntensityMax),
			Random.Range(scratchIntensityMin, scratchIntensityMax),
			0, 0 ));
		Graphics.Blit (source, destination, mat);
	}
}
