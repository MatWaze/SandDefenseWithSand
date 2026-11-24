using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace SandDefenseWithSand.Clay
{
    // Builds the depth-tested aim guide and the edit outlines in one mesh.
    internal sealed class ClayEditPreview
    {
        internal enum AimState { Miss, Blocked, Ready, ProtectedGround }

        private const float RemoveWidth = 8f;
        private static readonly Color32 RemoveColor = new Color32(255, 52, 40, 255);
        private static readonly Color32 ReadyColor = new Color32(54, 239, 255, 255);
        private static readonly Color32 MissColor = new Color32(215, 224, 235, 255);
        private static readonly Color32 BlockedColor = new Color32(255, 151, 48, 255);
        private static Material _material;

        private readonly List<Vector3> _vertices = new List<Vector3>(144);
        private readonly List<Color32> _colors = new List<Color32>(144);
        private readonly List<Vector3> _uvs = new List<Vector3>(144);
        private readonly List<int> _triangles = new List<int>(216);
        private readonly List<int> _aimTriangles = new List<int>(30);
        private readonly List<int> _hiddenPlacementTriangles = new List<int>(36);
        private readonly Vector3[] _corners = new Vector3[8];
        private Mesh _mesh;
        private MeshRenderer _renderer;
        private Material _aimMaterial;
        private Material _placementMaterial;
        private Material _hiddenPlacementMaterial;
        private Camera _camera;

        public void ConfigurePlacementPreview(GameObject outline, Color color)
        {
            MeshRenderer renderer = outline.GetComponentInChildren<MeshRenderer>(true);
            if (renderer == null || !LoadMaterial())
                return;

            Material original = renderer.sharedMaterial;
            if (original != null && original.HasProperty("_BaseColor"))
                color = original.GetColor("_BaseColor");
            else if (original != null && original.HasProperty("_Color"))
                color = original.GetColor("_Color");

            _placementMaterial = new Material(_material) { renderQueue = _material.renderQueue - 3 };
            _placementMaterial.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
            _placementMaterial.SetFloat("_Cull", (float)CullMode.Back);
            _placementMaterial.SetFloat("_SolidFill", 1f);
            _placementMaterial.SetColor("_FillColor", color);
            renderer.sharedMaterial = _placementMaterial;
            renderer.shadowCastingMode = ShadowCastingMode.Off;

            Mesh mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null)
                return;

            foreach (MeshRenderer other in outline.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (other != renderer && other.localToWorldMatrix.Equals(renderer.localToWorldMatrix) &&
                    other.GetComponent<MeshFilter>()?.sharedMesh == mesh)
                    other.enabled = false;
            }
        }

        public void SetPlacementContact(Matrix4x4 clayBounds, float feather)
        {
            if (_placementMaterial == null)
                return;

            _placementMaterial.SetMatrix("_ContactWorldToBox", clayBounds.inverse);
            _placementMaterial.SetVector("_ContactBoxScale", new Vector3(
                ((Vector3)clayBounds.GetColumn(0)).magnitude,
                ((Vector3)clayBounds.GetColumn(1)).magnitude,
                ((Vector3)clayBounds.GetColumn(2)).magnitude));
            _placementMaterial.SetFloat("_ContactFeather", feather);
        }

        public bool Begin()
        {
            _camera = Camera.main;
            if (_camera == null || _camera.pixelHeight <= 0)
                return false;

            if (!LoadMaterial())
                return false;

            _vertices.Clear();
            _colors.Clear();
            _uvs.Clear();
            _triangles.Clear();
            _aimTriangles.Clear();
            _hiddenPlacementTriangles.Clear();
            return true;
        }

        private static bool LoadMaterial()
        {
            if (_material == null)
                _material = Resources.Load<Material>("Clay Edit Preview");
            return _material != null;
        }

        public void Draw()
        {
            if (_triangles.Count == 0 && _aimTriangles.Count == 0)
            {
                Hide();
                return;
            }

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "Clay edit previews" };
                _mesh.MarkDynamic();
                var display = new GameObject("Clay edit previews") { hideFlags = HideFlags.HideAndDontSave };
                display.AddComponent<MeshFilter>().sharedMesh = _mesh;
                _renderer = display.AddComponent<MeshRenderer>();
                _aimMaterial = new Material(_material) { renderQueue = _material.renderQueue - 2 };
                _aimMaterial.SetFloat("_ZTest", (float)CompareFunction.LessEqual);
                _hiddenPlacementMaterial = new Material(_material) { renderQueue = _material.renderQueue - 1 };
                _hiddenPlacementMaterial.SetFloat("_ZTest", (float)CompareFunction.Greater);
                _hiddenPlacementMaterial.SetFloat("_Dashed", 1f);
                _renderer.sharedMaterials = new[] { _aimMaterial, _hiddenPlacementMaterial, _material };
                _renderer.shadowCastingMode = ShadowCastingMode.Off;
                _renderer.receiveShadows = false;
            }

            _mesh.Clear();
            _mesh.SetVertices(_vertices);
            _mesh.SetColors(_colors);
            _mesh.SetUVs(0, _uvs);
            _mesh.subMeshCount = 3;
            _mesh.SetTriangles(_aimTriangles, 0);
            _mesh.SetTriangles(_hiddenPlacementTriangles, 1);
            _mesh.SetTriangles(_triangles, 2);
            _renderer.enabled = true;
        }

        public void Hide()
        {
            if (_renderer != null)
                _renderer.enabled = false;
        }

        public void Dispose()
        {
            Hide();
            if (_renderer != null)
                Object.Destroy(_renderer.gameObject);
            if (_mesh != null)
                Object.Destroy(_mesh);
            if (_aimMaterial != null)
                Object.Destroy(_aimMaterial);
            if (_placementMaterial != null)
                Object.Destroy(_placementMaterial);
            if (_hiddenPlacementMaterial != null)
                Object.Destroy(_hiddenPlacementMaterial);
        }

        public void AddAim(Vector3 origin, Vector3 guideEnd, Vector3 marker, AimState state)
        {
            bool missed = state == AimState.Miss;
            Color32 color = state == AimState.ProtectedGround ? RemoveColor :
                state == AimState.Ready ? ReadyColor : missed ? MissColor : BlockedColor;
            AddLine(_aimTriangles, origin, guideEnd, color, 6f,
                dashPeriod: missed ? 22f : 0f, dashed: missed, capScale: 0f);

            float radius = WorldUnitsPerPixel(marker) * 14f;
            Vector3 right = _camera.transform.right * radius;
            Vector3 up = _camera.transform.up * radius;
            if (!missed)
            {
                AddLine(_aimTriangles, marker + up, marker + right, color, 6f);
                AddLine(_aimTriangles, marker + right, marker - up, color, 6f);
                AddLine(_aimTriangles, marker - up, marker - right, color, 6f);
                AddLine(_aimTriangles, marker - right, marker + up, color, 6f);
            }

            if (state != AimState.Ready)
            {
                float crossSize = missed ? 0.65f : 0.32f;
                right *= crossSize;
                up *= crossSize;
                AddLine(_aimTriangles, marker + up - right, marker - up + right, color, 5f);
                AddLine(_aimTriangles, marker + up + right, marker - up - right, color, 5f);
            }
        }

        public void AddPlacementMarker(Vector3 position, Color32 color)
        {
            float radius = WorldUnitsPerPixel(position) * 9f;
            Vector3 right = _camera.transform.right * radius;
            Vector3 up = _camera.transform.up * radius;
            AddLine(_aimTriangles, position - right, position + right, color, 5f);
            AddLine(_aimTriangles, position - up, position + up, color, 5f);
        }

        public void AddGroundBoundary(List<Vector3> points)
        {
            for (int i = 0; i < points.Count; i += 2)
                AddLine(_aimTriangles, points[i], points[i + 1], RemoveColor, 6f, capScale: 0f);
        }

        public void SampleGroundBoundary(Bounds bounds, int groundLayers, List<Vector3> points)
        {
            points.Clear();
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            _corners[0] = new Vector3(min.x, bounds.center.y, min.z);
            _corners[1] = new Vector3(max.x, bounds.center.y, min.z);
            _corners[2] = new Vector3(max.x, bounds.center.y, max.z);
            _corners[3] = new Vector3(min.x, bounds.center.y, max.z);
            for (int i = 0; i < 4; i++)
            {
                Vector3 start = _corners[i];
                Vector3 end = _corners[(i + 1) % 4];
                int dashCount = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(start, end) / .04f));
                for (int dash = 0; dash < dashCount; dash++)
                {
                    Vector3 dashStart = GroundPoint(Vector3.Lerp(start, end, (float)dash / dashCount));
                    Vector3 dashEnd = GroundPoint(Vector3.Lerp(start, end, (dash + .6f) / dashCount));
                    points.Add(dashStart);
                    points.Add(dashEnd);
                }
            }

            Vector3 GroundPoint(Vector3 point)
            {
                if (Physics.Raycast(point + Vector3.up, Vector3.down, out RaycastHit hit,
                    2f, groundLayers, QueryTriggerInteraction.Ignore))
                    point.y = hit.point.y;
                return point + Vector3.up * .006f;
            }
        }

        public void AddRemoval(Matrix4x4 box)
        {
            AddBoxSilhouette(box, _triangles, RemoveColor, RemoveWidth);
        }

        public void AddPlacementBounds(Matrix4x4 box, Color32 color)
        {
            // Draw each edge solid where visible and dashed where scene depth hides it.
            int startIndex = _aimTriangles.Count;
            AddBoxSilhouette(box, _aimTriangles, color, 5f, true);
            for (int i = startIndex; i < _aimTriangles.Count; i++)
                _hiddenPlacementTriangles.Add(_aimTriangles[i]);
        }

        private void AddBoxSilhouette(Matrix4x4 box, List<int> triangles, Color32 color, float width,
            bool dashedWhenHidden = false)
        {
            for (int i = 0; i < _corners.Length; i++)
                _corners[i] = box.MultiplyPoint3x4(new Vector3((i & 1) - .5f, ((i >> 1) & 1) - .5f, ((i >> 2) & 1) - .5f));

            Vector3Int visibleSides = GetVisibleSides(box.inverse);
            for (int axis = 0; axis < 3; axis++)
            {
                int axisBit = 1 << axis;
                int sideAxisA = (axis + 1) % 3;
                int sideAxisB = (axis + 2) % 3;
                for (int i = 0; i < _corners.Length; i++)
                {
                    if ((i & axisBit) != 0)
                        continue;

                    // Only the boundary between a front and back face belongs to the silhouette.
                    bool faceAVisible = visibleSides[sideAxisA] == (((i >> sideAxisA) & 1) == 0 ? -1 : 1);
                    bool faceBVisible = visibleSides[sideAxisB] == (((i >> sideAxisB) & 1) == 0 ? -1 : 1);
                    if (faceAVisible != faceBVisible)
                        AddLine(triangles, _corners[i], _corners[i | axisBit], color, width,
                            dashPeriod: dashedWhenHidden ? 12f : 0f);
                }
            }
        }

        private Vector3Int GetVisibleSides(Matrix4x4 worldToBox)
        {
            bool orthographic = _camera.orthographic;
            Vector3 view = orthographic
                ? worldToBox.MultiplyVector(-_camera.transform.forward)
                : worldToBox.MultiplyPoint3x4(_camera.transform.position);
            float face = orthographic ? 0f : .5f;
            var sides = Vector3Int.zero;
            for (int axis = 0; axis < 3; axis++)
                sides[axis] = view[axis] < -face ? -1 : view[axis] > face ? 1 : 0;
            return sides;
        }

        private float WorldUnitsPerPixel(Vector3 position)
        {
            float viewHeight = _camera.orthographic
                ? 2f * _camera.orthographicSize
                : 2f * Mathf.Max(_camera.nearClipPlane, Vector3.Dot(position - _camera.transform.position, _camera.transform.forward))
                    * Mathf.Tan(_camera.fieldOfView * Mathf.Deg2Rad * .5f);
            return viewHeight / _camera.pixelHeight;
        }

        private void AddLine(List<int> triangles, Vector3 start, Vector3 end, Color32 color, float width,
            float dashPeriod = 0f, bool dashed = false, float capScale = .6f)
        {
            Vector3 midpoint = (start + end) * .5f;
            Vector3 cameraOffset = midpoint - _camera.transform.position;
            Vector3 forward = _camera.transform.forward;
            Vector3 side = Vector3.Cross(end - start, _camera.orthographic ? forward : cameraOffset).normalized;
            if (side == Vector3.zero)
                return;

            side *= width * WorldUnitsPerPixel(midpoint) * .5f;
            Vector3 cap = (end - start).normalized * (side.magnitude * capScale);
            start -= cap;
            end += cap;
            float repeats = dashPeriod > 0f
                ? Mathf.Max(1f, Vector2.Distance(_camera.WorldToScreenPoint(start), _camera.WorldToScreenPoint(end)) / dashPeriod)
                : 1f;
            float dash = dashed ? 1f : 0f;

            int first = _vertices.Count;
            _vertices.Add(start - side);
            _vertices.Add(start + side);
            _vertices.Add(end - side);
            _vertices.Add(end + side);
            _uvs.Add(new Vector3(0f, 0f, dash));
            _uvs.Add(new Vector3(0f, 1f, dash));
            _uvs.Add(new Vector3(repeats, 0f, dash));
            _uvs.Add(new Vector3(repeats, 1f, dash));
            for (int i = 0; i < 4; i++)
                _colors.Add(color);

            triangles.Add(first);
            triangles.Add(first + 1);
            triangles.Add(first + 2);
            triangles.Add(first + 2);
            triangles.Add(first + 1);
            triangles.Add(first + 3);
        }
    }
}
