using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Legacy compatibility component. Semantic touch is intentionally never
    /// synthesized from a ray: head pats, cheek pinches and handshakes must be
    /// produced by the tracked-hand/contact-collider pipeline.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class QuestAvatarRayInteraction : MonoBehaviour
    {
        public string Status { get; private set; } = "Physical hand contact required";
        public static bool CanSynthesizeSemanticTouch => false;

        public void Bind(AvatarController target, AvatarHumanInteraction interaction, CompanionWorldMenu worldMenu)
        {
            Status = target == null
                ? "Physical hand contact waiting for avatar"
                : "Physical hand contact required";
        }
    }
}
