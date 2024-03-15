using Cysharp.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using Tuntenfisch.Extensions;
using Tuntenfisch.Generics;
using Tuntenfisch.Generics.Pool;
using Tuntenfisch.World;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Tuntenfisch.Voxels.DC
{
    [RequireComponent(typeof(VoxelConfig))]
    public class DualContouring : MonoBehaviour
    {
        private event Action OnDestroyed;

        [Range(1, 16)]
        [SerializeField]
        private int m_numberOfWorkers = 4;
        [Min(0)]
        [SerializeField]
        private int m_initialTaskPoolPopulation = 0;
        [Range(1.0f, 2.0f)]
        [SerializeField]
        private float m_readbackInflationFactor = 1.25f;

        private VoxelConfig m_voxelConfig;
        private Queue<Worker.Task> m_tasks;
        private Stack<Worker> m_availableWorkers;
        private ObjectPool<Worker.Task> m_taskPool;
        

        private void Awake()
        {
            
            m_voxelConfig = GetComponent<VoxelConfig>();
            m_tasks = new Queue<Worker.Task>();
            m_availableWorkers = new Stack<Worker>(Enumerable.Range(0, m_numberOfWorkers).Select(index => new Worker(this)));
            m_taskPool = new ObjectPool<Worker.Task>(() => { return new Worker.Task(); }, m_initialTaskPoolPopulation);
        }

        private void LateUpdate()
        {
            while (m_tasks.Count > 0 && m_availableWorkers.Count > 0)
            {
                DispatchWorker(m_tasks.Dequeue());
            }
        }

        private void OnDestroy() => OnDestroyed?.Invoke();

        private void OnValidate()
        {
            if (m_availableWorkers == null)
            {
                return;
            }

            foreach (Worker worker in m_availableWorkers)
            {
                worker.Dispose();
            }
            m_availableWorkers = new Stack<Worker>(Enumerable.Range(0, m_numberOfWorkers).Select(index => new Worker(this)));
        }

        public IRequest RequestMeshAsync
        (
            ComputeBuffer voxelVolumeBuffer,
            //int currentLOD,
            //int targetLOD,
            int maxLOD,
            int[] currentVertexCount,
            int[] currentTriangleCount,
            float3 worldPosition,
            OnMeshGenerated callback
        )
        {
            Worker.Task task = m_taskPool.Acquire();
            task.VoxelVolumeBuffer = voxelVolumeBuffer ?? throw new ArgumentNullException(nameof(voxelVolumeBuffer));
            //task.CurrentLOD = currentLOD;
            //task.TargetLOD = targetLOD;
            task.MaxLOD = maxLOD;
            task.CurrentVertexCount = currentVertexCount;
            task.CurrentTriangleCount = currentTriangleCount;
            task.VoxelVolumeToWorldSpaceOffset = worldPosition;
            task.Callback = callback ?? throw new ArgumentNullException(nameof(callback));

            // If a worker is available, directly dispatch the task.
            if (m_availableWorkers.Count > 0)
            {
                DispatchWorker(task);
            }
            else
            {
                m_tasks.Enqueue(task);
            }

            return task;
        }

        private void DispatchWorker(Worker.Task task)
        {
            if (task.Canceled)
            {
                m_taskPool.Release(task);

                return;
            }

           DispatchWorkerUniTask(task).Forget();
        }

        private async UniTaskVoid DispatchWorkerUniTask(Worker.Task task)
        {
            Worker worker = m_availableWorkers.Pop();

            worker.GenerateMeshAsync(task);

            do
            {
                await UniTask.NextFrame(this.GetCancellationTokenOnDestroy());
            }
            while (worker.Process() == Worker.Status.WaitingForGPUReadback);

            // Only call the callback if the task hasn't been canceled.
            if (!task.Canceled)
            {
                //Chunk.cs private void OnMeshGenerated
                task.Callback(worker.GeneratedVertices, worker.VertexCount, 0, worker.Triangles, worker.TriangleCount, 2);
            }
            m_taskPool.Release(task);

            if (m_availableWorkers.Count < m_numberOfWorkers)
            {
                m_availableWorkers.Push(worker);
            }
            else
            {
                worker.Dispose();
            }
        }

        private class Worker : IDisposable
        {
            public int[] VertexCount { get; private set; }
            public int[] TriangleCount { get; private set; }
            public NativeArray<GPUVertex>[] GeneratedVertices => m_generatedVertices;
            // In addition to the triangles, this native array also reads back the number of triangles and the number of vertices generated, i.e.
            // two additional integers.
            public NativeArray<int>[] Triangles => m_generatedTriangles;

            private DualContouring m_parent;

            private NativeArray<GPUVertex>[] m_generatedVertices;
            private NativeArray<int>[] m_generatedTriangles;

            // "counter" 타입의 컴퓨트 버퍼로 삼각형 버퍼를 선언할 수 없기 때문에, 생성된 삼각형의 수를 추적하기 위해 이 버퍼를 사용합니다.
            private AsyncComputeBuffer[] m_cellVertexInfoLookupTableBuffer;
            private AsyncComputeBuffer[] m_generatedVerticesBuffer0;
            private AsyncComputeBuffer[] m_generatedVerticesBuffer1;
            private AsyncComputeBuffer[] m_generatedTrianglesBuffer;
            private bool[] m_startedReadbackinProcess;

            public Worker(DualContouring parent)
            {
                m_parent = parent;
                m_parent.m_voxelConfig.VoxelVolumeConfig.OnDirtied += CreateBuffers;
                m_parent.OnDestroyed += Dispose;
                
                m_startedReadbackinProcess = new bool[WorldManager.Instance.MaxLOD];
                m_generatedVertices = new NativeArray<GPUVertex>[WorldManager.Instance.MaxLOD];
                m_generatedTriangles = new NativeArray<int>[WorldManager.Instance.MaxLOD];
                m_cellVertexInfoLookupTableBuffer = new AsyncComputeBuffer[WorldManager.Instance.MaxLOD];
                m_generatedVerticesBuffer0 =  new AsyncComputeBuffer[WorldManager.Instance.MaxLOD];
                m_generatedVerticesBuffer1 =  new AsyncComputeBuffer[WorldManager.Instance.MaxLOD];
                m_generatedTrianglesBuffer =  new AsyncComputeBuffer[WorldManager.Instance.MaxLOD];

                VertexCount = new int[WorldManager.Instance.MaxLOD];
                TriangleCount = new int[WorldManager.Instance.MaxLOD];
                
                CreateBuffers();
            }

            public void Dispose()
            {
                ReleaseBuffers();
                m_parent.m_voxelConfig.VoxelVolumeConfig.OnDirtied -= CreateBuffers;
                m_parent.OnDestroyed -= Dispose;
                m_parent = null;
            }

            public Status Process()
            {
                for (int lod = 0; lod < WorldManager.Instance.MaxLOD; ++lod)
                {
                    if (m_generatedVerticesBuffer0[lod].IsDataAvailable() && m_generatedTrianglesBuffer[lod].IsDataAvailable())
                    {
                        if (true == m_startedReadbackinProcess[lod])
                        {
                            continue;
                        }
                        int requestedVertexCount = m_generatedVerticesBuffer0[lod].EndReadback();
                        int requestedTriangleCount = m_generatedTrianglesBuffer[lod].EndReadback();

                        VertexCount[lod] = m_generatedTriangles[lod][0];
                        TriangleCount[lod] = 3 * m_generatedTriangles[lod][1];

                        if (requestedVertexCount < VertexCount[lod] || requestedTriangleCount < TriangleCount[lod] || m_generatedVerticesBuffer0[lod].HasError || m_generatedTrianglesBuffer[lod].HasError)
                        {
                            if (Debug.isDebugBuild && (m_generatedVerticesBuffer0[lod].HasError || m_generatedTrianglesBuffer[lod].HasError))
                            {
                                Debug.LogWarning("GPU readback error detected.");
                            }
                            // If we retrieved too few vertices/triangles, we need to start another readback to retrieve the correct count.
                            m_generatedVerticesBuffer0[lod].StartReadbackNonAlloc(ref m_generatedVertices[lod], VertexCount[lod]);
                            m_generatedTrianglesBuffer[lod].StartReadbackNonAlloc(ref m_generatedTriangles[lod], TriangleCount[lod] + 2);
                            m_startedReadbackinProcess[lod] = true;
                        }
                    }
                    else
                    {
                        return Status.WaitingForGPUReadback;        
                    }
                }

                for (int lod = 0; lod < WorldManager.Instance.MaxLOD; ++lod)
                {
                    if (false == m_startedReadbackinProcess[lod] || false == m_generatedVerticesBuffer0[lod].IsDataAvailable() || false == m_generatedTrianglesBuffer[lod].IsDataAvailable())
                    {
                        return Status.WaitingForGPUReadback; 
                    }
                    else
                    {
                        if(m_generatedVerticesBuffer0[lod].ReadbackInProgress)
                            m_generatedVerticesBuffer0[lod].EndReadback();
                        
                        if(m_generatedTrianglesBuffer[lod].ReadbackInProgress)
                            m_generatedTrianglesBuffer[lod].EndReadback();
                    }
                }

                for (int lod = 0; lod < WorldManager.Instance.MaxLOD; ++lod)
                {
                    m_startedReadbackinProcess[lod] = false;
                }

                return Status.Done;
            }

            public void GenerateMeshAsync(Task task)
            {
                int nextlod = 0;
                for (int lod = 0; lod < WorldManager.Instance.MaxLOD; ++lod)
                {
                    nextlod = lod + 1;

                    m_cellVertexInfoLookupTableBuffer[lod].SetCounterValue(0);
                    m_generatedVerticesBuffer0[lod].SetCounterValue(0);

                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetVector(ComputeShaderProperties.VoxelVolumeToWorldSpaceOffset, (Vector3)task.VoxelVolumeToWorldSpaceOffset);
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetInt(ComputeShaderProperties.CellStride, 1);

                    // First we generate the inner cell vertices, i.e. all vertices which cells do not reside on the surface of the voxel volume of this chunk.
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(0,
                        ComputeShaderProperties.VoxelVolume,
                        task.VoxelVolumeBuffer);
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(0,
                        ComputeShaderProperties.CellVertexInfoLookupTable, m_cellVertexInfoLookupTableBuffer[lod]);
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(0,
                        ComputeShaderProperties.GeneratedVertices0, m_generatedVerticesBuffer0[lod]);
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.Dispatch(0,
                        m_parent.m_voxelConfig.VoxelVolumeConfig.NumberOfCells - 2);

                    // 이 청크의 이전에 생성된 꼭짓점들에 대해 원하는 LOD를 생성합니다.
                    // LOD를 처리하는 가장 안전한 방법은 먼저 최고 LOD에서 메쉬 꼭짓점을 생성한 다음,
                    // 그 꼭짓점들을 병합하여 낮은 LOD를 생성하는 것입니다.
                    // GPU의 병렬성을 활용하기 위해, 우리는 이를 반복적으로 수행합니다, 이는 병렬 축소 작업이 작동하는 방식과 유사합니다.
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(1,
                        ComputeShaderProperties.CellVertexInfoLookupTable, m_cellVertexInfoLookupTableBuffer[lod]);


                    for (int cellStride = 2; cellStride <= (1 << lod); cellStride <<= 1)
                    {
                        m_generatedVerticesBuffer1[lod].SetCounterValue(0);

                        m_parent.m_voxelConfig.DualContouringConfig.Compute.SetInt(
                            ComputeShaderProperties.CellStride,
                            cellStride);
                        // We need two buffers to merge the vertices. The first buffer acts as the source and the second as the destination.
                        m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(1,
                            ComputeShaderProperties.GeneratedVertices0, m_generatedVerticesBuffer0[lod]);
                        m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(1,
                            ComputeShaderProperties.GeneratedVertices1, m_generatedVerticesBuffer1[lod]);
                        m_parent.m_voxelConfig.DualContouringConfig.Compute.Dispatch(1,
                            m_parent.m_voxelConfig.VoxelVolumeConfig.NumberOfCells / cellStride);

                        // Swap the buffers, so during the next iteration the source buffer will be the previous iteration's destination buffer.
                        (m_generatedVerticesBuffer0[lod], m_generatedVerticesBuffer1[lod]) =
                            (m_generatedVerticesBuffer1[lod], m_generatedVerticesBuffer0[lod]);
                    }

                    // After the desired lod has been generated, we populate the outermost cells with vertices at the highest level of detail. This will ensure that no
                    // seams will be visible, resulting in a watertight mesh.
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(2,
                        ComputeShaderProperties.VoxelVolume, task.VoxelVolumeBuffer);
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(2,
                        ComputeShaderProperties.CellVertexInfoLookupTable, m_cellVertexInfoLookupTableBuffer[lod]);
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(2,
                        ComputeShaderProperties.GeneratedVertices0, m_generatedVerticesBuffer0[lod]);
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.Dispatch(2,
                        m_parent.m_voxelConfig.VoxelVolumeConfig.NumberOfCells);

                    // Finally, we triangulate the vertices to form the mesh.
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(3,
                        ComputeShaderProperties.VoxelVolume, task.VoxelVolumeBuffer);
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(3,
                        ComputeShaderProperties.CellVertexInfoLookupTable, m_cellVertexInfoLookupTableBuffer[lod]);
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(3,
                        ComputeShaderProperties.GeneratedVertices0, m_generatedVerticesBuffer0[lod]);
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.SetBuffer(3,
                        ComputeShaderProperties.GeneratedTriangles, m_generatedTrianglesBuffer[lod]);
                    m_parent.m_voxelConfig.DualContouringConfig.Compute.Dispatch(3,
                        m_parent.m_voxelConfig.VoxelVolumeConfig.NumberOfCells - 1);

                    //일반적으로 생성된 꼭짓점과 삼각형을 검색하기 위해서는 먼저 해당 컴퓨트 버퍼의 카운터 값을 읽은 다음, 추가적인 리드백을 통해 꼭짓점과 삼각형 자체를 검색해야 합니다.
                    //그러나 이는 두 번의 리드백을 필요로 하며 메시 업데이트의 지연을 길게 만듭니다. 대신, 우리는 삼각형 버퍼의 시작 부분에 카운터 값을 복사한 다음, 컴퓨트 셰이더가 생성할 것으로 예상되는 꼭짓점과 삼각형의 수를 추정하여 꼭짓점과 삼각형을 검색합니다.
                    //나중에 리드백에서 데이터를 받게 되면, 실제 꼭짓점과 삼각형의 수를 우리가 처음에 기반으로 한 추정치와 비교할 수 있습니다. 두 가지 시나리오가 발생할 수 있습니다:
                    //우리가 꼭짓점과 삼각형의 수를 정확히 추정했고 첫 번째 리드백 동안 모든 꼭짓점과 삼각형을 얻었습니다. 주의할 점: 우리는 생성된 것보다 더 많은 꼭짓점과 삼각형을 검색하는 것에 대해 크게 신경 쓰지 않습니다, 단지 너무 자주 불필요하게 많이 검색하지 않는 한입니다.
                    //우리가 너무 적은 꼭짓점과 삼각형을 검색했습니다. 실제 꼭짓점/삼각형 수를 사용하여 정확한 수로 다시 꼭짓점과 삼각형을 검색해야 합니다.
                    //따라서, 최선의 경우는 한 번의 리드백이고, 최악의 경우는 두 번의 리드백입니다. 즉, 최악의 경우가 이전의 최선의 경우만큼 나쁘고, 최선의 경우는 이전보다 두 배 좋습니다.

                    (int estimatedVertexCount, int estimatedTriangleCount) =
                        EstimateVertexAndTriangleCounts(task, lod, nextlod);
                    // int estimatedVertexCount = 0;
                    //int estimatedTriangleCount = 0;

                    // Copy the number of vertices/triangles generated into the start of the triangles buffer.
                    ComputeBuffer.CopyCount(m_generatedVerticesBuffer0[lod], m_generatedTrianglesBuffer[lod], 0);
                    ComputeBuffer.CopyCount(m_cellVertexInfoLookupTableBuffer[lod], m_generatedTrianglesBuffer[lod],
                        sizeof(uint));


                            // Retrieve both the vertices and triangles buffer.
                    m_generatedVerticesBuffer0[lod]
                                .StartReadbackNonAlloc(ref m_generatedVertices[lod], estimatedVertexCount);
                            // We're adding 2 because the vertex and triangle counts are stored in the buffer as well.
                    m_generatedTrianglesBuffer[lod].StartReadbackNonAlloc(ref m_generatedTriangles[lod],
                                estimatedTriangleCount + 2);
                    
                }
            }

            private (int, int) EstimateVertexAndTriangleCounts(Task task, int lod, int nlod)
            {
                float factor = m_parent.m_readbackInflationFactor * math.pow(2.0f, nlod - lod);
            
                int estimatedVertexCount = (int)math.round(factor * task.CurrentVertexCount[lod]);
                estimatedVertexCount = math.clamp(1, estimatedVertexCount, m_generatedVertices[lod].Length);
            
                int estimatedTriangleCount = (int)math.round(factor * task.CurrentTriangleCount[lod]);
                estimatedTriangleCount = math.clamp(1, estimatedTriangleCount, m_generatedTriangles[lod].Length - 2);
            
                return (estimatedVertexCount, estimatedTriangleCount);
            }

            private void CreateBuffers()
            {
               
                
                // Create CPU buffers.
                int maxNumberOfVertices = m_parent.m_voxelConfig.VoxelVolumeConfig.CellCount;
                int generatedVerticesCapacity = maxNumberOfVertices;

                for (int lod = 0; lod < WorldManager.Instance.MaxLOD; ++lod)
                {

                    if (!m_generatedVertices[lod].IsCreated ||
                        m_generatedVertices[lod].Length != generatedVerticesCapacity)
                    {
                        if (m_generatedVertices[lod].IsCreated)
                        {
                            m_generatedVertices[lod].Dispose();
                        }

                        m_generatedVertices[lod] =
                            new NativeArray<GPUVertex>(generatedVerticesCapacity, Allocator.Persistent);
                    }

                    int maxNumberOfTriangles =
                        3 * 6 * (m_parent.m_voxelConfig.VoxelVolumeConfig.NumberOfCellsAlongX - 1)
                        * (m_parent.m_voxelConfig.VoxelVolumeConfig.NumberOfCellsAlongY - 1)
                        * (m_parent.m_voxelConfig.VoxelVolumeConfig.NumberOfCellsAlongZ - 1);
                    //int maxNumberOfTriangles = 3 * 6 * (int)math.round(math.pow(m_parent.m_voxelConfig.VoxelVolumeConfig.NumberOfCellsAlongAxis - 1, 3));

                    // As mentioned, in addition to storing the triangles, this buffer will also store the number of vertices and triangles generated, i.e.
                    // two additional integers.
                    int generatedTrianglesCapacity = maxNumberOfTriangles + 2;

                    if (!m_generatedTriangles[lod].IsCreated || m_generatedTriangles[lod].Length != generatedTrianglesCapacity)
                    {
                        if (m_generatedTriangles[lod].IsCreated)
                        {
                            m_generatedTriangles[lod].Dispose();
                        }

                        m_generatedTriangles[lod] = new NativeArray<int>(generatedTrianglesCapacity, Allocator.Persistent);
                    }

                    // Create GPU buffers.
                    if (m_cellVertexInfoLookupTableBuffer[lod]?.Count != m_parent.m_voxelConfig.VoxelVolumeConfig.CellCount)
                    {
                        m_cellVertexInfoLookupTableBuffer[lod]?.Release();
                        // Since we cannot declare the triangles buffer of compute buffer type "counter", we use this buffer to keep track of the number of triangles generated.
                        m_cellVertexInfoLookupTableBuffer[lod] = new AsyncComputeBuffer(
                            m_parent.m_voxelConfig.VoxelVolumeConfig.CellCount, sizeof(uint),
                            ComputeBufferType.Counter);
                    }

                    if (m_generatedVerticesBuffer0[lod]?.Count != m_generatedVertices[lod].Length)
                    {
                        m_generatedVerticesBuffer0[lod]?.Release();
                        // The counter attached to this compute buffer stores the number of vertices generated by dual contouring.
                        m_generatedVerticesBuffer0[lod] = new AsyncComputeBuffer(m_generatedVertices[lod].Length,
                            GPUVertex.SizeInBytes, ComputeBufferType.Counter);
                    }

                    if (m_generatedVerticesBuffer1[lod]?.Count != m_generatedVertices[lod].Length)
                    {
                        m_generatedVerticesBuffer1[lod]?.Release();
                        // The counter attached to this compute buffer stores the number of vertices generated by dual contouring.
                        m_generatedVerticesBuffer1[lod] = new AsyncComputeBuffer(m_generatedVertices[lod].Length,
                            GPUVertex.SizeInBytes, ComputeBufferType.Counter);
                    }

                    if (m_generatedTrianglesBuffer[lod]?.Count != m_generatedTriangles[lod].Length)
                    {
                        m_generatedTrianglesBuffer[lod]?.Release();
                        // To copy the counter values into the triangles buffer it needs to be of type "raw".
                        m_generatedTrianglesBuffer[lod] = new AsyncComputeBuffer(m_generatedTriangles[lod].Length, sizeof(uint),
                            ComputeBufferType.Raw);
                    }
                }
            }

            private void ReleaseBuffers()
            {
                for (int lod = 0; lod < WorldManager.Instance.MaxLOD; ++lod)
                {
                    // Dispose CPU buffers.
                    if (m_generatedVertices[lod].IsCreated)
                    {
                        if (m_generatedVerticesBuffer0[lod].ReadbackInProgress)
                        {
                            m_generatedVerticesBuffer0[lod].EndReadback();
                        }

                        m_generatedVertices[lod].Dispose();
                    }

                    if (m_generatedTriangles[lod].IsCreated)
                    {
                        if (m_generatedTrianglesBuffer[lod].ReadbackInProgress)
                        {
                            m_generatedTrianglesBuffer[lod].EndReadback();
                        }

                        m_generatedTriangles[lod].Dispose();
                    }

                    // Release GPU buffers.
                    if (m_cellVertexInfoLookupTableBuffer[lod] != null)
                    {
                        m_cellVertexInfoLookupTableBuffer[lod].Release();
                        m_cellVertexInfoLookupTableBuffer[lod] = null;
                    }

                    if (m_generatedVerticesBuffer0[lod] != null)
                    {
                        m_generatedVerticesBuffer0[lod].Release();
                        m_generatedVerticesBuffer0[lod] = null;
                    }

                    if (m_generatedVerticesBuffer1[lod] != null)
                    {
                        m_generatedVerticesBuffer1[lod].Release();
                        m_generatedVerticesBuffer1[lod] = null;
                    }

                    if (m_generatedTrianglesBuffer[lod] != null)
                    {
                        m_generatedTrianglesBuffer[lod].Release();
                        m_generatedTrianglesBuffer[lod] = null;
                    }
                }
            }

            public enum Status
            {
                WaitingForGPUReadback,
                Done
            }

            public class Task : IPoolable, IRequest
            {
                public bool Canceled { get; private set; }
                public ComputeBuffer VoxelVolumeBuffer { get; set; }
                //public int CurrentLOD { get; set; }
                //public int TargetLOD { get; set; }
                public int MaxLOD { get; set; }
                public int[] CurrentVertexCount { get; set; }
                public int[] CurrentTriangleCount { get; set; }
                public float3 VoxelVolumeToWorldSpaceOffset { get; set; }
                public OnMeshGenerated Callback { get; set; }

                public void OnAcquire() { }

                public void OnRelease()
                {
                    VoxelVolumeBuffer = null;
                    Callback = null;
                    Canceled = false;
                }

                public void Cancel() => Canceled = true;
            }
        }
    }
}