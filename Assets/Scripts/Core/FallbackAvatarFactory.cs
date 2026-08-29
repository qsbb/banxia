using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Creates a simple local placeholder so the interaction flow can be tested
    /// without importing a copyrighted or platform-specific MMD asset.
    /// </summary>
    public static class FallbackAvatarFactory
    {
        public static AvatarController Create(Vector3 position)
        {
            var root = new GameObject("Avatar_Fallback");
            root.transform.position = position;
            root.transform.localScale = Vector3.one * 0.8f;

            var controller = root.AddComponent<AvatarController>();
            var visualRoot = new GameObject("Visual").transform;
            visualRoot.SetParent(root.transform, false);

            CreatePrimitive(PrimitiveType.Capsule, "Body", visualRoot, new Vector3(0f, 0.85f, 0f), new Vector3(0.52f, 0.85f, 0.36f), new Color(0.20f, 0.48f, 0.78f));
            CreatePrimitive(PrimitiveType.Sphere, "Head", visualRoot, new Vector3(0f, 2.0f, 0f), new Vector3(0.56f, 0.56f, 0.56f), new Color(1.0f, 0.78f, 0.68f));
            CreatePrimitive(PrimitiveType.Sphere, "Hair", visualRoot, new Vector3(0f, 2.27f, -0.02f), new Vector3(0.59f, 0.35f, 0.59f), new Color(0.08f, 0.12f, 0.20f));
            CreatePrimitive(PrimitiveType.Cube, "LeftHand", visualRoot, new Vector3(-0.62f, 0.95f, 0f), new Vector3(0.16f, 0.48f, 0.16f), new Color(1.0f, 0.78f, 0.68f));
            CreatePrimitive(PrimitiveType.Cube, "RightHand", visualRoot, new Vector3(0.62f, 0.95f, 0f), new Vector3(0.16f, 0.48f, 0.16f), new Color(1.0f, 0.78f, 0.68f));

            controller.Initialize(visualRoot);
            return controller;
        }

        private static void CreatePrimitive(PrimitiveType type, string name, Transform parent, Vector3 localPosition, Vector3 localScale, Color color)
        {
            var part = GameObject.CreatePrimitive(type);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localScale = localScale;

            var collider = part.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }

            var renderer = part.GetComponent<Renderer>();
            if (renderer != null)
            {
                var material = new Material(FindDefaultShader());
                material.color = color;
                renderer.material = material;
            }
        }

        private static Shader FindDefaultShader()
        {
            return Shader.Find("Universal Render Pipeline/Unlit")
                ?? Shader.Find("Unlit/Texture")
                ?? Shader.Find("Standard");
        }
    }
}
