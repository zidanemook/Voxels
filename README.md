# Voxels
 Derived from https://github.com/Tuntenfisch/Voxels

Digging limit

Add InGameMenu

After editing Chunks in game it will be exported

Chunk size changed and some code changed for it


map Generation graph used for layered volume terrain Snow, Rock, Grass, Dirt, Rock

I modified it to generate meshes for each LOD (Level of Detail) stage when creating a mesh.

After creating the mesh, I modified it to delete the volume data.

Chunks are arranged one by one along the y-axis (laid out in two dimensions along the x and z axes). I'm contemplating whether to arrange multiple along the y-axis as well.






