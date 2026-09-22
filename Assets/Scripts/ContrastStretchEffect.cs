using UnityEngine;
using System.Collections;

[ExecuteInEditMode]
[AddComponentMenu("Image Effects/Contrast Stretch")]
public class ContrastStretchEffect : MonoBehaviour
{
	/// 适应速度，以 30 FPS 下每帧的变化比例表示。
	/// 默认值为 0.02，即每 1/30 秒变化 2%。
	public float adaptationSpeed = 0.02f;
	
	/// 场景过暗或过亮时，可能不希望
	/// 将对比度拉伸到整个范围。
	/// 最小限制为 0、最大限制为 1 时不进行拉伸。
	/// 最小限制为 1、最大限制为 0 时始终拉伸到完整色彩范围。
	
	/// 最低亮度的上限，范围为 0 到 1。
	public float limitMinimum = 0.2f;
	
	/// 最高亮度的下限，范围为 0 到 1。
	public float limitMaximum = 0.6f;
	
	
	// 使用两张 1×1 渲染纹理保存跨帧适应状态，
	// 并交替作为读写目标。
	private RenderTexture[] adaptRenderTex = new RenderTexture[2];
	private int curAdaptIndex = 0;
	
	
	// 计算场景的灰度亮度图。
	public Shader   shaderLum;
	private Material m_materialLum;
	protected Material materialLum {
		get {
			if( m_materialLum == null ) {
				m_materialLum = new Material(shaderLum);
				m_materialLum.hideFlags = HideFlags.HideAndDontSave;
			}
			return m_materialLum;
		}
	}
	
	// 每次缩小为原图的 1/2×1/2，同时计算最大和最小值。
	// 重复执行该着色器，将原始亮度图
	// 缩小为包含最大和最小亮度的 1×1 图像。
	public Shader   shaderReduce;
	private Material m_materialReduce;
	protected Material materialReduce {
		get {
			if( m_materialReduce == null ) {
				m_materialReduce = new Material(shaderReduce);
				m_materialReduce.hideFlags = HideFlags.HideAndDontSave;
			}
			return m_materialReduce;
		}
	}
	
	// 适应着色器逐步调整最大和最小亮度，
	// 输入为历史适应图与当前场景的 1×1 亮度图。
	public Shader   shaderAdapt;
	private Material m_materialAdapt;
	protected Material materialAdapt {
		get {
			if( m_materialAdapt == null ) {
				m_materialAdapt = new Material(shaderAdapt);
				m_materialAdapt.hideFlags = HideFlags.HideAndDontSave;
			}
			return m_materialAdapt;
		}
	}
	
	// 最终阶段根据当前适应后的亮度范围，
	// 拉伸原始场景的颜色。
	public Shader   shaderApply;
	private Material m_materialApply;
	protected Material materialApply {
		get {
			if( m_materialApply == null ) {
				m_materialApply = new Material(shaderApply);
				m_materialApply.hideFlags = HideFlags.HideAndDontSave;
			}
			return m_materialApply;
		}
	}
	
	void Start()
	{
		// 不支持图像效果时禁用组件。
		if (!SystemInfo.supportsImageEffects) {
			enabled = false;
			return;
		}
		
		if (!shaderAdapt.isSupported || !shaderApply.isSupported || !shaderLum.isSupported || !shaderReduce.isSupported) {
			enabled = false;
			return;
		}
	}
	
	void OnEnable()
	{		
		for( int i = 0; i < 2; ++i )
		{
			if( !adaptRenderTex[i] ) {
				adaptRenderTex[i] = new RenderTexture( 1, 1, 32 );
				adaptRenderTex[i].hideFlags = HideFlags.HideAndDontSave;
			}
		}
	}
	
	void OnDisable()
	{
		for( int i = 0; i < 2; ++i )
		{
			DestroyImmediate( adaptRenderTex[i] );
			adaptRenderTex[i] = null;
		}
		if( m_materialLum )
			DestroyImmediate( m_materialLum );
		if( m_materialReduce )
			DestroyImmediate( m_materialReduce );
		if( m_materialAdapt )
			DestroyImmediate( m_materialAdapt );
		if( m_materialApply )
			DestroyImmediate( m_materialApply );
	}
	

	/// 应用滤镜。
	void OnRenderImage (RenderTexture source, RenderTexture destination)
	{
		// 缩小渲染纹理并同时转换为亮度。
		const int TEMP_RATIO = 1; // 降采样比例由常量控制。
		RenderTexture rtTempSrc = RenderTexture.GetTemporary(source.width/TEMP_RATIO, source.height/TEMP_RATIO);
		Graphics.Blit (source, rtTempSrc, materialLum);
		
		// 重复缩小图像并计算最大和最小亮度，
		// 直到得到包含这两个值的 1×1 图像。
		const int FINAL_SIZE = 1;
		// 历史版本的最终尺寸常量。
		while( rtTempSrc.width > FINAL_SIZE || rtTempSrc.height > FINAL_SIZE )
		{
			const int REDUCE_RATIO = 2; // 着色器每次执行 2×2 缩减。
			int destW = rtTempSrc.width / REDUCE_RATIO;
			if( destW < FINAL_SIZE ) destW = FINAL_SIZE;
			int destH = rtTempSrc.height / REDUCE_RATIO;
			if( destH < FINAL_SIZE ) destH = FINAL_SIZE;
			RenderTexture rtTempDst = RenderTexture.GetTemporary(destW,destH);
			Graphics.Blit (rtTempSrc, rtTempDst, materialReduce);
			
			// 释放旧的临时源纹理，使用新纹理作为源。
			RenderTexture.ReleaseTemporary( rtTempSrc );
			rtTempSrc = rtTempDst;
		}
		
		// 更新观察者的亮度适应状态。
		CalculateAdaptation( rtTempSrc );
		
		// 使用当前适应参数拉伸原始场景对比度。
		materialApply.SetTexture("_AdaptTex", adaptRenderTex[curAdaptIndex] );
		Graphics.Blit (source, destination, materialApply);
		
		RenderTexture.ReleaseTemporary( rtTempSrc );
	}
	
	
	/// 逐渐适应最大和最小亮度的辅助函数。
	private void CalculateAdaptation( Texture curTexture )
	{
		int prevAdaptIndex = curAdaptIndex;
		curAdaptIndex = (curAdaptIndex+1) % 2;
		
		// 适应速度按 30 FPS 下的每帧比例表示。
		// 根据当前帧率计算插值比例。
		float adaptLerp = 1.0f - Mathf.Pow( 1.0f - adaptationSpeed, 30.0f * Time.deltaTime );
		const float kMinAdaptLerp = 0.01f;
		adaptLerp = Mathf.Clamp( adaptLerp, kMinAdaptLerp, 1 );
		
		materialAdapt.SetTexture("_CurTex", curTexture );
		materialAdapt.SetVector("_AdaptParams", new Vector4(
			adaptLerp,
			limitMinimum,
			limitMaximum,
			0.0f
		));
		Graphics.Blit (
			adaptRenderTex[prevAdaptIndex],
			adaptRenderTex[curAdaptIndex],
			materialAdapt);
	}
}
