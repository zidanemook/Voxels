using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using Tuntenfisch.Generics;
using Tuntenfisch.Generics.Pool;
using Tuntenfisch.Voxels;
using Tuntenfisch.Voxels.CSG;
using Tuntenfisch.Voxels.DC;
using Tuntenfisch.Voxels.Materials;
using Tuntenfisch.Voxels.Volume;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.UI;

namespace Tuntenfisch.World
{
    [RequireComponent(typeof(VoxelConfig), typeof(VoxelVolume), typeof(DualContouring))]
    [RequireComponent(typeof(CSGUtility))]
    public class WorldManager : SingletonComponent<WorldManager>
    {
        public static VoxelConfig VoxelConfig => Instance.m_voxelConfig;
        public static VoxelVolume VoxelVolume => Instance.m_voxelVolume;
        public static DualContouring DualContouring => Instance.m_dualContouring;
        private float ViewDistanceSquared => m_lodDistancesSquared[m_lodDistancesSquared.Length - 1];

        [SerializeField]
        private Transform m_viewer;
        [SerializeField]
        private float m_updateInterval = 20.0f;
        [SerializeField]
        private GameObject m_chunkPrefab;
        [SerializeField]
        private int m_initialChunkPoolPopulation = 0;
        [SerializeField]
        private float[] m_lodDistances;
        
        // Saving
        [SerializeField]
        private  Slider m_saveProgressBar;
        [SerializeField]
        private  TextMeshProUGUI m_saveMessageText;
        
        private bool m_isSaving;
        public bool IsSaving
        {
            get { return m_isSaving; }
        }

        private VoxelConfig m_voxelConfig;
        private VoxelVolume m_voxelVolume;
        private DualContouring m_dualContouring;
        private CSGUtility m_csgUtility;
        private ObjectPool<Chunk> m_chunkPool;
        private Dictionary<int3, Chunk> m_chunks;
        private List<int3> m_chunksOutsideOfViewDistance;
        private Queue<(int3, float3, int)> m_chunksToProcess;
        private HashSet<int3> m_processedChunkCoordinates;
        private float3 m_chunkDimensions;

        //safety for digging
        private float m_underlimit = 0f;
        
        private HashSet<int3> m_chunkFileList;
        private HashSet<int3> m_chunkToExport;
        private string m_chunkDirectoryPath;
        public string ChunkDirectoryPath
        {
            get { return m_chunkDirectoryPath; }
            private set { m_chunkDirectoryPath = value; }
        }

        private bool isExporting = false;

        // We don't want to update the world every frame.
        private float3 m_lastViewerPosition;
        private float m_updateIntervalSquared;
        private float[] m_lodDistancesSquared;

        public int MaxLOD
        {
            get { return m_lodDistances.Length; }
        }

        private string m_worldName;
        
        
        private void Start()
        {
            Assert.IsFalse(m_chunkPrefab.activeSelf);

            m_voxelConfig = GetComponent<VoxelConfig>();
            m_voxelConfig.VoxelVolumeConfig.OnLateDirtied += ApplyVoxelVolumeConfig;
            m_voxelConfig.DualContouringConfig.OnLateDirtied += ApplyDualContouringConfig;
            m_voxelConfig.GenerationGraph.OnLateDirtied += ApplyGenerationGraph;

            m_voxelVolume = GetComponent<VoxelVolume>();
            m_dualContouring = GetComponent<DualContouring>();
            m_csgUtility = GetComponent<CSGUtility>();

            m_chunkPool = new ObjectPool<Chunk>(() => { return Instantiate(m_chunkPrefab, transform).GetComponent<Chunk>(); }, m_initialChunkPoolPopulation);
            m_chunks = new Dictionary<int3, Chunk>();
            m_chunkFileList = new HashSet<int3>();
            m_chunkToExport = new HashSet<int3>();
            m_chunksOutsideOfViewDistance = new List<int3>();
            m_chunksToProcess = new Queue<(int3, float3, int)>();
            m_processedChunkCoordinates = new HashSet<int3>();
            m_chunkDimensions = CalculateChunkDimensions();
            m_underlimit = (m_chunkDimensions.y/2) * -1;

            m_lastViewerPosition = m_viewer.position;
            m_updateIntervalSquared = math.pow(m_updateInterval, 2.0f);
            m_lodDistancesSquared = CalculateLodDistancesSquared();

            m_chunkDirectoryPath = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments), "My Games", "Voxel", "Chunk");
            
            //청크파일 저장된 목록
            LoadChunkFileList();
            
            UpdateWorld(m_viewer.position);
            
        }

        private void Update()
        {
            if (math.lengthsq((float3)m_viewer.position - m_lastViewerPosition) >= m_updateIntervalSquared)
            {
                m_lastViewerPosition = m_viewer.position;
                UpdateWorld(m_viewer.position);
            }
        }

        private void OnDestroy()
        {
            m_voxelConfig.VoxelVolumeConfig.OnLateDirtied -= ApplyVoxelVolumeConfig;
            m_voxelConfig.DualContouringConfig.OnLateDirtied -= ApplyDualContouringConfig;
            m_voxelConfig.GenerationGraph.OnLateDirtied -= ApplyGenerationGraph;
        }

        private void OnValidate() => ApplySettings();

        public void DrawCSGPrimitiveHologram(CSGPrimitiveType primitiveType, float3 position, float3 scale)
        {
            m_csgUtility.DrawCSGPrimitiveHologram(primitiveType, Matrix4x4.TRS(position, quaternion.identity, scale));
        }

        public void ApplyCSGOperation(GPUCSGOperator csgOperator, GPUCSGPrimitive csgPrimitive, MaterialIndex materialIndex, float3 position, float3 scale)
        {
            if (csgOperator.OperatorIndex == CSGOperatorIndex.Difference ||
                csgOperator.OperatorIndex == CSGOperatorIndex.SmoothDifference)
            {
                if (m_underlimit > (position.y - scale.y))
                    return;
            }
            if (isExporting)
            {
                Debug.Log("Export in progress. Operation skipped.");
                return;
            }


            try
            {
                const float scaleInflationFactor = 1.5f;

                Matrix4x4 worldToObjectMatrix = Matrix4x4.TRS(position, quaternion.identity, scale).inverse;

                // Inflate the scale a bit to ensure CSG operations near the boundary of chunks are processed by all nearby chunks.
                int3 minChunkCoordinate = CalculateChunkCoordinate(position - 0.5f * scaleInflationFactor * scale);
                int3 maxChunkCoordinate = CalculateChunkCoordinate(position + 0.5f * scaleInflationFactor * scale);

                for (int3 chunkCoordinate = minChunkCoordinate;
                     chunkCoordinate.z <= maxChunkCoordinate.z;
                     chunkCoordinate.z++)
                {
                    for (chunkCoordinate.y = minChunkCoordinate.y;
                         chunkCoordinate.y <= maxChunkCoordinate.y;
                         chunkCoordinate.y++)
                    {
                        for (chunkCoordinate.x = minChunkCoordinate.x;
                             chunkCoordinate.x <= maxChunkCoordinate.x;
                             chunkCoordinate.x++)
                        {
                            if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
                            {
                                chunk.ApplyCSGPrimitiveOperation(csgOperator, csgPrimitive, materialIndex,
                                    worldToObjectMatrix);
                                m_chunkToExport.Add(chunkCoordinate);

                            }
                        }
                    }
                }
            }
            finally
            {

            }

        }
        
        public async UniTask ExportChunksAfterCSGOperation()
        {
            if (isExporting)
            {
                Debug.Log("Export already in progress.");
                return;
            }
    
            try
            {
                isExporting = true;
                
                foreach (int3 chunkCoordinate in m_chunkToExport)
                {
                    if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
                    {
                        await chunk.ExportVolumeDataAsync(m_chunkFileList, m_chunkDirectoryPath);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"An error occurred while exporting chunks: {e.Message}");
            }
            finally
            {
                m_chunkToExport.Clear();
                isExporting = false;
            }

        }

        public bool GetMaterialFromRaycastHit(RaycastHit hit, out MaterialIndex materialIndex)
        {
            materialIndex = default;

            if (!(hit.collider is MeshCollider))
            {
                return false;
            }

            int3 chunkCoordinate = CalculateChunkCoordinate(hit.point);

            if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk) && chunk.GetMaterialFromRaycastHit(hit, out materialIndex))
            {
                return true;
            }

            return false;
        }

        private void UpdateWorld(float3 viewerPosition)
        {
            DestroyChunksOutsideViewDistanceAsync(viewerPosition);
            
            CreateChunksWithinViewDistance(viewerPosition);
            
            //Debug.Log($"Chunk Count : {m_chunks.Count}");
        }

        private async UniTask DestroyChunksOutsideViewDistanceAsync(float3 viewerPosition)
        {
            foreach (KeyValuePair<int3, Chunk> pair in m_chunks)
            {
                float viewerToChunkDistanceSquared = math.lengthsq((float3)pair.Value.transform.position - viewerPosition);

                if (viewerToChunkDistanceSquared > ViewDistanceSquared)
                {
                    m_chunksOutsideOfViewDistance.Add(pair.Key);

                }
            }

            foreach (int3 chunkCoordinate in m_chunksOutsideOfViewDistance)
            {
                m_chunkPool.Release(m_chunks[chunkCoordinate]);
                m_chunks.Remove(chunkCoordinate);
            }
            m_chunksOutsideOfViewDistance.Clear();
        }
        
        private void CreateChunksWithinViewDistance(float3 viewerPosition)
        {
            int3 chunkCoordinate = CalculateChunkCoordinate(viewerPosition);
            
            float3 chunkPosition = chunkCoordinate * m_chunkDimensions;
            float viewerToChunkDistanceSquared = math.lengthsq(chunkPosition - viewerPosition);
            int lod = CalculateChunkLod(viewerToChunkDistanceSquared);
            
            m_processedChunkCoordinates.Clear();
            m_chunksToProcess.Clear();

            EnqueueChunk(chunkCoordinate, viewerPosition);

            while (m_chunksToProcess.Count > 0)
            {
                (chunkCoordinate, chunkPosition, lod) = m_chunksToProcess.Dequeue();

                if (m_chunks.TryGetValue(chunkCoordinate, out Chunk chunk))
                {
                    //chunk.CreateVoxelVolumeBuffer();
                    //chunk.RegenerateVoxelVolume();
                    //chunk.RegenerateMesh(lod);
                    chunk.OnLODChanged(lod);
                }
                else
                {

                    //m_chunkFileList 에 있으면 로딩 없으면 생성
                    if (m_chunkFileList.Contains(chunkCoordinate))
                    {
                        chunk = m_chunkPool.Acquire();
                        chunk.transform.position = chunkPosition;
                        chunk.ImportVoxelVolumeFromFile();
                        if(lod != 0)
                            chunk.TargetLOD = lod;
                        //ImportVolumeDataAsync 에서 chunk.RegenerateMesh(lod); 호출함
                        m_chunks[chunkCoordinate] = chunk;
                    }
                    else
                    {
                        // Create new chunk.
                        chunk = m_chunkPool.Acquire();
                        chunk.transform.position = chunkPosition;
                        chunk.RegenerateVoxelVolume();
                        chunk.RegenerateMesh(lod);
                        m_chunks[chunkCoordinate] = chunk;
                    }
                }

                EnqueueChunk(chunkCoordinate + new int3(1, 0, 0), viewerPosition);
                EnqueueChunk(chunkCoordinate - new int3(1, 0, 0), viewerPosition);
                EnqueueChunk(chunkCoordinate + new int3(0, 0, 1), viewerPosition);
                EnqueueChunk(chunkCoordinate - new int3(0, 0, 1), viewerPosition);
            }
        }
        
        private void EnqueueChunk(int3 neighbourChunkCoordinate, float3 viewerPosition)
        {
            if (!m_processedChunkCoordinates.Contains(neighbourChunkCoordinate))
            {
                float3 neighbourChunkPosition = neighbourChunkCoordinate * m_chunkDimensions;
                float viewerToNeighbourChunkDistanceSquared = math.lengthsq(neighbourChunkPosition - viewerPosition);

                if (viewerToNeighbourChunkDistanceSquared <= ViewDistanceSquared)
                {
                    m_chunksToProcess.Enqueue((neighbourChunkCoordinate, neighbourChunkPosition, CalculateChunkLod(viewerToNeighbourChunkDistanceSquared)));
                }
            }
            m_processedChunkCoordinates.Add(neighbourChunkCoordinate);
        }

        private float3 CalculateChunkDimensions()
        {
            const int voxelOverlap = 1;

            float inflationFactorX = 1.0f + (float)voxelOverlap / (VoxelConfig.VoxelVolumeConfig.NumberOfCellsAlongX - voxelOverlap);
            float inflationFactorY = 1.0f + (float)voxelOverlap / (VoxelConfig.VoxelVolumeConfig.NumberOfCellsAlongY - voxelOverlap);
            float inflationFactorZ = 1.0f + (float)voxelOverlap / (VoxelConfig.VoxelVolumeConfig.NumberOfCellsAlongZ - voxelOverlap);
            
            float3 inflationFactors = new float3(inflationFactorX, inflationFactorY, inflationFactorZ);
            
            //float inflationFactor = 1.0f + (float)voxelOverlap / (VoxelConfig.VoxelVolumeConfig.NumberOfCellsAlongAxis - voxelOverlap);

            return VoxelConfig.VoxelVolumeConfig.VoxelVolumeDimensions / inflationFactors;
        }

        public int3 CalculateChunkCoordinate(float3 position) => new int3((int)math.round(position.x / m_chunkDimensions.x), 0, (int)math.round(position.z / m_chunkDimensions.z));

        private float[] CalculateLodDistancesSquared()
        {
            float[] lodDistancesSquared = new float[m_lodDistances.Length];

            for (int index = 0; index < lodDistancesSquared.Length; index++)
            {
                lodDistancesSquared[index] = math.pow(m_lodDistances[index], 2.0f);
            }

            return lodDistancesSquared;
        }

        private int CalculateChunkLod(float viewerToChunkDistanceSquared)
        {
            int lod = Array.BinarySearch(m_lodDistancesSquared, viewerToChunkDistanceSquared);

            if (lod < 0)
            {
                lod = ~lod;
            }

            if (lod == m_lodDistancesSquared.Length)
            {
                lod = 0;
            }

            return lod;
        }

        private void ApplySettings()
        {
            if (!Application.isPlaying || !gameObject.activeSelf || m_voxelConfig == null)
            {
                return;
            }

            m_updateIntervalSquared = math.pow(m_updateInterval, 2.0f);
            m_lodDistancesSquared = CalculateLodDistancesSquared();
            m_chunkDimensions = CalculateChunkDimensions();

            foreach (Chunk chunk in m_chunks.Values)
            {
                m_chunkPool.Release(chunk);
            }
            m_chunks.Clear();

            UpdateWorld(m_viewer.position);
        }

        private void ApplyVoxelVolumeConfig() => ApplySettings();

        private void ApplyDualContouringConfig()
        {
            foreach (Chunk chunk in m_chunks.Values)
            {
                chunk.RegenerateMesh();
            }
        }

        private void ApplyGenerationGraph()
        {
            foreach (Chunk chunk in m_chunks.Values)
            {
                chunk.RegenerateVoxelVolume();
                chunk.RegenerateMesh();
            }
        }

        private void LoadChunkFileList()
        {
            if (Directory.Exists(m_chunkDirectoryPath))
            {
                // "chunk_"로 시작하는 모든 .dat 파일을 가져옴
                string[] files = Directory.GetFiles(m_chunkDirectoryPath, "chunk_*.dat");
                foreach (string file in files)
                {
                    // 파일 이름에서 좌표를 추출
                    int3 chunkCoordinate = ExtractCoordinatesFromFileName(Path.GetFileNameWithoutExtension(file));
                    m_chunkFileList.Add(chunkCoordinate);
                }
            }
            else
            {
                Debug.LogWarning($"Directory does not exist: {m_chunkDirectoryPath}");
            }
        }
        
        private int3 ExtractCoordinatesFromFileName(string fileName)
        {
            // 파일 이름에서 숫자를 추출하기 위한 정규식
            Regex regex = new Regex(@"chunk_(-?\d+)_(-?\d+)_(-?\d+)");
            Match match = regex.Match(fileName);
            if (match.Success)
            {
                int x = int.Parse(match.Groups[1].Value);
                int y = int.Parse(match.Groups[2].Value);
                int z = int.Parse(match.Groups[3].Value);
                return new int3(x, y, z);
            }
            else
            {
                Debug.LogError($"Invalid file name format: {fileName}");
                return int3.zero; // 유효하지 않은 경우, 기본값 반환
            }
        }
        
        private void UpdateSaveProgress(int chunksProcessed, int totalChunks)
        {
            if (m_saveProgressBar != null)
            {
                float progress = (float)chunksProcessed / totalChunks;
                m_saveProgressBar.value = progress * 100; // 0에서 100 사이의 값으로 변환
            }
        }
        
        IEnumerator ShowSaveMessageCoroutine(float displayTime)
        {
            m_saveMessageText.gameObject.SetActive(true);
            yield return new WaitForSeconds(displayTime);
            m_saveMessageText.gameObject.SetActive(false);
        }
    }
}