using System;
using System.Collections;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using Clayxels;
using Plawius.NonConvexCollider;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace SandDefenseWithSand.Clay
{
    /// <summary>Owns the paused ClayObjects and optional mesh and collider handoff during movement.</summary>
    internal sealed class ClayxelsMovementPreparation : IDisposable
    {
        internal struct Settings
        {
            public int ClayObjectsPerFrame;
            public int MaxWaitFrames;
            public int MeshUploadVerticesPerFrame;
            public int MaxConvexHulls;
            public int ConvexColliderResolution;
            public bool LogTimings;
        }

        private readonly ClayContainer _container;
        private readonly Settings _settings;
        private bool _piecesPaused;
        private readonly List<ClayObject> _disabledClayObjects = new List<ClayObject>(64);
        private readonly List<GameObject> _deactivatedClayObjectGameObjects = new List<GameObject>(64);
        // Snapshot data. All render components are on the ClayContainer GameObject.
        private Mesh _snapshotMesh;
        private Material _snapshotMaterial;
        private MeshFilter _snapshotMeshFilter;
        private MeshRenderer _snapshotRenderer;
        private SkinnedMeshRenderer _containerSkinnedRenderer;

        // Original root-renderer state, restored during cleanup.
        private Mesh _originalContainerMesh;
        private Material[] _originalContainerMaterials;
        private bool _originalRendererEnabled;
        private ShadowCastingMode _originalShadowCastingMode;
        private bool _originalReceiveShadows;
        private bool _originalSkinnedRendererEnabled;
        private bool _snapshotComponentsCaptured;
        private bool _createdSnapshotMeshFilter;
        private bool _createdSnapshotMeshRenderer;
        private bool _containerWasVisibleBeforeMovement;

        private GameObject _generatedColliderObject;
        private NonConvexColliderAsset _generatedColliderAsset;

        public bool IsReady { get; private set; }
        public bool HasGeneratedColliders => _generatedColliderObject != null;
        public MeshRenderer SnapshotRenderer => _snapshotRenderer;

        public ClayxelsMovementPreparation(ClayContainer container, Settings settings)
        {
            _container = container;
            _settings = settings;
        }

        public IEnumerator Prepare(bool useSnapshot, bool generateColliders)
        {
            if (_container == null)
                yield break;

            _containerWasVisibleBeforeMovement = _container.isVisible();
            if (useSnapshot || !_container.isFrozen())
            {
                yield return PrepareClayxelsForMovementAsync(_container);
                if (!_piecesPaused)
                    yield break;
            }

            if (useSnapshot && (generateColliders || (SystemInfo.supportsAsyncGPUReadback &&
                _container.getRenderMode() == (int)ClayContainer.RenderModes.smoothMesh)))
            {
                bool snapshotSucceeded = false;
                yield return GenerateSmoothMeshSnapshotCoroutine(_container, success => snapshotSucceeded = success);

                if (!snapshotSucceeded)
                {
                    // Box-collider movement can continue with the already prepared live model.
                    IsReady = !generateColliders;
                    yield break;
                }

                if (generateColliders)
                {
                    yield return GenerateConvexColliderCoroutine(_container, _snapshotMesh);
                    if (!HasGeneratedColliders)
                        yield break;
                }
            }

            IsReady = true;
        }

        public void Activate(bool showSnapshot)
        {
            if (HasGeneratedColliders)
                ReplaceClayObjectColliders();

            if (showSnapshot && _snapshotRenderer != null)
            {
                _container.setVisible(false);
                _snapshotRenderer.enabled = true;
                _container.ReleaseHiddenSnapshotBuffers();
            }
        }

        public void Dispose()
        {
            if (_generatedColliderObject != null)
            {
                _generatedColliderObject.SetActive(false);
                Object.Destroy(_generatedColliderObject);
                _generatedColliderObject = null;
            }
            if (_generatedColliderAsset != null)
            {
                DestroyGeneratedConvexMeshes(_generatedColliderAsset.ConvexMeshes);
                Object.Destroy(_generatedColliderAsset);
                _generatedColliderAsset = null;
            }

            if (IsReady || _piecesPaused || _disabledClayObjects.Count > 0)
                RestoreLiveClay();
            else
                DestroyMeshSnapshot();

            IsReady = false;
            _piecesPaused = false;
        }

        /// <summary>
        /// Pauses ClayObject behaviours in batches while their boxes remain active.
        /// Cancels the resulting scans to keep the generated Clayxels geometry intact.
        /// </summary>
        private IEnumerator PrepareClayxelsForMovementAsync(ClayContainer container)
        {
            float startedAt = Time.realtimeSinceStartup;

            if (container == null)
            {
                Debug.LogError("Cannot prepare movement: ClayContainer reference is null.", container);
                yield break;
            }

            if (!container.IsReadyForMovementSnapshot(out string readinessError))
            {
                Debug.LogError(readinessError, container);
                yield break;
            }

            int waitFrames = 0;
            if (!container.enabled && container.needsUpdate)
            {
                Debug.LogError(
                    "Clayxels is disabled while it still has a pending update. Enable the ClayContainer and let it finish updating before reanimating.",
                    container);
                yield break;
            }

            while (container.enabled && container.needsUpdate && waitFrames < _settings.MaxWaitFrames)
            {
                waitFrames++;
                yield return null;
            }

            if (container.enabled && container.needsUpdate)
            {
                Debug.LogError("Clayxels still has a pending update. Movement preparation was cancelled.", container);
                yield break;
            }

            ClayObject[] clayObjects;
            Stopwatch scanWatch = Stopwatch.StartNew();
            try
            {
                clayObjects = container.GetComponentsInChildren<ClayObject>(true);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, container);
                yield break;
            }
            scanWatch.Stop();

            _disabledClayObjects.Clear();

            int batchSize = Mathf.Max(1, _settings.ClayObjectsPerFrame);
            int disabledCount = 0;
            int batchCount = 0;
            double maxBatchMilliseconds = 0.0;
            Stopwatch batchWatch = Stopwatch.StartNew();

            for (int i = 0; i < clayObjects.Length; i++)
            {
                ClayObject clayObject = clayObjects[i];
                if (clayObject != null && clayObject.enabled)
                {
                    _disabledClayObjects.Add(clayObject);
                    clayObject.enabled = false;
                    disabledCount++;
                }

                batchCount++;
                if (batchCount >= batchSize || i == clayObjects.Length - 1)
                {
                    // ClayObject.OnDisable schedules a hierarchy scan. We preserve the
                    // current GPU geometry, so cancel that scan/update.
                    container.CancelPendingClayObjectScanForMovement();

                    batchWatch.Stop();
                    maxBatchMilliseconds = Math.Max(maxBatchMilliseconds, batchWatch.Elapsed.TotalMilliseconds);
                    batchCount = 0;

                    if (i < clayObjects.Length - 1)
                    {
                        yield return null;
                        batchWatch.Restart();
                    }
                }
            }

            container.CancelPendingClayObjectScanForMovement();

            if (_settings.LogTimings)
            {
                float elapsedMs = (Time.realtimeSinceStartup - startedAt) * 1000f;
                Debug.Log(
                    $"Clayxels movement preparation: {disabledCount}/{clayObjects.Length} ClayObject behaviours disabled; " +
                    $"hierarchy scan {scanWatch.Elapsed.TotalMilliseconds:F2} ms; " +
                    $"max disable batch {maxBatchMilliseconds:F2} ms; " +
                    $"{elapsedMs:F1} ms elapsed across frames. GPU buffers were NOT compacted or released.",
                    container);
            }

            _piecesPaused = true;
        }

        private void ReplaceClayObjectColliders()
        {
            foreach (ClayObject clayObject in _container.GetComponentsInChildren<ClayObject>(true))
            {
                GameObject piece = clayObject.gameObject;
                if (piece == _container.gameObject || !piece.activeSelf)
                    continue;

                _deactivatedClayObjectGameObjects.Add(piece);
                piece.SetActive(false);
            }

            _container.CancelPendingClayObjectScanForMovement();
            _generatedColliderObject.SetActive(true);
            Physics.SyncTransforms();
        }

        /// <summary>
        /// Delegates GPU readback and mesh construction to ClayContainer, which owns
        /// the private Clayxels buffers and material-copy logic. This class attaches
        /// the resulting mesh to the container root and manages its lifetime.
        /// </summary>
        private IEnumerator GenerateSmoothMeshSnapshotCoroutine(
            ClayContainer container,
            Action<bool> completed)
        {
            Mesh generatedMesh = null;
            Material generatedMaterial = null;
            bool succeeded = false;

            yield return container.CreateSmoothMeshSnapshotAsync(
                (mesh, material, success) =>
                {
                    generatedMesh = mesh;
                    generatedMaterial = material;
                    succeeded = success;
                },
                _settings.MeshUploadVerticesPerFrame,
                _settings.LogTimings);

            if (!succeeded || generatedMesh == null || generatedMaterial == null)
            {
                completed?.Invoke(false);
                yield break;
            }

            _snapshotMesh = generatedMesh;
            _snapshotMaterial = generatedMaterial;

            try
            {
                AttachSnapshotToContainer(container);
            }
            catch (Exception exception)
            {
                DestroyMeshSnapshot();
                Debug.LogException(exception, container);
                completed?.Invoke(false);
                yield break;
            }

            completed?.Invoke(true);
        }

        /// <summary>
        /// Adds/uses MeshFilter and MeshRenderer directly on the ClayContainer GameObject.
        /// Existing root components and their settings are retained for cleanup.
        /// </summary>
        private void AttachSnapshotToContainer(ClayContainer container)
        {
            _createdSnapshotMeshFilter = false;
            _createdSnapshotMeshRenderer = false;

            MeshFilter filter = container.GetComponent<MeshFilter>();
            if (filter == null)
            {
                filter = container.gameObject.AddComponent<MeshFilter>();
                _createdSnapshotMeshFilter = true;
            }

            MeshRenderer renderer = container.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = container.gameObject.AddComponent<MeshRenderer>();
                _createdSnapshotMeshRenderer = true;
            }

            _snapshotMeshFilter = filter;
            _snapshotRenderer = renderer;

            _originalContainerMesh = filter.sharedMesh;
            _originalContainerMaterials = renderer.sharedMaterials;
            _originalRendererEnabled = renderer.enabled;
            _originalShadowCastingMode = renderer.shadowCastingMode;
            _originalReceiveShadows = renderer.receiveShadows;

            _containerSkinnedRenderer = container.GetComponent<SkinnedMeshRenderer>();
            if (_containerSkinnedRenderer != null)
            {
                _originalSkinnedRendererEnabled = _containerSkinnedRenderer.enabled;
                _containerSkinnedRenderer.enabled = false;
            }

            _snapshotComponentsCaptured = true;

            // Prepare the snapshot renderer while it is disabled. The live Clayxels
            // container remains visible until the mesh and material are fully assigned.
            filter.sharedMesh = _snapshotMesh;
            renderer.sharedMaterial = _snapshotMaterial;
            renderer.enabled = false;
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
        }

        /// <summary>
        /// Generates a compound non-convex collider using Plawius VHACD. The native
        /// decomposition runs in Task.Run; this coroutine yields until it completes.
        /// Unity Mesh creation, asset creation and collider assignment remain on the
        /// main thread, as required by Unity's object APIs.
        /// </summary>
        private IEnumerator GenerateConvexColliderCoroutine(ClayContainer container, Mesh sourceMesh)
        {
            if (container == null || sourceMesh == null)
            {
                Debug.LogError("Cannot generate the convex collider: container or snapshot mesh is null.", container);
                yield break;
            }

            float startedAt = Time.realtimeSinceStartup;
            Parameters parameters = Parameters.LowResolution();
            parameters.maxConvexHulls = Mathf.Clamp(_settings.MaxConvexHulls, 1, 128);
            parameters.resolution = Mathf.Clamp(_settings.ConvexColliderResolution, 10000, 6400000);

            System.Threading.Tasks.Task<Mesh[]> generationTask;
            try
            {
                // IMPORTANT: do not use the synchronous GenerateConvexMeshes overload.
                // This async overload copies source mesh data on the main thread and
                // performs the expensive native VHACD decomposition on a worker thread.
                generationTask = API.GenerateConvexMeshesAsync(sourceMesh, parameters);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, container);
                yield break;
            }

            while (!generationTask.IsCompleted)
                yield return null;

            Mesh[] convexMeshes;
            try
            {
                // Safe because IsCompleted is true. Do not use Wait() or .Result here.
                convexMeshes = generationTask.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                Debug.LogError("Asynchronous convex collider generation failed.", container);
                Debug.LogException(exception, container);
                yield break;
            }

            if (convexMeshes == null || convexMeshes.Length == 0)
            {
                Debug.LogError("VHACD returned no convex hull meshes.", container);
                yield break;
            }

            // The snapshot may have been replaced or destroyed while VHACD was running.
            if (container == null || sourceMesh == null || sourceMesh != _snapshotMesh)
            {
                DestroyGeneratedConvexMeshes(convexMeshes);
                yield break;
            }

            GameObject colliderObject = null;
            NonConvexColliderAsset colliderAsset = null;
            try
            {
                float assetStartedAt = Time.realtimeSinceStartup;
                colliderAsset = NonConvexColliderAsset.CreateAsset(convexMeshes);
                float assetCreationMs = (Time.realtimeSinceStartup - assetStartedAt) * 1000f;

                // The plugin toggles its host while installing hulls. Keep the sculpture active.
                colliderObject = new GameObject("Generated clay colliders") { layer = container.gameObject.layer };
                colliderObject.SetActive(false);
                colliderObject.transform.SetParent(container.transform, false);
                var collider = colliderObject.AddComponent<NonConvexColliderComponent>();

                float attachStartedAt = Time.realtimeSinceStartup;
                collider.SetPhysicsCollider(colliderAsset);
                float attachMs = (Time.realtimeSinceStartup - attachStartedAt) * 1000f;

                _generatedColliderObject = colliderObject;
                _generatedColliderAsset = colliderAsset;

                if (_settings.LogTimings)
                {
                    float elapsedMs = (Time.realtimeSinceStartup - startedAt) * 1000f;
                    Debug.Log(
                        $"Convex collider generated: {convexMeshes.Length} hulls; " +
                        $"asset creation {assetCreationMs:F2} ms; collider assignment {attachMs:F2} ms; " +
                        $"{elapsedMs:F1} ms elapsed across frames. " +
                        $"VHACD used resolution {parameters.resolution} and max {parameters.maxConvexHulls} hulls.",
                        container);
                }
            }
            catch (Exception exception)
            {
                if (colliderObject != null)
                    Object.Destroy(colliderObject);
                if (colliderAsset != null)
                    Object.Destroy(colliderAsset);
                DestroyGeneratedConvexMeshes(convexMeshes);
                Debug.LogError("Convex collider asset creation or assignment failed.", container);
                Debug.LogException(exception, container);
            }
        }

        private static void DestroyGeneratedConvexMeshes(Mesh[] meshes)
        {
            if (meshes == null)
                return;

            for (int i = 0; i < meshes.Length; i++)
            {
                if (meshes[i] != null)
                    UnityEngine.Object.Destroy(meshes[i]);
            }
        }

        private void RestoreLiveClay()
        {
            if (_snapshotMesh != null || _snapshotComponentsCaptured)
                DestroyMeshSnapshot();

            if (_container == null)
                return;

            // Reactivate the ClayObject GameObjects first, then restore the behaviours
            // this component disabled. Objects that were originally inactive remain so.
            for (int i = 0; i < _deactivatedClayObjectGameObjects.Count; i++)
            {
                GameObject clayObjectGameObject = _deactivatedClayObjectGameObjects[i];
                if (clayObjectGameObject != null)
                    clayObjectGameObject.SetActive(true);
            }
            _deactivatedClayObjectGameObjects.Clear();

            for (int i = 0; i < _disabledClayObjects.Count; i++)
            {
                ClayObject clayObject = _disabledClayObjects[i];
                if (clayObject != null)
                    clayObject.enabled = true;
            }
            _disabledClayObjects.Clear();

            _container.setVisible(_containerWasVisibleBeforeMovement);
        }

        /// <summary>
        /// Restores the original MeshFilter/MeshRenderer state and removes only the
        /// components that this snapshot created. The snapshot is never a child object.
        /// </summary>
        private void DestroyMeshSnapshot()
        {
            if (_snapshotComponentsCaptured)
            {
                if (_snapshotRenderer != null)
                {
                    _snapshotRenderer.sharedMaterials = _originalContainerMaterials ?? Array.Empty<Material>();
                    _snapshotRenderer.enabled = _originalRendererEnabled;
                    _snapshotRenderer.shadowCastingMode = _originalShadowCastingMode;
                    _snapshotRenderer.receiveShadows = _originalReceiveShadows;
                }

                if (_snapshotMeshFilter != null)
                    _snapshotMeshFilter.sharedMesh = _originalContainerMesh;

                if (_containerSkinnedRenderer != null)
                    _containerSkinnedRenderer.enabled = _originalSkinnedRendererEnabled;
            }

            if (_createdSnapshotMeshRenderer && _snapshotRenderer != null)
                Object.Destroy(_snapshotRenderer);

            if (_createdSnapshotMeshFilter && _snapshotMeshFilter != null)
                Object.Destroy(_snapshotMeshFilter);

            if (_snapshotMesh != null)
                Object.Destroy(_snapshotMesh);

            if (_snapshotMaterial != null)
                Object.Destroy(_snapshotMaterial);

            _snapshotMeshFilter = null;
            _snapshotRenderer = null;
            _containerSkinnedRenderer = null;
            _snapshotMesh = null;
            _snapshotMaterial = null;

            _originalContainerMesh = null;
            _originalContainerMaterials = null;
            _snapshotComponentsCaptured = false;
            _createdSnapshotMeshFilter = false;
            _createdSnapshotMeshRenderer = false;
        }
    }
}
