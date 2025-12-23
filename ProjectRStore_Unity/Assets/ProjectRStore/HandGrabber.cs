using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public class HandGrabber : MonoBehaviour
{
    [Header("Hand")]
    public bool IsLeftHand = true;

    [Header("Rig Mode")]
    [Tooltip("OFF = Local rig (uses a physics joint, no parenting). ON = Mirror/remote rig (parents item to anchor).")]
    public bool IsMirrorRig = false;

    [Header("Grab")]
    [Tooltip("If null, auto-finds ItemAnchor under parent hand controller.")]
    public Transform LocalHandAnchor;
    public string ItemsLayerName = "Items";

    [Header("Local Rig Joint Settings")]
    [Tooltip("If true (recommended), local rig uses a joint instead of parenting.")]
    public bool UseJointOnLocalRig = true;

    [Tooltip("Disable gravity while held on local rig (joint mode).")]
    public bool DisableGravityWhileHeld = true;

    [Tooltip("If true, disables collisions between the jointed item and the physics anchor rigidbody.")]
    public bool JointEnableCollision = false;

    [Tooltip("Break force for the joint (Infinity = unbreakable).")]
    public float JointBreakForce = Mathf.Infinity;

    [Tooltip("Break torque for the joint (Infinity = unbreakable).")]
    public float JointBreakTorque = Mathf.Infinity;

    [Tooltip("Mass scaling for the joint (can help stability).")]
    public float JointMassScale = 1f;

    [Tooltip("Connected mass scaling for the joint (can help stability).")]
    public float JointConnectedMassScale = 1f;

    [Header("Debug")]
    public bool DebugLogs = true;
    public bool DebugItemMethodCalls = true;

    private int itemsLayer = -1;
    private readonly List<Rigidbody> nearby = new();
    private Rigidbody held;

    // Local rig joint bits
    private Transform physicsAnchor;
    private Rigidbody physicsAnchorRb;
    private FixedJoint heldJoint;
    private bool heldPrevUseGravity;

    // Cache NetworkItem by ItemID (for RPC lookups)
    private static readonly Dictionary<int, NetworkItem> idToItemCache = new();
    private static float lastCacheRebuildTime = -999f;

    // Reflection cache: Type -> methodName -> methods
    private static readonly Dictionary<Type, Dictionary<string, MethodInfo[]>> methodCache = new();

    private void Awake()
    {
        itemsLayer = LayerMask.NameToLayer(ItemsLayerName);
        Log($"Awake | ItemsLayer '{ItemsLayerName}' -> {itemsLayer}");
        if (itemsLayer == -1) LogErr($"❌ Layer '{ItemsLayerName}' not found.");

        if (LocalHandAnchor == null && transform.parent != null)
        {
            LocalHandAnchor = transform.parent.Find("ItemAnchor");
            Log(LocalHandAnchor ? $"✅ Auto-found LocalHandAnchor: {Path(LocalHandAnchor)}"
                               : "❌ Could not auto-find ItemAnchor under parent. Assign LocalHandAnchor.");
        }

        if (!IsMirrorRig && UseJointOnLocalRig)
            EnsurePhysicsAnchor();

        RebuildItemCacheIfNeeded(force: true);
    }

    private void OnDisable()
    {
        // Clean up if you disable the hand while holding something
        if (held != null) Drop();
    }

    private void FixedUpdate()
    {
        // Keep the physics anchor glued to the tracked anchor (local rig joint mode)
        if (!IsMirrorRig && UseJointOnLocalRig && physicsAnchorRb != null && LocalHandAnchor != null)
        {
            physicsAnchorRb.MovePosition(LocalHandAnchor.position);
            physicsAnchorRb.MoveRotation(LocalHandAnchor.rotation);
        }
    }

    // ---------------------------
    // INPUT ENTRY POINTS
    // ---------------------------

    // Primary = grab toggle (A)
    public void PrimaryButton()
    {
        Log($"PrimaryButton | held={(held ? held.name : "NONE")} nearby={nearby.Count}");
        if (held == null) TryGrab();
        else Drop();
    }

    // Secondary = tool toggle (B)
    public void SecondaryButton()
    {
        if (!held) { LogWarn("SecondaryButton: no held item."); return; }

        var ni = FindNetworkItemOnHeld();
        if (!ni || ni.ItemID == 0) { LogWarn("SecondaryButton: held item missing NetworkItem/ItemID."); return; }

        // Run locally
        ApplyItemButtonLocal(ni.ItemID, ItemButton.Secondary, 1f);

        // Sync via ItemNetworkManager PhotonView (NO PhotonView needed here)
        if (ItemNetworkManager.Instance != null)
            ItemNetworkManager.Instance.BroadcastItemButton(ni.ItemID, (int)ItemButton.Secondary, 1f);
        else
            LogWarn("SecondaryButton: ItemNetworkManager.Instance is null (no sync).");
    }

    // Trigger = shoot/use
    public void TriggerButton(float value = 1f)
    {
        if (!held) { LogWarn("TriggerButton: no held item."); return; }

        var ni = FindNetworkItemOnHeld();
        if (!ni || ni.ItemID == 0) { LogWarn("TriggerButton: held item missing NetworkItem/ItemID."); return; }

        ApplyItemButtonLocal(ni.ItemID, ItemButton.Trigger, value);

        if (ItemNetworkManager.Instance != null)
            ItemNetworkManager.Instance.BroadcastItemButton(ni.ItemID, (int)ItemButton.Trigger, value);
        else
            LogWarn("TriggerButton: ItemNetworkManager.Instance is null (no sync).");
    }

    // ---------------------------
    // GRAB / DROP
    // ---------------------------

    private void TryGrab()
    {
        CleanupNulls();

        if (nearby.Count == 0)
        {
            LogWarn("TryGrab: no nearby items.");
            return;
        }

        Rigidbody rb = GetClosest();
        if (!rb) { LogWarn("TryGrab: closest was null."); return; }

        NetworkItem ni = FindNetworkItem(rb);
        if (!ni) { LogWarn($"TryGrab: '{rb.name}' has no NetworkItem."); return; }
        if (ni.ItemID == 0) { LogWarn($"TryGrab: '{rb.name}' ItemID == 0."); return; }

        held = rb;

        // MIRROR/REMOTE RIG: parent + kinematic
        if (IsMirrorRig || !UseJointOnLocalRig)
        {
            if (LocalHandAnchor != null)
                rb.transform.SetParent(LocalHandAnchor, true);
            else
                LogWarn("LocalHandAnchor missing; parenting skipped.");

            rb.isKinematic = true;
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;

            Log($"✅ Grabbed (PARENT MODE) '{rb.name}' itemID={ni.ItemID}");
            return;
        }

        // LOCAL RIG: JOINT MODE (NO parenting)
        if (LocalHandAnchor == null)
        {
            LogWarn("TryGrab: LocalHandAnchor missing (joint mode). Falling back to parent mode.");
            rb.transform.SetParent(null, true);
            rb.transform.SetParent(LocalHandAnchor, true);
            rb.isKinematic = true;
            return;
        }

        EnsurePhysicsAnchor();

        if (physicsAnchorRb == null)
        {
            LogErr("TryGrab: physicsAnchorRb missing; cannot joint-grab.");
            return;
        }

        // Make sure item isn't parented to anything
        rb.transform.SetParent(null, true);

        // Remember & tweak physics while held
        heldPrevUseGravity = rb.useGravity;
        if (DisableGravityWhileHeld) rb.useGravity = false;

        // Optional: bump stability (you can tweak these if needed)
        // rb.interpolation = RigidbodyInterpolation.Interpolate;

        // Snap the item to the hand anchor pose (and apply optional snap offset)
        Quaternion targetRot = LocalHandAnchor.rotation;
        Vector3 targetPos = LocalHandAnchor.position;

        if (ni != null && ni.SnapGrab)
        {
            targetPos = LocalHandAnchor.TransformPoint(ni.SnapLocalPosition);
            targetRot = LocalHandAnchor.rotation * Quaternion.Euler(ni.SnapLocalEulerAngles);
        }

        rb.position = targetPos;
        rb.rotation = targetRot;

        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.isKinematic = false;

        // Create joint on the HELD item, connecting to our kinematic physics anchor
        heldJoint = rb.gameObject.AddComponent<FixedJoint>();
        heldJoint.connectedBody = physicsAnchorRb;
        heldJoint.enableCollision = JointEnableCollision;
        heldJoint.breakForce = JointBreakForce;
        heldJoint.breakTorque = JointBreakTorque;
        heldJoint.massScale = Mathf.Max(0.0001f, JointMassScale);
        heldJoint.connectedMassScale = Mathf.Max(0.0001f, JointConnectedMassScale);

        Log($"✅ Grabbed (JOINT MODE) '{rb.name}' itemID={ni.ItemID}");
    }

    private void Drop()
    {
        Rigidbody rb = held;
        held = null;
        if (!rb) return;

        // LOCAL JOINT MODE: remove joint + restore physics
        if (!IsMirrorRig && UseJointOnLocalRig)
        {
            if (heldJoint != null)
            {
                Destroy(heldJoint);
                heldJoint = null;
            }

            rb.useGravity = heldPrevUseGravity;
            rb.isKinematic = false;
            rb.transform.SetParent(null, true);

            Log($"✅ Dropped (JOINT MODE) '{rb.name}'");
            return;
        }

        // MIRROR/REMOTE MODE: unparent + un-kinematic
        rb.transform.SetParent(null, true);
        rb.isKinematic = false;

        Log($"✅ Dropped (PARENT MODE) '{rb.name}'");
    }

    private void EnsurePhysicsAnchor()
    {
        if (physicsAnchorRb != null) return;
        if (LocalHandAnchor == null) return;

        // Create a hidden-ish child under the tracked anchor that owns the kinematic Rigidbody
        var go = new GameObject(IsLeftHand ? "HG_PhysicsAnchor_L" : "HG_PhysicsAnchor_R");
        go.transform.SetParent(LocalHandAnchor, false);
        go.transform.localPosition = Vector3.zero;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        physicsAnchor = go.transform;

        physicsAnchorRb = go.AddComponent<Rigidbody>();
        physicsAnchorRb.isKinematic = true;
        physicsAnchorRb.useGravity = false;
        physicsAnchorRb.detectCollisions = false;
        physicsAnchorRb.interpolation = RigidbodyInterpolation.Interpolate;

        Log($"✅ Created physics anchor: {Path(physicsAnchor)}");
    }

    private Rigidbody GetClosest()
    {
        Rigidbody best = null;
        float bestDist = float.MaxValue;
        Vector3 p = transform.position;

        foreach (var rb in nearby)
        {
            if (!rb) continue;
            float d = Vector3.Distance(p, rb.worldCenterOfMass);
            if (d < bestDist) { bestDist = d; best = rb; }
        }
        return best;
    }

    private NetworkItem FindNetworkItemOnHeld() => held ? FindNetworkItem(held) : null;

    private NetworkItem FindNetworkItem(Rigidbody rb)
    {
        if (!rb) return null;
        var ni = rb.GetComponent<NetworkItem>();
        if (ni) return ni;
        ni = rb.GetComponentInParent<NetworkItem>();
        if (ni) return ni;
        return rb.GetComponentInChildren<NetworkItem>(true);
    }

    // ---------------------------
    // ITEM BUTTON EXECUTION (LOCAL + RPC)
    // ---------------------------

    public enum ItemButton { Primary = 0, Secondary = 1, Trigger = 2 }

    // Called by ItemNetworkManager RPC too:
    public static void ApplyItemButtonLocal(int itemId, ItemButton button, float value)
    {
        if (itemId == 0) return;

        RebuildItemCacheIfNeeded(force: false);

        if (!idToItemCache.TryGetValue(itemId, out var ni) || ni == null)
        {
            RebuildItemCacheIfNeeded(force: true);
            idToItemCache.TryGetValue(itemId, out ni);
        }

        if (ni == null)
        {
            Debug.LogWarning($"[HandGrabber] ApplyItemButtonLocal: could not find NetworkItem with ItemID={itemId}");
            return;
        }

        GameObject root = ni.gameObject;
        InvokeButtonMethods(root, button, value, debugCalls: true);
    }

    private static int InvokeButtonMethods(GameObject itemRoot, ItemButton button, float value, bool debugCalls)
    {
        if (!itemRoot) return 0;

        // EXACT names you asked for + harmless aliases
        string[] names = button switch
        {
            ItemButton.Primary => new[] { "Primary", "OnPrimary", "PrimaryButton", "OnPrimaryButton" },
            ItemButton.Secondary => new[] { "Secondary", "OnSecondary", "SecondaryButton", "OnSecondaryButton" },
            ItemButton.Trigger => new[] { "Trigger", "OnTrigger", "TriggerButton", "OnTriggerButton" },
            _ => Array.Empty<string>()
        };

        int calls = 0;
        var behaviours = itemRoot.GetComponentsInChildren<MonoBehaviour>(true);

        foreach (var mb in behaviours)
        {
            if (!mb) continue;

            Type t = mb.GetType();
            var map = GetCachedMethodsForType(t);

            foreach (string methodName in names)
            {
                if (!map.TryGetValue(methodName, out var methods) || methods == null) continue;

                foreach (var m in methods)
                {
                    if (m == null) continue;

                    var pars = m.GetParameters();

                    try
                    {
                        if (pars.Length == 0)
                        {
                            m.Invoke(mb, null);
                            calls++;
                            if (debugCalls) Debug.Log($"[ItemInput] {itemRoot.name}: {t.Name}.{m.Name}()");
                        }
                        else if (pars.Length == 1 && pars[0].ParameterType == typeof(float))
                        {
                            m.Invoke(mb, new object[] { value });
                            calls++;
                            if (debugCalls) Debug.Log($"[ItemInput] {itemRoot.name}: {t.Name}.{m.Name}({value})");
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[HandGrabber] Exception invoking {t.Name}.{m.Name} on '{itemRoot.name}': {e}");
                    }
                }
            }
        }

        return calls;
    }

    private static Dictionary<string, MethodInfo[]> GetCachedMethodsForType(Type t)
    {
        if (methodCache.TryGetValue(t, out var cached) && cached != null)
            return cached;

        var dict = new Dictionary<string, MethodInfo[]>(StringComparer.Ordinal);

        var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public);

        var temp = new Dictionary<string, List<MethodInfo>>(StringComparer.Ordinal);
        foreach (var m in methods)
        {
            if (m.ReturnType != typeof(void)) continue;

            if (!temp.TryGetValue(m.Name, out var list))
            {
                list = new List<MethodInfo>();
                temp[m.Name] = list;
            }
            list.Add(m);
        }

        foreach (var kv in temp)
            dict[kv.Key] = kv.Value.ToArray();

        methodCache[t] = dict;
        return dict;
    }

    private static void RebuildItemCacheIfNeeded(bool force)
    {
        if (!force && Time.time - lastCacheRebuildTime < 0.5f) return;

        idToItemCache.Clear();
        var all = GameObject.FindObjectsOfType<NetworkItem>(true);
        foreach (var ni in all)
        {
            if (!ni) continue;
            if (ni.ItemID == 0) continue;
            idToItemCache[ni.ItemID] = ni;
        }

        lastCacheRebuildTime = Time.time;
    }

    // ---------------------------
    // TRIGGER DETECTION
    // ---------------------------

    private void OnTriggerEnter(Collider other)
    {
        if (itemsLayer == -1) return;
        if (other.gameObject.layer != itemsLayer) return;

        var rb = other.attachedRigidbody;
        if (!rb) { LogWarn($"TriggerEnter '{other.name}' on Items layer but no Rigidbody."); return; }

        if (!nearby.Contains(rb))
        {
            nearby.Add(rb);
            Log($"Nearby: {rb.name} (count={nearby.Count})");
        }
    }

    private void OnTriggerExit(Collider other)
    {
        var rb = other.attachedRigidbody;
        if (rb != null && nearby.Remove(rb))
            Log($"Nearby remove: {rb.name} (count={nearby.Count})");
    }

    private void CleanupNulls()
    {
        for (int i = nearby.Count - 1; i >= 0; i--)
            if (nearby[i] == null) nearby.RemoveAt(i);
    }

    // ---------------------------
    // LOGGING
    // ---------------------------

    private void Log(string msg) { if (DebugLogs) Debug.Log($"[HG {(IsLeftHand ? "L" : "R")}] {msg}", this); }
    private void LogWarn(string msg) { if (DebugLogs) Debug.LogWarning($"[HG {(IsLeftHand ? "L" : "R")}] {msg}", this); }
    private void LogErr(string msg) { Debug.LogError($"[HG {(IsLeftHand ? "L" : "R")}] {msg}", this); }

    private static string Path(Transform t)
    {
        if (!t) return "NULL";
        string s = t.name;
        while (t.parent != null) { t = t.parent; s = t.name + "/" + s; }
        return s;
    }

#if UNITY_EDITOR
    [UnityEditor.CustomEditor(typeof(HandGrabber))]
    public class HandGrabberEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            UnityEditor.EditorGUILayout.Space(8);
            UnityEditor.EditorGUILayout.LabelField("Debug Buttons", UnityEditor.EditorStyles.boldLabel);

            var hg = (HandGrabber)target;

            using (new UnityEditor.EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Primary (A)")) hg.PrimaryButton();
                if (GUILayout.Button("Secondary (B)")) hg.SecondaryButton();
                if (GUILayout.Button("Trigger")) hg.TriggerButton(1f);
            }

            UnityEditor.EditorGUILayout.HelpBox(
                "Local rig (IsMirrorRig OFF) grabs with a FixedJoint to a kinematic physics anchor (no parenting). " +
                "Mirror rig (IsMirrorRig ON) uses parenting + kinematic.",
                UnityEditor.MessageType.Info
            );
        }
    }
#endif
}
