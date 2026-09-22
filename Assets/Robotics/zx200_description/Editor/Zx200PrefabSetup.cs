using System;
using UrdfUrpImporter;
using Unity.Robotics.UrdfImporter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Zx200.Editor
{
    /// <summary>
    /// Adds and configures the ZX200 runtime digging controller on imported prefabs.
    /// </summary>
    public sealed class Zx200PrefabSetup : IUrdfPrefabPostprocessor
    {
        private const string DefaultPrefabPath =
            "Assets/Robotics/zx200_description/Prefabs/zx200.prefab";

        /// <summary>
        /// Reports whether the imported robot is ZX200.
        /// </summary>
        /// <param name="layout">Resolved robot layout.</param>
        /// <returns>True when the URDF robot name is `zx200`.</returns>
        public bool CanProcess(UrdfAssetLayout layout)
        {
            return layout != null &&
                   layout.RobotName.Equals("zx200", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Adds or updates the controller on a temporary imported ZX200 hierarchy.
        /// </summary>
        /// <param name="robot">Temporary ZX200 root.</param>
        /// <param name="layout">Resolved ZX200 layout.</param>
        public void Process(GameObject robot, UrdfAssetLayout layout)
        {
            ConfigureRobot(robot);
        }

        /// <summary>
        /// Configures the generated default ZX200 prefab from the Unity menu.
        /// </summary>
        [MenuItem("Tools/ZX200/Configure Dig Controller")]
        public static void ConfigureDefaultPrefab()
        {
            ConfigurePrefab(DefaultPrefabPath);
        }

        [MenuItem("Tools/ZX200/Add To Current Scene")]
        private static void AddToCurrentSceneMenu()
        {
            AddToCurrentScene();
        }

        /// <summary>
        /// Instantiates and validates the configured ZX200 prefab in the active scene.
        /// </summary>
        /// <returns>The newly created ZX200 scene instance.</returns>
        public static GameObject AddToCurrentScene()
        {
            ConfigurePrefab(DefaultPrefabPath);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DefaultPrefabPath);
            if (prefab == null)
            {
                throw new InvalidOperationException($"Could not load ZX200 prefab: {DefaultPrefabPath}");
            }

            Scene scene = SceneManager.GetActiveScene();
            var instance = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
            if (instance == null)
            {
                throw new InvalidOperationException("Could not instantiate the ZX200 prefab.");
            }

            Undo.RegisterCreatedObjectUndo(instance, "Add ZX200 To Current Scene");
            Zx200DigController controller = instance.GetComponent<Zx200DigController>();
            if (controller == null || !controller.PrepareForPhysics())
            {
                Undo.DestroyObjectImmediate(instance);
                throw new InvalidOperationException(
                    "The ZX200 prefab does not contain a valid digging controller and four joints.");
            }

            instance.name = "ZX200_Digging_Demo";
            Selection.activeGameObject = instance;
            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log("ZX200 added and validated. Enter Play mode, focus Game view, then press Space.",
                instance);
            return instance;
        }

        /// <summary>
        /// Adds or updates one configured digging controller in a prefab asset.
        /// </summary>
        /// <param name="prefabAssetPath">Unity asset path of the ZX200 prefab.</param>
        public static void ConfigurePrefab(string prefabAssetPath)
        {
            GameObject root = PrefabUtility.LoadPrefabContents(prefabAssetPath);
            if (root == null)
            {
                throw new InvalidOperationException($"Could not load prefab: {prefabAssetPath}");
            }

            try
            {
                ConfigureRobot(root);
                PrefabUtility.SaveAsPrefabAsset(root, prefabAssetPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>将当前待机角度保存为默认预制体的编辑器姿态，不重设控制参数。</summary>
        [MenuItem("Tools/ZX200/Save Work-ready Pose To Prefab")]
        public static void SaveDefaultWorkReadyPose()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请退出运行模式后保存待机姿态。");
            GameObject root = PrefabUtility.LoadPrefabContents(DefaultPrefabPath);
            try
            {
                ApplyEditorWorkReadyPose(root.GetComponent<Zx200DigController>());
                PrefabUtility.SaveAsPrefabAsset(root, DefaultPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        /// <summary>在编辑器中将设备摆到其待机姿态，保留关节零位与场景自定义参数。</summary>
        /// <param name="controller">需要更新模型姿态的ZX200控制器；只可在非运行模式调用。</param>
        /// <exception cref="InvalidOperationException">运行模式或缺少关节时抛出。</exception>
        public static void ApplyEditorWorkReadyPose(Zx200DigController controller)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请退出运行模式后设置编辑器姿态。");
            if (controller == null || !controller.ResolveJoints())
                throw new InvalidOperationException("ZX200 缺少待机姿态所需关节。");
            Zx200JointPose pose = controller.workReadyPose;
            SetEditorJointPose(controller.swingJoint, pose.swing);
            SetEditorJointPose(controller.boomJoint, pose.boom);
            SetEditorJointPose(controller.armJoint, pose.arm);
            SetEditorJointPose(controller.bucketJoint, pose.bucket);
        }

        /// <summary>把选中场景设备摆到它自己的待机位，支持Undo，由用户保存场景。</summary>
        [MenuItem("Tools/ZX200/Apply Work-ready Pose To Selected")]
        public static void ApplySelectedWorkReadyPose()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请退出运行模式后设置编辑器姿态。");
            GameObject selected = Selection.activeGameObject;
            Zx200DigController controller = selected != null
                ? selected.GetComponentInParent<Zx200DigController>() : null;
            if (controller == null || EditorUtility.IsPersistent(controller))
                throw new InvalidOperationException("请选中场景中的ZX200设备。");
            Undo.RegisterFullObjectHierarchyUndo(controller.gameObject, "Apply ZX200 work-ready pose");
            ApplyEditorWorkReadyPose(controller);
            EditorSceneManager.MarkSceneDirty(controller.gameObject.scene);
        }

        private static void SetEditorJointPose(ArticulationBody joint, float angle)
        {
            ArticulationBody parent = joint.transform.parent.GetComponentInParent<ArticulationBody>(true);
            ArticulationDrive drive = joint.xDrive;
            angle = Zx200DigController.ClampTarget(angle, drive.lowerLimit, drive.upperLimit);
            // 保留URDF零位锚点。自动匹配会把摆好的姿态重新当成零位，导致运行时再次甩臂。
            Vector3 parentAnchor = joint.parentAnchorPosition;
            Quaternion parentRotation = joint.parentAnchorRotation;
            joint.matchAnchors = false;
            Quaternion rotation = parent.transform.rotation * parentRotation *
                Quaternion.AngleAxis(angle, Vector3.right) * Quaternion.Inverse(joint.anchorRotation);
            Vector3 position = parent.transform.TransformPoint(parentAnchor) -
                rotation * Vector3.Scale(joint.anchorPosition, joint.transform.lossyScale);
            joint.transform.SetPositionAndRotation(position, rotation);
            joint.parentAnchorPosition = parentAnchor;
            joint.parentAnchorRotation = parentRotation;
            drive.target = angle;
            drive.targetVelocity = 0f;
            joint.xDrive = drive;
            if (PrefabUtility.IsPartOfPrefabInstance(joint))
            {
                PrefabUtility.RecordPrefabInstancePropertyModifications(joint.transform);
                PrefabUtility.RecordPrefabInstancePropertyModifications(joint);
            }
            EditorUtility.SetDirty(joint.transform);
            EditorUtility.SetDirty(joint);
        }

        private static void ConfigureRobot(GameObject robot)
        {
            if (robot == null) throw new ArgumentNullException(nameof(robot));

            Unity.Robotics.UrdfImporter.Control.Controller legacyController =
                robot.GetComponent<Unity.Robotics.UrdfImporter.Control.Controller>();
            if (legacyController != null)
            {
                legacyController.enabled = false;
                EditorUtility.SetDirty(legacyController);
            }

            ArticulationBody rootBody = FindJoint(robot, "track_link");
            ArticulationBody swingBody = FindJoint(robot, "body_link");
            ArticulationBody boomBody = FindJoint(robot, "boom_link");
            ArticulationBody armBody = FindJoint(robot, "arm_link");
            ArticulationBody bucketBody = FindJoint(robot, "bucket_link");
            UseAutomaticInertia(rootBody);
            UseAutomaticInertia(swingBody);
            UseAutomaticInertia(boomBody);
            UseAutomaticInertia(armBody);
            ConfigureBucketColliders(bucketBody);
            UseAutomaticInertia(bucketBody);

            ArticulationBody bucketEndBody = FindJoint(robot, "bucket_end_link");
            bucketEndBody.jointType = ArticulationJointType.FixedJoint;
            UseFixedJointMetadata(bucketEndBody);

            Zx200KeyboardController keyboard = robot.GetComponent<Zx200KeyboardController>();
            if (keyboard == null) keyboard = robot.AddComponent<Zx200KeyboardController>();
            keyboard.fixedBase = false;
            Zx200TrackDrive trackDrive = robot.GetComponent<Zx200TrackDrive>();
            if (trackDrive == null) trackDrive = robot.AddComponent<Zx200TrackDrive>();
            trackDrive.keyboard = keyboard;
            trackDrive.rootBody = rootBody;

            Zx200FixedBasePhysics physics = robot.GetComponent<Zx200FixedBasePhysics>();
            if (physics == null) physics = robot.AddComponent<Zx200FixedBasePhysics>();
            physics.rootBody = rootBody;
            physics.swingBody = swingBody;
            physics.boomBody = boomBody;
            physics.armBody = armBody;
            physics.bucketBody = bucketBody;
            physics.rootMass = 7000f;
            physics.swingMass = 8000f;
            physics.boomMass = 2000f;
            physics.armMass = 1200f;
            physics.bucketMass = 800f;
            physics.linearDamping = 0.1f;
            physics.angularDamping = 0.2f;
            physics.jointFriction = 0.05f;
            if (!physics.Configure())
            {
                throw new InvalidOperationException("ZX200 fixed-base physics configuration failed.");
            }

            Zx200DigController controller = robot.GetComponent<Zx200DigController>();
            if (controller == null) controller = robot.AddComponent<Zx200DigController>();

            controller.swingJoint = FindJoint(robot, "body_link");
            controller.boomJoint = FindJoint(robot, "boom_link");
            controller.armJoint = FindJoint(robot, "arm_link");
            controller.bucketJoint = FindJoint(robot, "bucket_link");
            controller.fixedBasePhysics = physics;
            controller.stiffness = 10000000f;
            controller.damping = 500000f;
            controller.forceLimit = 750000f;
            controller.jointToleranceDegrees = 1.5f;
            controller.settleTimeoutSeconds = 3f;
            controller.workReadyPose = new Zx200JointPose(0f, -48f, 68f, 25f, 1f);
            controller.approachPose = new Zx200JointPose(0f, -60f, 52f, 5f, 1.2f);
            controller.curlPose = new Zx200JointPose(0f, -62f, 92f, 105f, 1.1f);
            controller.curlHoldDuration = 0.25f;
            controller.liftPose = new Zx200JointPose(0f, -25f, 100f, 110f, 1.3f);
            controller.swingToDumpPose = new Zx200JointPose(60f, -25f, 100f, 110f, 1.4f);
            controller.dumpPreparePose = new Zx200JointPose(60f, -32f, 78f, 80f, 0.6f);
            controller.dumpPose = new Zx200JointPose(60f, -32f, 68f, -25f, 0.9f);
            controller.dumpHoldDuration = 0.35f;
            controller.returnSwingPose = new Zx200JointPose(0f, -35f, 72f, -20f, 1.4f);
            controller.enabled = true;

            keyboard.digController = controller;
            keyboard.articulationRoot = FindJoint(robot, "track_link");
            keyboard.fixedBase = false;
            keyboard.moveSpeed = 2f;
            keyboard.turnSpeed = 35f;
            keyboard.swingSpeed = 35f;
            keyboard.boomSpeed = 25f;
            keyboard.armSpeed = 30f;
            keyboard.bucketSpeed = 45f;
            keyboard.enabled = true;

            Zx200CameraController cameraController = robot.GetComponent<Zx200CameraController>();
            if (cameraController == null) cameraController = robot.AddComponent<Zx200CameraController>();
            cameraController.cabinAnchor = controller.swingJoint.transform;
            cameraController.orbitAnchor = keyboard.articulationRoot.transform;
            cameraController.startInThirdPerson = true;
            cameraController.initialThirdPersonYaw = 0f;
            cameraController.firstPersonForwardEuler = Vector3.zero;
            cameraController.enabled = true;
            if (!controller.PrepareForPhysics())
            {
                throw new InvalidOperationException("ZX200 articulation configuration failed.");
            }
            ApplyEditorWorkReadyPose(controller);
            EditorUtility.SetDirty(controller);
            EditorUtility.SetDirty(physics);
            EditorUtility.SetDirty(keyboard);
            EditorUtility.SetDirty(trackDrive);
            EditorUtility.SetDirty(cameraController);
        }

        private static void ConfigureBucketColliders(ArticulationBody bucket)
        {
            // The saved convex decomposition follows the bucket shell. One convex
            // hull of the complete visual mesh would fill the cavity and seal its mouth.
            const int hullCount = 23;
            const string meshDirectory = "Assets/Robotics/zx200_description/meshes/";
            var hulls = new Mesh[hullCount];
            for (int i = 0; i < hulls.Length; i++)
            {
                string path = $"{meshDirectory}zx200-bucket_{i + 1}.asset";
                hulls[i] = AssetDatabase.LoadAssetAtPath<Mesh>(path);
                if (hulls[i] == null)
                    throw new InvalidOperationException($"Missing ZX200 bucket collision hull: {path}");
            }

            var owned = new System.Collections.Generic.List<MeshCollider>();
            foreach (MeshCollider collider in bucket.GetComponentsInChildren<MeshCollider>(true))
            {
                if (collider.GetComponentInParent<ArticulationBody>() == bucket) owned.Add(collider);
            }
            if (owned.Count == 0)
                throw new InvalidOperationException("ZX200 bucket has no collision mesh to replace.");

            GameObject collisionObject = owned[0].gameObject;
            foreach (MeshCollider collider in owned)
            {
                if (collider.gameObject != collisionObject)
                    throw new InvalidOperationException("ZX200 bucket collision meshes must share the imported mesh transform.");
            }

            // Reuse the original imported transform so decomposition vertices retain
            // the DAE-to-Unity rotation. Repeated setup must not duplicate colliders.
            for (int i = owned.Count - 1; i >= hullCount; i--)
            {
                UnityEngine.Object.DestroyImmediate(owned[i]);
                owned.RemoveAt(i);
            }
            while (owned.Count < hullCount) owned.Add(collisionObject.AddComponent<MeshCollider>());
            for (int i = 0; i < hullCount; i++)
            {
                owned[i].sharedMesh = hulls[i];
                owned[i].convex = true;
                owned[i].isTrigger = false;
                owned[i].enabled = true;
                EditorUtility.SetDirty(owned[i]);
            }
        }

        private static void UseAutomaticInertia(ArticulationBody body)
        {
            UrdfInertial inertial = body.GetComponent<UrdfInertial>();
            if (inertial != null)
            {
                inertial.useUrdfData = false;
                EditorUtility.SetDirty(inertial);
            }

            body.automaticCenterOfMass = true;
            body.automaticInertiaTensor = true;
            EditorUtility.SetDirty(body);
        }

        private static void UseFixedJointMetadata(ArticulationBody body)
        {
            UrdfJoint existingJoint = body.GetComponent<UrdfJoint>();
            if (existingJoint is UrdfJointFixed) return;

            string jointName = existingJoint != null ? existingJoint.jointName : "bucket_end_joint";
            if (existingJoint != null) UnityEngine.Object.DestroyImmediate(existingJoint);
            UrdfJointFixed fixedJoint = body.gameObject.AddComponent<UrdfJointFixed>();
            fixedJoint.jointName = jointName;
            EditorUtility.SetDirty(fixedJoint);
        }

        private static ArticulationBody FindJoint(GameObject robot, string linkName)
        {
            foreach (ArticulationBody body in robot.GetComponentsInChildren<ArticulationBody>(true))
            {
                if (body.name == linkName) return body;
            }

            throw new InvalidOperationException($"ZX200 prefab is missing articulation link: {linkName}");
        }
    }
}
