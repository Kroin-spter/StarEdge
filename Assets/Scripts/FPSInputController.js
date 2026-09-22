private var motor : CharacterMotor;

// 初始化组件状态。
function Awake () {
	motor = GetComponent(CharacterMotor);
}

// 每帧更新。
function Update () {
	// 读取键盘或模拟摇杆的方向输入。
	var directionVector = new Vector3(Input.GetAxis("Horizontal"), 0, Input.GetAxis("Vertical"));
	
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
	
	// 将方向传入 CharacterMotor。
	motor.inputMoveDirection = transform.rotation * directionVector;
	motor.inputJump = Input.GetButton("Jump");
}

// 要求同一对象挂载角色控制器。
@script RequireComponent (CharacterMotor)
@script AddComponentMenu ("Character/FPS Input Controller")
