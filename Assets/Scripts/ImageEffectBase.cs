using UnityEngine;

[RequireComponent (typeof(Camera))]
[AddComponentMenu("")]
public class ImageEffectBase : MonoBehaviour {
	/// 提供在检视面板设置的着色器引用，
	/// 并根据着色器创建材质实例。
	public Shader   shader;
	private Material m_Material;

	protected virtual void Start ()
	{
		// 不支持图像效果时禁用组件。
		if (!SystemInfo.supportsImageEffects) {
			enabled = false;
			return;
		}
		
		// 当显卡无法运行该着色器时，
		// 禁用图像效果。
		if (!shader || !shader.isSupported)
			enabled = false;
	}

	protected Material material {
		get {
			if (m_Material == null) {
				m_Material = new Material (shader);
				m_Material.hideFlags = HideFlags.HideAndDontSave;
			}
			return m_Material;
		} 
	}
	
	protected virtual void OnDisable() {
		if( m_Material ) {
			DestroyImmediate( m_Material );
		}
	}
}
