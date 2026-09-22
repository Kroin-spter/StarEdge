
// 要求同一对象挂载角色控制器。
@script RequireComponent(CharacterController)

public var idleAnimation : AnimationClip;
public var walkAnimation : AnimationClip;
public var runAnimation : AnimationClip;
public var jumpPoseAnimation : AnimationClip;

public var walkMaxAnimationSpeed : float = 0.75;
public var trotMaxAnimationSpeed : float = 1.0;
public var runMaxAnimationSpeed : float = 1.0;
public var jumpAnimationSpeed : float = 1.15;
public var landAnimationSpeed : float = 1.0;

private var _animation : Animation;

enum CharacterState {
	Idle = 0,
	Walking = 1,
	Trotting = 2,
	Running = 3,
	Jumping = 4,
}

private var _characterState : CharacterState;

// 步行速度。
var walkSpeed = 2.0;
// 步行达到指定时间后切换为小跑速度。
var trotSpeed = 4.0;
// 按下 Fire3 时开始奔跑。
var runSpeed = 6.0;

var inAirControlAcceleration = 3.0;

// 短按跳跃时的跳跃高度。
var jumpHeight = 0.5;

// 角色所受重力。
var gravity = 20.0;
// 受控下降模式下的重力。
var speedSmoothing = 10.0;
var rotateSpeed = 500.0;
var trotAfterSeconds = 3.0;

var canJump = true;

private var jumpRepeatTime = 0.05;
private var jumpTimeout = 0.15;
private var groundedTimeout = 0.25;

// 相机延迟短暂时间后再跟随目标，减少来回晃动。
private var lockCameraTimer = 0.0;

// 当前水平移动方向。
private var moveDirection = Vector3.zero;
// 当前竖直速度。
private var verticalSpeed = 0.0;
// 当前水平移动速度。
private var moveSpeed = 0.0;

// 记录 controller.Move 最近返回的碰撞标记。
private var collisionFlags : CollisionFlags; 

// 是否处于按键触发后尚未落地的跳跃状态。
private var jumping = false;
private var jumpingReachedApex = false;

// 是否倒退；倒退时锁定相机，避免旋转 180 度。
private var movingBack = false;
// 玩家是否正在按下移动键。
private var isMoving = false;
// 开始步行的时刻，用于延迟切换小跑。
private var walkTimeStart = 0.0;
// 最近按下跳跃键的时刻。
private var lastJumpButtonTime = -10.0;
// 最近起跳的时刻。
private var lastJumpTime = -1.0;


// 起跳高度，用于计算额外跳跃作用的持续时间。
private var lastJumpStartHeight = 0.0;


private var inAirVelocity = Vector3.zero;

private var lastGroundedTime = 0.0;


private var isControllable = true;

function Awake ()
{
	moveDirection = transform.TransformDirection(Vector3.forward);
	
	_animation = GetComponent(Animation);
	if(!_animation)
		Debug.Log("The character you would like to control doesn't have animations. Moving her might look weird.");
	
	/*
public var idleAnimation : AnimationClip;
public var walkAnimation : AnimationClip;
public var runAnimation : AnimationClip;
public var jumpPoseAnimation : AnimationClip;	
	*/
	if(!idleAnimation) {
		_animation = null;
		Debug.Log("No idle animation found. Turning off animations.");
	}
	if(!walkAnimation) {
		_animation = null;
		Debug.Log("No walk animation found. Turning off animations.");
	}
	if(!runAnimation) {
		_animation = null;
		Debug.Log("No run animation found. Turning off animations.");
	}
	if(!jumpPoseAnimation && canJump) {
		_animation = null;
		Debug.Log("No jump animation found and the character has canJump enabled. Turning off animations.");
	}
			
}


function UpdateSmoothedMovementDirection ()
{
	var cameraTransform = Camera.main.transform;
	var grounded = IsGrounded();
	
	// 相机前方向在水平面上的投影。
	var forward = cameraTransform.TransformDirection(Vector3.forward);
	forward.y = 0;
	forward = forward.normalized;

	// 相对于相机的右方向，
	// 始终与前方向正交。
	var right = Vector3(forward.z, 0, -forward.x);

	var v = Input.GetAxisRaw("Vertical");
	var h = Input.GetAxisRaw("Horizontal");

	// 判断是否正在向后移动或观察。
	if (v < -0.2)
		movingBack = true;
	else
		movingBack = false;
	
	var wasMoving = isMoving;
	isMoving = Mathf.Abs (h) > 0.1 || Mathf.Abs (v) > 0.1;
		
	// 计算相对于相机的目标方向。
	var targetDirection = h * right + v * forward;
	
	// 接地状态下的控制。
	if (grounded)
	{
		// 在移动和静止切换时短暂锁定相机。
		lockCameraTimer += Time.deltaTime;
		if (isMoving != wasMoving)
			lockCameraTimer = 0.0;

		// 分别记录速度和方向，
		// 确保角色静止时仍保留有效朝向。
		// 移动方向始终归一化，且仅在存在输入时更新。
		if (targetDirection != Vector3.zero)
		{
			// 速度很低时直接对齐目标方向。
			if (moveSpeed < walkSpeed * 0.9 && grounded)
			{
				moveDirection = targetDirection.normalized;
			}
			// 否则平滑转向目标。
			else
			{
				moveDirection = Vector3.RotateTowards(moveDirection, targetDirection, rotateSpeed * Mathf.Deg2Rad * Time.deltaTime, 1000);
				
				moveDirection = moveDirection.normalized;
			}
		}
		
		// 根据当前目标方向平滑调整速度。
		var curSmooth = speedSmoothing * Time.deltaTime;
		
		// 选择目标速度。
		// 支持模拟输入，同时防止斜向移动比单轴移动更快。
		var targetSpeed = Mathf.Min(targetDirection.magnitude, 1.0);
	
		_characterState = CharacterState.Idle;
		
		// 选择速度修正系数。
		if (Input.GetKey (KeyCode.LeftShift) | Input.GetKey (KeyCode.RightShift))
		{
			targetSpeed *= runSpeed;
			_characterState = CharacterState.Running;
		}
		else if (Time.time - trotAfterSeconds > walkTimeStart)
		{
			targetSpeed *= trotSpeed;
			_characterState = CharacterState.Trotting;
		}
		else
		{
			targetSpeed *= walkSpeed;
			_characterState = CharacterState.Walking;
		}
		
		moveSpeed = Mathf.Lerp(moveSpeed, targetSpeed, curSmooth);
		
		// 减速时重置步行计时。
		if (moveSpeed < walkSpeed * 0.3)
			walkTimeStart = Time.time;
	}
	// 空中状态下的控制。
	else
	{
		// 空中锁定相机。
		if (jumping)
			lockCameraTimer = 0.0;

		if (isMoving)
			inAirVelocity += targetDirection.normalized * Time.deltaTime * inAirControlAcceleration;
	}
	

		
}


function ApplyJumping ()
{
	// 限制连续起跳间隔。
	if (lastJumpTime + jumpRepeatTime > Time.time)
		return;

	if (IsGrounded()) {
		// 处理跳跃：
		// 仅响应按键按下事件；
		// 允许在落地前短暂提前输入。
		if (canJump && Time.time < lastJumpButtonTime + jumpTimeout) {
			verticalSpeed = CalculateJumpVerticalSpeed (jumpHeight);
			SendMessage("DidJump", SendMessageOptions.DontRequireReceiver);
		}
	}
}


function ApplyGravity ()
{
	if (isControllable)	// 不可控制时完全停止玩家移动输入。
	{
		// 施加重力。
		var jumpButton = Input.GetButton("Jump");
		
		
		// 到达跳跃最高点时发送消息。
		if (jumping && !jumpingReachedApex && verticalSpeed <= 0.0)
		{
			jumpingReachedApex = true;
			SendMessage("DidJumpReachApex", SendMessageOptions.DontRequireReceiver);
		}
	
		if (IsGrounded ())
			verticalSpeed = 0.0;
		else
			verticalSpeed -= gravity * Time.deltaTime;
	}
}

function CalculateJumpVerticalSpeed (targetJumpHeight : float)
{
	// 根据跳跃高度和重力计算所需的初始上升速度，
	// 使角色能够到达预期最高点。
	return Mathf.Sqrt(2 * targetJumpHeight * gravity);
}

function DidJump ()
{
	jumping = true;
	jumpingReachedApex = false;
	lastJumpTime = Time.time;
	lastJumpStartHeight = transform.position.y;
	lastJumpButtonTime = -10;
	
	_characterState = CharacterState.Jumping;
}

function Update() {
	
	if (!isControllable)
	{
		// 不可控制时清除全部输入。
		Input.ResetInputAxes();
	}

	if (Input.GetButtonDown ("Jump"))
	{
		lastJumpButtonTime = Time.time;
	}

	UpdateSmoothedMovementDirection();
	
	// 施加重力。
	// 额外跳跃作用会改变重力效果。
	// 受控下降模式也会改变重力效果。
	ApplyGravity ();

	// 应用跳跃逻辑。
	ApplyJumping ();
	
	// 计算实际移动量。
	var movement = moveDirection * moveSpeed + Vector3 (0, verticalSpeed, 0) + inAirVelocity;
	movement *= Time.deltaTime;
	
	// 移动角色控制器。
	var controller : CharacterController = GetComponent(CharacterController);
	collisionFlags = controller.Move(movement);
	
	// 动画状态处理。
	if(_animation) {
		if(_characterState == CharacterState.Jumping) 
		{
			if(!jumpingReachedApex) {
				_animation[jumpPoseAnimation.name].speed = jumpAnimationSpeed;
				_animation[jumpPoseAnimation.name].wrapMode = WrapMode.ClampForever;
				_animation.CrossFade(jumpPoseAnimation.name);
			} else {
				_animation[jumpPoseAnimation.name].speed = -landAnimationSpeed;
				_animation[jumpPoseAnimation.name].wrapMode = WrapMode.ClampForever;
				_animation.CrossFade(jumpPoseAnimation.name);				
			}
		} 
		else 
		{
			if(controller.velocity.sqrMagnitude < 0.1) {
				_animation.CrossFade(idleAnimation.name);
			}
			else 
			{
				if(_characterState == CharacterState.Running) {
					_animation[runAnimation.name].speed = Mathf.Clamp(controller.velocity.magnitude, 0.0, runMaxAnimationSpeed);
					_animation.CrossFade(runAnimation.name);	
				}
				else if(_characterState == CharacterState.Trotting) {
					_animation[walkAnimation.name].speed = Mathf.Clamp(controller.velocity.magnitude, 0.0, trotMaxAnimationSpeed);
					_animation.CrossFade(walkAnimation.name);	
				}
				else if(_characterState == CharacterState.Walking) {
					_animation[walkAnimation.name].speed = Mathf.Clamp(controller.velocity.magnitude, 0.0, walkMaxAnimationSpeed);
					_animation.CrossFade(walkAnimation.name);	
				}
				
			}
		}
	}
	// 动画状态处理。
	
	// 使朝向跟随移动方向。
	if (IsGrounded())
	{
		
		transform.rotation = Quaternion.LookRotation(moveDirection);
			
	}	
	else
	{
		var xzMove = movement;
		xzMove.y = 0;
		if (xzMove.sqrMagnitude > 0.001)
		{
			transform.rotation = Quaternion.LookRotation(xzMove);
		}
	}	
	
	// 从跳跃状态切换为刚接地状态。
	if (IsGrounded())
	{
		lastGroundedTime = Time.time;
		inAirVelocity = Vector3.zero;
		if (jumping)
		{
			jumping = false;
			SendMessage("DidLand", SendMessageOptions.DontRequireReceiver);
		}
	}
}

function OnControllerColliderHit (hit : ControllerColliderHit )
{
// 可在此绘制碰撞法线。
	if (hit.moveDirection.y > 0.01) 
		return;
}

function GetSpeed () {
	return moveSpeed;
}

function IsJumping () {
	return jumping;
}

function IsGrounded () {
	return (collisionFlags & CollisionFlags.CollidedBelow) != 0;
}

function GetDirection () {
	return moveDirection;
}

function IsMovingBackwards () {
	return movingBack;
}

function GetLockCameraTimer () 
{
	return lockCameraTimer;
}

function IsMoving ()  : boolean
{
	return Mathf.Abs(Input.GetAxisRaw("Vertical")) + Mathf.Abs(Input.GetAxisRaw("Horizontal")) > 0.5;
}

function HasJumpReachedApex ()
{
	return jumpingReachedApex;
}

function IsGroundedWithTimeout ()
{
	return lastGroundedTime + groundedTimeout > Time.time;
}

function Reset ()
{
	gameObject.tag = "Player";
}

