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

        private readonly List<Viseme> visemes = new List<Viseme>();
        private AvatarController avatar;
        private AvatarHumanInteraction humanInteraction;
        private Pcm16StreamAudioPlayer audioPlayer;
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

        public int MatchedVisemeCount => visemes.Count;
        public string Status => avatar == null ? "Waiting for avatar" : $"{state} | visemes:{visemes.Count} jaw:{(jaw == null ? "no" : "yes")}";

        public void Bind(AvatarController target, AvatarHumanInteraction human, Pcm16StreamAudioPlayer streamPlayer)
        {
            RestoreMouth();
            RestoreJaw();
            RestoreHead();
            avatar = target;
            humanInteraction = human;
            audioPlayer = streamPlayer;
            head = null;
            jaw = null;
            visemes.Clear();
            gazeBlend = 0f;
            lookAtMode = "none";
            mouthWasActive = false;

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
            Debug.Log($"[ConversationPresenter] Bound mouth: visemes={visemes.Count}, jaw={(jaw == null ? "no" : "yes")}.", this);
        }

        public void SetConversationState(ConversationState next)
        {
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
            lookAtMode = lookAt == "hand" ? "none" : lookAt;
            var reactionSeconds = Mathf.Clamp(durationMs <= 0 ? 2f : durationMs / 1000f, .25f, 8f);
            if (gesture == "handshake")
            {
                humanInteraction?.PlayReaction(HumanInteractionKind.Handshake, reactionSeconds);
            }
            else if (gesture == "head_pat")
            {
                humanInteraction?.PlayReaction(HumanInteractionKind.HeadPat, reactionSeconds);
            }
            else if (gesture == "cheek_pinch")
            {
                humanInteraction?.PlayReaction(HumanInteractionKind.CheekPinch, reactionSeconds);
            }
            else if (gesture == "wave" || gesture == "bow" || gesture == "idle")
            {
                avatar.PlayAction(gesture);
            }
            else
            {
                // talk uses the audio/mouth layer. refuse and step_back need a
                // model capability that the current PMX adapter does not expose.
                avatar.PlayAction("idle");
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
            RestoreJaw();
            RestoreHead();
        }
    }
}
