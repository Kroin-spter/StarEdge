using UnityEngine;

namespace UrdfUrpImporter
{
    /// <summary>
    /// Extends generic URDF import with optional robot-specific prefab configuration.
    /// </summary>
    public interface IUrdfPrefabPostprocessor
    {
        /// <summary>
        /// Reports whether this postprocessor handles the resolved robot layout.
        /// </summary>
        /// <param name="layout">Resolved paths and robot metadata.</param>
        /// <returns>True when this processor should run for the robot.</returns>
        bool CanProcess(UrdfAssetLayout layout);

        /// <summary>
        /// Configures the temporary imported hierarchy before it is saved as a prefab.
        /// </summary>
        /// <param name="robot">Temporary imported robot root.</param>
        /// <param name="layout">Resolved paths and robot metadata.</param>
        void Process(GameObject robot, UrdfAssetLayout layout);
    }
}
