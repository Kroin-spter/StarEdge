using UnityEngine;
using System.Collections;

public class SwarmCenter : MonoBehaviour
{
	public float speed;
	
	// 初始化组件状态。
	void Start ()
	{
		transform.Rotate(new Vector3(0, Random.Range(0.0f, 360.0f), 0));
	}
	
	// 每帧更新。
	void Update ()
	{
		transform.Rotate(new Vector3(0, speed * Time.deltaTime, 0));
	}
}
