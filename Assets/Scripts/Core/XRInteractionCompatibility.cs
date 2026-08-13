using System;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Provider-neutral contracts matching the useful subset of XRI/MR
    /// interactor and interactable semantics. Optional SDK adapters can map
    /// their callbacks to these contracts without replacing the touch layer.
    /// </summary>
    public interface IAvatarPokeInteractor
    {
        string InteractorId { get; }
        bool IsTracked { get; }
        Pose CurrentPose { get; }
    }

    public interface IAvatarPokeInteractable
    {
        string InteractableId { get; }
        void OnPoke(PokeInteractionEvent interaction);
    }

    public enum PokeInteractionPhase
    {
        Enter,
        Hover,
        Exit
    }

    public readonly struct PokeInteractionEvent
    {
        public PokeInteractionEvent(
            PokeInteractionPhase phase,
            XRNode handNode,
            XRHandJointID joint,
            TrackedHandContactProbe probe,
            AvatarContactRegion region,
            Vector3 point,
            Vector3 normal,
            float penetrationDepth,
            bool pinching)
        {
            Phase = phase;
            HandNode = handNode;
            Joint = joint;
            Probe = probe;
            Region = region;
            Point = point;
            SurfaceNormal = normal.sqrMagnitude > .0000001f ? normal.normalized : Vector3.zero;
            PenetrationDepth = Mathf.Max(0f, penetrationDepth);
            Pinching = pinching;
        }

        public PokeInteractionPhase Phase { get; }
        public XRNode HandNode { get; }
        public XRHandJointID Joint { get; }
        public TrackedHandContactProbe Probe { get; }
        public AvatarContactRegion Region { get; }
        public Vector3 Point { get; }
        public Vector3 SurfaceNormal { get; }
        public float PenetrationDepth { get; }
        public bool Pinching { get; }
    }

    /// <summary>
    /// Converts the existing tracked-contact lifecycle to Poke semantics.
    /// It deliberately does not apply a pose correction or infer a reaction.
    /// </summary>
    public sealed class PokeInteractionLifecycle
    {
        public bool IsActive { get; private set; }

        public PokeInteractionEvent Observe(TrackedHandContactFact fact)
        {
            var phase = fact.Phase == TrackedHandContactPhase.Began
                ? PokeInteractionPhase.Enter
                : fact.Phase == TrackedHandContactPhase.Ended
                    ? PokeInteractionPhase.Exit
                    : PokeInteractionPhase.Hover;
            IsActive = phase != PokeInteractionPhase.Exit;
            return new PokeInteractionEvent(
                phase,
                fact.HandNode,
                fact.Joint,
                fact.Probe,
                fact.Region,
                fact.Point,
                fact.SurfaceNormal,
                fact.PenetrationDepth,
                fact.Pinching);
        }

        public void Reset()
        {
            IsActive = false;
        }
    }

    /// <summary>
    /// Optional bridge for XRI/Meta/Pico adapters. It forwards Poke facts to
    /// the existing semantic interaction service and never moves the hand.
    /// </summary>
    public sealed class AvatarPokeInteractableAdapter : MonoBehaviour, IAvatarPokeInteractable
    {
        [SerializeField] private AvatarHumanInteraction interaction;

        public string InteractableId => gameObject == null ? string.Empty : gameObject.name;

        public void Bind(AvatarHumanInteraction target)
        {
            interaction = target;
        }

        public void OnPoke(PokeInteractionEvent poke)
        {
            if (interaction == null || poke.Phase == PokeInteractionPhase.Exit)
            {
                return;
            }

            interaction.ReportTrackedHandContact(
                poke.Region,
                poke.Pinching,
                poke.Point,
                poke.SurfaceNormal * -poke.PenetrationDepth);
        }
    }
}
