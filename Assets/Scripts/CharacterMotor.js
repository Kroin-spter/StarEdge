#pragma strict
#pragma implicit
#pragma downcast

// 是否响应控制输入。
var canControl : boolean = true;

var useFixedUpdate : boolean = true;

// 后续变量使用 System.NonSerialized，不序列化，也不在检视面板显示。
// 将运行状态与配置分开。

// 期望角色移动的世界方向。
@System.NonSerialized
var inputMoveDirection : Vector3 = Vector3.zero;

// 是否持续按下跳跃；通过该接口接收输入，
// 使人工控制和自动控制能够共用角色运动逻辑。
@System.NonSerialized
var inputJump : boolean = false;

class CharacterMotorMovement {
	// 移动时的最大水平速度。
	var maxForwardSpeed : float = 10.0;
	var maxSidewaysSpeed : float = 10.0;
	var maxBackwardsSpeed : float = 10.0;
	
	// 根据坡度调整速度的曲线，负值表示下坡。
	var slopeSpeedMultiplier : AnimationCurve = AnimationCurve(Keyframe(-90, 1), Keyframe(0, 1), Keyframe(90, 0));
	
	// 改变速度的加速度，数值越大响应越快。
	var maxGroundAcceleration : float = 30.0;
	var maxAirAcceleration : float = 20.0;

	// 角色所受重力。
	var gravity : float = 10.0;
	var maxFallSpeed : float = 20.0;
	
	// 后续变量不序列化，也不在检视面板显示。
	// 将运行状态与配置分开。

	// 记录 controller.Move 最近返回的碰撞标记。
	@System.NonSerialized
	var collisionFlags : CollisionFlags; 

	// 记录角色的当前速度。
	@System.NonSerialized
	var velocity : Vector3;
	
	// 记录角色离地时的速度。
	@System.NonSerialized
	var frameVelocity : Vector3 = Vector3.zero;
	
	@System.NonSerialized
	var hitPoint : Vector3 = Vector3.zero;
	
	@System.NonSerialized
	var lastHitPoint : Vector3 = Vector3(Mathf.Infinity, 0, 0);
}

var movement : CharacterMotorMovement = CharacterMotorMovement();

enum MovementTransferOnJump {
	None, // 跳跃不继承地面速度。
	InitTransfer, // 起跳时继承地面速度，之后逐渐衰减。
	PermaTransfer, // 起跳时继承地面速度，并保持到落地。
	PermaLocked // 跳跃相对于最后接触的平台运动，始终跟随该平台。
}

// 将跳跃相关参数集中到辅助类中。
class CharacterMotorJumping {
	// 是否允许跳跃。
	var enabled : boolean = true;

	// 短按跳跃时的跳跃高度。
	var baseHeight : float = 1.0;
	
	// 长按跳跃时额外增加的高度，单位为米。
	var extraHeight : float = 4.1;
	
	// 在可行走坡面上，起跳方向偏向表面法线的程度。
	// 0 表示完全竖直，1 表示完全沿表面法线。
	var perpAmount : float = 0.0;
	
	// 在过陡坡面上，起跳方向偏向表面法线的程度。
	// 0 表示完全竖直，1 表示完全沿表面法线。
	var steepPerpAmount : float = 0.5;
	
	// 后续变量不序列化，也不在检视面板显示。
	// 将运行状态与配置分开。

	// 是否处于按键触发后尚未落地的跳跃状态。
	// 判断起跳或下落造成的离地状态时，应读取 grounded。
	@System.NonSerialized
	var jumping : boolean = false;
	
	@System.NonSerialized
	var holdingJumpButton : boolean = false;

	// 起跳时刻，用于计算长按跳跃的额外作用时间。
	@System.NonSerialized
	var lastStartTime : float = 0.0;
	
	@System.NonSerialized
	var lastButtonDownTime : float = -100;
	
	@System.NonSerialized
	var jumpDir : Vector3 = Vector3.up;
}

var jumping : CharacterMotorJumping = CharacterMotorJumping();

class CharacterMotorMovingPlatform {
	var enabled : boolean = true;
	
	var movementTransfer : MovementTransferOnJump = MovementTransferOnJump.PermaTransfer;
	
	@System.NonSerialized
	var hitPlatform : Transform;
	
	@System.NonSerialized
	var activePlatform : Transform;
	
	@System.NonSerialized
	var activeLocalPoint : Vector3;
	
	@System.NonSerialized
	var activeGlobalPoint : Vector3;
	
	@System.NonSerialized
	var activeLocalRotation : Quaternion;
	
	@System.NonSerialized
	var activeGlobalRotation : Quaternion;
	
	@System.NonSerialized
	var lastMatrix : Matrix4x4;
	
	@System.NonSerialized
	var platformVelocity : Vector3;
	
	@System.NonSerialized
	var newPlatform : boolean;
}

var movingPlatform : CharacterMotorMovingPlatform = CharacterMotorMovingPlatform();

class CharacterMotorSliding {
	// 是否会沿过陡坡面滑动。
	var enabled : boolean = true;
	
	// 沿陡坡滑动的速度。
	var slidingSpeed : float = 15;
	
	// 玩家对侧向滑动方向的控制比例。
	// 0.5 表示侧向速度最多为下坡滑动速度的一半。
	var sidewaysControl : float = 1.0;
	
	// 玩家对滑动速度的控制比例。
	// 0.5 表示可将滑动速度调整到基础速度的 50% 至 150%。
	var speedControl : float = 0.4;
}

var sliding : CharacterMotorSliding = CharacterMotorSliding();

@System.NonSerialized
var grounded : boolean = true;

@System.NonSerialized
var groundNormal : Vector3 = Vector3.zero;

private var lastGroundNormal : Vector3 = Vector3.zero;

private var tr : Transform;

private var controller : CharacterController;

function Awake () {
	controller = GetComponent (CharacterController);
	tr = transform;
}

private function UpdateFunction () {
	// 复制当前速度，以便计算本次速度变化。
	var velocity : Vector3 = movement.velocity;
	
	// 根据输入更新速度。
	velocity = ApplyInputVelocityChange(velocity);
	
	// 施加重力与跳跃作用。
	velocity = ApplyGravityAndJumping (velocity);
	
	// 处理移动平台。
	var moveDistance : Vector3 = Vector3.zero;
	if (MoveWithPlatform()) {
		var newGlobalPoint : Vector3 = movingPlatform.activePlatform.TransformPoint(movingPlatform.activeLocalPoint);
		moveDistance = (newGlobalPoint - movingPlatform.activeGlobalPoint);
		if (moveDistance != Vector3.zero)
			controller.Move(moveDistance);
		
		// 同时处理移动平台的旋转。
        var newGlobalRotation : Quaternion = movingPlatform.activePlatform.rotation * movingPlatform.activeLocalRotation;
        var rotationDiff : Quaternion = newGlobalRotation * Quaternion.Inverse(movingPlatform.activeGlobalRotation);
        
        var yRotation = rotationDiff.eulerAngles.y;
        if (yRotation != 0) {
	        // 避免改变角色的局部上方向。
	        tr.Rotate(0, yRotation, 0);
        }
	}
	
	// 保存上一位置，用于计算实际速度。
	var lastPosition : Vector3 = tr.position;
	
	// 乘以帧时长，使运动量不依赖帧率。
	var currentMovementOffset : Vector3 = velocity * Time.deltaTime;
	
	// 计算额外向下位移，避免角色下台阶或经过坡度突变处时
	// 意外失去接地状态。
	var pushDownOffset : float = Mathf.Max(controller.stepOffset, Vector3(currentMovementOffset.x, 0, currentMovementOffset.z).magnitude);
	if (grounded)
		currentMovementOffset -= pushDownOffset * Vector3.up;
	
	// 重置将由碰撞回调更新的变量。
	movingPlatform.hitPlatform = null;
	groundNormal = Vector3.zero;
	
   	// 执行角色移动。
	movement.collisionFlags = controller.Move (currentMovementOffset);
	
	movement.lastHitPoint = movement.hitPoint;
	lastGroundNormal = groundNormal;
	
	if (movingPlatform.enabled && movingPlatform.activePlatform != movingPlatform.hitPlatform) {
		if (movingPlatform.hitPlatform != null) {
			movingPlatform.activePlatform = movingPlatform.hitPlatform;
			movingPlatform.lastMatrix = movingPlatform.hitPlatform.localToWorldMatrix;
			movingPlatform.newPlatform = true;
		}
	}
	
	// 根据移动前后位置计算速度，
	// 使记录的速度反映碰撞约束后的实际位移。
	var oldHVelocity : Vector3 = new Vector3(velocity.x, 0, velocity.z);
	movement.velocity = (tr.position - lastPosition) / Time.deltaTime;
	var newHVelocity : Vector3 = new Vector3(movement.velocity.x, 0, movement.velocity.z);
	
	// CharacterController 碰撞可能引入额外偏移，
	// 这里避免这些偏移污染记录的速度。
	if (oldHVelocity == Vector3.zero) {
		movement.velocity = new Vector3(0, movement.velocity.y, 0);
	}
	else {
		var projectedNewVelocity : float = Vector3.Dot(newHVelocity, oldHVelocity) / oldHVelocity.sqrMagnitude;
		movement.velocity = oldHVelocity * Mathf.Clamp01(projectedNewVelocity) + movement.velocity.y * Vector3.up;
	}
	
	if (movement.velocity.y < velocity.y - 0.001) {
		if (movement.velocity.y < 0) {
			// 外部作用导致角色下落快于预期，
			// 忽略这部分额外下落速度。
			movement.velocity.y = velocity.y;
		}
		else {
			// 角色向上的运动受到阻挡，
			// 按撞到天花板处理并停止继续跳跃。
			jumping.holdingJumpButton = false;
		}
	}
	
	// 角色刚刚失去接地状态。
	if (grounded && !IsGroundedTest()) {
		grounded = false;
		
		// 继承平台运动惯性。
		if (movingPlatform.enabled &&
			(movingPlatform.movementTransfer == MovementTransferOnJump.InitTransfer ||
			movingPlatform.movementTransfer == MovementTransferOnJump.PermaTransfer)
		) {
			movement.frameVelocity = movingPlatform.platformVelocity;
			movement.velocity += movingPlatform.platformVelocity;
		}
		
		SendMessage("OnFall", SendMessageOptions.DontRequireReceiver);
		// 此前为保持贴地而附加了向下位移，
		// 若下方没有地面，则撤销偏移，使下落平滑。
		tr.position += pushDownOffset * Vector3.up;
	}
	// 角色刚从空中落到地面。
	else if (!grounded && IsGroundedTest()) {
		grounded = true;
		jumping.jumping = false;
		SubtractNewPlatformVelocity();
		
		SendMessage("OnLand", SendMessageOptions.DontRequireReceiver);
	}
	
	// 处理移动平台。
	if (MoveWithPlatform()) {
		// 使用胶囊体下半球中心作为平台参考点，
		// 适用于站在移动或倾斜平台上的情况。
		movingPlatform.activeGlobalPoint = tr.position + Vector3.up * (controller.center.y - controller.height*0.5 + controller.radius);
		movingPlatform.activeLocalPoint = movingPlatform.activePlatform.InverseTransformPoint(movingPlatform.activeGlobalPoint);
		
		// 同时处理移动平台的旋转。
        movingPlatform.activeGlobalRotation = tr.rotation;
        movingPlatform.activeLocalRotation = Quaternion.Inverse(movingPlatform.activePlatform.rotation) * movingPlatform.activeGlobalRotation; 
	}
}

function FixedUpdate () {
	if (movingPlatform.enabled) {
		if (movingPlatform.activePlatform != null) {
			if (!movingPlatform.newPlatform) {
				var lastVelocity : Vector3 = movingPlatform.platformVelocity;
				
				movingPlatform.platformVelocity = (
					movingPlatform.activePlatform.localToWorldMatrix.MultiplyPoint3x4(movingPlatform.activeLocalPoint)
					- movingPlatform.lastMatrix.MultiplyPoint3x4(movingPlatform.activeLocalPoint)
				) / Time.deltaTime;
			}
			movingPlatform.lastMatrix = movingPlatform.activePlatform.localToWorldMatrix;
			movingPlatform.newPlatform = false;
		}
		else {
			movingPlatform.platformVelocity = Vector3.zero;	
		}
	}
	
	if (useFixedUpdate)
		UpdateFunction();
}

function Update () {
	if (!useFixedUpdate)
		UpdateFunction();
}

private function ApplyInputVelocityChange (velocity : Vector3) {	
	if (!canControl)
		inputMoveDirection = Vector3.zero;
	
	// 计算目标速度。
	var desiredVelocity : Vector3;
	if (grounded && TooSteep()) {
		// 当前滑动方向。
		desiredVelocity = Vector3(groundNormal.x, 0, groundNormal.z).normalized;
		// 计算输入方向在滑动方向上的投影。
		var projectedMoveDir = Vector3.Project(inputMoveDirection, desiredVelocity);
		// 组合下坡方向、速度控制与侧向控制。
		desiredVelocity = desiredVelocity + projectedMoveDir * sliding.speedControl + (inputMoveDirection - projectedMoveDir) * sliding.sidewaysControl;
		// 按滑动速度缩放。
		desiredVelocity *= sliding.slidingSpeed;
	}
	else
		desiredVelocity = GetDesiredHorizontalVelocity();
	
	if (movingPlatform.enabled && movingPlatform.movementTransfer == MovementTransferOnJump.PermaTransfer) {
		desiredVelocity += movement.frameVelocity;
		desiredVelocity.y = 0;
	}
	
	if (grounded)
		desiredVelocity = AdjustGroundVelocityToNormal(desiredVelocity, groundNormal);
	else
		velocity.y = 0;
	
	// 限制最大速度变化。
	var maxVelocityChange : float = GetMaxAcceleration(grounded) * Time.deltaTime;
	var velocityChangeVector : Vector3 = (desiredVelocity - velocity);
	if (velocityChangeVector.sqrMagnitude > maxVelocityChange * maxVelocityChange) {
		velocityChangeVector = velocityChangeVector.normalized * maxVelocityChange;
	}
	// 空中且不允许控制时不改变速度；
	// 接地且不允许控制时仍应用减速，模拟摩擦。
	if (grounded || canControl)
		velocity += velocityChangeVector;
	
	if (grounded) {
		// 上坡时 CharacterController 会自动产生所需的上升位移，
		// 不额外手动抬高，避免离地。
		// 下坡时增加向下位移，因为陡坡上仅靠重力不足以贴地。
		velocity.y = Mathf.Min(velocity.y, 0);
	}
	
	return velocity;
}

private function ApplyGravityAndJumping (velocity : Vector3) {
	
	if (!inputJump || !canControl) {
		jumping.holdingJumpButton = false;
		jumping.lastButtonDownTime = -100;
	}
	
	if (inputJump && jumping.lastButtonDownTime < 0 && canControl)
		jumping.lastButtonDownTime = Time.time;
	
	if (grounded)
		velocity.y = Mathf.Min(0, velocity.y) - movement.gravity * Time.deltaTime;
	else {
		velocity.y = movement.velocity.y - movement.gravity * Time.deltaTime;
		
		// 持续按住跳跃时，在一段时间内抵消重力，
		// 从而通过按键时长调节跳跃高度。
		if (jumping.jumping && jumping.holdingJumpButton) {
			// 计算额外跳跃作用的有效时间。
			// 仍处于有效时段时，继续施加额外作用。
			if (Time.time < jumping.lastStartTime + jumping.extraHeight / CalculateJumpVerticalSpeed(jumping.baseHeight)) {
				// 抵消刚施加的重力，并沿跳跃方向提供作用。
				velocity += jumping.jumpDir * movement.gravity * Time.deltaTime;
			}
		}
		
		// 限制最大下落速度，形成终端速度。
		velocity.y = Mathf.Max (velocity.y, -movement.maxFallSpeed);
	}
		
	if (grounded) {
		// 仅在过去 0.2 秒内按过跳跃时起跳。
		// 使用短暂输入缓冲，而不是只检查当前帧，
		// 以允许玩家在即将落地时提前按键。
		// 这样可以避免按键略早于落地时被忽略，
		// 改善连续跳跃的操作响应。
		if (jumping.enabled && canControl && (Time.time - jumping.lastButtonDownTime < 0.2)) {
			grounded = false;
			jumping.jumping = true;
			jumping.lastStartTime = Time.time;
			jumping.lastButtonDownTime = -100;
			jumping.holdingJumpButton = true;
			
			// 计算跳跃方向。
			if (TooSteep())
				jumping.jumpDir = Vector3.Slerp(Vector3.up, groundNormal, jumping.steepPerpAmount);
			else
				jumping.jumpDir = Vector3.Slerp(Vector3.up, groundNormal, jumping.perpAmount);
			
			// 先清除竖直速度，再施加跳跃速度。
			velocity.y = 0;
			velocity += jumping.jumpDir * CalculateJumpVerticalSpeed (jumping.baseHeight);
			
			// 继承平台运动惯性。
			if (movingPlatform.enabled &&
				(movingPlatform.movementTransfer == MovementTransferOnJump.InitTransfer ||
				movingPlatform.movementTransfer == MovementTransferOnJump.PermaTransfer)
			) {
				movement.frameVelocity = movingPlatform.platformVelocity;
				velocity += movingPlatform.platformVelocity;
			}
			
			SendMessage("OnJump", SendMessageOptions.DontRequireReceiver);
		}
		else {
			jumping.holdingJumpButton = false;
		}
	}
	
	return velocity;
}

function OnControllerColliderHit (hit : ControllerColliderHit) {
	if (hit.normal.y > 0 && hit.normal.y > groundNormal.y && hit.moveDirection.y < 0) {
		if ((hit.point - movement.lastHitPoint).sqrMagnitude > 0.001 || lastGroundNormal == Vector3.zero)
			groundNormal = hit.normal;
		else
			groundNormal = lastGroundNormal;
		
		movingPlatform.hitPlatform = hit.collider.transform;
		movement.hitPoint = hit.point;
		movement.frameVelocity = Vector3.zero;
	}
}

private function SubtractNewPlatformVelocity () {
	// 落地时从角色速度中减去新地面的速度，
	// 因为接地运动应相对于平台计算。
	if (movingPlatform.enabled &&
		(movingPlatform.movementTransfer == MovementTransferOnJump.InitTransfer ||
		movingPlatform.movementTransfer == MovementTransferOnJump.PermaTransfer)
	) {
		// 刚落到新平台时，需要等待两个物理帧，
		// 再读取平台在角色下方的速度。
		if (movingPlatform.newPlatform) {
			var platform : Transform = movingPlatform.activePlatform;
			yield WaitForFixedUpdate();
			yield WaitForFixedUpdate();
			if (grounded && platform == movingPlatform.activePlatform)
				yield 1;
		}
		movement.velocity -= movingPlatform.platformVelocity;
	}
}

private function MoveWithPlatform () : boolean {
	return (
		movingPlatform.enabled
		&& (grounded || movingPlatform.movementTransfer == MovementTransferOnJump.PermaLocked)
		&& movingPlatform.activePlatform != null
	);
}

private function GetDesiredHorizontalVelocity () {
	// 计算目标速度。
	var desiredLocalDirection : Vector3 = tr.InverseTransformDirection(inputMoveDirection);
	var maxSpeed : float = MaxSpeedInDirection(desiredLocalDirection);
	if (grounded) {
		// 根据坡度速度曲线调整最大速度。
		var movementSlopeAngle = Mathf.Asin(movement.velocity.normalized.y)  * Mathf.Rad2Deg;
		maxSpeed *= movement.slopeSpeedMultiplier.Evaluate(movementSlopeAngle);
	}
	return tr.TransformDirection(desiredLocalDirection * maxSpeed);
}

private function AdjustGroundVelocityToNormal (hVelocity : Vector3, groundNormal : Vector3) : Vector3 {
	var sideways : Vector3 = Vector3.Cross(Vector3.up, hVelocity);
	return Vector3.Cross(sideways, groundNormal).normalized * hVelocity.magnitude;
}

private function IsGroundedTest () {
	return (groundNormal.y > 0.01);
}

function GetMaxAcceleration (grounded : boolean) : float {
	// 接地与空中状态的最大加速度。
	if (grounded)
		return movement.maxGroundAcceleration;
	else
		return movement.maxAirAcceleration;
}

function CalculateJumpVerticalSpeed (targetJumpHeight : float) {
	// 根据跳跃高度和重力计算所需的初始上升速度，
	// 使角色能够到达预期最高点。
	return Mathf.Sqrt (2 * targetJumpHeight * movement.gravity);
}

function IsJumping () {
	return jumping.jumping;
}

function IsSliding () {
	return (grounded && sliding.enabled && TooSteep());
}

function IsTouchingCeiling () {
	return (movement.collisionFlags & CollisionFlags.CollidedAbove) != 0;
}

function IsGrounded () {
	return grounded;
}

function TooSteep () {
	return (groundNormal.y <= Mathf.Cos(controller.slopeLimit * Mathf.Deg2Rad));
}

function GetDirection () {
	return inputMoveDirection;
}

function SetControllable (controllable : boolean) {
	canControl = controllable;
}

// 根据前进、侧移和后退速度，将方向投影到椭圆各象限。
// 返回投影后向量的长度。
function MaxSpeedInDirection (desiredMovementDirection : Vector3) : float {
	if (desiredMovementDirection == Vector3.zero)
		return 0;
	else {
		var zAxisEllipseMultiplier : float = (desiredMovementDirection.z > 0 ? movement.maxForwardSpeed : movement.maxBackwardsSpeed) / movement.maxSidewaysSpeed;
		var temp : Vector3 = new Vector3(desiredMovementDirection.x, 0, desiredMovementDirection.z / zAxisEllipseMultiplier).normalized;
		var length : float = new Vector3(temp.x, 0, temp.z * zAxisEllipseMultiplier).magnitude * movement.maxSidewaysSpeed;
		return length;
	}
}

function SetVelocity (velocity : Vector3) {
	grounded = false;
	movement.velocity = velocity;
	movement.frameVelocity = Vector3.zero;
	SendMessage("OnExternalVelocity");
}

// 要求同一对象挂载角色控制器。
@script RequireComponent (CharacterController)
@script AddComponentMenu ("Character/Character Motor")
