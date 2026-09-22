using System.Collections;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ExcavatorControls.Tests
{
    public class BladeMainSceneTests
    {
        /// <summary>Use actual articulation entry from the main ready pose and verify cut/collection/spoil accounting.</summary>
        /// <returns>Main scene initialization, real joint motion, settling and cleanup.</returns>
        [UnityTest]
        public IEnumerator MainSceneDownwardEntryCutsAtTeethAndAccountsForDisplacement()
        {
            const string path = "Assets/Scenes/sence_test.unity";
            Scene previous = SceneManager.GetActiveScene();
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(path, new LoadSceneParameters(LoadSceneMode.Additive));
            Scene scene = SceneManager.GetSceneByPath(path);
            AsyncOperation unload;
            try
            {
                SceneManager.SetActiveScene(scene);
                yield return ManualSoilTestMotion.WaitForSiteReady(scene);
                Zx200DigController dig = null;
                foreach (var root in scene.GetRootGameObjects())
                {
                    var candidate = root.GetComponentInChildren<Zx200DigController>();
                    if (candidate != null) dig = candidate;
                }
                Assert.That(dig, Is.Not.Null);
                dig.GetComponent<Zx200KeyboardController>().enabled = false;
                var cutter = dig.bucketJoint.GetComponent<BucketSoilCutter>();
                var unloader = cutter.GetComponent<BucketSoilUnloader>();
                for (int i=0;i<250;i++) yield return new WaitForFixedUpdate();
                Assert.That(cutter.soil.RecalculateRemovedVolumeM3(), Is.Zero.Within(1e-8),
                    "Preparing the Cesium boundary must not count as excavation");
                if (SystemInfo.graphicsDeviceType != UnityEngine.Rendering.GraphicsDeviceType.Null)
                {
                    Assert.That(cutter.soil.Terrain.terrainData.terrainLayers.Length, Is.EqualTo(1));
                    Assert.That(cutter.soil.Terrain.terrainData.IsHole(0,0), Is.True,
                        "Original map must show through the rectangular Terrain corners");
                }
                CaptureClose(cutter,dig.GetComponent<Zx200CameraController>().targetCamera,true);
                var soilData = cutter.soil.Terrain.terrainData;
                Assert.That(soilData.size.z / (soilData.heightmapResolution - 1),
                    Is.LessThan(cutter.bladeThickness * .5f), "Main soil grid must resolve the thin physical tooth proxy");
                double initial = cutter.ExcavatedVolumeM3;
                for (int i=0;i<250 && cutter.ExcavatedVolumeM3-initial<.008;i++)
                {
                    dig.AdjustManualPose(0,10*Time.fixedDeltaTime,0,0);
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(cutter.ExcavatedVolumeM3-initial, Is.GreaterThan(.001),
                    "The open ready pose can vertically penetrate soil; intake posture cannot veto actual tooth cutting");
                Assert.That(cutter.CollectedVolumeM3+cutter.DisplacedVolumeM3,
                    Is.EqualTo(cutter.ExcavatedVolumeM3).Within(1e-8));
                Assert.That(cutter.ExcavatedVolumeM3,
                    Is.EqualTo(cutter.VolumeM3+unloader.AirborneVolumeM3+unloader.SurfacePendingVolumeM3+cutter.DeliveredVolumeM3).Within(1e-5));
                Debug.Log($"VERTICAL MAIN cut={cutter.ExcavatedVolumeM3:F6}, collected={cutter.CollectedVolumeM3:F6}, displaced={cutter.DisplacedVolumeM3:F6}, bucket={cutter.VolumeM3:F6}, tip={cutter.TipWorldPosition}, status={cutter.Status}");
                CaptureClose(cutter,dig.GetComponent<Zx200CameraController>().targetCamera,false);
                double beforeArm = cutter.ExcavatedVolumeM3;
                float armStart = dig.ReadActualPose().arm;
                for (int i=0;i<180;i++)
                {
                    dig.AdjustManualPose(0,0,10*Time.fixedDeltaTime,0);
                    yield return new WaitForFixedUpdate();
                }
                Debug.Log($"ARM AFTER ENTRY start={armStart:F3} end={dig.ReadActualPose().arm:F3} cut={cutter.ExcavatedVolumeM3-beforeArm:F6} status={cutter.Status} guard={cutter.GetComponent<BucketSoilResistance>().BlockReason}");
                Vector3 pivot = dig.armJoint.transform.TransformPoint(dig.armJoint.anchorPosition);
                Vector3 axis = dig.armJoint.transform.TransformDirection(dig.armJoint.anchorRotation*Vector3.right);
                Vector3 predicted = pivot+Quaternion.AngleAxis(.2f,axis)*(cutter.TipWorldPosition-pivot)-cutter.TipWorldPosition;
                Debug.Log($"ARM GEOMETRY move={predicted.ToString("F6")} direction={predicted.normalized.ToString("F6")} cutAxis={cutter.transform.TransformDirection(cutter.cutDirectionLocal).normalized} tip={cutter.TipWorldPosition} pivot={pivot} drive={dig.armJoint.xDrive.target} canCut={cutter.CanCutMovement(predicted,out string cutReason)} reason={cutReason}");
                Assert.That(dig.ReadActualPose().arm, Is.GreaterThan(armStart+1), "Arm retraction after vertical entry must not deadlock");
                Assert.That(cutter.ExcavatedVolumeM3-beforeArm, Is.GreaterThan(.001), "Actual arm sweep must remove new soil");
                ManualSoilTestMotion.AssertBalance(cutter);
                // Later control can lift out; the interaction never locks escape.
                float entryY = cutter.TipWorldPosition.y;
                for (int i=0;i<120;i++)
                {
                    dig.AdjustManualPose(0,-10*Time.fixedDeltaTime,0,0);
                    yield return new WaitForFixedUpdate();
                }
                Assert.That(cutter.TipWorldPosition.y, Is.GreaterThan(entryY+.4f));
                cutter.ResetExcavation();
                Assert.That(cutter.ExcavatedVolumeM3+cutter.CollectedVolumeM3+cutter.DisplacedVolumeM3, Is.Zero);
                Assert.That(unloader.AirborneVolumeM3+unloader.SurfacePendingVolumeM3, Is.Zero);
                var appearance = cutter.soil.GetComponent("ConstructionSiteSurfaceAppearance");
                if (appearance != null)
                {
                    Object.DestroyImmediate(appearance);
                    Assert.That(cutter.soil.Terrain.terrainData.terrainLayers.Length, Is.EqualTo(1),
                        "Removing the appearance must restore live Terrain layers before destroying captured imagery");
                }
            }
            finally
            {
                if(previous.IsValid()&&previous.isLoaded) SceneManager.SetActiveScene(previous);
                unload = SceneManager.UnloadSceneAsync(scene);
            }
            if(unload!=null) yield return unload;
        }

        internal static void CaptureClose(BucketSoilCutter cutter, Camera camera, bool overlay, string tag = "blade-main")
        {
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return;
            Vector3 savedPosition=camera.transform.position;
            Quaternion savedRotation=camera.transform.rotation;
            RenderTexture savedTarget=camera.targetTexture, savedActive=RenderTexture.active;
            var target=new RenderTexture(1280,900,24);
            var image=new Texture2D(1280,900,TextureFormat.RGB24,false);
            var lines=new GameObject("Diagnostic tooth geometry");
            var material=new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            material.SetColor("_BaseColor",Color.cyan);
            try
            {
                if(overlay)
                {
                    Vector3[] points=cutter.GetBladeWorldCorners();
                    for(int i=0;i<8;i++) for(int bit=1;bit<=4;bit*=2)
                    {
                        if((i&bit)!=0)continue;
                        var edge=new GameObject("Tooth edge");edge.transform.SetParent(lines.transform);
                        var line=edge.AddComponent<LineRenderer>();line.sharedMaterial=material;
                        line.startWidth=line.endWidth=.008f;line.positionCount=2;
                        line.SetPositions(new[]{points[i],points[i|bit]});
                    }
                }
                Vector3 center=cutter.transform.TransformPoint(new Vector3(0,.65f,.65f));
                camera.transform.position=center+cutter.transform.right*1.4f+Vector3.up*1.7f+
                    Vector3.ProjectOnPlane(cutter.transform.TransformDirection(cutter.cutDirectionLocal),Vector3.up).normalized*2.6f;
                camera.transform.LookAt(center);
                camera.targetTexture=target;camera.Render();RenderTexture.active=target;
                image.ReadPixels(new Rect(0,0,1280,900),0,0);image.Apply();
                System.IO.Directory.CreateDirectory("Diagnostics");
                System.IO.File.WriteAllBytes("Diagnostics/"+tag+"-"+(overlay?"geometry":"cut")+".png",image.EncodeToPNG());
            }
            finally
            {
                camera.transform.SetPositionAndRotation(savedPosition,savedRotation);
                camera.targetTexture=savedTarget;RenderTexture.active=savedActive;
                Object.DestroyImmediate(lines);Object.DestroyImmediate(material);
                Object.DestroyImmediate(image);Object.DestroyImmediate(target);
            }
        }
    }
}
