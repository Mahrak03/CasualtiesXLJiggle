using UnityEngine;

namespace CasualtiesJiggle
{
    /// Per-body jiggle simulation, redesigned after WgMod's WgPhysics.
    public partial class JiggleBody : MonoBehaviour
    {
        private Body _body;
        private int _bellyIndex = -1;
        private BellyMesh _bellyMesh;
        private bool _meshAttempted;
        private float _meshRetryAfter = -1f;

        // optional chest soft body on the UpTorso limb (see SoftProfile.ChestEnabled)
        private BellyMesh _chestMesh;
        private float _chestRetryAfter = -1f;
        private float _chestShareBase;

        private Vector2 _sPos;
        private Vector2 _sVel;
        private Vector2 _smoothAccel; // smoothed WORLD acceleration of the body
        private Vector2 _lastVel; // last sampled velocity of the accel source
        private bool _haveVel;
        private Vector2 _meshDrive;
        private float _pendingMeshTime;
        private float _meshStepDt;

        // how much of the shared spring each limb rides (index like Body.limbs)
        private float[] _share;
        private Vector3[] _appliedLocalOff;
        private Vector3[] _lastWrittenPos;
        private bool[] _appliedValid;

        private float _wobble; // weight-coupled global amplitude (0 at stage 0)
        private bool _wasGrounded = true;
        private float _stuckHold;
        private float _airVelY;
        private float _fallF; // fall state 0..1 (airborne + descending), fast attack / slow release
        private float _breathT;
        private float _lastDbg; // TEMPORARY 1 Hz tuning probe

        private static int _groundMask = -1;

        public static JiggleBody ForBody(Body body)
        {
            if (body == null || !JiggleConfig.Enabled.Value)
                return null;
            return body.GetComponent<JiggleBody>();
        }

        private void Start()
        {
            _body = GetComponent<Body>();
            RebuildState();
        }

        private void OnDisable()
        {
            UndoAll();
            RestoreMesh();
        }

        private void OnDestroy()
        {
            UndoAll();
            RestoreMesh();
        }

        private void RebuildState()
        {
            UndoAll();
            _pendingMeshTime = 0f;
            _bellyMesh = null;
            _meshAttempted = false;
            _meshRetryAfter = -1f;
            _chestMesh = null;
            _chestRetryAfter = -1f;
            _bellyIndex = -1;
            if (_body == null || _body.limbs == null || _body.limbs.Length == 0)
                return;
            int n = _body.limbs.Length;
            _share = new float[n];
            _appliedLocalOff = new Vector3[n];
            _lastWrittenPos = new Vector3[n];
            _appliedValid = new bool[n];

            // Body.LimbNum: Head=0, UpTorso=1, DownTorso=2, arms 3..8, legs 9..14.
            for (int i = 0; i < n; i++)
            {
                Limb l = _body.limbs[i];
                if (l == null)
                    continue;
                if ((l.isAbdomen || i == 2) && _bellyIndex < 0)
                {
                    _bellyIndex = i;
                    continue;
                }
                bool isTorso = i == 1;
                bool isHead = l.isHead || i == 0;
                // shares of the shared spring: chest rides the belly the most, planted feet the least
                float s =
                    l.isLegLimb ? 0.08f
                    : isTorso ? 0.35f
                    : isHead ? 0.20f
                    : 0.25f;
                // limbs that visually grow with weight wobble more (like WgMod's stage layers)
                _share[i] = s * (1f + Mathf.Max(0f, l.weightVisualScaleMult) * 0.35f);
            }
            int chestIdx = SoftProfile.Chest.LimbIndex;
            _chestShareBase = chestIdx < n ? _share[chestIdx] : 0f;
            if (chestIdx < n && _body.limbs[chestIdx] != null)
                _chestMesh = BellyMesh.Find(_body.limbs[chestIdx]);
            if (_bellyIndex >= 0)
            {
                // adopt a surviving mesh (limb arrays can rebuild around it)
                _bellyMesh = BellyMesh.Find(_body.limbs[_bellyIndex]);
                _meshAttempted = _bellyMesh != null;
                _share[_bellyIndex] =
                    (_bellyMesh != null && _bellyMesh.Valid) || JiggleConfig.MeshBelly.Value
                        ? 0f
                        : 1f;
            }

            if (_groundMask == -1)
                _groundMask = LayerMask.GetMask("Ground");
        }

        private void FixedUpdate()
        {
            if (_body == null || _body.limbs == null)
                return;
            if (_share == null || _share.Length != _body.limbs.Length)
                RebuildState();
            if (_share == null || !JiggleConfig.Enabled.Value || Time.timeScale <= 0f)
            {
                _pendingMeshTime = 0f;
                return;
            }

            float dt = Time.fixedDeltaTime;
            if (dt <= 0f || !float.IsFinite(dt))
            {
                _pendingMeshTime = 0f;
                return;
            }

            float amount;
            if (JiggleConfig.ScaleWithWeight.Value)
            {
                int stage = CasualtiesExtraApi.GetWeightStage(_body);
                float stageF;
                if (stage > 0)
                {
                    float lower = (stage > 8) ? 460f : 60f * stage;
                    stageF = stage + Mathf.Clamp01(Mathf.Max(0f, _body.weightOffset - lower) / 60f);
                }
                else
                {
                    stageF = Mathf.Max(0f, _body.weightOffset) / 60f;
                }
                amount = stageF < 1f ? 0f : 0.5f + stageF * 0.55f; // stage 1 -> ~1.05, stage 8+ -> capped 4
                amount = Mathf.Min(amount, 4f);
            }
            else
            {
                amount = 1f;
            }
            amount *= JiggleConfig.Intensity.Value;
            _wobble = Mathf.Max(amount, 0f);

            // XL flips stuck off for a moment while wiggling, so hold it for 0.3 s.
            if (CasualtiesExtraApi.GetStuck(_body))
                _stuckHold = 0.3f;
            else
                _stuckHold = Mathf.Max(0f, _stuckHold - dt);
            bool stuckBody = _stuckHold > 0f;

            // Stage 0 nothing else to simulate.
            if (_wobble <= 0.001f)
            {
                _pendingMeshTime = 0f;
                _sPos *= 0.85f;
                _sVel *= 0.85f;
                _press *= 0.85f;
                if (_bellyMesh != null && _bellyMesh.Valid)
                    _bellyMesh.SoftReset();
                if (_chestMesh != null && _chestMesh.Valid)
                    _chestMesh.SoftReset();
                _fallF *= 0.85f;
                return;
            }

            if (!JiggleConfig.MeshBelly.Value)
            {
                if (_bellyMesh != null)
                {
                    _bellyMesh.Restore();
                    _bellyMesh = null;
                    _meshAttempted = false;
                }
                if (_bellyIndex >= 0)
                    _share[_bellyIndex] = 1f;
            }
            else if (_bellyIndex >= 0 && _bellyMesh == null && !_meshAttempted)
            {
                if (Time.time >= _meshRetryAfter)
                {
                    _bellyMesh = BellyMesh.Build(_body.limbs[_bellyIndex], SoftProfile.Belly);
                    _meshAttempted = true;
                    if (_bellyMesh != null && JiggleConfig.DebugEnabled.Value)
                    {
                        Sprite built = _bellyMesh.CurrentSprite;
                        JigglePlugin.Log.LogInfo(
                            $"[JiggleBody] belly mesh built on limb {_bellyIndex} (sprite '{(built != null ? built.name : "null")}')."
                        );
                    }
                    else
                    {
                        _meshRetryAfter = Time.time + 10f;
                    }
                }
                if (_bellyMesh == null && _meshAttempted && _bellyIndex >= 0)
                    _share[_bellyIndex] = 1f;
            }
            if (_bellyMesh != null && !_bellyMesh.Valid)
            {
                _bellyMesh = null;
                _meshAttempted = false;
                _meshRetryAfter = Time.time + 10f;
                if (_bellyIndex >= 0)
                    _share[_bellyIndex] = 1f;
            }
            if (_bellyMesh != null && _bellyIndex >= 0)
                _share[_bellyIndex] = 0f;

            Limb belly = _bellyIndex >= 0 ? _body.limbs[_bellyIndex] : null;
            Transform bt = belly != null ? belly.transform : transform;

            int ci = SoftProfile.Chest.LimbIndex;
            if (ci < _body.limbs.Length && ci != _bellyIndex)
            {
                if (
                    SoftProfile.ChestEnabled
                    && JiggleConfig.MeshBelly.Value
                    && _body.limbs[ci] != null
                )
                {
                    if (_chestMesh == null && Time.time >= _chestRetryAfter)
                    {
                        _chestMesh = BellyMesh.Build(_body.limbs[ci], SoftProfile.Chest);
                        if (_chestMesh != null)
                        {
                            if (JiggleConfig.DebugEnabled.Value)
                                JigglePlugin.Log.LogInfo(
                                    $"[JiggleBody] chest mesh built on limb {ci}."
                                );
                        }
                        else
                            _chestRetryAfter = Time.time + 10f;
                    }
                    if (_chestMesh != null && !_chestMesh.Valid)
                    {
                        _chestMesh = null;
                        _chestRetryAfter = Time.time + 10f;
                    }
                }
                else if (_chestMesh != null)
                {
                    _chestMesh.Restore();
                    _chestMesh = null;
                }
                // with a mesh the chest deforms itself; otherwise it rides the shared spring
                _share[ci] = (_chestMesh != null && _chestMesh.Valid) ? 0f : _chestShareBase;
            }

            Rigidbody2D src = _body.standing
                ? _body.rb
                : ((belly != null && belly.rb != null && belly.rb.simulated) ? belly.rb : _body.rb);
            Vector2 v = src != null ? src.velocity : Vector2.zero;
            if (!_haveVel)
            {
                _lastVel = v;
                _haveVel = true;
            }
            Vector2 dv = v - _lastVel;
            _lastVel = v;
            if (dv.sqrMagnitude > 625f)
            {
                // >25 world units in one step: a teleport or a physics slam — never feed it in
                _smoothAccel *= 0.3f;
            }
            else
            {
                Vector2 raw = dv / dt;
                float clamp = JiggleConfig.AccelClamp.Value;
                if (raw.sqrMagnitude > clamp * clamp)
                    raw = raw.normalized * clamp;
                float alpha =
                    1f - Mathf.Exp(-dt / Mathf.Max(0.01f, JiggleConfig.AccelSmoothTime.Value));
                _smoothAccel = Vector2.Lerp(_smoothAccel, raw, alpha);
            }

            // --- fall state: airborne + descending fast enough to billow the belly up ---
            float fallTarget = !_body.grounded && v.y < -2f ? Mathf.Clamp01((-v.y - 2f) / 10f) : 0f;
            float fallRate = fallTarget > _fallF ? 8f : 5f;
            _fallF += (fallTarget - _fallF) * (1f - Mathf.Exp(-fallRate * dt));

            Vector2 gravL = (Vector2)
                bt.InverseTransformVector(
                    new Vector3(Physics2D.gravity.x, Physics2D.gravity.y, 0f)
                );
            Vector2 accL = (Vector2)
                bt.InverseTransformVector(new Vector3(_smoothAccel.x, _smoothAccel.y, 0f));
            float sagGain = JiggleConfig.GravitySag.Value * 0.045f * Mathf.Clamp(_wobble, 0f, 2.5f);
            float sagMul = 1f - _fallF * JiggleConfig.FallSagFade.Value;
            Vector2 rest = (gravL - accL) * (sagGain * JiggleConfig.InertiaGain.Value * sagMul);

            float maxSq = JiggleConfig.MaxSquash.Value;
            float driveCap = maxSq * 0.5f;
            if (rest.sqrMagnitude > driveCap * driveCap)
                rest = rest.normalized * driveCap;

            Vector2 lift = Vector2.zero;
            if (_fallF > 0.001f)
            {
                Vector2 upL = gravL.sqrMagnitude > 1e-10f ? -gravL.normalized : Vector2.up;
                float wob4 = Mathf.Clamp(_wobble, 0f, 4f) / 4f;
                lift = upL * (JiggleConfig.FallLift.Value * maxSq * wob4 * _fallF);
                rest += lift;
            }

            Vector2 wallAdd = Vector2.zero; // kept out of the soft body's drive (collision handles walls)
            if (_press > 0.001f && JiggleConfig.WallSquash.Value > 0f && belly != null)
            {
                Vector2 wdir = (Vector2)bt.InverseTransformVector(new Vector3(-_pressSide, 0f, 0f));
                float m = wdir.magnitude;
                if (m > 1e-5f)
                {
                    float width =
                        _bellyMesh != null && _bellyMesh.Valid
                            ? (
                                _bellyMesh.HasSilhouette
                                    ? Mathf.Max(
                                        0.05f,
                                        _bellyMesh.FrontExtent + _bellyMesh.BackExtent
                                    )
                                    : Mathf.Max(0.05f, _bellyMesh.BaseRect.size.x)
                            )
                            : 1f;
                    float fat = Mathf.Clamp01(0.5f + _wobble * 0.25f);
                    float wallCap = maxSq * 0.5f;
                    float depth =
                        _press
                        * JiggleConfig.WallSquash.Value
                        * (JiggleConfig.WallSquashMax.Value * width)
                        * fat;
                    depth = Mathf.Min(depth, wallCap);
                    wallAdd = (wdir / m) * depth;
                    rest += wallAdd;
                }
            }

            // idle breathing, if enabled
            if (JiggleConfig.BellyBreathing.Value)
            {
                _breathT += dt;
                float rate = 1f + _wobble * 0.12f;
                rest.y += Mathf.Sin(_breathT * rate * Mathf.PI) * 0.05f * (0.5f + _wobble * 0.25f);
            }

            Vector2 restSoft = rest - wallAdd;
            float restCap = maxSq * 1.5f;
            if (restSoft.sqrMagnitude > restCap * restCap)
                restSoft = restSoft.normalized * restCap;
            if (rest.sqrMagnitude > restCap * restCap)
                rest = rest.normalized * restCap;
            if (!float.IsFinite(rest.x) || !float.IsFinite(rest.y))
                rest = Vector2.zero;
            if (!float.IsFinite(restSoft.x) || !float.IsFinite(restSoft.y))
                restSoft = Vector2.zero;
            // While stuck the belly mesh is hidden, so calm the spring or the other limbs drift away from the belly.
            if (stuckBody)
            {
                rest = Vector2.zero;
                restSoft = Vector2.zero;
                _sPos *= 0.7f;
                _sVel *= 0.5f;
            }

            float rumbleAmpNow = 0f;
            float rumbleSin = 0f;
            if (_rumbleLeft > 0f)
            {
                _rumbleLeft -= dt;
                float prog = 1f - Mathf.Clamp01(_rumbleLeft / Mathf.Max(0.05f, _rumbleDur));
                float env = Mathf.Clamp01(prog / 0.15f) * Mathf.Clamp01((1f - prog) / 0.3f);
                float churn = 0.6f + 0.4f * Mathf.PerlinNoise(Time.time * 4f, _rumbleSeed);
                float freq =
                    _rumbleFreq
                    * (0.85f + 0.3f * Mathf.PerlinNoise(Time.time * 2f, _rumbleSeed + 7f));
                freq = Mathf.Min(freq, 0.4f / dt); // stay under Nyquist for the fixed step
                _rumblePhase += 2f * Mathf.PI * freq * dt;
                rumbleAmpNow = _rumbleAmp * env * churn;
                rumbleSin = Mathf.Sin(_rumblePhase);
            }
            if (_reboundT > 0f)
            {
                _reboundT -= dt;
                if (_reboundT <= 0f)
                {
                    _sVel += _reboundDir * (_reboundImp * 0.6f);
                    SoftImpulseDir(_reboundDir, _reboundImp);
                }
            }
            if (rumbleAmpNow > 0f)
            {
                rest += Vector2.up * (rumbleSin * rumbleAmpNow * 0.12f);
                if (JiggleConfig.SoftBody.Value)
                {
                    if (_bellyMesh != null && _bellyMesh.Valid)
                        _bellyMesh.SoftRumble(rumbleAmpNow, _rumblePhase);
                    if (_chestMesh != null && _chestMesh.Valid)
                        _chestMesh.SoftRumble(rumbleAmpNow, _rumblePhase);
                }
            }

            float k = JiggleConfig.BellyStiffness.Value;
            float damp = Mathf.Exp(-JiggleConfig.Damping.Value * dt);
            _sVel += (rest - _sPos) * k * dt;
            _sVel *= damp;
            _sPos += _sVel * dt;
            if (
                !float.IsFinite(_sPos.x)
                || !float.IsFinite(_sPos.y)
                || !float.IsFinite(_sVel.x)
                || !float.IsFinite(_sVel.y)
            )
            {
                _sPos = Vector2.zero;
                _sVel = Vector2.zero;
            }
            if (_sPos.sqrMagnitude > maxSq * maxSq)
            {
                _sPos = _sPos.normalized * maxSq;
                _sVel *= 0.6f;
            }

            bool wasGrounded = _wasGrounded;
            _wasGrounded = _body.grounded;
            if (!_body.grounded && _body.rb != null)
                _airVelY = _body.rb.velocity.y;
            if (_body.grounded && !wasGrounded && _airVelY < -3f)
            {
                float imp =
                    (-_airVelY - 3f)
                    * 0.5f
                    * Mathf.Min(_wobble, 2.5f)
                    * JiggleConfig.LandingImpulse.Value;
                _sVel += LocalDown() * imp;
                SoftImpulseAll(imp);
            }

            bool stuckNow = (_bellyMesh != null || _chestMesh != null) && stuckBody;
            if (_bellyMesh != null && _bellyMesh.Valid)
                _bellyMesh.SetStuck(stuckNow);
            if (_chestMesh != null && _chestMesh.Valid)
                _chestMesh.SetStuck(stuckNow);
            // Coalesce catch-up ticks: cosmetic grid solving must not scale with fast-forward.
            _meshDrive = restSoft;
            _meshStepDt = dt;
            _pendingMeshTime = Mathf.Min(_pendingMeshTime + dt, 0.1f);

            if (JiggleConfig.DebugEnabled.Value && Time.time - _lastDbg >= 1f)
            {
                _lastDbg = Time.time;
                float posMag = _sPos.magnitude;
                float restMag = rest.magnitude;
                float aMag = _smoothAccel.magnitude;
                string meshInfo =
                    _bellyMesh != null && _bellyMesh.Valid
                        ? $" meshMaxDisp={_bellyMesh.LastMaxDisp:0.000}u ({_bellyMesh.LastMaxDisp * _bellyMesh.PixelsPerUnit:0.00}px) maxW={_bellyMesh.MaxRegionWeight:0.00}"
                        : " (no mesh)";
                string softInfo =
                    JiggleConfig.SoftBody.Value && _bellyMesh != null && _bellyMesh.Valid
                        ? $"\n  SOFT maxDisp={_bellyMesh.SoftMaxDispNow:0.000}u ({_bellyMesh.SoftMaxDispNow * _bellyMesh.PixelsPerUnit:0.00}px)"
                            + $" maxVel={_bellyMesh.SoftMaxVelNow:0.0}u/s NaNresets={_bellyMesh.SoftNaNResets}"
                            + $" contacts={_bellyMesh.SoftContactsNow} pen={_bellyMesh.SoftPenNow:0.00} gated={_bellyMesh.SoftAlphaSkipped}"
                        : "\n  SOFT disabled (single-spring mesh mode)";
                JigglePlugin.Log.LogInfo(
                    $"[JiggleDbg] wobble={_wobble:0.00} grounded={_body.grounded} standing={_body.standing} press={_press:0.00} fall={_fallF:0.00} vy={v.y:0.00}\n"
                        + $"  pos={_sPos.ToString("0.000")} |pos|={posMag:0.000}/{maxSq:0.000}{(posMag >= maxSq * 0.99f ? " AT-CLAMP" : "")}"
                        + $" vel={_sVel.ToString("0.000")}\n"
                        + $"  rest={rest.ToString("0.000")} |rest|={restMag:0.000} (driveCap={driveCap:0.000} restCap={restCap:0.000}{(restMag >= restCap * 0.99f ? " REST-CAPPED" : "")})\n"
                        + $"  gravityTerm={(gravL * (sagGain * JiggleConfig.InertiaGain.Value * sagMul)).ToString("0.000")}"
                        + $" accelTerm={(-accL * (sagGain * JiggleConfig.InertiaGain.Value * sagMul)).ToString("0.000")}"
                        + $" liftTerm={lift.ToString("0.000")}\n"
                        + $"  smoothAccel={_smoothAccel.ToString("0.0")} |a|={aMag:0.0}/{JiggleConfig.AccelClamp.Value:0}"
                        + $" sagGain={sagGain:0.0000} sagMul={sagMul:0.00} InertiaGain={JiggleConfig.InertiaGain.Value:0.00}"
                        + meshInfo
                        + softInfo
                );
            }
        }

        private void LateUpdate()
        {
            if (
                _body == null
                || _body.limbs == null
                || _share == null
                || _share.Length != _body.limbs.Length
            )
                return;
            if (!JiggleConfig.Enabled.Value)
            {
                UndoAll();
                RestoreMesh();
                return;
            }

            Limb belly = _bellyIndex >= 0 ? _body.limbs[_bellyIndex] : null;
            bool bellyAlive =
                belly != null && !belly.dismembered && belly.gameObject.activeInHierarchy;
            Transform bt = bellyAlive ? belly.transform : transform;

            float meshTime = _pendingMeshTime;
            _pendingMeshTime = 0f;
            if (meshTime > 0f && Time.timeScale > 0f)
            {
                ProbeWall(bt, meshTime);
                if (
                    JiggleConfig.SoftBody.Value
                    && bellyAlive
                    && _bellyMesh != null
                    && _bellyMesh.Valid
                )
                {
                    _bellyMesh.SoftStep(
                        _meshDrive,
                        _meshStepDt,
                        _press,
                        _pressSide,
                        _body.grounded,
                        bt,
                        _groundMask
                    );
                }
                if (JiggleConfig.SoftBody.Value && _chestMesh != null && _chestMesh.Valid)
                {
                    Limb cl = _body.limbs[SoftProfile.Chest.LimbIndex];
                    if (cl != null && !cl.dismembered && cl.gameObject.activeInHierarchy)
                    {
                        Transform ct = cl.transform;
                        Vector3 dWorld = bt.TransformVector(
                            new Vector3(_meshDrive.x, _meshDrive.y, 0f)
                        );
                        Vector3 dLocal = ct.InverseTransformVector(dWorld);
                        Vector2 chestDrive =
                            new Vector2(dLocal.x, dLocal.y) * SoftProfile.Chest.DriveMul;
                        _chestMesh.SoftStep(
                            chestDrive,
                            _meshStepDt,
                            _press,
                            _pressSide,
                            _body.grounded,
                            ct,
                            _groundMask
                        );
                    }
                }
            }

            if (bellyAlive && _bellyMesh != null && _bellyMesh.Valid)
                _bellyMesh.UpdateMesh(_sPos);
            if (_chestMesh != null && _chestMesh.Valid)
                _chestMesh.UpdateMesh(Vector2.zero);

            // Sleeping/ragdolled limbs provide floor support; only their child meshes may jiggle.
            if (!_body.standing)
            {
                UndoAll();
                return;
            }

            Vector3 wdisp = bt.TransformVector(new Vector3(_sPos.x, _sPos.y, 0f));
            float maxOff = JiggleConfig.MaxOffset.Value;
            for (int i = 0; i < _body.limbs.Length; i++)
            {
                Limb l = _body.limbs[i];
                if (
                    l == null
                    || l.dismembered
                    || !l.gameObject.activeInHierarchy
                    || _share[i] <= 0.0001f
                    || (l.rb != null && l.rb.simulated)
                )
                {
                    UndoLimb(i);
                    continue;
                }
                Vector2 off = (Vector2)wdisp * _share[i];
                if (off.sqrMagnitude > maxOff * maxOff)
                    off = off.normalized * maxOff;

                Transform t = l.transform;
                Vector3 curPos = t.localPosition;
                Vector3 basePos =
                    (_appliedValid[i] && curPos == _lastWrittenPos[i])
                        ? curPos - _appliedLocalOff[i]
                        : curPos;
                Vector3 localOff = Vector3.zero;
                if (off.sqrMagnitude > 1e-10f && t.parent != null)
                {
                    Vector3 wo = t.parent.InverseTransformVector(new Vector3(off.x, off.y, 0f));
                    localOff = new Vector3(wo.x, wo.y, 0f);
                }
                t.localPosition = basePos + localOff;

                _appliedLocalOff[i] = localOff;
                _lastWrittenPos[i] = t.localPosition;
                _appliedValid[i] = true;
            }
        }

        public void RemoveLimbOffsets()
        {
            UndoAll();
        }

        public void OnFootStep()
        {
            if (
                _share == null
                || _body == null
                || _bellyIndex < 0
                || _wobble <= 0.001f
                || !_body.grounded
            )
                return;
            float imp = 1.0f * Mathf.Min(_wobble, 2.5f) * JiggleConfig.FootstepImpulse.Value;
            _sVel += LocalDown() * imp;
            SoftImpulseAll(imp);
        }

        public void OnJump()
        {
            if (_share == null || _body == null || _bellyIndex < 0 || _wobble <= 0.001f)
                return;
            float imp = 0.8f * _wobble * JiggleConfig.JumpImpulse.Value;
            _sVel += LocalDown() * imp;
            SoftImpulseAll(imp);
        }

        public void OnEat(float weightGain)
        {
            if (_share == null || _body == null || _bellyIndex < 0 || _wobble <= 0.001f)
                return;
            float imp =
                (2.0f + Mathf.Max(0f, weightGain) * 0.02f)
                * _wobble
                * JiggleConfig.EatImpulse.Value;
            _sVel += LocalDown() * imp;
            SoftImpulseAll(imp);
        }

        private void SoftImpulseAll(float imp)
        {
            SoftImpulseDir(LocalDown(), imp);
        }

        private void SoftImpulseDir(Vector2 bellyLocalDir, float imp)
        {
            if (!JiggleConfig.SoftBody.Value)
                return;
            if (_bellyMesh != null && _bellyMesh.Valid)
                _bellyMesh.SoftImpulse(bellyLocalDir, imp);
            if (_chestMesh != null && _chestMesh.Valid)
            {
                Limb cl = _body.limbs[SoftProfile.Chest.LimbIndex];
                Transform ct = cl != null ? cl.transform : transform;
                Limb bl = _bellyIndex >= 0 ? _body.limbs[_bellyIndex] : null;
                Transform bt = bl != null && !bl.dismembered ? bl.transform : transform;
                Vector3 dWorld = bt.TransformVector(
                    new Vector3(bellyLocalDir.x, bellyLocalDir.y, 0f)
                );
                Vector2 d = (Vector2)ct.InverseTransformVector(dWorld);
                float m = d.magnitude;
                _chestMesh.SoftImpulse(m > 1e-5f ? d / m : bellyLocalDir, imp);
            }
        }

        private Vector2 LocalDown()
        {
            Limb belly =
                _bellyIndex >= 0
                && _body != null
                && _body.limbs != null
                && _bellyIndex < _body.limbs.Length
                    ? _body.limbs[_bellyIndex]
                    : null;
            Transform bt = belly != null && !belly.dismembered ? belly.transform : transform;
            Vector2 d = (Vector2)bt.InverseTransformVector(Vector3.down);
            float m = d.magnitude;
            return m > 1e-5f ? d / m : Vector2.down;
        }

        private void UndoLimb(int i)
        {
            if (_appliedValid == null || i >= _appliedValid.Length || !_appliedValid[i])
                return;
            if (_body?.limbs == null || i >= _body.limbs.Length)
            {
                _appliedValid[i] = false;
                return;
            }
            Limb l = _body.limbs[i];
            if (l == null)
            {
                _appliedValid[i] = false;
                return;
            }
            Transform t = l.transform;
            if (t.localPosition == _lastWrittenPos[i])
                t.localPosition -= _appliedLocalOff[i];
            _appliedValid[i] = false;
            _appliedLocalOff[i] = Vector3.zero;
        }

        private void UndoAll()
        {
            if (_appliedValid == null)
                return;
            for (int i = 0; i < _appliedValid.Length; i++)
                UndoLimb(i);
        }

        private void RestoreMesh()
        {
            _pendingMeshTime = 0f;
            if (_bellyMesh != null)
            {
                _bellyMesh.Restore();
                _bellyMesh = null;
            }
            else if (_bellyIndex >= 0 && _body?.limbs != null && _bellyIndex < _body.limbs.Length)
            {
                BellyMesh ex = BellyMesh.Find(_body.limbs[_bellyIndex]);
                if (ex != null)
                    ex.Restore();
            }
            if (_chestMesh != null)
            {
                _chestMesh.Restore();
                _chestMesh = null;
            }
            _meshAttempted = false;
            _meshRetryAfter = -1f;
            _chestRetryAfter = -1f;
        }
    }
}
