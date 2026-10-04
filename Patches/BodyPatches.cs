using System.Runtime.CompilerServices;
using CasualtiesExtra;
using HarmonyLib;
using UnityEngine;

namespace CasualtiesJiggle
{
    [HarmonyPatch]
    internal static class BodyPatches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(Body), "Start")]
        private static void Body_Start(Body __instance)
        {
            if (!JiggleConfig.Enabled.Value)
                return;
            if (__instance.GetComponent<JiggleBody>() == null)
                __instance.gameObject.AddComponent<JiggleBody>();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AnimBody), "Footstep")]
        private static void AnimBody_Footstep(AnimBody __instance)
        {
            Body body = __instance.body;
            if (body != null && body.standing && body.grounded)
                JiggleBody.ForBody(body)?.OnFootStep();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AnimBody), "FootstepForced")]
        private static void AnimBody_FootstepForced(AnimBody __instance)
        {
            AnimBody_Footstep(__instance);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Body), "Jump")]
        private static void Body_Jump(Body __instance)
        {
            JiggleBody.ForBody(__instance)?.OnJump();
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Body), "Eat")]
        private static void Body_Eat(Body __instance, float weightGain)
        {
            JiggleBody.ForBody(__instance)?.OnEat(weightGain);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Body), "Update")]
        [HarmonyPriority(Priority.First)]
        private static void Body_Update_RemoveLimbOffsets(Body __instance)
        {
            __instance.GetComponent<JiggleBody>()?.RemoveLimbOffsets();
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Body), "FixedUpdate")]
        [HarmonyPriority(Priority.First)]
        private static void Body_FixedUpdate_RestoreTransforms(Body __instance)
        {
            __instance.GetComponent<JiggleBody>()?.RemoveLimbOffsets();
            RestoreStuckScale(__instance);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Body), "Ragdoll")]
        [HarmonyPriority(Priority.First)]
        private static void Body_Ragdoll_RemoveLimbOffsets(Body __instance)
        {
            __instance.GetComponent<JiggleBody>()?.RemoveLimbOffsets();
            RestoreStuckScale(__instance);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Body), nameof(Body.SetLimbCollisionType))]
        private static void Body_SetLimbCollisionType(
            Body __instance,
            ref CollisionDetectionMode2D col
        )
        {
            // Vanilla downgrades unconscious limbs to discrete collision, allowing tile tunneling.
            if (!__instance.standing && JiggleBody.ForBody(__instance) != null)
                col = CollisionDetectionMode2D.Continuous;
        }

        private sealed class StuckScaleState
        {
            public bool Applied;
            public Transform Torso1;
            public Transform Torso2;
            public float Raw1;
            public float Raw2;
            public float Written1;
            public float Written2;
        }

        private static readonly ConditionalWeakTable<Body, StuckScaleState> StuckScales =
            new ConditionalWeakTable<Body, StuckScaleState>();

        private static void SetScaleX(Transform t, float x)
        {
            Vector3 s = t.localScale;
            t.localScale = new Vector3(x, s.y, s.z);
        }

        // XL smooths limb scale from the value it reads at the start of Body.Update.
        // Give it back the raw scale, not our neutralized one, or it feeds on itself and blows up at high fps.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Body), "Update")]
        [HarmonyPriority(Priority.First)]
        private static void Body_Update_RestoreScale(Body __instance)
        {
            RestoreStuckScale(__instance);
        }

        private static void RestoreStuckScale(Body body)
        {
            try
            {
                if (!StuckScales.TryGetValue(body, out StuckScaleState state) || !state.Applied)
                    return;
                state.Applied = false;
                if (state.Torso1 != null && state.Torso1.localScale.x == state.Written1)
                    SetScaleX(state.Torso1, state.Raw1);
                if (state.Torso2 != null && state.Torso2.localScale.x == state.Written2)
                    SetScaleX(state.Torso2, state.Raw2);
            }
            catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Body), "Update")]
        [HarmonyAfter("thesofteeveeboy.mods.CasualtiesExtra")]
        private static void Body_Update(Body __instance)
        {
            if (
                !JiggleConfig.Enabled.Value
                || !JiggleConfig.NeutralizeStuckScale.Value
                || !__instance.standing
            )
                return;
            try
            {
                if (__instance.limbs == null || __instance.limbs.Length <= 2)
                    return;
                int stage = CasualtiesExtraApi.GetWeightStage(__instance);
                if (stage < 2 || !CasualtiesExtraApi.GetStuck(__instance))
                    return;
                Limb l1 = __instance.limbs[1];
                Limb l2 = __instance.limbs[2];
                if (
                    l1 == null
                    || l2 == null
                    || (l2.rb != null && l2.rb.simulated)
                    || (stage == 2 && l1.rb != null && l1.rb.simulated)
                )
                    return;
                Transform t1 = l1.transform;
                Transform t2 = l2.transform;
                StuckScaleState state = StuckScales.GetOrCreateValue(__instance);
                state.Torso1 = stage == 2 ? t1 : null;
                state.Torso2 = t2;
                state.Raw1 = t1.localScale.x;
                state.Raw2 = t2.localScale.x;
                state.Written1 = 1f + l1.weightVisualScaleMult * 0.01f;
                state.Written2 = 1f + l2.weightVisualScaleMult * 0.01f;
                if (state.Torso1 != null)
                    SetScaleX(t1, state.Written1);
                SetScaleX(t2, state.Written2);
                state.Applied = true;
            }
            catch
            {
                // never let the undo kill the game's update
            }
        }
    }
}
