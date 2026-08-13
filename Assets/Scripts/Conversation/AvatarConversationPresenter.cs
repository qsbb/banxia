using System;
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
        private AvatarPlacementService placement;
        private RuntimeDebugLog diagnostics;
        private System.Threading.Tasks.Task activeDanceTask;
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
        private float smoothedMouthAmount;
        private string targetEmotion = "neutral";
        private float targetEmotionIntensity;
        private string manualExpression = "neutral";
        private string lastLoggedAction = "idle";
        [SerializeField, Range(1f, 12f)] private float expressionBlendSpeed = 6f;
        [SerializeField, Range(1f, 24f)] private float mouthAttackSpeed = 11f;
        [SerializeField, Range(1f, 24f)] private float mouthReleaseSpeed = 7f;
        [SerializeField, Range(2f, 10f)] private float visemeCyclesPerSecond = 5f;

        public int MatchedVisemeCount => visemes.Count;
        public string ManualExpression => manualExpression;
        public string Status => avatar == null
            ? "Waiting for avatar"
            : $"{state} | gesture:{behavior.LastGesture} visemes:{visemes.Count} jaw:{(jaw == null ? "no" : "yes")}";

        public void Bind(AvatarController target, AvatarHumanInteraction human, Pcm16StreamAudioPlayer streamPlayer)
        {
            if (avatar != null)
            {
                avatar.ActionChanged -= HandleAvatarActionChanged;
            }
            RestoreMouth();
            RestoreExpressions();
            RestoreJaw();
            RestoreHead();
            avatar = target;
            humanInteraction = human;
            audioPlayer = streamPlayer;
            vmdActions = GetComponent<VmdActionLibrary>();
            placement = GetComponent<AvatarPlacementService>();
            diagnostics = GetComponent<RuntimeDebugLog>();
            head = null;
            jaw = null;
            visemes.Clear();
            expressions.Clear();
            targetEmotion = "neutral";
            targetEmotionIntensity = 0f;
            manualExpression = "neutral";
            gazeBlend = 0f;
            lookAtMode = "none";
            mouthWasActive = false;
            smoothedMouthAmount = 0f;
            behavior.Reset(Time.unscaledTime, UnityEngine.Random.value);

            if (avatar == null)
            {
                return;
            }

            lastLoggedAction = avatar.CurrentAction;
            avatar.ActionChanged += HandleAvatarActionChanged;

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
                behavior.DeferIdle(Time.unscaledTime, UnityEngine.Random.value);
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
            var requestedGesture = string.IsNullOrWhiteSpace(gesture) ? string.Empty : gesture.Trim().ToLowerInvariant();
            // World-placement actions remain exact enum values. Free-form text
            // never reaches AvatarPlacementService.
            gesture = requestedGesture == "sit" || requestedGesture == "lie" || requestedGesture == "lie_down"
                ? requestedGesture == "lie" ? "lie_down" : requestedGesture
                : AstrBotProtocol.SanitizeGesture(gesture);
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
                diagnostics?.Record("AvatarAction", "后端动作意图被本地仲裁阻止：" + gesture);
                diagnostics?.RecordStage("avatar_action", "blocked", "action_arbitration_blocked");
                return;
            }

            diagnostics?.Record("AvatarAction", "后端动作意图已接受：" + acceptedGesture);
            diagnostics?.RecordStage("avatar_action", "processing", "backend_intent_accepted");

            if (acceptedGesture == "sit" || acceptedGesture == "lie_down")
            {
                if (placement == null || !placement.TryExecuteRestingAction(acceptedGesture))
                {
                    diagnostics?.RecordStage("avatar_action", "limited", "rest_target_unavailable");
                }
            }
            else if (placement != null && placement.IsRestingOrAligning && acceptedGesture != "talk")
            {
                placement.TryReturnToStanding(acceptedGesture);
            }
            else if (acceptedGesture == "handshake")
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
            else if (acceptedGesture == "wave" || acceptedGesture == "bow" ||
                acceptedGesture == "dance" || acceptedGesture == "dance_next" ||
                acceptedGesture == "nod" || acceptedGesture == "sway" ||
                acceptedGesture == "raise_hand" || acceptedGesture == "turn_half" ||
                acceptedGesture == "refuse" || acceptedGesture == "step_back" ||
                acceptedGesture == "idle")
            {
                if (acceptedGesture == "dance" || acceptedGesture == "dance_next")
                {
                    _ = PlayRecommendedDance(acceptedGesture == "dance_next");
                }
                else
                {
                    avatar.PlayActionFromSource(acceptedGesture, AvatarActionSource.Backend);
                }
            }
        }

        public void SetManualExpression(string expression)
        {
            manualExpression = NormalizeExpression(expression);
            targetEmotion = manualExpression;
            targetEmotionIntensity = manualExpression == "neutral" ? 0f : .42f;
            avatar?.SetEmotion(manualExpression);
        }

        public void PlayLocalAction(string action)
        {
            var normalized = string.IsNullOrWhiteSpace(action)
                ? "idle"
                : action.Trim().ToLowerInvariant();
            diagnostics?.Record("AvatarAction", "本地动作回退执行：" + normalized);
            diagnostics?.RecordStage("avatar_action", "processing", "local_action_fallback");
            if (normalized == "sit" || normalized == "lie_down")
            {
                if (placement == null || !placement.TryExecuteRestingAction(normalized))
                {
                    diagnostics?.RecordStage("avatar_action", "limited", "rest_target_unavailable");
                }
                return;
            }
            if (placement != null && placement.IsRestingOrAligning)
            {
                placement.TryReturnToStanding(normalized);
                return;
            }
            if (normalized == "dance" || normalized == "dance_next")
            {
                _ = PlayRecommendedDance(normalized == "dance_next");
                return;
            }

            if (avatar != null)
            {
                avatar.PlayActionFromSource(normalized, AvatarActionSource.Manual);
            }
        }

        private async System.Threading.Tasks.Task PlayRecommendedDance(bool selectNext = false)
        {
            if (activeDanceTask != null && !activeDanceTask.IsCompleted)
            {
                diagnostics?.Record("AvatarAction", "舞蹈请求合并到正在加载的动作");
                await activeDanceTask;
                return;
            }

            activeDanceTask = PlayRecommendedDanceCore(selectNext);
            try
            {
                await activeDanceTask;
            }
            finally
            {
                activeDanceTask = null;
            }
        }

        private async System.Threading.Tasks.Task PlayRecommendedDanceCore(bool selectNext)
        {
            diagnostics?.Record("AvatarAction", selectNext
                ? "开始查找下一支可播放的自定义舞蹈动作"
                : "开始查找可播放的自定义舞蹈动作");
            if (vmdActions != null && vmdActions.BoundModel)
            {
                try
                {
                    var played = selectNext
                        ? await vmdActions.PlayNextDanceAsync()
                        : await vmdActions.PlayRecommendedDanceAsync();
                    diagnostics?.Record(
                        "AvatarAction",
                        played
                            ? "舞蹈请求已播放导入动作：" + vmdActions.CurrentActionId
                            : "舞蹈请求未找到可播放的导入动作");
                    diagnostics?.RecordStage(
                        "avatar_action",
                        played ? "completed" : "limited",
                        played ? "custom_dance_started" : "custom_dance_unavailable");
                    if (played)
                    {
                        return;
                    }
                }
                catch (Exception exception)
                {
                    diagnostics?.Record("AvatarAction", "导入舞蹈播放失败：" + exception.Message);
                    Debug.LogWarning("[ConversationPresenter] Dance motion fallback failed: " + exception.Message, this);
                }
            }
            else
            {
                diagnostics?.Record(
                    "AvatarAction",
                    vmdActions == null
                        ? "自定义动作库未绑定，改用内置舞蹈"
                        : "角色模型未绑定到自定义动作库，改用内置舞蹈");
            }

            // A built-in fallback keeps the explicit request visible even when
            // no imported dance is installed or the VMD is not compatible.
            diagnostics?.Record("AvatarAction", "播放内置舞蹈回退动作");
            avatar?.PlayActionFromSource("dance", AvatarActionSource.Backend);
        }

        public static string NormalizeExpression(string expression)
        {
            var value = string.IsNullOrWhiteSpace(expression) ? "neutral" : expression.Trim().ToLowerInvariant();
            return value == "happy" || value == "shy" || value == "surprised" || value == "sad"
                ? value
                : "neutral";
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
                    UnityEngine.Random.value,
                    out var gesture))
            {
                diagnostics?.Record("AvatarAction", "自动待机动作：" + gesture);
                avatar.PlayActionFromSource(gesture, AvatarActionSource.Idle);
            }
        }

        private bool IsImportedMotionBusy()
        {
            return vmdActions != null &&
                (vmdActions.IsLoading || vmdActions.IsPlaying ||
                    vmdActions.IsHoldingEndPose || vmdActions.IsBlendingOut);
        }

        private void HandleAvatarActionChanged(string nextAction)
        {
            var normalized = string.IsNullOrWhiteSpace(nextAction) ? "idle" : nextAction;
            diagnostics?.Record(
                "AvatarAction",
                "\u52a8\u4f5c\u5207\u6362: " + lastLoggedAction + " -> " + normalized +
                " source=" + (avatar == null ? AvatarActionSource.Unknown : avatar.CurrentActionSource));
            diagnostics?.RecordStage("avatar_action", "completed", "action_state_changed");
            lastLoggedAction = normalized;
        }

        private void OnDestroy()
        {
            if (avatar != null)
            {
                avatar.ActionChanged -= HandleAvatarActionChanged;
            }
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

            var targetAmount = Mathf.Clamp01((rms - .0025f) * 22f);
            var amount = SmoothMouthAmount(
                smoothedMouthAmount,
                targetAmount,
                Time.unscaledDeltaTime,
                mouthAttackSpeed,
                mouthReleaseSpeed);
            smoothedMouthAmount = amount;
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
            var visemeClock = Time.unscaledTime * Mathf.Max(1f, visemeCyclesPerSecond);
            var active = visemes.Count == 0 ? -1 : Mathf.FloorToInt(visemeClock) % visemes.Count;
            var next = visemes.Count <= 1 ? active : (active + 1) % visemes.Count;
            var crossfade = Mathf.SmoothStep(0f, 1f, visemeClock - Mathf.Floor(visemeClock));
            for (var i = 0; i < visemes.Count; i++)
            {
                var viseme = visemes[i];
                var influence = i == active
                    ? 1f - crossfade
                    : i == next
                        ? crossfade
                        : 0f;
                var add = amount * influence * 68f;
                viseme.Renderer.SetBlendShapeWeight(viseme.Index, Mathf.Clamp(viseme.BaseWeight + add, 0f, 100f));
            }
            if (jaw != null)
            {
                jaw.localRotation = Quaternion.Slerp(jawBaseRotation, jawBaseRotation * Quaternion.Euler(amount * 13f, 0f, 0f), amount);
            }
        }

        public static float SmoothMouthAmount(
            float current,
            float target,
            float deltaTime,
            float attackSpeed,
            float releaseSpeed)
        {
            var safeCurrent = Mathf.Clamp01(current);
            var safeTarget = Mathf.Clamp01(target);
            var speed = safeTarget > safeCurrent
                ? Mathf.Max(.01f, attackSpeed)
                : Mathf.Max(.01f, releaseSpeed);
            return Mathf.MoveTowards(safeCurrent, safeTarget, Mathf.Max(0f, deltaTime) * speed);
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
            var amount = Mathf.Clamp01(intensity) * 38f;
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
