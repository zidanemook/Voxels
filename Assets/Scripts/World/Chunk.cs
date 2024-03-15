using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using Cysharp.Threading.Tasks;
using Tuntenfisch.Generics;
using Tuntenfisch.Generics.Pool;
using Tuntenfisch.Voxels.CSG;
using Tuntenfisch.Voxels.DC;
using Tuntenfisch.Voxels.Materials;
using Tuntenfisch.Voxels.Volume;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace Tuntenfisch.World
{
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
    public class Chunk : MonoBehaviour, IPoolable
    {
        private int m_currentLOD;
        private int m_targetLOD;

        public int TargetLOD
        {
            set { m_targetLOD = value; }
        }
        private int[] m_vertexCount;
        private int[] m_triangleCount;

        private Mesh[] m_mesh;
        private MeshFilter m_meshFilter;
        private MeshRenderer m_meshRenderer;
        private MeshCollider m_meshCollider;
        private OnMeshGenerated m_onMeshGeneratedDelegate;

        private ComputeBuffer m_voxelVolumeBuffer;
        private IRequest m_request;
        private JobHandle m_bakeJobHandle;
        private List<GPUVoxelVolumeCSGOperation> m_voxelVolumeCSGOperations;
        private ChunkFlags m_flags;

        private void Awake()
        {
            m_vertexCount = new int[WorldManager.Instance.MaxLOD];
            m_triangleCount = new int[WorldManager.Instance.MaxLOD];
            
            WorldManager.VoxelConfig.MaterialConfig.OnDirtied += ApplyRenderMaterial;
            m_mesh = new Mesh[WorldManager.Instance.MaxLOD];
            InitializeMeshComponents();
            ApplyRenderMaterial();
            m_voxelVolumeCSGOperations = new List<GPUVoxelVolumeCSGOperation>();
        }
        
        private void Update()
        {
            if (m_flags == 0)
            {
                return;
            }

            if ((m_flags & ChunkFlags.CreateVoxelVolumeBufferRequest) == ChunkFlags.CreateVoxelVolumeBufferRequest)
            {
                m_flags &= ~ChunkFlags.CreateVoxelVolumeBufferRequest;
                CreateBuffers();
            }
            
            if ((m_flags & ChunkFlags.VoxelVolumeRegenerationRequested) == ChunkFlags.VoxelVolumeRegenerationRequested)
            {
                m_flags &= ~ChunkFlags.VoxelVolumeRegenerationRequested;
                WorldManager.VoxelVolume.GenerateVoxelVolume(m_voxelVolumeBuffer, transform.position);
            }

            if ((m_flags & ChunkFlags.VoxelVolumeImportFromFileRequested) == ChunkFlags.VoxelVolumeImportFromFileRequested)
            {
                m_flags &= ~ChunkFlags.VoxelVolumeImportFromFileRequested;
                ImportVolumeDataAsync(WorldManager.Instance.ChunkDirectoryPath);
            }

            if ((m_flags & ChunkFlags.CSGOperationPerformed) == ChunkFlags.CSGOperationPerformed)
            {
                m_flags &= ~ChunkFlags.CSGOperationPerformed;
                WorldManager.VoxelVolume.ApplyVoxelVolumeCSGOperations(m_voxelVolumeBuffer, transform.position, m_voxelVolumeCSGOperations);
                m_voxelVolumeCSGOperations.Clear();
            }

            if ((m_flags & ChunkFlags.MeshRegenerationRequested) == ChunkFlags.MeshRegenerationRequested
                && (m_flags & ChunkFlags.IsBakingMesh) != ChunkFlags.IsBakingMesh 
                && m_request == null)
            {
                m_flags &= ~ChunkFlags.MeshRegenerationRequested;
                m_request = WorldManager.DualContouring.RequestMeshAsync
                (
                    m_voxelVolumeBuffer,
                    //m_currentLOD,
                    //m_targetLOD,
                    WorldManager.Instance.MaxLOD,
                    m_vertexCount,
                    m_triangleCount,
                    transform.position,
                    m_onMeshGeneratedDelegate
                );
            }

            if ((m_flags & ChunkFlags.IsBakingMesh) == ChunkFlags.IsBakingMesh && m_bakeJobHandle.IsCompleted)
            {
                m_flags &= ~ChunkFlags.IsBakingMesh;
                m_meshCollider.sharedMesh = null;
                m_meshCollider.sharedMesh = m_mesh[m_currentLOD];
            }
        }

        private void OnDestroy()
        {
            
            WorldManager.VoxelConfig.MaterialConfig.OnDirtied -= ApplyRenderMaterial;
            ReleaseBuffers();
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(transform.position, WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions);
        }

        public void OnAcquire()
        {
            m_currentLOD = m_targetLOD = -1;
            // for (int lod = 0; lod < WorldManager.Instance.MaxLOD; ++lod)
            // {
            //     m_vertexCount[lod] = -1;
            //     m_triangleCount[lod] = -1;
            // }
            
            CreateBuffers();
            gameObject.SetActive(true);
        }

        public void OnRelease()
        {
            m_meshFilter.sharedMesh = null;
            m_meshCollider.sharedMesh = null;
            m_request?.Cancel();
            m_request = null;
            m_voxelVolumeCSGOperations.Clear();
            m_flags = 0;
            gameObject.SetActive(false);
        }

        public bool GetMaterialFromRaycastHit(RaycastHit hit, out MaterialIndex materialIndex)
        {
            materialIndex = default;

            if (hit.triangleIndex >= m_triangleCount[m_currentLOD])
            {
                return false;
            }

            using Mesh.MeshDataArray meshDataArray = Mesh.AcquireReadOnlyMeshData(m_mesh);
            {
                Mesh.MeshData meshData = meshDataArray[0];
                NativeArray<int> triangles = meshData.GetIndexData<int>();
                NativeArray<GPUVertex> vertices = meshData.GetVertexData<GPUVertex>();

                float shortestDistanceSquared = float.MaxValue;

                for (int index = 0; index < 3; index++)
                {
                    GPUVertex vertex = vertices[triangles[3 * hit.triangleIndex + index]];
                    float distanceSquared = math.lengthsq(hit.transform.TransformPoint(vertex.Position) - hit.point);

                    if (distanceSquared < shortestDistanceSquared)
                    {
                        shortestDistanceSquared = distanceSquared;
                        materialIndex = vertex.MaterialIndex;
                    }
                }
            }

            return true;
        }

        private void CreateBuffers()
        {
            if (m_voxelVolumeBuffer?.count != WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount)
            {
                m_voxelVolumeBuffer?.Release();
                m_voxelVolumeBuffer = new ComputeBuffer(WorldManager.VoxelConfig.VoxelVolumeConfig.VoxelCount, 2 * sizeof(uint));
            }
        }

        private void ReleaseBuffers()
        {
            if (m_voxelVolumeBuffer != null)
            {
                m_voxelVolumeBuffer.Release();
                m_voxelVolumeBuffer = null;
            }
        }

        public void CreateVoxelVolumeBuffer() => m_flags |= ChunkFlags.CreateVoxelVolumeBufferRequest;
        public void RegenerateVoxelVolume() => m_flags |= ChunkFlags.VoxelVolumeRegenerationRequested;
        public void ImportVoxelVolumeFromFile() => m_flags |= ChunkFlags.VoxelVolumeImportFromFileRequested;

        public void RegenerateMesh(int lod = -1)
        {
            if (lod != -1 && lod != m_targetLOD)
            {
                m_targetLOD = lod;
                //m_flags |= ChunkFlags.MeshRegenerationRequested;
            }
            else if (lod == -1)
            {
                m_flags |= ChunkFlags.MeshRegenerationRequested;
            }
        }

        public void ApplyCSGPrimitiveOperation(GPUCSGOperator csgOperator, GPUCSGPrimitive csgPrimitive, MaterialIndex materialIndex, Matrix4x4 worldToObjectMatrix)
        {
            m_voxelVolumeCSGOperations.Add(new GPUVoxelVolumeCSGOperation(csgOperator, csgPrimitive, materialIndex, worldToObjectMatrix));
            m_flags |= ChunkFlags.CSGOperationPerformed | ChunkFlags.MeshRegenerationRequested;
        }

        private void OnMeshGenerated(NativeArray<GPUVertex>[] vertices, int[] vertexCount, int vertexStartIndex, NativeArray<int>[] triangles, int[] triangleCount, int triangleStartIndex)
        {
            m_request = null;
            m_currentLOD = m_targetLOD;
            m_vertexCount = vertexCount;
            m_triangleCount = triangleCount;

            if (vertexCount[m_currentLOD] == 0 || triangleCount[m_currentLOD] == 0)
            {
                m_meshFilter.sharedMesh = null;
                m_meshCollider.sharedMesh = null;

                return;
            }

            m_mesh[m_currentLOD].SetVertexBufferParams(vertexCount[m_currentLOD], GPUVertex.Attributes);
            m_mesh[m_currentLOD].SetIndexBufferParams(triangleCount[m_currentLOD], IndexFormat.UInt32);
#if !UNITY_EDITOR
            MeshUpdateFlags flags = MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontResetBoneBounds | MeshUpdateFlags.DontValidateIndices;
            m_mesh.SetVertexBufferData(vertices, vertexStartIndex, 0, vertexCount, 0, flags);
            m_mesh.SetIndexBufferData(triangles, triangleStartIndex, 0, triangleCount, flags);
            m_mesh.SetSubMesh(0, new SubMeshDescriptor(0, triangleCount), flags);
            m_mesh.RecalculateBounds(flags);
#else
            try
            {
                m_mesh[m_currentLOD].SetVertexBufferData(vertices[m_currentLOD], vertexStartIndex, 0, vertexCount[m_currentLOD]);
                m_mesh[m_currentLOD].SetIndexBufferData(triangles[m_currentLOD], triangleStartIndex, 0, triangleCount[m_currentLOD]);
                m_mesh[m_currentLOD].SetSubMesh(0, new SubMeshDescriptor(0, triangleCount[m_currentLOD]));
                m_mesh[m_currentLOD].RecalculateBounds(MeshUpdateFlags.DontValidateIndices);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
            
#endif
            
        }

        private void OnLODChanged()
        {
            m_meshFilter.sharedMesh = null;
            m_meshFilter.sharedMesh = m_mesh[m_currentLOD];

            m_bakeJobHandle = new BakeJob(m_mesh[m_currentLOD].GetInstanceID()).Schedule();
            m_flags |= ChunkFlags.IsBakingMesh;
            
            if(m_targetLOD != 0)
                ReleaseBuffers();
        }

        private void InitializeMeshComponents()
        {
            for (int lod = 0; lod < WorldManager.Instance.MaxLOD; ++lod)
            {
                m_mesh[lod] = new Mesh();
                m_mesh[lod].MarkDynamic();
            }
                
            m_meshFilter = GetComponent<MeshFilter>();
            m_meshRenderer = GetComponent<MeshRenderer>();
            m_meshCollider = GetComponent<MeshCollider>();
            m_onMeshGeneratedDelegate = OnMeshGenerated;
        }

        private void ApplyRenderMaterial() => m_meshRenderer.material = WorldManager.VoxelConfig.MaterialConfig.RenderMaterial;
        
        [Flags]
        private enum ChunkFlags
        {
            CreateVoxelVolumeBufferRequest = 1,
            VoxelVolumeRegenerationRequested = 2,
            CSGOperationPerformed = 4,
            MeshRegenerationRequested = 8,
            IsBakingMesh = 16,
            VoxelVolumeImportFromFileRequested = 32
        }
        
        public async UniTask ExportVolumeDataAsync(HashSet<int3> chunkFileList, string DirectoryPath)
        {
            //Debug.Log("ExportVolumeDataAsync");
            
            int3 chunkCoordinate = WorldManager.Instance.CalculateChunkCoordinate(transform.position);
            
            string fileName = $"chunk_{chunkCoordinate.x}_{chunkCoordinate.y}_{chunkCoordinate.z}.dat";
            System.IO.Directory.CreateDirectory(DirectoryPath);

            string filePath = Path.Combine(DirectoryPath, fileName);
            
            var request = AsyncGPUReadback.Request(m_voxelVolumeBuffer);
            await UniTask.WaitUntil(() => request.done);

            if (request.hasError)
            {
                Debug.LogError("GPU readback error.");
                return;
            }

            
            uint[] bufferDataCopy = request.GetData<uint>().ToArray();
            try
            {
                await UniTask.Run(() =>
                {
                    using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    using (var writer = new BinaryWriter(fs))
                    {
                        foreach (uint item in bufferDataCopy)
                        {
                            writer.Write(item);
                        }
                    }
                }, cancellationToken: this.GetCancellationTokenOnDestroy());
            }
            catch (Exception e)
            {
                Debug.LogError($"Failed to write file: {e}");
            }

            chunkFileList.Add(chunkCoordinate);

        }

        public async UniTask<bool> ImportVolumeDataAsync(string DirectoryPath)
        {
            int3 chunkCoordinate = WorldManager.Instance.CalculateChunkCoordinate(transform.position);
            
            string fileName = $"chunk_{chunkCoordinate.x}_{chunkCoordinate.y}_{chunkCoordinate.z}.dat";
            string filePath = Path.Combine(DirectoryPath, fileName);
            
            if (!File.Exists(filePath))
            {
                Debug.LogWarning($"Chunk data file not found: {filePath}");
                return false;
            }
            
            byte[] fileData = await File.ReadAllBytesAsync(filePath);
            
            int numUint = fileData.Length / sizeof(uint);
            uint[] volumeData = new uint[numUint];
            Buffer.BlockCopy(fileData, 0, volumeData, 0, fileData.Length);
            
            m_voxelVolumeBuffer.SetData(volumeData);
    
            RegenerateMesh(m_targetLOD);
            return true;
        }
    }
    
   
}