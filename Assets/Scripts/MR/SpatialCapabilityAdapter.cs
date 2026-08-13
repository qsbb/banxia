using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace QuestMmdPlayer
{
    public enum SpatialCapabilityState
    {
        Unavailable,
        Fallback,
        Available
    }

    [Serializable]
    public struct SpatialCapabilitySnapshot
    {
        public SpatialCapabilityState MetaOpenXr;
        public SpatialCapabilityState Mruk;
        public SpatialCapabilityState PlaneTracking;
        public SpatialCapabilityState Occlusion;
        public SpatialCapabilityState VirtualCollision;
        public string Status;
        public string Reason;

        public bool SceneCaptureAvailable => MetaOpenXr == SpatialCapabilityState.Available;

        public override string ToString()
        {
            return $"meta={MetaOpenXr}; mruk={Mruk}; planes={PlaneTracking}; " +
                $"occlusion={Occlusion}; collision={VirtualCollision}; status={Status}; reason={Reason}";
        }
    }

    /// <summary>
    /// Provider-neutral capability probe. MRUK is intentionally detected by
    /// reflection so projects without the optional package keep compiling.
    /// </summary>
    public static class SpatialCapabilityAdapter
    {
        private static readonly string[] MrukTypeNames =
        {
            "Meta.XR.MRUtilityKit.MRUK",
            "Meta.XR.MRUtilityKit.MRUKRoom",
            "Meta.XR.MRUtilityKit.MRUKAnchor"
        };

        public static SpatialCapabilitySnapshot Detect(
            ARPlaneManager planeManager,
            Component xrOrigin,
            XRSessionSubsystem session)
        {
            var planes = planeManager != null
                ? SpatialCapabilityState.Available
                : SpatialCapabilityState.Unavailable;
            var meta = HasSceneCapture(session)
                ? SpatialCapabilityState.Available
                : SpatialCapabilityState.Fallback;
            var mruk = HasOptionalMruk() ? SpatialCapabilityState.Available : SpatialCapabilityState.Unavailable;
            var occlusion = HasComponentType(xrOrigin, "UnityEngine.XR.ARFoundation.AROcclusionManager")
                ? SpatialCapabilityState.Available
                : SpatialCapabilityState.Fallback;
            var collision = HasPhysicsSupport()
                ? SpatialCapabilityState.Available
                : SpatialCapabilityState.Unavailable;
            var status = planes == SpatialCapabilityState.Available
                ? "Spatial tracking available"
                : "Spatial tracking unavailable; front-of-user fallback active";
            var reason = mruk == SpatialCapabilityState.Unavailable
                ? "mruk_optional_not_installed"
                : "provider_capabilities_detected";
            return new SpatialCapabilitySnapshot
            {
                MetaOpenXr = meta,
                Mruk = mruk,
                PlaneTracking = planes,
                Occlusion = occlusion,
                VirtualCollision = collision,
                Status = status,
                Reason = reason
            };
        }

        public static bool HasSceneCapture(XRSessionSubsystem session)
        {
            return session != null && FindSceneCaptureMethod(session.GetType()) != null;
        }

        public static bool TryRequestSceneCapture(XRSessionSubsystem session, out bool requested)
        {
            requested = false;
            var method = session == null ? null : FindSceneCaptureMethod(session.GetType());
            if (method == null)
            {
                return false;
            }
            try
            {
                var result = method.Invoke(session, null);
                requested = result is bool value && value;
                return true;
            }
            catch (TargetInvocationException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public static bool HasOptionalMruk()
        {
            for (var index = 0; index < MrukTypeNames.Length; index++)
            {
                if (FindType(MrukTypeNames[index]) != null)
                {
                    return true;
                }
            }
            return false;
        }

        public static bool TryReadMrukSurfaces(
            ICollection<RoomSurfaceObservation> destination,
            out string reason)
        {
            reason = "mruk_optional_not_installed";
            if (destination == null)
            {
                reason = "destination_missing";
                return false;
            }

            var mrukType = FindType(MrukTypeNames[0]);
            if (mrukType == null)
            {
                return false;
            }
            try
            {
                var instance = GetStaticMember(mrukType, "Instance");
                if (instance == null)
                {
                    reason = "mruk_instance_unavailable";
                    return false;
                }
                var getCurrentRoom = instance.GetType().GetMethod(
                    "GetCurrentRoom",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
                var room = getCurrentRoom == null ? null : getCurrentRoom.Invoke(instance, null);
                var anchors = room == null ? null : GetMember(room, "Anchors") as IEnumerable;
                if (anchors == null)
                {
                    reason = "mruk_room_unavailable";
                    return false;
                }

                var projected = new List<RoomSurfaceObservation>();
                foreach (var anchor in anchors)
                {
                    if (TryProjectMrukAnchor(anchor, projected.Count, out var observation))
                    {
                        projected.Add(observation);
                    }
                }
                if (projected.Count == 0)
                {
                    reason = "mruk_room_has_no_supported_surfaces";
                    return false;
                }
                destination.Clear();
                for (var index = 0; index < projected.Count; index++)
                {
                    destination.Add(projected[index]);
                }
                reason = "mruk_semantics_projected";
                return true;
            }
            catch (Exception)
            {
                reason = "mruk_reflection_failed";
                return false;
            }
        }

        public static bool TryProjectMrukAnchor(
            object anchor,
            int _,
            out RoomSurfaceObservation observation)
        {
            observation = default;
            if (anchor == null || !(anchor is Component component))
            {
                return false;
            }
            var label = Convert.ToString(GetMember(anchor, "Label")) ?? string.Empty;
            if (!TryMapMrukLabel(label, out var classification, out var semanticKind))
            {
                return false;
            }
            if (!TryReadGeometry(anchor, out var localCenter, out var size))
            {
                return false;
            }
            // This ID is intentionally process-local. Runtime selection can use
            // it as a stable hint; persisted placement bookmarks must retain
            // their existing geometry fallback and never treat it as a durable
            // room, anchor, account, or device identifier.
            var instanceId = component.GetInstanceID();
            var id = $"mruk-{label.ToLowerInvariant()}-{instanceId}";
            observation = new RoomSurfaceObservation(
                id,
                classification,
                new Pose(component.transform.TransformPoint(localCenter), component.transform.rotation),
                size,
                semanticKind);
            return RoomUnderstandingService.IsUsableObservation(observation);
        }

        public static bool TryMapMrukLabel(
            string label,
            out PlaneClassification classification,
            out RoomPlacementSurfaceKind? semanticKind)
        {
            classification = PlaneClassification.None;
            semanticKind = null;
            var normalized = (label ?? string.Empty).ToUpperInvariant();
            if (normalized.Contains("BED"))
            {
                classification = PlaneClassification.Seat;
                semanticKind = RoomPlacementSurfaceKind.Bed;
                return true;
            }
            if (normalized.Contains("COUCH") || normalized.Contains("SOFA"))
            {
                classification = PlaneClassification.Seat;
                semanticKind = RoomPlacementSurfaceKind.Couch;
                return true;
            }
            if (normalized.Contains("TABLE"))
            {
                classification = PlaneClassification.Table;
                semanticKind = RoomPlacementSurfaceKind.Table;
                return true;
            }
            if (normalized.Contains("FLOOR"))
            {
                classification = PlaneClassification.Floor;
                semanticKind = RoomPlacementSurfaceKind.Floor;
                return true;
            }
            if (normalized.Contains("WALL"))
            {
                classification = PlaneClassification.Wall;
                return true;
            }
            return false;
        }

        private static bool TryReadGeometry(
            object anchor,
            out Vector3 localCenter,
            out Vector2 size)
        {
            localCenter = Vector3.zero;
            size = Vector2.zero;
            var planeRect = GetNullableValue(GetMember(anchor, "PlaneRect"));
            if (planeRect is Rect rect)
            {
                localCenter = new Vector3(rect.center.x, rect.center.y, 0f);
                size = new Vector2(Mathf.Abs(rect.width), Mathf.Abs(rect.height));
                return size.x > 0f && size.y > 0f;
            }
            var volumeBounds = GetNullableValue(GetMember(anchor, "VolumeBounds"));
            if (volumeBounds is Bounds bounds)
            {
                localCenter = bounds.center;
                var dimensions = new[]
                {
                    Mathf.Abs(bounds.size.x),
                    Mathf.Abs(bounds.size.y),
                    Mathf.Abs(bounds.size.z)
                };
                Array.Sort(dimensions);
                size = new Vector2(dimensions[2], dimensions[1]);
                return size.x > 0f && size.y > 0f;
            }
            return false;
        }

        private static object GetStaticMember(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (property != null) return property.GetValue(null, null);
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
            return field == null ? null : field.GetValue(null);
        }

        private static object GetMember(object target, string name)
        {
            if (target == null) return null;
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null) return property.GetValue(target, null);
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            return field == null ? null : field.GetValue(target);
        }

        private static object GetNullableValue(object value)
        {
            if (value == null) return null;
            var type = value.GetType();
            if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(Nullable<>))
            {
                return value;
            }
            var hasValue = (bool)GetMember(value, "HasValue");
            return hasValue ? GetMember(value, "Value") : null;
        }

        private static MethodInfo FindSceneCaptureMethod(Type type)
        {
            return type == null
                ? null
                : type.GetMethod(
                    "TryRequestSceneCapture",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    Type.EmptyTypes,
                    null);
        }

        private static bool HasComponentType(Component root, string typeName)
        {
            if (root == null)
            {
                return false;
            }
            var type = FindType(typeName);
            return type != null && root.GetComponent(type) != null;
        }

        private static bool HasPhysicsSupport()
        {
            return Physics.defaultPhysicsScene.IsValid();
        }

        private static Type FindType(string fullName)
        {
            var type = Type.GetType(fullName, false);
            if (type != null)
            {
                return type;
            }
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var index = 0; index < assemblies.Length; index++)
            {
                type = assemblies[index].GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }
    }
}
