using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public class BackpackStorage : MonoBehaviour
{
    [Header("State")]
    [SerializeField] private bool isOpen = false;
    public bool IsOpen => isOpen;

    [Header("Detection")]
    [Tooltip("Trigger collider representing the backpack opening. Items must enter this trigger to be stored.")]
    public Collider MouthTrigger;

    [Tooltip("Only store colliders on this layer (match your HandGrabber Items layer).")]
    public string ItemsLayerName = "Items";

    [Header("Store/Dump Points")]
    [Tooltip("Where stored items will be parented while inside the backpack. If null, uses this transform.")]
    public Transform StorePoint;

    [Tooltip("Where dumped items appear when released. (Best if this is OUTSIDE the MouthTrigger volume.)")]
    public Transform DumpSpawnPoint;

    [Header("Dumping")]
    [Tooltip("Start dumping when tilted at/over this many degrees away from upright.")]
    [Range(0f, 180f)] public float DumpStartAngle = 45f;

    [Tooltip("Stop dumping when tilt goes below this angle (hysteresis to prevent flicker).")]
    [Range(0f, 180f)] public float DumpStopAngle = 40f;

    [Tooltip("Seconds between each item dump.")]
    public float DumpInterval = 0.75f;

    [Tooltip("How hard to push items out when dumping.")]
    public float EjectForce = 1.25f;

    [Tooltip("Extra downward push (helps it actually fall out).")]
    public float DownForce = 0.75f;

    [Header("Intake Rules")]
    [Tooltip("If true, backpack will NOT intake items while tilted at/over DumpStartAngle.")]
    public bool BlockIntakeAtDumpAngle = true;

    [Tooltip("If true, backpack will NOT intake items while currently dumping.")]
    public bool BlockIntakeWhileDumping = true;

    [Header("Backpack Storage Filter")]
    [Tooltip("If true, items must have CanBeStoredInBackpack == true (usually on NetworkItem). If flag not found, item is allowed.")]
    public bool RequireCanBeStoredInBackpackTrue = true;

    [Header("Open/Close Animation")]
    [Tooltip("The object that rotates locally on X when opening/closing (flap/lid).")]
    public Transform LidOrFlap;
    public float OpenXAngle = -100f;
    public float ClosedXAngle = 0f;
    public float RotateLerpSpeed = 12f;

    [Header("Debug")]
    public bool DebugLogs = true;

    private int itemsLayer = -1;
    private bool isDumping = false;
    private Coroutine dumpRoutine;

    private float flapStartY, flapStartZ;

    private class StoredItem
    {
        public GameObject root;
        public Rigidbody rb;
    }

    // LIFO: newest stored is last in list
    private readonly List<StoredItem> stored = new();

    private void Awake()
    {
        itemsLayer = LayerMask.NameToLayer(ItemsLayerName);
        if (itemsLayer == -1) Log($"❌ Layer '{ItemsLayerName}' not found.");

        if (!MouthTrigger)
            Log("❌ MouthTrigger is not assigned (no storing will happen).");

        if (MouthTrigger && !MouthTrigger.isTrigger)
            Log("❌ MouthTrigger collider must be set to IsTrigger.");

        if (!DumpSpawnPoint)
            Log("⚠️ DumpSpawnPoint not assigned. Will dump at backpack position.");

        if (LidOrFlap)
        {
            var e = LidOrFlap.localEulerAngles;
            flapStartY = e.y;
            flapStartZ = e.z;
        }
    }

    private void Update()
    {
        AnimateFlap();

        if (!isOpen) return;

        float tilt = TiltAngleFromUpright();

        bool wantsDump = stored.Count > 0 && tilt >= DumpStartAngle;
        bool shouldStop = isDumping && tilt < DumpStopAngle;

        if (!isDumping && wantsDump)
            StartDumping();

        if (shouldStop)
            StopDumping();
    }

    // ----------------------------
    // INPUT (called by your HandGrabber reflection)
    // ----------------------------

    public void Secondary() => ToggleOpen();
    public void SecondaryButton() => Secondary();
    public void OnSecondary() => Secondary();

    public void ToggleOpen()
    {
        if (isOpen) Close();
        else Open();
    }

    public void Open()
    {
        isOpen = true;
        Log("🎒 Backpack OPEN");
    }

    public void Close()
    {
        isOpen = false;
        StopDumping();
        Log("🎒 Backpack CLOSED");
    }

    // ----------------------------
    // STORE
    // ----------------------------

    private void OnTriggerEnter(Collider other)
    {
        if (!isOpen) return;
        if (!other) return;

        // ✅ no intake at dumping tilt angle
        if (BlockIntakeAtDumpAngle && TiltAngleFromUpright() >= DumpStartAngle)
            return;

        // optional: also block intake while actively dumping
        if (BlockIntakeWhileDumping && isDumping)
            return;

        // Only accept items on Items layer (matching your setup)
        if (itemsLayer != -1 && other.gameObject.layer != itemsLayer)
            return;

        // Find the rigidbody (treat that as the item root)
        Rigidbody rb = other.attachedRigidbody;
        if (!rb) rb = other.GetComponentInParent<Rigidbody>();
        if (!rb) return;

        // Don't store ourselves
        if (rb.transform.IsChildOf(transform)) return;

        GameObject root = rb.gameObject;
        if (!root) return;

        // Don’t store already-inactive stuff (usually means it’s already stored)
        if (!root.activeInHierarchy) return;

        // ✅ Check item flag
        if (RequireCanBeStoredInBackpackTrue && !ItemAllowsBackpack(root))
            return;

        // Prevent double-store
        for (int i = 0; i < stored.Count; i++)
            if (stored[i].root == root)
                return;

        StoreItem(root, rb);
    }

    private bool ItemAllowsBackpack(GameObject root)
    {
        if (!root) return false;

        // 1) Fast path: your common item script name
        // If your NetworkItem class exists in the project, this compiles and works.
        // If you renamed it, the reflection fallback below still covers you.
        var ni = root.GetComponentInParent<NetworkItem>();
        if (ni != null)
        {
            if (!ni.CanBeStoredInBackpack)
            {
                Log($"🚫 Not storable (NetworkItem.CanBeStoredInBackpack=false): {root.name}");
                return false;
            }
            return true;
        }

        // 2) Reflection fallback: look for a bool field/property named CanBeStoredInBackpack
        // on any component on root or its parents.
        var comps = root.GetComponentsInParent<Component>(true);
        for (int i = 0; i < comps.Length; i++)
        {
            var c = comps[i];
            if (!c) continue;

            var t = c.GetType();

            // field
            var f = t.GetField("CanBeStoredInBackpack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(bool))
            {
                bool v = (bool)f.GetValue(c);
                if (!v) Log($"🚫 Not storable ({t.Name}.CanBeStoredInBackpack=false): {root.name}");
                return v;
            }

            // property
            var p = t.GetProperty("CanBeStoredInBackpack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(bool) && p.CanRead)
            {
                bool v = (bool)p.GetValue(c, null);
                if (!v) Log($"🚫 Not storable ({t.Name}.CanBeStoredInBackpack=false): {root.name}");
                return v;
            }
        }

        // If the flag isn't found, allow by default (so random props still work)
        return true;
    }

    private void StoreItem(GameObject root, Rigidbody rb)
    {
        Transform parent = StorePoint ? StorePoint : transform;

        // Parent it (while still active so it keeps world pose cleanly)
        root.transform.SetParent(parent, true);

        // Kill motion so it doesn’t “resume” with old velocity later
        if (rb)
        {
            rb.velocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        // Disable it (your “true pause”)
        root.SetActive(false);

        stored.Add(new StoredItem { root = root, rb = rb });
        Log($"Stored: {root.name} (count={stored.Count})");
    }

    // ----------------------------
    // DUMPING
    // ----------------------------

    private void StartDumping()
    {
        if (isDumping) return;
        isDumping = true;
        dumpRoutine = StartCoroutine(DumpLoop());
        Log("⬇️ Dumping START");
    }

    private void StopDumping()
    {
        if (!isDumping) return;
        isDumping = false;

        if (dumpRoutine != null)
        {
            StopCoroutine(dumpRoutine);
            dumpRoutine = null;
        }

        Log("⏹️ Dumping STOP");
    }

    private IEnumerator DumpLoop()
    {
        while (isOpen && isDumping && stored.Count > 0 && TiltAngleFromUpright() >= DumpStopAngle)
        {
            // LIFO: most recent stored dumped first
            var s = stored[stored.Count - 1];
            stored.RemoveAt(stored.Count - 1);

            DumpOne(s);

            yield return new WaitForSeconds(DumpInterval);

            if (TiltAngleFromUpright() < DumpStopAngle)
                break;
        }

        isDumping = false;
        dumpRoutine = null;
        Log("⬇️ Dumping END");
    }

    private void DumpOne(StoredItem s)
    {
        if (s == null || !s.root) return;

        Transform spawn = DumpSpawnPoint ? DumpSpawnPoint : transform;

        // Keep it inactive while we reposition
        if (s.root.activeSelf) s.root.SetActive(false);

        // Unparent, move, rotate
        s.root.transform.SetParent(null, true);
        s.root.transform.position = spawn.position;
        s.root.transform.rotation = spawn.rotation;

        // Enable it again
        s.root.SetActive(true);

        Rigidbody rb = s.rb ? s.rb : s.root.GetComponentInChildren<Rigidbody>();

        // Push it out
        if (rb)
        {
            rb.WakeUp();
            Vector3 push = (spawn.forward * EjectForce) + (Vector3.down * DownForce);
            rb.AddForce(push, ForceMode.VelocityChange);
        }

        Log($"Dumped: {s.root.name} (remaining={stored.Count})");
    }

    private float TiltAngleFromUpright()
    {
        // 0 = upright, 180 = upside down
        return Vector3.Angle(transform.up, Vector3.up);
    }

    // ----------------------------
    // OPEN/CLOSE ANIM
    // ----------------------------

    private void AnimateFlap()
    {
        if (!LidOrFlap) return;

        float targetX = isOpen ? OpenXAngle : ClosedXAngle;

        float curX = LidOrFlap.localEulerAngles.x;
        if (curX > 180f) curX -= 360f;

        float t = 1f - Mathf.Exp(-RotateLerpSpeed * Time.deltaTime);
        float newX = Mathf.Lerp(curX, targetX, t);

        LidOrFlap.localEulerAngles = new Vector3(newX, flapStartY, flapStartZ);
    }

    // ----------------------------
    // LOG
    // ----------------------------

    private void Log(string msg)
    {
        if (DebugLogs) Debug.Log($"[Backpack] {msg}", this);
    }
}
