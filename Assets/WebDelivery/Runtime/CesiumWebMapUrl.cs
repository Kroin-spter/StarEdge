using System;
using CesiumForUnity;
using UnityEngine;

/// <summary>Resolve the bundled construction map relative to the Web page.</summary>
[DefaultExecutionOrder(-10000)]
public sealed class CesiumWebMapUrl : MonoBehaviour
{
    private void Awake()
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        var tileset = GetComponent<Cesium3DTileset>();
        if (tileset != null)
            tileset.url = new Uri(new Uri(Application.absoluteURL),
                "maps/osgb_small_005_004/tileset.json").AbsoluteUri;
#endif
    }
}
