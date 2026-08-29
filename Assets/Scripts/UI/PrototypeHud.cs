using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Temporary debug HUD. It is intentionally OnGUI-based so the prototype
    /// needs no prefab or font asset. Replace it with world-space UI before UX
    /// polish or store release.
    /// </summary>
    public sealed class PrototypeHud : MonoBehaviour
    {
        private QuestMmdPlayerBootstrap bootstrap;
        private string inputText = "让角色挥手";

        public void Initialize(QuestMmdPlayerBootstrap owner)
        {
            bootstrap = owner;
        }

        private void OnGUI()
        {
            if (bootstrap == null || bootstrap.Avatar == null)
            {
                return;
            }

            GUILayout.BeginArea(new Rect(20f, 20f, 470f, 500f), GUI.skin.box);
            GUILayout.Label("伴夏 Prototype");
            GUILayout.Label($"Passthrough: {bootstrap.Passthrough.Status}");
            GUILayout.Label($"Action: {bootstrap.Avatar.CurrentAction} / {(bootstrap.Avatar.IsPlaying ? "playing" : "paused")}");
            GUILayout.Label($"Emotion: {bootstrap.Avatar.CurrentEmotion}");
            GUILayout.Label($"AstrBot: {bootstrap.AstrBot.Status}");
            GUILayout.Label($"Touch: {(bootstrap.TouchInteraction == null ? "disabled" : bootstrap.TouchInteraction.Status)}");
            GUILayout.Label($"Human: {(bootstrap.HumanInteraction == null ? "disabled" : bootstrap.HumanInteraction.Status)}");
            GUILayout.Label($"Conversation: {(bootstrap.Conversation == null ? "disabled" : bootstrap.Conversation.Status)}");
            GUILayout.Label($"Locomotion: {(bootstrap.Locomotion == null ? "disabled" : bootstrap.Locomotion.Status)}");
            GUILayout.Label($"Placement: {(bootstrap.Placement == null ? "disabled" : bootstrap.Placement.Status)}");
            GUILayout.Label($"Presence: {(bootstrap.Presence == null ? "disabled" : bootstrap.Presence.Status)}");
            if (bootstrap.Conversation != null)
            {
                GUILayout.Label($"Heard: {bootstrap.Conversation.Transcript}");
                GUILayout.Label($"Reply: {bootstrap.Conversation.ReplyText}");
                GUILayout.Label($"Presenter: {bootstrap.Conversation.PresenterStatus} | audio:{bootstrap.Conversation.BufferedAudioSeconds:F2}s");
            }
            GUILayout.Space(8f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Idle")) bootstrap.Avatar.PlayActionFromSource("idle", AvatarActionSource.Manual);
            if (GUILayout.Button("Wave")) bootstrap.Avatar.PlayActionFromSource("wave", AvatarActionSource.Manual);
            if (GUILayout.Button("Bow")) bootstrap.Avatar.PlayActionFromSource("bow", AvatarActionSource.Manual);
            if (GUILayout.Button("Pause")) bootstrap.Avatar.TogglePlayback();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Handshake")) bootstrap.HumanInteraction?.SimulateInteraction(HumanInteractionKind.Handshake);
            if (GUILayout.Button("Head pat")) bootstrap.HumanInteraction?.SimulateInteraction(HumanInteractionKind.HeadPat);
            if (GUILayout.Button("Cheek pinch")) bootstrap.HumanInteraction?.SimulateInteraction(HumanInteractionKind.CheekPinch);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset")) bootstrap.Avatar.ResetTransform();
            if (GUILayout.Button("Place on floor")) bootstrap.Placement?.RequestPlacement();
            if (GUILayout.Button("Toggle Passthrough")) bootstrap.Passthrough.Toggle();
            if (GUILayout.Button("Test JSON"))
            {
                bootstrap.AstrBot.TryIngestCommandJson("{\"command\":\"play_motion\",\"motionId\":\"wave\"}");
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.Label("Conversation test input:");
            inputText = GUILayout.TextField(inputText);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Start mock conversation"))
            {
                bootstrap.Conversation?.StartMockConversation(inputText);
            }
            if (GUILayout.Button("Interrupt"))
            {
                bootstrap.Conversation?.Interrupt();
            }
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Log input to AstrBot bridge"))
            {
                bootstrap.AstrBot.SendUserInput(inputText);
            }

            GUILayout.Space(8f);
            GUILayout.Label("Editor: WASD move | Q/E rotate | R/F scale | 1/2/3 actions | Space pause");
            GUILayout.Label("Quest: left stick = move | right stick = turn | right stick click = place");
            GUILayout.Label("Quest: near = touch | grip/trigger = drag | two hands = rotate + scale");
            GUILayout.Label("Quest buttons: Right A wave | Right B pause | Left A bow | Left B reset");
            GUILayout.Label("Voice: hold left stick click to talk | release to send | menu has TALK / SEND");
            GUILayout.Label("Human: pinch near cheek | palm near head | grip near hand");
            GUILayout.EndArea();
        }
    }
}
