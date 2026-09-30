using UnityEngine;

namespace CasualtiesJiggle
{
    public partial class JiggleBody
    {
        // wall press on the belly (0..1, fast attack / slow release) + probe side
        private float _press;
        private int _pressSide;
        private float _lastPressLog;
        private float _lastWallDbg; // @TODO REMOVE IT TEMPORARY wall-contact probe

        private void ProbeWall(Transform bt, float dt)
        {
            float best = 0f;
            int bestSide = 0;
            Collider2D bestHit = null;
            float bestDist = 0f;
            float bestEdge = 0f;
            if (JiggleConfig.WallSquash.Value > 0f && _bellyIndex >= 0)
            {
                Limb belly = _body.limbs[_bellyIndex];
                if (belly != null && !belly.dismembered && belly.gameObject.activeInHierarchy)
                {
                    Bounds lb;
                    if (_bellyMesh != null && _bellyMesh.Valid)
                        lb = _bellyMesh.BaseRect;
                    else
                    {
                        SpriteRenderer br = belly.GetComponent<SpriteRenderer>();
                        lb = br != null ? br.localBounds : default;
                    }
                    if (lb.extents.x > 0.01f)
                    {
                        float depth = Mathf.Max(0.05f, JiggleConfig.WallSquashDepth.Value);
                        bool hasSil =
                            _bellyMesh != null && _bellyMesh.Valid && _bellyMesh.HasSilhouette;
                        for (int sIdx = -1; sIdx <= 1; sIdx += 2)
                        {
                            Vector2 dir = sIdx > 0 ? Vector2.right : Vector2.left;
                            Vector2 dl = (Vector2)
                                bt.InverseTransformVector(new Vector3(dir.x, dir.y, 0f));
                            float adx = Mathf.Abs(dl.x);
                            if (adx < 1e-4f)
                                continue; // ray parallel to the belly plane
                            bool front = dl.x > 0f;
                            float extent = hasSil
                                ? (front ? _bellyMesh.FrontExtent : _bellyMesh.BackExtent)
                                : lb.extents.x;
                            float rowY = hasSil
                                ? (front ? _bellyMesh.FrontRowY : _bellyMesh.BackRowY)
                                : lb.center.y;
                            float edge = extent / adx;
                            Vector2 origin = bt.TransformPoint(new Vector3(0f, rowY, 0f));
                            RaycastHit2D hit = Physics2D.Raycast(
                                origin,
                                dir,
                                edge + depth,
                                _groundMask
                            );
                            if (!IsWallSurface(hit) || hit.fraction <= 0f)
                                continue;
                            float press = Mathf.Clamp01((edge - hit.distance) / depth);
                            if (press > best)
                            {
                                best = press;
                                bestSide = sIdx;
                                bestHit = hit.collider;
                                bestDist = hit.distance;
                                bestEdge = edge;
                            }
                        }
                    }
                }
            }
            if (bestSide != 0 && bestSide != _pressSide && _press > 0.2f && best < 0.9f)
            {
                best = 0f;
                bestSide = _pressSide;
            }
            if (bestSide != 0)
                _pressSide = bestSide;
            if (JiggleConfig.DebugEnabled.Value && best > 0.35f && bestHit != null && Time.time - _lastPressLog > 5f)
            {
                _lastPressLog = Time.time;
                JigglePlugin.Log.LogInfo(
                    $"[JiggleBody] belly press {best:0.00} side {_pressSide} hit '{bestHit.name}' edge {bestEdge:0.00} dist {bestDist:0.00}"
                );
            }

            float rate = best > _press ? 6f : 5f;
            _press += (best - _press) * (1f - Mathf.Exp(-rate * dt));

            bool phantom =
                _press < 0.05f
                && _bellyMesh != null
                && _bellyMesh.Valid
                && _bellyMesh.SoftContactsNow > 0;
            if (JiggleConfig.DebugEnabled.Value && (_press > 0.05f || phantom) && Time.time - _lastWallDbg >= 1f)
            {
                _lastWallDbg = Time.time;
                string tag = phantom ? " PHANTOM CONTACT" : "";
                float facing = _body.transform.localScale.x >= 0f ? 1f : -1f;
                bool frontPress =
                    ((Vector2)bt.InverseTransformVector(new Vector3(_pressSide, 0f, 0f))).x > 0f;
                string sil =
                    _bellyMesh != null && _bellyMesh.Valid && _bellyMesh.HasSilhouette
                        ? (
                            frontPress
                                ? $"front sil {_bellyMesh.FrontExtent:0.00}u (rect {_bellyMesh.BaseRect.extents.x:0.00}u)"
                                : $"back sil {_bellyMesh.BackExtent:0.00}u (rect {_bellyMesh.BaseRect.extents.x:0.00}u)"
                        )
                        : $"no silhouette (rect {(_bellyMesh != null && _bellyMesh.Valid ? _bellyMesh.BaseRect.extents.x : 0f):0.00}u)";
                if (JiggleConfig.SoftBody.Value && _bellyMesh != null && _bellyMesh.Valid)
                {
                    Vector3 soft = bt.TransformVector(
                        new Vector3(_bellyMesh.SoftMeanDisp.x, _bellyMesh.SoftMeanDisp.y, 0f)
                    );
                    bool away =
                        Mathf.Abs(soft.x) < 0.005f || Mathf.Sign(soft.x) != Mathf.Sign(_pressSide);
                    JigglePlugin.Log.LogInfo(
                        $"[JiggleWallDbg]{tag} press={_press:0.00} side={_pressSide:+0;-0} facing={facing:+0;-0} "
                            + $"softInterior.x={soft.x:0.000} springPos.x={_sPos.x:0.000} {sil} -> "
                            + (away ? "AWAY from wall (ok)" : "TOWARD the wall (BUG)")
                    );
                }
                else
                {
                    Vector3 woff = bt.TransformVector(new Vector3(_sPos.x, _sPos.y, 0f));
                    bool away =
                        Mathf.Abs(woff.x) < 0.005f || Mathf.Sign(woff.x) != Mathf.Sign(_pressSide);
                    JigglePlugin.Log.LogInfo(
                        $"[JiggleWallDbg]{tag} press={_press:0.00} side={_pressSide:+0;-0} facing={facing:+0;-0} "
                            + $"springPos.x={_sPos.x:0.000} worldVertexOffset.x={woff.x:0.000} {sil} -> "
                            + (away ? "AWAY from wall (ok)" : "TOWARD the wall (BUG)")
                    );
                }
            }
        }

        internal static bool IsWallSurface(RaycastHit2D hit)
        {
            if (hit.collider == null || hit.collider.isTrigger)
                return false;
            if (Mathf.Abs(hit.normal.y) > 0.5f)
                return false;
            return hit.collider.GetComponentInParent<Body>() == null;
        }
    }
}
