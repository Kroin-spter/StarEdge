using System.Collections;
using UnityEngine;
using NUnit.Framework;

namespace ExcavatorControls.Tests
{
    /// <summary>Exercise the same manual command interface as the keyboard, using an actual scooping posture.</summary>
    internal static class ManualSoilTestMotion
    {
        internal static IEnumerator WaitForSiteReady(UnityEngine.SceneManagement.Scene scene)
        {
            MonoBehaviour site = null;
            foreach (var root in scene.GetRootGameObjects())
                foreach (var component in root.GetComponentsInChildren<MonoBehaviour>())
                    if (component != null && component.GetType().Name == "CesiumMoundSite") site = component;
            if (site == null) yield break;
            float deadline = Time.realtimeSinceStartup + 40;
            var ready = site.GetType().GetProperty("IsReady");
            while (!(bool)ready.GetValue(site) && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That((bool)ready.GetValue(site), Is.True, "Original map support and mound must be ready before physical controls are released");
        }

        internal static void AssertBalance(BucketSoilCutter cutter)
        {
            var flight = cutter.GetComponent<SoilFlightTransport>();
            double inFlight = flight != null ? flight.AirborneVolumeM3 + flight.SurfacePendingVolumeM3 : 0;
            double returned = 0;
            foreach (var receipt in cutter.Landings)
                if (receipt.Destination == cutter.soil) returned += receipt.VolumeM3;
            Assert.That(cutter.ExcavatedVolumeM3,
                Is.EqualTo(cutter.VolumeM3 + inFlight + cutter.DeliveredVolumeM3).Within(1e-5));
            Assert.That(cutter.soil.RecalculateRemovedVolumeM3(),
                Is.EqualTo(cutter.ExcavatedVolumeM3 - returned).Within(1e-5));
            Assert.That(cutter.CollectedVolumeM3 + cutter.DisplacedVolumeM3,
                Is.EqualTo(cutter.ExcavatedVolumeM3).Within(1e-8));
        }

        internal static IEnumerator Scoop(Zx200DigController dig, BucketSoilCutter cutter,
            double targetVolume = .08, System.Action observe = null)
        {
            // Close the initially open viewing pose in free air, before interacting with the soil.
            for (int i = 0; i < 140; i++)
            {
                dig.AdjustManualPose(0, 0, 0, 15 * Time.fixedDeltaTime);
                yield return new WaitForFixedUpdate();
                observe?.Invoke();
            }
            for (int i = 0; i < 300; i++)
            {
                Vector3 tip = cutter.TipWorldPosition;
                float surface = cutter.soil.Terrain.SampleHeight(tip) + cutter.soil.transform.position.y;
                if (tip.y <= surface + .025f) break;
                dig.AdjustManualPose(0, 7 * Time.fixedDeltaTime, 0, 0);
                yield return new WaitForFixedUpdate();
                observe?.Invoke();
            }
            // Arm retraction sweeps soil into the mouth; vertical entry is tested separately.
            for (int i = 0; i < 300 && cutter.VolumeM3 < targetVolume; i++)
            {
                dig.AdjustManualPose(0, 0, 10 * Time.fixedDeltaTime, 0);
                yield return new WaitForFixedUpdate();
                observe?.Invoke();
            }
        }
    }
}
