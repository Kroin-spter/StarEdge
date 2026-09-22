

var cameraTransform : Transform;
private var _target : Transform;

// 与目标在水平面上的距离。

var distance = 7.0;

// 相机相对于目标的高度。
var height = 3.0;

var angularSmoothLag = 0.3;
var angularMaxSpeed = 15.0;

var heightSmoothLag = 0.3;

var snapSmoothLag = 0.2;
var snapMaxSpeed = 720.0;

var clampHeadPositionScreenSpace = 0.75;

var lockCameraTimeout = 0.2;

private var headOffset = Vector3.zero;
private var centerOffset = Vector3.zero;

private var heightVelocity = 0.0;
private var angleVelocity = 0.0;
private var snap = false;
private var controller : ThirdPersonController;
private var targetHeight = 100000.0; 

function Awake ()
{
	if(!cameraTransform && Camera.main)
		cameraTransform = Camera.main.transform;
	if(!cameraTransform) {
		Debug.Log("Please assign a camera to the ThirdPersonCamera script.");
		enabled = false;	
	}
			
		
	_target = transform;
	if (_target)
	{
		controller = _target.GetComponent(ThirdPersonController);
	}
	
	if (controller)
	{
		var characterController : CharacterController = _target.GetComponent.<Collider>();
		centerOffset = characterController.bounds.center - _target.position;
		headOffset = centerOffset;
		headOffset.y = characterController.bounds.max.y - _target.position.y;
	}
	else
		Debug.Log("Please assign a target to the camera that has a ThirdPersonController script attached.");

	
	Cut(_target, centerOffset);
}

function DebugDrawStuff ()
{
	Debug.DrawLine(_target.position, _target.position + headOffset);

}

function AngleDistance (a : float, b : float)
{
	a = Mathf.Repeat(a, 360);
	b = Mathf.Repeat(b, 360);
	
	return Mathf.Abs(b - a);
}

function Apply (dummyTarget : Transform, dummyCenter : Vector3)
{
	// 未设置目标时直接返回。
	if (!controller)
		return;
	
	var targetCenter = _target.position + centerOffset;
	var targetHead = _target.position + headOffset;

// 可在此启用相机调试绘制。

	// 计算当前和目标旋转角。
	var originalTargetAngle = _target.eulerAngles.y;
	var currentAngle = cameraTransform.eulerAngles.y;

	// 相机锁定时调整实际目标角。
	var targetAngle = originalTargetAngle; 
	
	// 按下 Fire2 时快速转向目标方向，
	// 到达目标朝向后停止快速对齐。
	if (Input.GetButton("Fire2"))
		snap = true;
	
	if (snap)
	{
		// 已经接近目标朝向，停止快速对齐。
		if (AngleDistance (currentAngle, originalTargetAngle) < 3.0)
			snap = false;
		
		currentAngle = Mathf.SmoothDampAngle(currentAngle, targetAngle, angleVelocity, snapSmoothLag, snapMaxSpeed);
	}
	// 常规相机运动。
	else
	{
		if (controller.GetLockCameraTimer () < lockCameraTimeout)
		{
			targetAngle = currentAngle;
		}

		// 倒退时锁定相机，
		// 避免转身引起 180 度相机旋转。
		if (AngleDistance (currentAngle, targetAngle) > 160 && controller.IsMovingBackwards ())
			targetAngle += 180;

		currentAngle = Mathf.SmoothDampAngle(currentAngle, targetAngle, angleVelocity, angularSmoothLag, angularMaxSpeed);
	}


	// 跳跃时仅允许相机向下跟随，不立即向上跟随。
	if (controller.IsJumping ())
	{
		// 只有高度差足够大时才向上移动相机。
		var newTargetHeight = targetCenter.y + height;
		if (newTargetHeight < targetHeight || newTargetHeight - targetHeight > 5)
			targetHeight = targetCenter.y + height;
	}
	// 行走时始终更新目标高度。
	else
	{
		targetHeight = targetCenter.y + height;
	}

	// 平滑高度变化。
	var currentHeight = cameraTransform.position.y;
	currentHeight = Mathf.SmoothDamp (currentHeight, targetHeight, heightVelocity, heightSmoothLag);

	// 将角度转换为旋转，再据此计算相机位置。
	var currentRotation = Quaternion.Euler (0, currentAngle, 0);
	
	// 设置相机在水平面上的位置，
	// 使其位于目标后方指定距离。
	cameraTransform.position = targetCenter;
	cameraTransform.position += currentRotation * Vector3.back * distance;

	// 设置相机高度。
	cameraTransform.position.y = currentHeight;
	
	// 始终朝向目标。
	SetUpRotation(targetCenter, targetHead);
}

function LateUpdate () {
	Apply (transform, Vector3.zero);
}

function Cut (dummyTarget : Transform, dummyCenter : Vector3)
{
	var oldHeightSmooth = heightSmoothLag;
	var oldSnapMaxSpeed = snapMaxSpeed;
	var oldSnapSmooth = snapSmoothLag;
	
	snapMaxSpeed = 10000;
	snapSmoothLag = 0.001;
	heightSmoothLag = 0.001;
	
	snap = true;
	Apply (transform, Vector3.zero);
	
	heightSmoothLag = oldHeightSmooth;
	snapMaxSpeed = oldSnapMaxSpeed;
	snapSmoothLag = oldSnapSmooth;
}

function SetUpRotation (centerPos : Vector3, headPos : Vector3)
{
	// 跳跃时需要额外处理屏幕构图：
	// 上下跳跃时不强制角色始终处于屏幕中心，
	// 以保留跳跃高度感并减少相机大幅运动。
	//   
	// 同时保持角色可见，且所有旋转平滑。
	//
	// 依次执行以下步骤：
	//
	// 第一步：计算绕 Y 轴的旋转，使角色保持水平居中。
	// 第二步：接地时让角色居中。
	// 第三步：跳跃时保持相机角度，仅在头部超出阈值时调整。
	// 第四步：落地后平滑恢复居中构图。
	var cameraPos = cameraTransform.position;
	var offsetToCenter = centerPos - cameraPos;
	
	// 只生成绕 Y 轴的基础旋转。
	var yRotation = Quaternion.LookRotation(Vector3(offsetToCenter.x, 0, offsetToCenter.z));

	var relativeOffset = Vector3.forward * distance + Vector3.down * height;
	cameraTransform.rotation = yRotation * Quaternion.LookRotation(relativeOffset);

	// 计算目标中心与顶部在世界坐标中的投影位置。
	var centerRay = cameraTransform.GetComponent.<Camera>().ViewportPointToRay(Vector3(.5, 0.5, 1));
	var topRay = cameraTransform.GetComponent.<Camera>().ViewportPointToRay(Vector3(.5, clampHeadPositionScreenSpace, 1));

	var centerRayPos = centerRay.GetPoint(distance);
	var topRayPos = topRay.GetPoint(distance);
	
	var centerToTopAngle = Vector3.Angle(centerRay.direction, topRay.direction);
	
	var heightToAngle = centerToTopAngle / (centerRayPos.y - topRayPos.y);

	var extraLookAngle = heightToAngle * (centerRayPos.y - centerPos.y);
	if (extraLookAngle < centerToTopAngle)
	{
		extraLookAngle = 0;
	}
	else
	{
		extraLookAngle = extraLookAngle - centerToTopAngle;
		cameraTransform.rotation *= Quaternion.Euler(-extraLookAngle, 0, 0);
	}
}

function GetCenterOffset ()
{
	return centerOffset;
}
