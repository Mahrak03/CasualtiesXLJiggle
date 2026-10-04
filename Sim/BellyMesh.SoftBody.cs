using System;
using System.Collections.Generic;
using UnityEngine;

namespace CasualtiesJiggle
{
    public partial class BellyMesh
    {
        private Vector2[] _pt; // current point position (local space)
        private Vector2[] _ptLast; // previous position: velocity = (pt - ptLast)/dt
        private Vector2[] _base2; // home positions (xy of _base) for the soft body
        private bool[] _ptPinned;
        private int _stride; // grid row stride (res + 1)
        private int[] _sprA; // neighbor spring endpoint indices (into the point arrays)
        private int[] _sprB;
        private float[] _sprRest; // rest lengths from the home positions
        private bool _softBuilt;

        // Stomach rumble stuff
        private Vector2[] _rumbleDir;
        private float[] _rumblePh;
        private float _rumbleAmp;
        private float _rumblePhase;

        public float SoftMaxDispNow { get; private set; }
        public float SoftMaxVelNow { get; private set; }
        public int SoftNaNResets { get; private set; }

        public Vector2 SoftMeanDisp { get; private set; }

        // PBD iterations per step
        private const int SoftIterations = 4;

        /// Builds the verlet points and neighbor springs from the current grid.
        private void BuildSoftBody(int res)
        {
            int n = (res + 1) * (res + 1);
            _stride = res + 1;
            if (_base2 == null || _base2.Length != n)
                _base2 = new Vector2[n];
            if (_pt == null || _pt.Length != n)
            {
                _pt = new Vector2[n];
                _ptLast = new Vector2[n];
                _ptPinned = new bool[n];
            }
            if (_ovf == null || _ovf.Length != n)
            {
                _ovf = new Vector2[n];
            }
            else
            {
                Array.Clear(_ovf, 0, n);
            }
            if (_contact == null || _contact.Length != n)
                _contact = new bool[n];

            _rumbleDir = new Vector2[n];
            _rumblePh = new float[n];
            _rumbleAmp = 0f;
            var rng = new System.Random(n * 7919 + res);
            for (int j = 0; j <= res; j++)
            {
                for (int i = 0; i <= res; i++)
                {
                    int idx = j * _stride + i;
                    Vector2 rd = new Vector2(
                        (float)(rng.NextDouble() * 1.2 - 0.6),
                        (float)(rng.NextDouble() * 2.0 - 1.0)
                    );
                    _rumbleDir[idx] = rd.sqrMagnitude > 1e-4f ? rd.normalized : Vector2.up;
                    _rumblePh[idx] = i * 0.7f + j * 1.1f + (float)rng.NextDouble() * 1.5f;
                }
            }

            for (int j = 0; j <= res; j++)
            {
                _rowY[j] = _base[j * _stride].y;
                for (int i = 0; i <= res; i++)
                {
                    int idx = j * _stride + i;
                    _base2[idx] = new Vector2(_base[idx].x, _base[idx].y);
                    _pt[idx] = _base2[idx];
                    _ptLast[idx] = _base2[idx];
                    _ptPinned[idx] = i == 0 || i == res || j == 0 || j == res;
                }
                _colX[j] = _base[j].x;
            }

            var aList = new List<int>(n * 4);
            var bList = new List<int>(n * 4);
            var restList = new List<float>(n * 4);
            for (int j = 0; j <= res; j++)
            {
                for (int i = 0; i <= res; i++)
                {
                    int a = j * _stride + i;
                    for (int d = 0; d < 4; d++)
                    {
                        int ni = i + (d == 0 || d == 2 || d == 3 ? 1 : 0);
                        int nj =
                            j
                            + (
                                d == 1 ? 1
                                : d == 2 ? 1
                                : d == 3 ? -1
                                : 0
                            );
                        if (ni < 0 || ni > res || nj < 0 || nj > res)
                            continue;
                        int b = nj * _stride + ni;
                        aList.Add(a);
                        bList.Add(b);
                        restList.Add(Vector2.Distance(_base2[a], _base2[b]));
                    }
                }
            }
            _sprA = aList.ToArray();
            _sprB = bList.ToArray();
            _sprRest = restList.ToArray();
            _softBuilt = true;
        }

        public void SoftStep(
            Vector2 drive,
            float dt,
            float press,
            int pressSide,
            bool nearGround,
            Transform bt,
            int groundMask
        )
        {
            if (!_softBuilt || bt == null || _stuck || dt <= 0f || !float.IsFinite(dt))
                return;
            float kSpring = Mathf.Clamp01(Profile.Stiffness) * 0.5f;
            float kHome = Mathf.Clamp01(Profile.HomePull);
            float damp = Mathf.Exp(-Mathf.Max(0.05f, Profile.Damping) * dt);
            float maxDisp = Mathf.Max(0.02f, Profile.MaxDisp);

            for (int i = 0; i < _pt.Length; i++)
            {
                if (_ptPinned[i])
                    continue;
                Vector2 v = (_pt[i] - _ptLast[i]) * damp;
                _ptLast[i] = _pt[i];
                _pt[i] += v;
            }

            for (int it = 0; it < SoftIterations; it++)
            {
                for (int s = 0; s < _sprA.Length; s++)
                {
                    int a = _sprA[s],
                        b = _sprB[s];
                    Vector2 d = _pt[b] - _pt[a];
                    float dist = d.magnitude;
                    if (dist < 1e-6f)
                        continue;
                    float err = dist - _sprRest[s];
                    if (err > -1e-4f && err < 1e-4f)
                        continue;
                    Vector2 c = d * (err * kSpring / dist);
                    if (!_ptPinned[a])
                        _pt[a] += c * _w[a];
                    if (!_ptPinned[b])
                        _pt[b] -= c * _w[b];
                }
                if (CollideEnabled)
                    CollidePoints(bt, groundMask);
            }

            for (int i = 0; i < _pt.Length; i++)
            {
                if (_ptPinned[i])
                    continue;
                Vector2 target =
                    _base2[i] + drive * _w[i] + (_ovf != null ? _ovf[i] : Vector2.zero);
                if (_rumbleAmp > 0f && _rumbleDir != null && i < _rumbleDir.Length)
                    target +=
                        _rumbleDir[i]
                        * (Mathf.Sin(_rumblePhase + _rumblePh[i]) * _rumbleAmp * _w[i]);
                Vector2 move = (target - _pt[i]) * kHome;
                float mm = move.magnitude;
                if (mm > 0.25f)
                    move = move / mm * 0.25f;
                _pt[i] += move;
                Vector2 d = _pt[i] - _base2[i];
                float m = d.magnitude;
                if (m > maxDisp)
                {
                    _pt[i] = _base2[i] + d / m * maxDisp;
                    _ptLast[i] += (_pt[i] - _ptLast[i]) * 0.5f;
                }
                if (!float.IsFinite(_pt[i].x) || !float.IsFinite(_pt[i].y))
                {
                    _pt[i] = _ptLast[i] = _base2[i]; // Thx wg mod for teaching me how to reset funky points!!!
                    SoftNaNResets++;
                }
            }

            _rumbleAmp = 0f;
            if (CollideEnabled)
                CollidePoints(bt, groundMask);
            UpdateOverflow(bt);

            Vector2 org = bt.position;
            for (int i = 0; i < _pt.Length; i++)
            {
                if (!_ptPinned[i])
                    continue;
                _pt[i] = _ptLast[i] = _base2[i];
                if (!CollideEnabled)
                    continue;
                Vector2 nw,
                    nn;
                float pn;
                if (
                    _vertA[i] >= CollideAlphaMin
                    && ProbePoint(bt, org, _pt[i], 0.03f, out nw, out nn, out pn)
                )
                    _pt[i] = _ptLast[i] = (Vector2)
                        bt.InverseTransformPoint(new Vector3(nw.x, nw.y, 0f));
            }

            // TEMPORARY tuning-probe outputs
            float md = 0f,
                mv = 0f;
            Vector2 mean = Vector2.zero;
            int meanN = 0;
            int gatedN = 0;
            for (int i = 0; i < _pt.Length; i++)
            {
                Vector2 d = _pt[i] - _base2[i];
                float m = d.magnitude;
                if (m > md)
                    md = m;
                float vm = (_pt[i] - _ptLast[i]).magnitude / dt;
                if (vm > mv)
                    mv = vm;
                if (_vertA[i] < CollideAlphaMin && (_ptPinned[i] || _w[i] > 0.02f))
                    gatedN++;
                if (!_ptPinned[i] && _w[i] > 0.33f)
                {
                    mean += d;
                    meanN++;
                }
            }
            SoftMaxDispNow = md;
            SoftMaxVelNow = mv;
            SoftMeanDisp = meanN > 0 ? mean / meanN : Vector2.zero;
            SoftAlphaSkipped = gatedN;
        }

        public void SoftImpulse(Vector2 dirLocal, float amount)
        {
            if (!_softBuilt)
                return;
            amount *= Profile.ImpulseMul;
            for (int i = 0; i < _pt.Length; i++)
            {
                if (_ptPinned[i] || _w[i] <= 0f)
                    continue;
                _ptLast[i] -= dirLocal * (amount * 0.2f * _w[i]); // vel += dir * amount * 10 * w
            }
        }

        public void SoftRumble(float amp, float phase)
        {
            if (!_softBuilt)
                return;
            _rumbleAmp = amp * Profile.RumbleMul;
            _rumblePhase = phase;
        }
    }
}
