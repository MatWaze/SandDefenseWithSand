using System.Collections.Generic;
using Clayxels;
using UnityEngine;

namespace SandDefenseWithSand.Clay
{
    // Keeps an editable starter at the original sculpting site as each figurine leaves.
    [DisallowMultipleComponent]
    public sealed class FigurineStart : MonoBehaviour
    {
        [SerializeField] private ClayxelsHopper _firstFigurine;
        [SerializeField] private ClayxelsHopper _template;
        [Tooltip("Side length of the red no-dig square in world units. The square always covers the starter's base.")]
        [SerializeField, Min(0f)] private float _protectedGroundSize = .18f;

        private ClayxelsHopper _current;
        private Bounds _groundFootprint;
        private bool _hasGroundFootprint;
        private readonly ClayEditPreview _groundBoundary = new ClayEditPreview();
        private int _groundLayers;
        private readonly List<Vector3> _dashPoints = new List<Vector3>();
        private Bounds _sampledBounds;
        private Matrix4x4 _view, _projection;
        private int _pixelHeight;

        internal void InvalidateGroundBoundary() => _dashPoints.Clear();

        private void Awake()
        {
            if (_firstFigurine == null || _template == null)
            {
                Debug.LogError("Assign the initial figurine and the disabled starter template.", this);
                return;
            }

            // Snapshot the site from the inactive template so it stays put when a figurine leaves.
            foreach (BoxCollider box in _template.GetComponentsInChildren<BoxCollider>(true))
            {
                if (!box.enabled || (box.isTrigger && box != _template.PlacementAnchor))
                    continue;
                Transform boxTransform = box.transform;
                Vector3 halfSize = box.size * .5f;
                Vector3 x = boxTransform.TransformVector(Vector3.right * halfSize.x);
                Vector3 y = boxTransform.TransformVector(Vector3.up * halfSize.y);
                Vector3 z = boxTransform.TransformVector(Vector3.forward * halfSize.z);
                Vector3 size = new Vector3(Mathf.Abs(x.x) + Mathf.Abs(y.x) + Mathf.Abs(z.x), 0f,
                    Mathf.Abs(x.z) + Mathf.Abs(y.z) + Mathf.Abs(z.z)) * 2f;
                var bounds = new Bounds(boxTransform.TransformPoint(box.center), size);
                if (_hasGroundFootprint)
                    _groundFootprint.Encapsulate(bounds);
                else
                {
                    _groundFootprint = bounds;
                    _hasGroundFootprint = true;
                }
            }

            _current = _firstFigurine;
            _current.Reanimated += ReplaceStarter;
            _groundLayers = LayerMask.GetMask("Terrain");
        }

        private void ReplaceStarter(ClayxelsHopper previous)
        {
            ClayxelsHopper next = Instantiate(_template, _template.transform.position, _template.transform.rotation);
            next.name = _template.name;
            next.gameObject.SetActive(true);
            ClayContainer replacement = next.GetComponent<ClayContainer>();
            // Reanimation can finish after the normal Clayxels Update has run.
            // Submit the starter immediately so the site is visible this frame.
            replacement.computeClay();
            replacement.drawClayxels();
            Physics.SyncTransforms();

            previous.Reanimated -= ReplaceStarter;
            _current = next;
            _current.Reanimated += ReplaceStarter;
            ClayContainer previousContainer = previous.GetComponent<ClayContainer>();
            foreach (FigurineGeneratorBase generator in FindObjectsByType<FigurineGeneratorBase>(FindObjectsInactive.Include))
                if (generator.TargetFigurine == previousContainer)
                    generator.SetTargetFigurine(replacement);
        }

        internal bool OverlapsProtectedGround(Vector3 position, Vector2 halfBrushSize)
        {
            if (!_hasGroundFootprint)
                return false;

            Bounds bounds = GetProtectedGroundBounds();
            // A scoop centered outside the square can still reach into it.
            bounds.Expand(new Vector3(halfBrushSize.x, 0f, halfBrushSize.y) * 2f);
            return position.x >= bounds.min.x && position.x <= bounds.max.x &&
                position.z >= bounds.min.z && position.z <= bounds.max.z;
        }

        private Bounds GetProtectedGroundBounds()
        {
            Vector3 baseSize = _groundFootprint.size;
            float side = Mathf.Max(_protectedGroundSize, Mathf.Max(baseSize.x, baseSize.z));
            return new Bounds(_groundFootprint.center, new Vector3(side, 0f, side));
        }

        private void LateUpdate()
        {
            Camera camera = Camera.main;
            if (!_hasGroundFootprint || camera == null || camera.pixelHeight <= 0)
            {
                _pixelHeight = 0;
                _groundBoundary.Hide();
                return;
            }

            Bounds bounds = GetProtectedGroundBounds();
            bool resample = _dashPoints.Count == 0 || !bounds.Equals(_sampledBounds);
            if (!resample && _view.Equals(camera.worldToCameraMatrix) &&
                _projection.Equals(camera.projectionMatrix) && _pixelHeight == camera.pixelHeight)
                return;
            if (!_groundBoundary.Begin()) return;
            if (resample)
            {
                _groundBoundary.SampleGroundBoundary(bounds, _groundLayers, _dashPoints);
                _sampledBounds = bounds;
            }
            _view = camera.worldToCameraMatrix;
            _projection = camera.projectionMatrix;
            _pixelHeight = camera.pixelHeight;
            _groundBoundary.AddGroundBoundary(_dashPoints);
            _groundBoundary.Draw();
        }

        private void OnDisable()
        {
            InvalidateGroundBoundary();
            _groundBoundary.Hide();
        }

        private void OnDestroy()
        {
            _groundBoundary.Dispose();
            if (_current != null)
                _current.Reanimated -= ReplaceStarter;
        }
    }
}
