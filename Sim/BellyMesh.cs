using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace CasualtiesJiggle
{
    public partial class BellyMesh : MonoBehaviour
    {
        private SpriteRenderer _sr;
        private MeshRenderer _mr;
        private MeshFilter _mf;
        private Mesh _mesh;

        internal const float CollideAlphaMin = 0.04f;

        private Vector3[] _base;      // undeformed vertex positions (limb local space)
        private Vector2[] _u01;      // normalized position inside the sprite rect (x right, y up)
        private Vector2[] _uv;
        private float[] _w;          // belly-region falloff weight; 0 on the border ring
        private float[] _vertA;      // per-vertex sprite alpha; collision only where visible
        private readonly List<Vector3> _verts = new List<Vector3>(256);
        private readonly List<Vector2> _uvs = new List<Vector2>(256);
        private readonly List<Vector3> _normals = new List<Vector3>(256);
        private readonly List<Vector4> _tangents = new List<Vector4>(256);
        private readonly List<Color32> _colors = new List<Color32>(256);
        private readonly List<int> _tris = new List<int>(2048);

        private Limb Owner { get; set; }
        private SoftProfile Profile { get; set; }
        private float _rx, _ry, _rw, _rh, _rf, _bulge;   // region bulge, fetched from the profile
        private static readonly bool DumpWeightMap = true;   // ASCII weight map in the log on every rebuild
        private Vector2 _regionCenter;   // in u01 space
        private float _regionHalfH = 0.3f;
        private Sprite _sprite;
        private Bounds _baseRect;        // bounds of the undeformed grid, for the wall probe
        private bool _warnedSpriteReenabled;
        private bool _warnedUngatedCollision;
        private MaterialPropertyBlock _mpb;

        // @TODO: TEMPORARY tuning-probe outputs (remove after tuning):
        public float LastMaxDisp { get; private set; }

        public float PixelsPerUnit { get; private set; } = 8f;
        public float MaxRegionWeight { get; private set; }


        public bool HasSilhouette { get; private set; }
        public float FrontExtent { get; private set; }
        public float BackExtent { get; private set; }
        public float FrontRowY { get; private set; }
        public float BackRowY { get; private set; }


        private float[] _rowY;         // local y of each grid row (points row stride = res+1)
        private float[] _rowFrontExt;  // per grid row: front silhouette extent (world units)
        private float[] _rowBackExt;   // per grid row: back silhouette extent
        private float[] _colX;         // local x of each grid column
        private float[] _colBotExt;    // per grid column: bottom silhouette extent (world units)

        public bool CollideEnabled = true;

        private static readonly bool HideMeshWhenStuck = true;
        private bool _stuck;

        public bool Valid { get; private set; }


        public void SetStuck(bool stuck)
        {
            if (!HideMeshWhenStuck)
                stuck = false;
            if (stuck == _stuck)
                return;
            _stuck = stuck;
            if (!Valid || _sr == null || _mr == null)
                return;
            _mr.enabled = !stuck;
            _sr.enabled = stuck;
            if (stuck)
                SoftReset();
        }

        public void SoftReset()
        {
            if (!_softBuilt)
                return;
            for (int i = 0; i < _pt.Length; i++)
            {
                _pt[i] = _ptLast[i] = _base2[i];
                if (_ovf != null && i < _ovf.Length)
                    _ovf[i] = Vector2.zero;
            }
        }

        public Sprite CurrentSprite => _sprite;

        public Bounds BaseRect => _baseRect;

        public static BellyMesh Build(Limb limb, SoftProfile profile)
        {
            if (limb == null || profile == null)
                return null;
            BellyMesh existing = Find(limb);
            if (existing != null && existing.Valid)
                return existing;

            SpriteRenderer sr = limb.GetComponent<SpriteRenderer>();
            if (sr == null || sr.sprite == null)
                return null;

            if (existing != null)
            {
                existing._sr = sr;
                existing.Profile = profile;
                existing.CollideEnabled = profile.Collide;
                if (existing.Rebuild())
                    return existing;
                Destroy(existing.gameObject);
                return null;
            }

            GameObject go = new GameObject("JiggleBellyMesh");
            go.layer = limb.gameObject.layer;
            go.transform.SetParent(limb.transform, false);
            BellyMesh bm = go.AddComponent<BellyMesh>();
            bm._sr = sr;
            bm.Owner = limb;
            bm.Profile = profile;
            bm.CollideEnabled = profile.Collide;
            if (!bm.Rebuild())
            {
                Destroy(go);
                return null;
            }
            return bm;
        }


        public static BellyMesh Find(Limb limb)
        {
            if (limb == null)
                return null;
            foreach (Transform c in limb.transform)
            {
                BellyMesh m = c.GetComponent<BellyMesh>();
                if (m != null && m.Owner == limb)
                    return m;
            }
            return null;
        }


        public bool Rebuild()
        {
            Sprite s = _sr != null ? _sr.sprite : null;
            if (s == null)
                return false;
            Texture2D tex = s.texture;
            if (tex == null)
                return false;
            int res = Mathf.Clamp(Mathf.RoundToInt(Mathf.Max(s.rect.width, s.rect.height) / Mathf.Max(1f, SoftProfile.CellPx)), 8, 18);
            Profile.GetRegion(out _rx, out _ry, out _rw, out _rh, out _rf, out _bulge);
            Rect r = s.rect;
            if (r.width < 1f || r.height < 1f)
                return false;
            if (!UvWithinRect(s, tex))
            {
                JigglePlugin.Log.LogWarning($"[BellyMesh] sprite '{s.name}' has UVs outside its rect (rotated atlas?) — not meshing this limb.");
                return false;
            }

            float ppu = s.pixelsPerUnit;
            if (ppu <= 0f)
                ppu = 8f;
            Vector2 piv = s.pivot;

            int n = (res + 1) * (res + 1);
            if (_base == null || _base.Length != n)
            {
                _base = new Vector3[n];
                _u01 = new Vector2[n];
                _uv = new Vector2[n];
                _w = new float[n];
                _vertA = new float[n];
            }

            Vector3 mn = new Vector3(float.PositiveInfinity, float.PositiveInfinity, 0f);
            Vector3 mx = new Vector3(float.NegativeInfinity, float.NegativeInfinity, 0f);
            for (int iy = 0; iy <= res; iy++)
            {
                float v01 = (float)iy / res;
                for (int ix = 0; ix <= res; ix++)
                {
                    float u01 = (float)ix / res;
                    int i = iy * (res + 1) + ix;
                    _base[i] = new Vector3(
                        (r.xMin - piv.x + u01 * r.width) / ppu,
                        (r.yMin - piv.y + v01 * r.height) / ppu, 0f);
                    _u01[i] = new Vector2(u01, v01);
                    _uv[i] = new Vector2(
                        (r.xMin + u01 * r.width) / tex.width,
                        (r.yMin + v01 * r.height) / tex.height);
                    // region falloff; the border ring is hard-pinned so seams can never open
                    float w = RegionWeight(u01, v01);
                    if (ix == 0 || ix == res || iy == 0 || iy == res)
                        w = 0f;
                    _w[i] = w;
                    mn = Vector3.Min(mn, _base[i]);
                    mx = Vector3.Max(mx, _base[i]);
                }
            }
            _baseRect = new Bounds((mn + mx) * 0.5f, mx - mn);

            _tris.Clear();
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int a = y * (res + 1) + x;
                    int b = a + 1;
                    int c = a + res + 1;
                    int d = c + 1;
                    // front side
                    _tris.Add(a); _tris.Add(c); _tris.Add(d);
                    _tris.Add(a); _tris.Add(d); _tris.Add(b);
                    // back side 
                    _tris.Add(a); _tris.Add(d); _tris.Add(c);
                    _tris.Add(a); _tris.Add(b); _tris.Add(d);
                }
            }

            if (_mesh == null)
            {
                _mesh = new Mesh { name = "JiggleBellyGrid" };
                _mesh.MarkDynamic();
            }
            else
            {
                _mesh.Clear();
            }
            _verts.Clear(); _uvs.Clear(); _normals.Clear(); _tangents.Clear(); _colors.Clear();
            for (int i = 0; i < n; i++)
            {
                _verts.Add(_base[i]);
                _uvs.Add(_uv[i]);
                _normals.Add(new Vector3(0f, 0f, -1f)); // what a SpriteRenderer quad provides
                _tangents.Add(new Vector4(1f, 0f, 0f, 0f));
                _colors.Add((Color32)_sr.color);   // what the SpriteRenderer would tint with
            }
            _mesh.SetVertices(_verts);
            _mesh.SetUVs(0, _uvs);
            _mesh.SetNormals(_normals);
            _mesh.SetTangents(_tangents);
            _mesh.SetColors(_colors);
            _mesh.SetTriangles(_tris, 0);

            float inf = 2f * Mathf.Max(
                Mathf.Max(JiggleConfig.MaxSquash.Value, JiggleConfig.SoftMaxDisp.Value),
                _baseRect.size.x) + 0.1f;
            Bounds inflated = _baseRect;
            inflated.Expand(inf);
            _mesh.bounds = inflated;

            if (_mf == null)
            {
                _mf = gameObject.AddComponent<MeshFilter>();
                _mr = gameObject.AddComponent<MeshRenderer>();
                _mr.shadowCastingMode = ShadowCastingMode.Off;
                _mr.receiveShadows = false;
                _mr.lightProbeUsage = LightProbeUsage.Off;
                _mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            }

            _mf.mesh = _mesh;
            _mr.sharedMaterial = _sr.sharedMaterial;   
            _mr.sortingLayerID = _sr.sortingLayerID;
            _mr.sortingOrder = _sr.sortingOrder;

            if (_mpb == null)
                _mpb = new MaterialPropertyBlock();
            _sr.GetPropertyBlock(_mpb);
            Material mat = _mr.sharedMaterial;
            _mpb.SetTexture("_MainTex", tex);
            if (mat != null)
            {
                if (mat.HasProperty("_AlphaTex") && mat.GetTexture("_AlphaTex") != null)
                    _mpb.SetTexture("_AlphaTex", mat.GetTexture("_AlphaTex"));
                if (mat.HasProperty("_EnableExternalAlpha"))
                    _mpb.SetFloat("_EnableExternalAlpha", mat.GetFloat("_EnableExternalAlpha"));
                if (mat.HasProperty("_RendererColor"))
                    _mpb.SetColor("_RendererColor", mat.GetColor("_RendererColor"));
                if (mat.HasProperty("_Color"))
                    _mpb.SetColor("_Color", mat.GetColor("_Color"));
                if (mat.HasProperty("_Flip"))
                    _mpb.SetVector("_Flip", mat.GetVector("_Flip"));
            }
            _mr.SetPropertyBlock(_mpb);

            _regionCenter = new Vector2(_rx + _rw * 0.5f, _ry + _rh * 0.5f);
            _regionHalfH = Mathf.Max(0.05f, _rh * 0.5f);

            // TEMPORARY tuning-probe outputs
            PixelsPerUnit = ppu;
            float maxW = 0f;
            for (int i = 0; i < _w.Length; i++)
                if (_w[i] > maxW)
                    maxW = _w[i];
            MaxRegionWeight = maxW;

            // Wall-contact silhouette
            HasSilhouette = false;
            FrontExtent = BackExtent = _baseRect.extents.x;
            FrontRowY = BackRowY = 0f;
            // per-row/column silhouette bands (allocated here, filled inside the pixel scan)
            int bandN = res + 1;
            if (_rowFrontExt == null || _rowFrontExt.Length != bandN)
            {
                _rowFrontExt = new float[bandN];
                _rowBackExt = new float[bandN];
                _rowY = new float[bandN];
                _colBotExt = new float[bandN];
                _colX = new float[bandN];
            }
            for (int i = 0; i < _vertA.Length; i++)
                _vertA[i] = 1f;
            try
            {
                int px0 = Mathf.RoundToInt(r.x);
                int py0 = Mathf.RoundToInt(r.y);
                int pw = Mathf.RoundToInt(r.width);
                int ph = Mathf.RoundToInt(r.height);
                if (pw > 0 && ph > 0)
                {
                    Color[] cols = tex.GetPixels(px0, py0, pw, ph); // row 0 = bottom, alpha 0..1
                    float pivX = piv.x, pivY = piv.y;
                    float front = 0f, back = 0f;
                    // per grid row/column silhouette bands (world-unit extents from the pivot)
                    for (int j = 0; j < _rowFrontExt.Length; j++)
                    {
                        _rowFrontExt[j] = 0f;
                        _rowBackExt[j] = 0f;
                    }
                    for (int j = 0; j < _colBotExt.Length; j++)
                        _colBotExt[j] = 0f;
                    for (int y = 0; y < ph; y++)
                    {
                        int fX = 0, bX = 0;
                        for (int x = pw - 1; x >= 0; x--)
                            if (cols[x + y * pw].a > CollideAlphaMin) { fX = x + 1; break; }
                        for (int x = 0; x < pw; x++)
                            if (cols[x + y * pw].a > CollideAlphaMin) { bX = x; break; }
                        if (fX == 0)
                            continue; // fully transparent row
                        float fext = (fX - pivX) / ppu;
                        float bext = (pivX - bX) / ppu;
                        int rowBand = Mathf.Clamp(y * (res + 1) / ph, 0, res);
                        if (fext > _rowFrontExt[rowBand]) _rowFrontExt[rowBand] = fext;
                        if (bext > _rowBackExt[rowBand]) _rowBackExt[rowBand] = bext;
                        float botExt = (pivY - r.yMin - y) / ppu;
                        for (int x = bX; x < fX; x++)
                        {
                            if (cols[x + y * pw].a <= CollideAlphaMin)
                                continue;
                            int colBand = Mathf.Clamp(x * (res + 1) / pw, 0, res);
                            if (botExt > _colBotExt[colBand])
                                _colBotExt[colBand] = botExt;
                        }
                        if (fext > front)
                        {
                            front = fext;
                            FrontRowY = (r.yMin + y - pivY) / ppu;
                        }
                        if (bext > back)
                        {
                            back = bext;
                            BackRowY = (r.yMin + y - pivY) / ppu;
                        }
                    }
                    // per-vertex alpha mask: gate collision on visible pixels (cols row 0 = bottom,
                    // same orientation as v01)
                    for (int iy = 0; iy <= res; iy++)
                    {
                        float v01 = (float)iy / res;
                        int fy = Mathf.Clamp(Mathf.RoundToInt(v01 * (ph - 1)), 0, ph - 1);
                        for (int ix = 0; ix <= res; ix++)
                        {
                            float u01 = (float)ix / res;
                            int fx = Mathf.Clamp(Mathf.RoundToInt(u01 * (pw - 1)), 0, pw - 1);
                            float a = 0f;
                            for (int oy = 0; oy <= 1; oy++)
                            {
                                int py = Mathf.Min(fy + oy, ph - 1);
                                for (int ox = 0; ox <= 1; ox++)
                                {
                                    int px = Mathf.Min(fx + ox, pw - 1);
                                    float pa = cols[px + py * pw].a;
                                    if (pa > a)
                                        a = pa;
                                }
                            }
                            _vertA[iy * (res + 1) + ix] = a;
                        }
                    }
                    if (front > 0f && back > 0f)
                    {
                        HasSilhouette = true;
                        FrontExtent = front;
                        BackExtent = back;
                    }
                }
            }
            catch
            {
                // texture not readable — keep the rect fallback; collision stays ungated
                for (int i = 0; i < _vertA.Length; i++)
                    _vertA[i] = 1f;
                if (!_warnedUngatedCollision)
                {
                    _warnedUngatedCollision = true;
                    JigglePlugin.Log.LogWarning(
                        $"[BellyMesh] sprite '{_sr.sprite.name}' texture is not readable — per-vertex alpha gate disabled, collision is ungated for this sprite.");
                }
            }

            BuildSoftBody(res);

            _sprite = s;
            Valid = true;
            _sr.enabled = _stuck;   // the mesh renders the sprite from now on (unless stuck)
            _mr.enabled = !_stuck;

            if (JiggleConfig.DebugEnabled.Value)
            {
                // TEMPORARY build-time diagnostics (remove after milestone-1 verification):
                // sprite atlas UVs, pivot math (mesh base rect vs the SpriteRenderer's own
                // localBounds), material/shader, sorting values, renderer bounds.
                try
                {
                    string uvsTxt = (s.uv != null && s.uv.Length > 0)
                        ? string.Join(" ", Array.ConvertAll(s.uv, u => u.ToString("0.###")))
                        : "none";
                    Bounds slb = _sr.localBounds;
                    bool match = Mathf.Abs(slb.center.x - _baseRect.center.x) < 0.01f
                              && Mathf.Abs(slb.center.y - _baseRect.center.y) < 0.01f
                              && Mathf.Abs(slb.size.x - _baseRect.size.x) < 0.02f
                              && Mathf.Abs(slb.size.y - _baseRect.size.y) < 0.02f;
                    string shaderName = _mr.sharedMaterial != null && _mr.sharedMaterial.shader != null
                        ? _mr.sharedMaterial.shader.name : "null";
                    JigglePlugin.Log.LogInfo(
                        "[BellyMesh] diag: sprite '" + s.name + "'" +
                        " rect=(" + r.xMin.ToString("0.#") + "," + r.yMin.ToString("0.#") + " " + r.width.ToString("0.#") + "x" + r.height.ToString("0.#") + ")" +
                        " tex=" + tex.width + "x" + tex.height +
                        " ppu=" + ppu + " pivot=" + piv.ToString("0.###") + "\n" +
                        "  sprite.uv=[" + uvsTxt + "]" +
                        " meshUV[0]=" + _uv[0].ToString("0.###") + " meshUV[last]=" + _uv[_uv.Length - 1].ToString("0.###") + "\n" +
                        "  sr.localBounds=" + slb.ToString("0.###") + " meshBase=" + _baseRect.ToString("0.###") +
                        " pivotMathMatch=" + (match ? "yes" : "NO") + "\n" +
                        "  material='" + (_mr.sharedMaterial != null ? _mr.sharedMaterial.name : "null") +
                        "' shader='" + shaderName + "'\n" +
                        "  sortingLayer='" + _mr.sortingLayerName + "' order=" + _mr.sortingOrder +
                        " rendererBounds=" + _mr.bounds.ToString("0.##") + "\n" +
                        "  texture plumbing: mat.HasProperty(_MainTex)=" + (mat != null && mat.HasProperty("_MainTex")) +
                        " mat._MainTex=" + TexDesc(mat != null ? mat.GetTexture("_MainTex") : null) +
                        " mpb._MainTex=" + TexDesc(_mpb.GetTexture("_MainTex")) +
                        " mpb._RendererColor=" + ((mat != null && mat.HasProperty("_RendererColor")) ? _mpb.GetColor("_RendererColor").ToString() : "n/a") + "\n" +
                        "  verts=" + _mesh.vertexCount + " tris=" + (_mesh.triangles.Length / 3) +
                        " meshOnFilter=" + (_mf.sharedMesh == _mesh));
                }
                catch { }
                DumpMap(res);
            }
            return true;
        }

        public void UpdateMesh(Vector2 springLocal)
        {
            if (!Valid)
                return;
            if (_sr.sprite != _sprite)
            {
                if (!Rebuild())
                {
                    Restore();
                    return;
                }
            }
            if (_stuck)
                return;   // vanilla sprite is drawn while stuck
            // Checklist probe: does anything re-enable the SpriteRenderer under us while the
            // mesh is active? (Would double-draw, not hide — logged once, not fought.)
            if (_sr.enabled && !_warnedSpriteReenabled)
            {
                _warnedSpriteReenabled = true;
                JigglePlugin.Log.LogWarning("[BellyMesh] the SpriteRenderer was re-enabled by the game/another mod while the belly mesh is active — check for double-drawing.");
            }

            // milestone 2: the soft body owns the vertices; each vertex renders its own point
            // displacement plus a vertical bulge driven by its own horizontal compression.
            if (_softBuilt && JiggleConfig.SoftBody.Value)
            {
                float bulgeGain = _bulge;
                float invHSoft = 1f / _regionHalfH;
                float maxDs = 0f;
                for (int i = 0; i < _base.Length; i++)
                {
                    Vector2 d = _pt[i] - _base2[i];
                    float by = 0f;
                    if (bulgeGain > 0f && Mathf.Abs(d.x) > 1e-5f && _w[i] > 0f)
                        by = Mathf.Abs(d.x) * bulgeGain * _w[i]
                            * Mathf.Clamp((_u01[i].y - _regionCenter.y) * invHSoft, -1f, 1f);
                    _verts[i] = new Vector3(_base[i].x + d.x, _base[i].y + d.y + by, _base[i].z);
                    float ds = d.sqrMagnitude;
                    if (ds > maxDs)
                        maxDs = ds;
                }
                LastMaxDisp = Mathf.Sqrt(maxDs);
                _mesh.SetVertices(_verts);
                return;
            }
            float bulge = Mathf.Abs(springLocal.x) * _bulge;
            float invH = 1f / _regionHalfH;
            float maxDispSqr = 0f;   // TEMPORARY tuning probe
            for (int i = 0; i < _base.Length; i++)
            {
                float w = _w[i];
                float by = 0f;
                if (bulge > 0f && w > 0f)
                    by = bulge * w * Mathf.Clamp((_u01[i].y - _regionCenter.y) * invH, -1f, 1f);
                float dx = springLocal.x * w;
                float dy = springLocal.y * w + by;
                float ds = dx * dx + dy * dy;
                if (ds > maxDispSqr)
                    maxDispSqr = ds;
                _verts[i] = new Vector3(
                    _base[i].x + dx,
                    _base[i].y + dy,
                    _base[i].z);
            }
            LastMaxDisp = Mathf.Sqrt(maxDispSqr);   // TEMPORARY tuning probe
            _mesh.SetVertices(_verts);
        }

        public void Restore()
        {
            _softBuilt = false;
            if (!Valid)
                return;
            Valid = false;
            // Hide the mesh too, or the mesh and the sprite both draw.
            if (_mr != null)
                _mr.enabled = false;
            if (_sr != null)
                _sr.enabled = true;
        }

        private void OnDestroy()
        {
            Restore();
            if (_mesh != null)
            {
                Destroy(_mesh);
                _mesh = null;
            }
        }

        // --- helpers ---


        private void DumpMap(int res)
        {
            if (!DumpWeightMap)
                return;
            const string ramp = " .:-=+*#%@";
            var sb = new System.Text.StringBuilder();
            sb.Append("[BellyMesh] weight map '").Append(Profile.Name).Append("' res=").Append(res)
              .Append(" region=(").Append(_rx.ToString("0.00")).Append(',').Append(_ry.ToString("0.00"))
              .Append(' ').Append(_rw.ToString("0.00")).Append('x').Append(_rh.ToString("0.00")).Append("):");
            for (int j = res; j >= 0; j--)
            {
                sb.Append('\n');
                for (int i = 0; i <= res; i++)
                    sb.Append(ramp[Mathf.Clamp((int)(_w[j * _stride + i] * 9f), 0, 9)]);
            }
            JigglePlugin.Log.LogInfo(sb.ToString());
        }

        private static string TexDesc(Texture t)
        {
            if (t == null)
                return "none";
            Texture2D t2 = t as Texture2D;
            return "'" + t.name + "' id=" + t.GetInstanceID()
                + (t2 != null ? (" " + t2.width + "x" + t2.height) : "");
        }

        private static bool UvWithinRect(Sprite s, Texture2D tex)
        {
            Vector2[] suv = s.uv;
            if (suv == null || suv.Length == 0)
                return true; // nothing to verify; assume the rect
            Rect r = s.rect;
            Vector2 lo = new Vector2(r.xMin / tex.width, r.yMin / tex.height);
            Vector2 hi = new Vector2(r.xMax / tex.width, r.yMax / tex.height);
            const float eps = 0.002f;
            foreach (Vector2 uv in suv)
            {
                if (uv.x < lo.x - eps || uv.x > hi.x + eps || uv.y < lo.y - eps || uv.y > hi.y + eps)
                    return false;
            }
            return true;
        }

        private float RegionWeight(float u, float v)
        {
            float m = Mathf.Max(0.03f, _rf);
            return Edge01(u, _rx, _rx + _rw, m) * Edge01(v, _ry, _ry + _rh, m);
        }

        private static float Edge01(float t, float lo, float hi, float m)
        {
            return Smooth(Mathf.Clamp01((t - (lo - m)) / m)) * Smooth(Mathf.Clamp01(((hi + m) - t) / m));
        }

        private static float Smooth(float t)
        {
            return t * t * (3f - 2f * t);
        }
    }
}
