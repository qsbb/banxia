using System;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// The small, provider-neutral command shape shared by the local prototype
    /// and the future AstrBot transport adapter.
    /// </summary>
    [Serializable]
    public sealed class AvatarCommand
    {
        public string name;
        public string motionId;
        public string emotion;
        public string text;
        public float value;
        public float blendSeconds = 0.2f;
        public Vector3 vector;

        public static AvatarCommand Create(string commandName)
        {
            return new AvatarCommand { name = commandName };
        }
    }

    [Serializable]
    public sealed class AstrBotCommandEnvelope
    {
        public string type;
        public string command;
        public string motionId;
        public string emotion;
        public float value;
        public float blendSeconds = 0.2f;
        public string text;
    }
}
