using UnityEngine;

namespace CasualtiesJiggle
{
    public partial class BellyMesh
    {
        private bool[] _contact;
        private Vector2[] _ovf; // smoothed overflow offset per point (local space)
        private Vector2 _lastCSum,
            _lastNSum;
        private float _lastPen;
        private int _lastCN;

        // @TODO: TEMPORARY tuning-probe outputs (remove after tuning):
        public int SoftContactsNow => _lastCN;
        public float SoftPenNow => _lastPen;
        public int SoftAlphaSkipped { get; private set; }

        private const float OverflowGain = 1f; // overall strength
        private const float OverflowMax = 0.8f; // max offset, world units (8 px per unit ~= 6 px)
        private const float OverflowForward = 1f; // toward the wall
        private const float OverflowSide = 0.35f; // along the surface, away from the contact
        private const float OverflowRadius = 0.7f; // gaussian falloff distance from the contact edge

        private static int _groundMaskCached = -1;

        private void CollidePoints(Transform bt, int groundMask)
        {
            _groundMaskCached = groundMask;
            const float skin = 0.03f;
            if (_contact == null || _contact.Length != _pt.Length)
                _contact = new bool[_pt.Length];
            Vector2 origin = bt.position;
            Vector2 cSum = Vector2.zero,
                nSum = Vector2.zero;
            float penSum = 0f;
            int cN = 0;
            for (int i = 0; i < _pt.Length; i++)
            {
                _contact[i] = false;
                if (_ptPinned[i] || _w[i] <= 0.02f || _vertA[i] < CollideAlphaMin)
                    continue;
                Vector2 newWorld,
                    n;
                float pen;
                if (!ProbePoint(bt, origin, _pt[i], skin, out newWorld, out n, out pen))
                    continue;
                Vector3 vWorld = bt.TransformVector(
                    new Vector3(_pt[i].x - _ptLast[i].x, _pt[i].y - _ptLast[i].y, 0f)
                );
                float vn = vWorld.x * n.x + vWorld.y * n.y;
                if (vn < 0f)
                {
                    vWorld.x -= n.x * vn;
                    vWorld.y -= n.y * vn;
                }
                _pt[i] = (Vector2)bt.InverseTransformPoint(new Vector3(newWorld.x, newWorld.y, 0f));
                Vector2 vLocal = (Vector2)bt.InverseTransformVector(vWorld);
                _ptLast[i] = _pt[i] - vLocal;
                float cap = Mathf.Max(Profile.MaxDisp * 1.25f, 0.8f);
                Vector2 dc = _pt[i] - _base2[i];
                float cm = dc.magnitude;
                if (cm > cap)
                {
                    _pt[i] = _base2[i] + dc / cm * cap;
                    _ptLast[i] += (_pt[i] - _ptLast[i]) * 0.5f;
                }
                _contact[i] = true;
                cSum += newWorld;
                nSum += n;
                penSum += pen;
                cN++;
            }
            _lastCSum = cSum;
            _lastNSum = nSum;
            _lastPen = penSum;
            _lastCN = cN;
        }

        private void UpdateOverflow(Transform bt)
        {
            if (_ovf == null || _ovf.Length != _pt.Length)
                _ovf = new Vector2[_pt.Length];
            bool has =
                CollideEnabled
                && _contact != null
                && _lastCN > 0
                && _lastPen > 0.02f
                && _lastNSum.sqrMagnitude > 1e-6f;
            Vector2 centroid = Vector2.zero,
                nAvg = Vector2.up,
                tangent = Vector2.right;
            float tMin = 0f,
                tMax = 0f,
                amp = 0f;
            if (has)
            {
                centroid = _lastCSum / _lastCN;
                nAvg = _lastNSum.normalized;
                tangent = new Vector2(-nAvg.y, nAvg.x);
                tMin = float.PositiveInfinity;
                tMax = float.NegativeInfinity;
                for (int i = 0; i < _pt.Length; i++)
                {
                    if (!_contact[i])
                        continue;
                    Vector2 cw = bt.TransformPoint(new Vector3(_pt[i].x, _pt[i].y, 0f));
                    float al = Vector2.Dot(cw - centroid, tangent);
                    if (al < tMin)
                        tMin = al;
                    if (al > tMax)
                        tMax = al;
                }
                amp = Mathf.Min(OverflowMax, _lastPen * 0.03f * OverflowGain * Profile.OverflowMul);
            }
            float invR2 = 1f / (OverflowRadius * OverflowRadius);
            for (int i = 0; i < _pt.Length; i++)
            {
                Vector2 goal = Vector2.zero;
                if (has && !_ptPinned[i] && !_contact[i] && _w[i] > 0.02f && _vertA[i] >= CollideAlphaMin)
                {
                    Vector2 wp = bt.TransformPoint(new Vector3(_pt[i].x, _pt[i].y, 0f));
                    float along = Vector2.Dot(wp - centroid, tangent);
                    float d = along > tMax ? along - tMax : (along < tMin ? tMin - along : 0f);
                    float bump = Mathf.Exp(-d * d * invR2);
                    float sgn = along >= 0f ? 1f : -1f;
                    Vector2 wg =
                        (-nAvg * OverflowForward + tangent * (sgn * OverflowSide))
                        * (amp * bump * _w[i]);
                    goal = (Vector2)bt.InverseTransformVector(new Vector3(wg.x, wg.y, 0f));
                }
                _ovf[i] = Vector2.Lerp(_ovf[i], goal, 0.3f);
            }
        }

        private static bool ProbePoint(
            Transform bt,
            Vector2 origin,
            Vector2 local,
            float skin,
            out Vector2 newWorld,
            out Vector2 normal,
            out float pen
        )
        {
            newWorld = default;
            normal = default;
            pen = 0f;
            Vector2 target = bt.TransformPoint(new Vector3(local.x, local.y, 0f));
            Vector2 delta = target - origin;
            if (delta.magnitude < 0.05f)
                return false;
            RaycastHit2D hit = Physics2D.Linecast(origin, target, _groundMaskCached);
            if (hit.collider == null || hit.collider.isTrigger || hit.fraction <= 0f)
                return false;
            if (hit.collider.GetComponentInParent<Body>() != null)
                return false; // never the character's own colliders
            Vector2 n = hit.normal;
            if (Vector2.Dot(n, delta) > 0f)
                n = -n; // normal must face back toward the belly centre
            newWorld = hit.point + n * skin;
            normal = n;
            pen = (target - newWorld).magnitude;
            return true;
        }
    }
}
