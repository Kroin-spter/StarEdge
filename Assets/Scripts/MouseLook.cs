using UnityEngine;
using System.Collections;

/// 根据鼠标位移旋转对象。
/// 可使用最小值和最大值限制旋转范围。

/// 创建第一人称角色的步骤：
/// 创建胶囊体。
/// 为胶囊体添加 MouseLook 脚本。
/// 设置为 LookX，仅控制水平转向，不倾斜角色。
/// 为胶囊体添加 FPSInputController 脚本。
/// 将自动添加 CharacterMotor 和 CharacterController 组件。

/// 创建相机，设为胶囊体子对象，并重置其局部变换。
/// 为相机添加 MouseLook 脚本。
/// 设置为 LookY，控制类似头部的上下俯仰，水平转向由角色负责。
[AddComponentMenu("Camera-Control/Mouse Look")]
public class MouseLook : MonoBehaviour {

	public enum RotationAxes { MouseXAndY = 0, MouseX = 1, MouseY = 2 }
	public RotationAxes axes = RotationAxes.MouseXAndY;
	public float sensitivityX = 15F;
	public float sensitivityY = 15F;

	public float minimumX = -360F;
	public float maximumX = 360F;

	public float minimumY = -60F;
	public float maximumY = 60F;

	float rotationY = 0F;

	void Update ()
	{
		if (axes == RotationAxes.MouseXAndY)
		{
			float rotationX = transform.localEulerAngles.y + Input.GetAxis("Mouse X") * sensitivityX;
			
			rotationY += Input.GetAxis("Mouse Y") * sensitivityY;
			rotationY = Mathf.Clamp (rotationY, minimumY, maximumY);
			
			transform.localEulerAngles = new Vector3(-rotationY, rotationX, 0);
		}
		else if (axes == RotationAxes.MouseX)
		{
			transform.Rotate(0, Input.GetAxis("Mouse X") * sensitivityX, 0);
		}
		else
		{
			rotationY += Input.GetAxis("Mouse Y") * sensitivityY;
			rotationY = Mathf.Clamp (rotationY, minimumY, maximumY);
			
			transform.localEulerAngles = new Vector3(-rotationY, transform.localEulerAngles.y, 0);
		}
	}
	
	void Start ()
	{
		// 禁止刚体自行改变旋转。
		if (GetComponent<Rigidbody>())
			GetComponent<Rigidbody>().freezeRotation = true;
	}
}
