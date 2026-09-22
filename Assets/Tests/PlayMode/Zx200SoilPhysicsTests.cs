using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    /// <summary>以实际关节物理反馈驱动切削，验证体积与地形同步。</summary>
    public class Zx200SoilPhysicsTests
    {
        private GameObject robot;
        private GameObject ground;
        private GameObject soilObject;
        private TerrainData source;

        /// <summary>实际动臂下降切土，抬升不补挖，复位后源资源保持完整。</summary>
        /// <returns>初始化、切削、抬升及复位的物理帧。</returns>
        [UnityTest]
        public IEnumerator RealBucketCutsConservesVolumeAndLiftsWithoutCutting()
        {
            ground = new GameObject("切削测试地面");
            ground.transform.position = new Vector3(0, -.5f, 0);
            ground.AddComponent<BoxCollider>().size = new Vector3(100, 1, 100);
            robot = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(
                "Assets/Robotics/zx200_description/Prefabs/zx200.prefab"));
            var keyboard = robot.GetComponent<Zx200KeyboardController>();
            keyboard.enabled = false;
            keyboard.fixedBase = true;
            robot.GetComponent<Zx200FixedBasePhysics>().Configure();
            var dig = robot.GetComponent<Zx200DigController>();
            for (int i = 0; i < 250; i++) yield return new WaitForFixedUpdate();
            source = new TerrainData { heightmapResolution = 65, size = new Vector3(6, 1, 6) };
            var heights = new float[65, 65];
            for (int z = 0; z < 65; z++)
                for (int x = 0; x < 65; x++) heights[z, x] = .6f;
            source.SetHeights(0, 0, heights);
            float initialSource = source.GetHeights(32, 32, 1, 1)[0, 0];
            soilObject = Terrain.CreateTerrainGameObject(source);
            soilObject.transform.position = new Vector3(-3, 0, 4);
            var soil = soilObject.AddComponent<DeformableSoilTerrain>();
            soil.maximumCutDepth = .55f;
            var cutter = dig.bucketJoint.gameObject.AddComponent<BucketSoilCutter>();
            cutter.soil = soil;
            foreach (Transform child in dig.bucketJoint.GetComponentsInChildren<Transform>())
                if (child.name == "bucket_end_link") cutter.tipMarker = child;
            Assert.That(cutter.tipMarker, Is.Not.Null);
            yield return ManualSoilTestMotion.Scoop(dig, cutter);
            for (int i = 0; i < 75; i++) yield return new WaitForFixedUpdate();
            double volume = cutter.VolumeM3;
            Debug.Log($"实际铲斗切削：斗尖={cutter.TipWorldPosition}，斗量={volume:F5}m³");
            Assert.That(volume, Is.GreaterThan(.02));
            ManualSoilTestMotion.AssertBalance(cutter);
            for (int i = 0; i < 100; i++)
            {
                dig.AdjustManualPose(0, -10 * Time.fixedDeltaTime, 0, 0);
                yield return new WaitForFixedUpdate();
            }
            Assert.That(cutter.VolumeM3, Is.EqualTo(volume).Within(.00001));
            Assert.That(cutter.TipWorldPosition.y, Is.GreaterThan(.6f));
            cutter.ResetExcavation();
            Assert.That(cutter.VolumeM3, Is.Zero);
            Assert.That(soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(.00001));
            Assert.That(source.GetHeights(32, 32, 1, 1)[0, 0], Is.EqualTo(initialSource));
        }

        /// <summary>清理设备、土区克隆与测试源资源。</summary>
        [TearDown]
        public void Cleanup()
        {
            if (robot != null) Object.DestroyImmediate(robot);
            if (soilObject != null) Object.DestroyImmediate(soilObject);
            if (ground != null) Object.DestroyImmediate(ground);
            if (source != null) Object.DestroyImmediate(source);
        }
    }
}
