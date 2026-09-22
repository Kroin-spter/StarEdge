using System;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UrdfUrpImporter.Tests
{
    public class Zx200PrefabSetupIntegrationTests
    {
        private const string PrefabPath =
            "Assets/Robotics/zx200_description/Prefabs/zx200.prefab";

        [Test]
        public void ConfigurePrefab_LeavesPrimaryLinksUsingAutomaticInertia()
        {
            ConfigurePrefab();

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Type inertialType = Type.GetType(
                "Unity.Robotics.UrdfImporter.UrdfInertial, Unity.Robotics.UrdfImporter");
            Assert.That(inertialType, Is.Not.Null);
            FieldInfo useUrdfData = inertialType.GetField("useUrdfData");
            Assert.That(useUrdfData, Is.Not.Null);

            foreach (string linkName in new[]
                     { "track_link", "body_link", "boom_link", "arm_link", "bucket_link" })
            {
                ArticulationBody body = FindBody(prefab, linkName);
                Component inertial = body.GetComponent(inertialType);
                Assert.That(inertial, Is.Not.Null, linkName);
                Assert.That((bool)useUrdfData.GetValue(inertial), Is.False, linkName);
                Assert.That(body.automaticCenterOfMass, Is.True, linkName);
                Assert.That(body.automaticInertiaTensor, Is.True, linkName);
            }
        }

        [Test]
        public void ConfigurePrefab_ConvertsBucketEndMarkerToFixedJoint()
        {
            ConfigurePrefab();

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            ArticulationBody bucketEnd = FindBody(prefab, "bucket_end_link");

            Assert.That(bucketEnd.jointType, Is.EqualTo(ArticulationJointType.FixedJoint));
            Type revoluteType = Type.GetType(
                "Unity.Robotics.UrdfImporter.UrdfJointRevolute, Unity.Robotics.UrdfImporter");
            Type fixedType = Type.GetType(
                "Unity.Robotics.UrdfImporter.UrdfJointFixed, Unity.Robotics.UrdfImporter");
            Assert.That(revoluteType, Is.Not.Null);
            Assert.That(fixedType, Is.Not.Null);
            Assert.That(bucketEnd.GetComponent(revoluteType), Is.Null);
            Assert.That(bucketEnd.GetComponent(fixedType), Is.Not.Null);
        }

        [Test]
        public void ConfigurePrefab_DisablesLegacyUrdfControllerThatOverwritesDrives()
        {
            ConfigurePrefab();

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Type legacyControllerType = Type.GetType(
                "Unity.Robotics.UrdfImporter.Control.Controller, Unity.Robotics.UrdfImporter");
            Assert.That(legacyControllerType, Is.Not.Null);
            var legacyController = prefab.GetComponent(legacyControllerType) as Behaviour;
            Assert.That(legacyController, Is.Not.Null);
            Assert.That(legacyController.enabled, Is.False);
        }

        [Test]
        public void ConfigurePrefab_AddsOneControllerWithFourJointReferences()
        {
            Type setupType = Type.GetType("Zx200.Editor.Zx200PrefabSetup, Zx200.Tools.Editor");
            Assert.That(setupType, Is.Not.Null);
            MethodInfo configure = setupType.GetMethod(
                "ConfigurePrefab", BindingFlags.Public | BindingFlags.Static);
            Assert.That(configure, Is.Not.Null);

            configure.Invoke(null, new object[] { PrefabPath });
            configure.Invoke(null, new object[] { PrefabPath });

            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Type controllerType = Type.GetType("Zx200DigController, ExcavatorControls");
            Component[] controllers = prefab.GetComponents(controllerType);
            Assert.That(controllers, Has.Length.EqualTo(1));
            Component controller = controllers[0];
            foreach (string fieldName in new[]
                     { "swingJoint", "boomJoint", "armJoint", "bucketJoint" })
            {
                FieldInfo field = controllerType.GetField(fieldName);
                Assert.That(field?.GetValue(controller), Is.Not.Null, fieldName);
            }

            Type keyboardType = Type.GetType(
                "Zx200KeyboardController, ExcavatorControls");
            Type trackType = Type.GetType("Zx200TrackDrive, ExcavatorControls");
            Assert.That(prefab.GetComponents(trackType), Has.Length.EqualTo(1));
            Type cameraType = Type.GetType(
                "Zx200CameraController, ExcavatorControls");
            Assert.That(prefab.GetComponents(keyboardType), Has.Length.EqualTo(1));
            Assert.That(prefab.GetComponents(cameraType), Has.Length.EqualTo(1));

            Type physicsType = Type.GetType(
                "Zx200FixedBasePhysics, ExcavatorControls");
            Assert.That(physicsType, Is.Not.Null);
            Component[] physicsComponents = prefab.GetComponents(physicsType);
            Assert.That(physicsComponents, Has.Length.EqualTo(1));
            Component physics = physicsComponents[0];
            AssertBody(physicsType, physics, "rootBody", 7000f, true, false);
            AssertBody(physicsType, physics, "swingBody", 8000f, true, false);
            AssertBody(physicsType, physics, "boomBody", 2000f, true, false);
            AssertBody(physicsType, physics, "armBody", 1200f, true, false);
            AssertBody(physicsType, physics, "bucketBody", 800f, true, false);

            Component keyboard = prefab.GetComponent(keyboardType);
            Assert.That((bool)keyboardType.GetField("fixedBase")?.GetValue(keyboard), Is.False);
            Assert.That(controllerType.GetField("stabilizeForAnimation"), Is.Null);
            foreach (Collider collider in prefab.GetComponentsInChildren<Collider>(true))
            {
                Assert.That(collider.enabled, Is.True, collider.name);
            }
        }

        private static void AssertBody(
            Type physicsType,
            Component physics,
            string fieldName,
            float expectedMass,
            bool expectedGravity,
            bool expectedImmovable)
        {
            var body = physicsType.GetField(fieldName)?.GetValue(physics) as ArticulationBody;
            Assert.That(body, Is.Not.Null, fieldName);
            Assert.That(body.immovable, Is.EqualTo(expectedImmovable), fieldName);
            Assert.That(body.useGravity, Is.EqualTo(expectedGravity), fieldName);
            if (expectedMass > 0f)
            {
                Assert.That(body.mass, Is.EqualTo(expectedMass), fieldName);
            }
        }

        private static void ConfigurePrefab()
        {
            Type setupType = Type.GetType("Zx200.Editor.Zx200PrefabSetup, Zx200.Tools.Editor");
            Assert.That(setupType, Is.Not.Null);
            MethodInfo configure = setupType.GetMethod(
                "ConfigurePrefab", BindingFlags.Public | BindingFlags.Static);
            Assert.That(configure, Is.Not.Null);
            configure.Invoke(null, new object[] { PrefabPath });
        }

        private static ArticulationBody FindBody(GameObject robot, string linkName)
        {
            foreach (ArticulationBody body in robot.GetComponentsInChildren<ArticulationBody>(true))
            {
                if (body.name == linkName) return body;
            }

            Assert.Fail($"ZX200 prefab is missing articulation link: {linkName}");
            return null;
        }

        [Test]
        public void AddToCurrentScene_InstantiatesConfiguredPrefab()
        {
            Scene testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,
                NewSceneMode.Single);
            try
            {
                Type setupType = Type.GetType(
                    "Zx200.Editor.Zx200PrefabSetup, Zx200.Tools.Editor");
                MethodInfo add = setupType?.GetMethod(
                    "AddToCurrentScene", BindingFlags.Public | BindingFlags.Static);
                Assert.That(add, Is.Not.Null);

                var instance = add.Invoke(null, null) as GameObject;

                Assert.That(instance, Is.Not.Null);
                Assert.That(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance),
                    Is.EqualTo(PrefabPath));
                Type controllerType = Type.GetType(
                    "Zx200DigController, ExcavatorControls");
                Component controller = instance.GetComponent(controllerType);
                Assert.That(controller, Is.Not.Null);
                Assert.That(controllerType.GetField("swingJoint")?.GetValue(controller),
                    Is.Not.Null);
            }
            finally
            {
                EditorSceneManager.CloseScene(testScene, true);
            }
        }
    }
}
