using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace ExcavatorControls.Tests
{
    /// <summary>保证未运行的预制体几何姿态与物理关节待机角度一致。</summary>
    public class Zx200SavedPoseTests
    {
        /// <summary>从锚点坐标独立测量保存的关节姿态，防止只改目标而未改模型。</summary>
        [Test]
        public void SavedPrefabAlreadyDisplaysWorkReadyPose()
        {
            GameObject robot = AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Robotics/zx200_description/Prefabs/zx200.prefab");
            var dig = robot.GetComponent<Zx200DigController>();
            AssertAngle(dig.swingJoint, 0f);
            AssertAngle(dig.boomJoint, -48f);
            AssertAngle(dig.armJoint, 68f);
            AssertAngle(dig.bucketJoint, 25f);
        }

        /// <summary>切土场景自己的待机角度也必须保存到模型，而非仅覆盖控制器目标。</summary>
        [Test]
        public void SavedSoilDemoDisplaysItsOwnWorkReadyPose()
        {
            const string path = "Assets/Robotics/zx200_description/Demos/SoilCutting/Demo/ZX200SoilDemo.unity";
            var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path,
                UnityEditor.SceneManagement.OpenSceneMode.Additive);
            try
            {
                Zx200DigController dig = null;
                foreach (GameObject root in scene.GetRootGameObjects())
                    if (root.TryGetComponent(out Zx200DigController candidate)) dig = candidate;
                Assert.That(dig, Is.Not.Null);
                AssertAngle(dig.boomJoint, -26f);
                AssertAngle(dig.armJoint, 68f);
                AssertAngle(dig.bucketJoint, 25f);
            }
            finally { UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true); }
        }

        private static void AssertAngle(ArticulationBody body, float expected)
        {
            ArticulationBody parent = body.transform.parent.GetComponentInParent<ArticulationBody>(true);
            Quaternion parentFrame = parent.transform.rotation * body.parentAnchorRotation;
            Quaternion childFrame = body.transform.rotation * body.anchorRotation;
            Quaternion relative = Quaternion.Inverse(parentFrame) * childFrame;
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(relative.eulerAngles.x, expected)), Is.LessThan(.05f), body.name);
            Assert.That(Vector3.Angle(relative * Vector3.right, Vector3.right), Is.LessThan(.05f), "仅绕关节X轴旋转");
        }
    }
}
