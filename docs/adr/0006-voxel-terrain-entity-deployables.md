# Voxel terrain, with player-placed objects as entities rather than voxels

Terrain is 16×16 voxel chunks with vertical sections, greedy meshed on worker threads. Generators, lights, and cables are deployable entities, not voxels, so v1 needs no voxel editing. The data model keeps voxel edits possible later through a per-chunk edit log. Buildings and villages are prefab structures stamped during generation.
