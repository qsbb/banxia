using System.Collections.Generic;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer
{
    [DefaultExecutionOrder(10500)]
    public sealed class AvatarConversationPresenter : MonoBehaviour
    {
        private struct Viseme
        {
            public SkinnedMeshRenderer Renderer;
            public int Index;
            public float BaseWeight;
        }

        private struct ExpressionMorph
        {
            public SkinnedMeshRenderer Renderer;
            public int Index;
            public string Name;
            public float BaseWeight;
            public float CurrentWeight;
        }

        private readonly List<Viseme> visemes = new List<Viseme>();
        private readonly List<ExpressionMorph> expressions = new List<ExpressionMorph>();
        private readonly AvatarBehaviorCoordinator behavior = new AvatarBehaviorCoordinator();
        private AvatarController avatar;
        private AvatarHumanInteraction humanInteraction;
        private Pcm16StreamAudioPlayer audioPlayer;
        private VmdActionLibrary vmdActions;
        private Transform head;
        private Transform jaw;
        private Quaternion headBaseRotation;
        private Quaternion jawBaseRotation;
        private ConversationState state;
        private string lookAtMode = "none";
        [SerializeField] private bool gazeAtUserWhileIdle = true;
        [SerializeField, Range(.5f, 6f)] private float idleGazeBlendSpeed = 2.25f;
        private float gazeBlend;
        private bool mouthWasActive;
        private string targetEmotion = "neutral";
        private float targetEmotionIntensity;
        [SerializeField, Range(1f, 12f)] private float expressionBlendSpeed = 6f;

        public int MatchedVisemeCount => visemes.Count;
        public string Status => avatar == null
            ? "Waiting for avatar"
            : $"{state} | gesture:{behavior.LastGesture} visemes:{visemes.Count} jaw:{(jaw == null ? "no" : "yes")}";

        public void Bind(AvatarController target, AvatarHumanInteraction human, Pcm16StreamAudioPlayer streamPlayer)
        {
            RestoreMouth();
            RestoreExpressions();
            RestoreJaw();
            RestoreHead();
            avatar = target;
            humanInteraction = human;
            audioPlayer = streamPlayer;
            vmdActions = GetComponent<VmdActionLibrary>();
            head = null;
            jaw = null;
            visemes.Clear();
            expressions.Clear();
            targetEmotion = "neutral";
            targetEmotionIntensity = 0f;
            gazeBlend = 0f;
            lookAtMode = "none";
            mouthWasActive = false;
            behavior.Reset(Time.unscaledTime, Random.value);

            if (avatar == null)
            {
                return;
            }

            head = FindHead(avatar);
            if (head != null)
            {
                headBaseRotation = head.localRotation;
            }
            jaw = FindJaw(avatar);
            if (jaw != null)
            {
                jawBaseRotation = jaw.localRotation;
            }
            CacheVisemes();
            CacheExpressions();
            Debug.Log($"[ConversationPresenter] Bound mouth: visemes={visemes.Count}, expressions={expressions.Count}, jaw={(jaw == null ? "no" : "yes")}.", this);
        }

        public void SetConversationState(ConversationState next)
        {
            if (state != ConversationState.Idle && next == ConversationState.Idle)
            {
                behavior.DeferIdle(Time.unscaledTime, Random.value);
            }
            state = next;
        }

        public void ApplyIntent(string emotion, string gesture, string lookAt, float intensity = 1f, int durationMs = 2000)
        {
            if (avatar == null)
            {
                return;
            }

            emotion = AstrBotProtocol.SanitizeEmotion(emotion);
            gesture = AstrBotProtocol.SanitizeGesture(gesture);
            lookAt = AstrBotProtocol.SanitizeLookAt(lookAt);
            avatar.SetEmotion(emotion);
            targetEmotion = emotion;
            targetEmotionIntensity = Mathf.Clamp01(intensity);
            lookAtMode = lookAt == "hand" ? "none" : lookAt;
            var reactionSeconds = Mathf.Clamp(durationMs <= 0 ? 2f : durationMs / 1000f, .25f, 8f);
            var semanticContact = humanInteraction != null && humanInteraction.HasSemanticContact;
            if (!behavior.TryAcceptIntent(
                    gesture,
                    semanticContact,
                    IsImportedMotionBusy(),
                    Time.unscaledTime,
                    out var acceptedGesture))
            {
                return;
            }

            if (acceptedGesture == "handshake")
            {
                humanInteraction?.PlayReaction(HumanInteractionKind.Handshake, reactionSeconds);
            }
            else if (acceptedGesture == "head_pat")
            {
                humanInteraction?.PlayReaction(HumanInteractionKind.HeadPat, reactionSeconds);
            }
            else if (acceptedGesture == "cheek_pinch")
            {
                humanInteraction?.PlayReaction(HumanInteractionKind.CheekPinch, reactionSeconds);
            }
            else if (acceptedGesture == "wave" || acceptedGesture == "bow" || acceptedGesture == "idle")
            {
                avatar.PlayAction(acceptedGesture);
            }
        }

        private void LateUpdate()
        {
            if (avatar == null)
            {
                return;
            }

            var semanticContact = humanInteraction != null && humanInteraction.HasSemanticContact;
            var idleAttention = ShouldUseIdleUserGaze(state, semanticContact, gazeAtUserWhileIdle);
            var wantsAttention = !semanticContact && (lookAtMode != "none" || idleAttention);
            gazeBlend = Mathf.MoveTowards(gazeBlend, wantsAttention ? 1f : 0f, Time.unscaledDeltaTime * (idleAttention ? idleGazeBlendSpeed : 3.5f));
            var gazeMode = idleAttention && lookAtMode == "none" ? "user" : lookAtMode;
            ApplyGaze(gazeBlend, gazeMode);

            var speechLevel = state == ConversationState.Speaking && audioPlayer != null ? audioPlayer.LatestRms : 0f;
            ApplyMouth(speechLevel);
            ApplyExpressions();
            UpdateIdleBehavior(semanticContact);
        }

        private void UpdateIdleBehavior(bool semanticContact)
        {
            if (behavior.TryTakeIdleBehavior(
                    state,
                    semanticContact,
                    IsImportedMotionBusy(),
                    avatar.CurrentAction,
                    Time.unscaledTime,
                    Random.value,
                    out var gesture))
            {
                avatar.PlayAction(gesture);
            }
        }

        private bool IsImportedMotionBusy()
        {
            return vmdActions != null &&
                (vmdActions.IsLoading || vmdActions.IsPlaying ||
                    vmdActions.IsHoldingEndPose || vmdActions.IsBlendingOut);
        }

        public static bool ShouldUseIdleUserGaze(ConversationState conversationState, bool semanticContact, bool enabled)
        {
            return enabled && !semanticContact && conversationState == ConversationState.Idle;
        }

        private void ApplyGaze(float amount, string mode)
        {
            if (head == null)
            {
                return;
            }
            if (amount <= .001f || Camera.main == null)
            {
                head.localRotation = Quaternion.Slerp(head.localRotation, headBaseRotation, Time.unscaledDeltaTime * 8f);
                return;
            }

            var direction = Camera.main.transform.position - head.position;
            if (direction.sqrMagnitude <= .0001f)
            {
                return;
            }
            var localDirection = avatar.transform.InverseTransformDirection(direction.normalized);
            var userYaw = Mathf.Clamp(Mathf.Atan2(localDirection.x, localDirection.z) * Mathf.Rad2Deg, -24f, 24f);
            var yaw = mode == "away" ? (userYaw >= 0f ? -18f : 18f) : userYaw;
            var pitch = mode == "away"
                ? 2f
                : Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(localDirection.y, -1f, 1f)) * Mathf.Rad2Deg, -14f, 14f);
            var target = headBaseRotation * Quaternion.Euler(pitch, yaw * .65f, 0f);
            head.localRotation = Quaternion.Slerp(headBaseRotation, target, amount);
        }

        private void ApplyMouth(float rms)
        {
            if (visemes.Count == 0 && jaw == null)
            {
                return;
            }

            var amount = Mathf.Clamp01((rms - .0025f) * 22f);
            if (amount <= .001f)
            {
                if (mouthWasActive)
                {
                    RestoreMouth();
                    mouthWasActive = false;
                }
                return;
            }

            mouthWasActive = true;
            var active = visemes.Count == 0 ? -1 : Mathf.FloorToInt(Time.unscaledTime * 8f) % visemes.Count;
            for (var i = 0; i < visemes.Count; i++)
            {
                var viseme = visemes[i];
                var add = i == active ? amount * 68f : 0f;
                viseme.Renderer.SetBlendShapeWeight(viseme.Index, Mathf.Clamp(viseme.BaseWeight + add, 0f, 100f));
            }
            if (jaw != null)
            {
                jaw.localRotation = Quaternion.Slerp(jawBaseRotation, jawBaseRotation * Quaternion.Euler(amount * 13f, 0f, 0f), amount);
            }
        }

        private void CacheVisemes()
        {
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var r = 0; r < renderers.Length; r++)
            {
                var mesh = renderers[r].sharedMesh;
                if (mesh == null)
                {
                    continue;
                }
                for (var i = 0; i < mesh.blendShapeCount; i++)
                {
                    if (!IsMouthShape(Normalize(mesh.GetBlendShapeName(i))))
                    {
                        continue;
                    }
                    visemes.Add(new Viseme
                    {
                        Renderer = renderers[r],
                        Index = i,
                        BaseWeight = renderers[r].GetBlendShapeWeight(i)
                    });
                }
            }
        }

        private void CacheExpressions()
        {
            if (avatar == null)
            {
                return;
            }

            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                var mesh = renderer.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }
                for (var shapeIndex = 0; shapeIndex < mesh.blendShapeCount; shapeIndex++)
                {
                    var name = Normalize(mesh.GetBlendShapeName(shapeIndex));
                    if (IsMouthShape(name) || !IsExpressionShape(name))
                    {
                        continue;
                    }
                    expressions.Add(new ExpressionMorph
                    {
                        Renderer = renderer,
                        Index = shapeIndex,
                        Name = name,
                        BaseWeight = renderer.GetBlendShapeWeight(shapeIndex),
                        CurrentWeight = 0f
                    });
                }
            }
        }

        private void ApplyExpressions()
        {
            if (expressions.Count == 0)
            {
                return;
            }

            var blinkPulse = Mathf.Pow(Mathf.Clamp01(Mathf.Sin(Time.unscaledTime * .66f + .8f)), 28f);
            var step = Time.unscaledDeltaTime * expressionBlendSpeed;
            for (var index = 0; index < expressions.Count; index++)
            {
                var expression = expressions[index];
                var target = GetExpressionWeight(expression.Name, targetEmotion, targetEmotionIntensity);
                if (IsBlinkShape(expression.Name))
                {
                    target = Mathf.Max(target, blinkPulse * 72f);
                }
                expression.CurrentWeight = Mathf.MoveTowards(expression.CurrentWeight, target, step * 100f);
                if (expression.Renderer != null)
                {
                    expression.Renderer.SetBlendShapeWeight(
                        expression.Index,
                        Mathf.Clamp(expression.BaseWeight + expression.CurrentWeight, 0f, 100f));
                }
                expressions[index] = expression;
            }
        }

        private void RestoreExpressions()
        {
            for (var index = 0; index < expressions.Count; index++)
            {
                var expression = expressions[index];
                if (expression.Renderer != null)
                {
                    expression.Renderer.SetBlendShapeWeight(expression.Index, expression.BaseWeight);
                }
            }
        }

        public static float GetExpressionWeight(string shapeName, string emotion, float intensity)
        {
            var name = Normalize(shapeName);
            var value = string.IsNullOrWhiteSpace(emotion) ? "neutral" : emotion.ToLowerInvariant();
            var amount = Mathf.Clamp01(intensity) * 62f;
            if (value == "happy" || value == "joy" || value == "fond")
            {
                return ContainsAny(name, "smile", "happy", "joy", "laugh", "笑", "微笑",
                    "笑い", "なごみ", "にっこり", "にやり", "口角上げ") ? amount : 0f;
            }
            if (value == "sad" || value == "sorrow")
            {
                return ContainsAny(name, "sad", "sorrow", "cry", "涙", "悲",
                    "困る", "えーん", "悲しい") ? amount : 0f;
            }
            if (value == "angry" || value == "anger")
            {
                return ContainsAny(name, "angry", "anger", "mad", "怒",
                    "怒り", "怒る", "キリッ") ? amount : 0f;
            }
            if (value == "surprised" || value == "surprise")
            {
                return ContainsAny(name, "surprise", "astonish", "驚",
                    "びっくり", "驚き", "はっ") ? amount : 0f;
            }
            if (value == "embarrassed" || value == "shy")
            {
                return ContainsAny(name, "blush", "shy", "embarrass", "照", "赤",
                    "照れ", "赤面") ? amount * .7f : 0f;
            }
            return 0f;
        }

        private static bool IsExpressionShape(string name)
        {
            return ContainsAny(name, "smile", "happy", "joy", "laugh", "sad", "sorrow", "cry",
                "angry", "anger", "mad", "surprise", "astonish", "blush", "shy", "embarrass",
                "blink", "eyeclose", "eyesclose", "まばたき", "またたき", "瞬き",
                "笑", "微笑", "笑い", "なごみ", "にっこり", "にやり", "口角上げ",
                "悲", "困る", "えーん", "悲しい", "怒", "怒り", "怒る", "キリッ",
                "驚", "びっくり", "驚き", "はっ", "照", "照れ", "赤", "赤面");
        }

        private static bool IsBlinkShape(string name)
        {
            return ContainsAny(name, "blink", "eyeclose", "eyesclose", "まばたき", "またたき", "瞬き");
        }

        private static bool ContainsAny(string value, params string[] candidates)
        {
            for (var index = 0; index < candidates.Length; index++)
            {
                if (value.Contains(candidates[index]))
                {
                    return true;
                }
            }
            return false;
        }
        private void RestoreMouth()
        {
            for (var i = 0; i < visemes.Count; i++)
            {
                var viseme = visemes[i];
                if (viseme.Renderer != null)
                {
                    viseme.Renderer.SetBlendShapeWeight(viseme.Index, viseme.BaseWeight);
                }
            }
        }

        private void RestoreHead()
        {
            if (head != null)
            {
                head.localRotation = headBaseRotation;
            }
        }

        private void RestoreJaw()
        {
            if (jaw != null)
            {
                jaw.localRotation = jawBaseRotation;
            }
        }

        private static Transform FindJaw(AvatarController target)
        {
            var bones = target.GetComponentsInChildren<MMDBoneTransform>(true);
            for (var i = 0; i < bones.Length; i++)
            {
                var name = Normalize(string.IsNullOrWhiteSpace(bones[i].boneName) ? bones[i].name : bones[i].boneName);
                if (name == "jaw" || name == "lowerjaw" || name == "mouth" || name == "口" || name == "あご" || name == "下顎" || name.Contains("jaw") || name.Contains("lowerjaw"))
                {
                    return bones[i].transform;
                }
            }
            return null;
        }

        private static Transform FindHead(AvatarController target)
        {
            var bones = target.GetComponentsInChildren<MMDBoneTransform>(true);
            for (var i = 0; i < bones.Length; i++)
            {
                var name = Normalize(string.IsNullOrWhiteSpace(bones[i].boneName) ? bones[i].name : bones[i].boneName);
                if (name == "head" || name == "\u982d" || name == "\u5934")
                {
                    return bones[i].transform;
                }
            }
            return null;
        }

        private static bool IsMouthShape(string name)
        {
            return name.Contains("mouth") || name.Contains("lip") || name.Contains("口") || name.Contains("あ") || name.Contains("い") || name.Contains("う") || name.Contains("え") || name.Contains("お") || name == "a" || name == "i" || name == "u" || name == "e" || name == "o"
                || name == "aa" || name == "ih" || name == "ou" || name == "ee" || name == "oh"
                || name == "\u3042" || name == "\u3044" || name == "\u3046" || name == "\u3048" || name == "\u304a"
                || name == "\u53e3\u3042" || name == "\u53e3\u3044" || name == "\u53e3\u3046" || name == "\u53e3\u3048" || name == "\u53e3\u304a"
                || name == "moutha" || name == "mouthi" || name == "mouthu" || name == "mouthe" || name == "moutho"
                || name == "vrcvaa" || name == "vrcvih" || name == "vrcvou" || name == "vrcvee" || name == "vrcvoh";
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(".", string.Empty);
        }

        private void OnDisable()
        {
            RestoreMouth();
            RestoreExpressions();
            RestoreJaw();
            RestoreHead();
        }
    }
}
