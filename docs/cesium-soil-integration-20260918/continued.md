# Cesium surface integration follow-up

The work pad now receives a top-down capture of the actual loaded Cesium map. The same image is projected in world coordinates onto a second runtime Terrain layer, blended through the perimeter with a wider, slightly irregular transition. The central excavation soil retains its existing material and calibrated working height. The bottom plate remains solid but its grey renderer is hidden after successful capture.

This is a local visual replacement, not excavation of Cesium triangles or full reconstruction of the surrounding terrain. The planar support pad remains, and elevation discontinuities at its outer boundary may still be visible from low angles. Map imagery inherits the resolution and lighting of the available tiles. A missing or incomplete capture retains the previous appearance. Web has not been rebuilt or tested for this capture path.

The preparation lifecycle now clones the soil before sampling, uses the prepared heightmap as its reset and volume baseline, and restores clipping after sampling timeout. Sampling requires complete coverage and will not alter a soil region after excavation. The transition width is measured in world metres, including non-square Terrain dimensions. Sampled heights cannot go below the soil floor.

Validation artifacts will be stored beside this document. Main-scene checks cover actual imagery assignment, zero excavation volume after preparation, vertical entry, drive/brake/scoop/lift, loading the truck, backfill, and volume conservation. EditMode additionally checks that preparing soil preserves the source asset, rejects preparation after excavation, and restores the prepared surface on reset.

The existing streamed-mesh lifecycle error is not claimed fixed by this follow-up. It needs a separate ownership/lifetime change in the clipping implementation.
