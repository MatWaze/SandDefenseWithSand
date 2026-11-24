using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using AOT;
using UnityEngine;
using UnityEngine.Rendering;

namespace Plawius.NonConvexCollider
{
    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    public struct Parameters
    {
        /// Maximum number of convex hulls. Default = 32. Range = [1 .. 128]
        [Tooltip("Maximum number of convex hulls. Default = 32. Range = [1 .. 128]")]
        [UnityEngine.Range(1, 128)]
        public int maxConvexHulls;

        /// Maximum number of voxels generated during the voxelization stage. Default = 50000. Range = [10000 .. 6400000]
        [Tooltip("Maximum number of voxels generated during the voxelization stage. Default = 50000. Range = [10000 .. 6400000]")]
        [UnityEngine.Range(10000, 6400000)]
        public int resolution; 
        
        /// Maximum concavity. Default = 0.0025. Range = [0.0 .. 1.0]
        [Tooltip("Maximum concavity. Default = 0.0025. Range = [0.0 .. 1.0]")]
        [UnityEngine.Range(0.0f, 1.0f)]
        public double concavity;
        
        /// Controls the granularity of the search for the "best" clipping plane. Default = 4. Range = [1 .. 16]
        [Tooltip("Controls the granularity of the search for the \"best\" clipping plane. Default = 4. Range = [1 .. 16]")]
        [UnityEngine.Range(1, 16)]
        public int planeDownsampling;
        
        /// Controls the precision of the convex - hull generation process during the clipping plane selection stage. Default = 4. Range = [1 .. 16]
        [Tooltip("Controls the precision of the convex - hull generation process during the clipping plane selection stage. Default = 4. Range = [1 .. 16]")]
        [UnityEngine.Range(1, 16)]
        public int convexhullApproximation;
        
        /// Controls the bias toward clipping along symmetry planes. Default = 0.05. Range = [0.0 .. 1.0]
        [Tooltip("Controls the bias toward clipping along symmetry planes. Default = 0.05. Range = [0.0 .. 1.0]")]
        [UnityEngine.Range(0.0f, 1.0f)]
        public double alpha;
        
        /// Controls the bias toward clipping along revolution axes. Default = 0.05. Range = [0.0 .. 1.0]
        [Tooltip("Controls the bias toward clipping along revolution axes. Default = 0.05. Range = [0.0 .. 1.0]")]
        [UnityEngine.Range(0.0f, 1.0f)]
        public double beta;
        
        /// Enable / disable normalizing the mesh before applying the convex decomposition. Default = 1. Range = [0 .. 1]
        [Tooltip("Enable / disable normalizing the mesh before applying the convex decomposition. Default = 1. Range = [0 .. 1]")]
        [UnityEngine.Range(0, 1)]
        public int pca;
        
        /// 0: voxel - based approximate convex decomposition, 1 : tetrahedron - based approximate convex decomposition. Default = 0. Range = [0 .. 1]
        [Tooltip("0: voxel - based approximate convex decomposition, 1 : tetrahedron - based approximate convex decomposition. Default = 0. Range = [0 .. 1]")]
        [UnityEngine.Range(0, 1)]
        public int mode;
        
        /// Controls the maximum number of triangles per convex - hull. Default = 64. Range = [4 .. 1024]
        [Tooltip("Controls the maximum number of triangles per convex - hull. Default = 64. Range = [4 .. 1024]")]
        [UnityEngine.Range(4, 1024)]
        public int maxNumVerticesPerCH;
        
        /// Controls the adaptive sampling of the generated convex - hulls. Default = 0.0001. Range = [0.0 .. 0.01]
        [Tooltip("Controls the adaptive sampling of the generated convex - hulls. Default = 0.0001. Range = [0.0 .. 0.01]")]
        [UnityEngine.Range(0.0f, 0.01f)]
        public double minVolumePerCH;

        public delegate void NativeCallbackDelegate(double overallProgress, double stageProgress, double operationProgress, 
                                                    IntPtr stage, IntPtr operation);

        [HideInInspector]
        public NativeCallbackDelegate callback;

        public static Parameters LowResolution()
        {
            return new Parameters
            {
                resolution = 10000,
                maxConvexHulls = 8,
                concavity = 0.0025,
                planeDownsampling = 4,
                convexhullApproximation = 4,
                alpha = 0.05,
                beta = 0.05,
                pca = 1,
                mode = 0,
                maxNumVerticesPerCH = 64,
                minVolumePerCH = 0.0001,
                callback = null
            };
        }
        
        public static Parameters Default()
        {
            return new Parameters
            {
                resolution = 50000,
                maxConvexHulls = 32,
                concavity = 0.0025,
                planeDownsampling = 4,
                convexhullApproximation = 4,
                alpha = 0.05,
                beta = 0.05,
                pca = 1,
                mode = 0,
                maxNumVerticesPerCH = 64,
                minVolumePerCH = 0.0001,
                callback = null
            };
        }

        public static Parameters HighResolution()
        {
            return new Parameters
            {
                resolution = 4000000,
                maxConvexHulls = 32,
                concavity = 0.0025,
                planeDownsampling = 4,
                convexhullApproximation = 4,
                alpha = 0.05,
                beta = 0.05,
                pca = 1,
                mode = 0,
                maxNumVerticesPerCH = 64,
                minVolumePerCH = 0.0001,
                callback = null
            };
        }
        
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + maxConvexHulls.GetHashCode();
                hash = hash * 31 + resolution.GetHashCode();
                hash = hash * 31 + concavity.GetHashCode();
                hash = hash * 31 + planeDownsampling.GetHashCode();
                hash = hash * 31 + convexhullApproximation.GetHashCode();
                hash = hash * 31 + alpha.GetHashCode();
                hash = hash * 31 + beta.GetHashCode();
                hash = hash * 31 + pca.GetHashCode();
                hash = hash * 31 + mode.GetHashCode();
                hash = hash * 31 + maxNumVerticesPerCH.GetHashCode();
                hash = hash * 31 + minVolumePerCH.GetHashCode();
                return hash;
            }
        }
    }
    
    /// <summary>
    /// API to generate convex meshes from a non-convex source mesh.
    /// The asynchronous method copies Unity mesh data on the calling thread,
    /// runs only the native VHACD call on a worker, then creates Unity Mesh
    /// objects after returning to the captured Unity SynchronizationContext.
    /// </summary>
    public static class API
    {
        private static Action<float, string> _currentProgressCallback;
        private static readonly object _nativeGenerationLock = new object();

        private sealed class ConvexMeshData
        {
            public Vector3[] Vertices;
            public int[] Triangles;
            public string Name;
        }

        public static Mesh[] GenerateConvexMeshes(Mesh nonconvexMesh)
        {
            return GenerateConvexMeshes(nonconvexMesh, Parameters.Default(), null);
        }

        /// <summary>
        /// Synchronous version. Call from Unity's main thread because it reads
        /// the source Mesh and constructs the returned Unity Mesh objects.
        /// </summary>
        public static Mesh[] GenerateConvexMeshes(
            Mesh nonconvexMesh,
            Parameters parameters,
            Action<float, string> progressCallback = null)
        {
            if (nonconvexMesh == null)
                throw new ArgumentNullException(nameof(nonconvexMesh));

            // Unity Mesh access must remain on the main thread.
            Vector3[] sourceVertices = nonconvexMesh.vertices;
            int vertexCount = nonconvexMesh.vertexCount;
            int[] sourceTriangles = nonconvexMesh.triangles;

            ConvexMeshData[] data = GenerateConvexMeshData(
                sourceVertices, vertexCount, sourceTriangles, parameters, progressCallback);

            return CreateUnityMeshes(data);
        }

        /// <summary>
        /// Asynchronously decomposes a Unity Mesh. Must be called on Unity's
        /// main thread. Mesh extraction and Mesh creation stay on the main
        /// thread; the native VHACD operation and unmanaged-memory copying run
        /// on a worker thread.
        /// </summary>
        public static async Task<Mesh[]> GenerateConvexMeshesAsync(
            Mesh nonconvexMesh,
            Parameters parameters,
            Action<float, string> progressCallback = null)
        {
            if (nonconvexMesh == null)
                throw new ArgumentNullException(nameof(nonconvexMesh));

            SynchronizationContext unityContext = SynchronizationContext.Current;
            if (unityContext == null)
            {
                throw new InvalidOperationException(
                    "GenerateConvexMeshesAsync must be called from Unity's main thread so the generated Mesh objects can be created safely.");
            }

            // Snapshot all Unity Mesh data before leaving the main thread.
            Vector3[] sourceVertices = nonconvexMesh.vertices;
            int vertexCount = nonconvexMesh.vertexCount;
            int[] sourceTriangles = nonconvexMesh.triangles;

            // NativeCallback is invoked from the worker thread. Marshal user
            // progress callbacks back to Unity's main thread.
            Action<float, string> mainThreadProgress = null;
            if (progressCallback != null)
            {
                mainThreadProgress = (progress, message) =>
                {
                    unityContext.Post(_ => progressCallback(progress, message), null);
                };
            }

            ConvexMeshData[] data = await Task.Run(() =>
                GenerateConvexMeshData(
                    sourceVertices, vertexCount, sourceTriangles, parameters, mainThreadProgress));

            // Await captures Unity's SynchronizationContext, so this executes
            // on the main thread and may safely create UnityEngine.Mesh objects.
            return CreateUnityMeshes(data);
        }

        [MonoPInvokeCallback(typeof(Parameters.NativeCallbackDelegate))]
        private static void NativeCallback(
            double overallProgress,
            double stageProgress,
            double operationProgress,
            IntPtr stagePtr,
            IntPtr operationPtr)
        {
            string stage = Marshal.PtrToStringAnsi(stagePtr);
            string operation = Marshal.PtrToStringAnsi(operationPtr);
            float progress01 = (float)(overallProgress * 0.01);
            string title = stage != operation
                ? string.Format("{0}. {1}", stage, operation)
                : stage;

            Action<float, string> callback = Volatile.Read(ref _currentProgressCallback);
            if (callback != null)
                callback(progress01, title);
        }

        /// <summary>
        /// Contains no UnityEngine.Mesh API calls. This method is therefore
        /// suitable for Task.Run, provided the native plugin supports calls
        /// from a worker thread. The lock serializes calls because the package
        /// uses a static native progress callback.
        /// </summary>
        private static ConvexMeshData[] GenerateConvexMeshData(
            Vector3[] sourceVertices,
            int vertexCount,
            int[] sourceTriangles,
            Parameters parameters,
            Action<float, string> progressCallback)
        {
            lock (_nativeGenerationLock)
            {
                if (sourceVertices == null || sourceTriangles == null)
                    throw new ArgumentNullException("Source vertex or triangle data is null.");

                if (parameters.maxConvexHulls == 0 || parameters.resolution == 0)
                    parameters = Parameters.Default();

                IntPtr outPoints = IntPtr.Zero;
                IntPtr outTriangles = IntPtr.Zero;
                IntPtr indexes = IntPtr.Zero;
                int indexesCount = 0;
                GCHandle gcVertices = default(GCHandle);
                GCHandle gcTriangles = default(GCHandle);
                Parameters.NativeCallbackDelegate nativeCallback = NativeCallback;

                try
                {
                    if (progressCallback != null)
                        progressCallback(0f, "Initialization...");

                    if (progressCallback != null)
                        progressCallback(0.1f, "Generation is in progress, please wait...");

                    parameters.callback = nativeCallback;
                    Volatile.Write(ref _currentProgressCallback, progressCallback);

                    int meshCount;
                    gcVertices = GCHandle.Alloc(sourceVertices, GCHandleType.Pinned);
                    gcTriangles = GCHandle.Alloc(sourceTriangles, GCHandleType.Pinned);

                    try
                    {
                        meshCount = Interop.GetMeshEx(
                            gcVertices.AddrOfPinnedObject(),
                            vertexCount * 3,
                            gcTriangles.AddrOfPinnedObject(),
                            sourceTriangles.Length,
                            out outPoints,
                            out outTriangles,
                            out indexes,
                            out indexesCount,
                            parameters);
                    }
                    finally
                    {
                        if (gcVertices.IsAllocated)
                            gcVertices.Free();
                        if (gcTriangles.IsAllocated)
                            gcTriangles.Free();
                    }

                    GC.KeepAlive(nativeCallback);

                    if (meshCount <= 0)
                    {
                        throw new Exception(
                            "GenerateConvexMeshes failed: no meshes were returned. Check the source mesh and Parameters.");
                    }

                    if (meshCount > parameters.maxConvexHulls)
                    {
                        throw new Exception(
                            "GenerateConvexMeshes failed: returned " + meshCount +
                            " meshes, but maxConvexHulls is " + parameters.maxConvexHulls + ".");
                    }

                    bool sanityCheck = indexesCount >= 0 &&
                                       indexesCount % 2 == 0 &&
                                       meshCount == indexesCount / 2;
                    if (!sanityCheck)
                        throw new Exception("GenerateConvexMeshes failed: returned index data is corrupted.");

                    if (progressCallback != null)
                        progressCallback(0.4f, "Generation is done. Getting results...");

                    int[] indexesArray = new int[indexesCount];
                    Marshal.Copy(indexes, indexesArray, 0, indexesCount);

                    ConvexMeshData[] result = new ConvexMeshData[meshCount];
                    int pointsStartIndex = 0;
                    int trianglesStartIndex = 0;

                    for (int meshIndex = 0; meshIndex < meshCount; meshIndex++)
                    {
                        int pointCount = indexesArray[meshIndex * 2];
                        int triangleIndexCount = indexesArray[meshIndex * 2 + 1];

                        if (pointCount < 0 || triangleIndexCount < 0 || pointCount % 3 != 0)
                            throw new Exception("GenerateConvexMeshes failed: one of the generated meshes is corrupted.");

                        double[] pointValues = new double[pointCount];
                        int[] triangleValues = new int[triangleIndexCount];

                        IntPtr pointsPointer = new IntPtr(
                            outPoints.ToInt64() + sizeof(double) * (long)pointsStartIndex);
                        IntPtr trianglesPointer = new IntPtr(
                            outTriangles.ToInt64() + sizeof(int) * (long)trianglesStartIndex);

                        Marshal.Copy(pointsPointer, pointValues, 0, pointCount);
                        Marshal.Copy(trianglesPointer, triangleValues, 0, triangleIndexCount);

                        pointsStartIndex += pointCount;
                        trianglesStartIndex += triangleIndexCount;

                        Vector3[] vertices = new Vector3[pointCount / 3];
                        for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
                        {
                            vertices[vertexIndex] = new Vector3(
                                (float)pointValues[vertexIndex * 3],
                                (float)pointValues[vertexIndex * 3 + 1],
                                (float)pointValues[vertexIndex * 3 + 2]);
                        }

                        result[meshIndex] = new ConvexMeshData
                        {
                            Vertices = vertices,
                            Triangles = triangleValues,
                            Name = "Generated convex submesh " + (meshIndex + 1)
                        };

                        if (progressCallback != null)
                        {
                            float progress = 0.4f + 0.6f * (meshIndex + 1) / meshCount;
                            progressCallback(progress, "Generation is done. Getting result " +
                                (meshIndex + 1) + "/" + meshCount + "...");
                        }
                    }

                    if (progressCallback != null)
                        progressCallback(1f, "Generation is done. Cleaning up...");

                    return result;
                }
                finally
                {
                    Volatile.Write(ref _currentProgressCallback, null);

                    if (gcVertices.IsAllocated)
                        gcVertices.Free();
                    if (gcTriangles.IsAllocated)
                        gcTriangles.Free();

                    if (indexes != IntPtr.Zero)
                        Interop.ReleaseMemory(indexes);
                    if (outPoints != IntPtr.Zero)
                        Interop.ReleaseMemory(outPoints);
                    if (outTriangles != IntPtr.Zero)
                        Interop.ReleaseMemory(outTriangles);
                }
            }
        }

        private static Mesh[] CreateUnityMeshes(ConvexMeshData[] data)
        {
            Mesh[] result = new Mesh[data.Length];

            for (int i = 0; i < data.Length; i++)
            {
                Mesh mesh = new Mesh
                {
                    name = data[i].Name
                };

                if (data[i].Vertices.Length > 65535)
                    mesh.indexFormat = IndexFormat.UInt32;

                mesh.vertices = data[i].Vertices;
                mesh.triangles = data[i].Triangles;
                mesh.RecalculateBounds();
                result[i] = mesh;
            }

            return result;
        }
    }
}
