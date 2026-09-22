// 默认让角色朝向当前移动方向。
var autoRotate : boolean = true;
var maxRotationSpeed : float = 360;

private var motor : CharacterMotor;

// 初始化组件状态。
function Awake () {
	motor = GetComponent(CharacterMotor);
}

// 每帧更新。
function Update () {
	// 读取键盘或模拟摇杆的方向输入。
	var directionVector = new Vector3(Input.GetAxis("Horizontal"), Input.GetAxis("Vertical"), 0);
	
	if (directionVector != Vector3.zero) {
		// 先计算方向向量长度，再归一化。
		// 已知长度时直接相除，避免重复求长度。
		var directionLength = directionVector.magnitude;
		directionVector = directionVector / directionLength;
		
		// 将输入长度限制为不超过 1。
		directionLength = Mathf.Min(1, directionLength);
		
		// 降低中间区域灵敏度，增强靠近极值的响应。
		// 便于使用模拟摇杆控制低速移动。
		directionLength = directionLength * directionLength;
		
		// 使用调整后的长度缩放归一化方向。
		directionVector = directionVector * directionLength;
	}
	
	// 将输入方向转换到相机坐标系，使上下左右与相机一致。
	directionVector = Camera.main.transform.rotation * directionVector;
	
	// 将输入投影到垂直于角色上方向的平面。
	var camToCharacterSpace = Quaternion.FromToRotation(-Camera.main.transform.forward, transform.up);
	directionVector = (camToCharacterSpace * directionVector);
	
	// 将方向传入 CharacterMotor。
	motor.inputMoveDirection = directionVector;
	motor.inputJump = Input.GetButton("Jump");
	
	// 使朝向跟随移动方向。
	if (autoRotate && directionVector.sqrMagnitude > 0.01) {
		var newForward : Vector3 = ConstantSlerp(
			transform.forward,
			directionVector,
			maxRotationSpeed * Time.deltaTime
		);
		newForward = ProjectOntoPlane(newForward, transform.up);
		transform.rotation = Quaternion.LookRotation(newForward, transform.up);
	}
}

function ProjectOntoPlane (v : Vector3, normal : Vector3) {
	return v - Vector3.Project(v, normal);
}

function ConstantSlerp (from : Vector3, to : Vector3, angle : float) {
	var value : float = Mathf.Min(1, angle / Vector3.Angle(from, to));
	return Vector3.Slerp(from, to, value);
}

// 要求同一对象挂载角色控制器。
@script RequireComponent (CharacterMotor)
@script AddComponentMenu ("Character/Platform Input Controller")
