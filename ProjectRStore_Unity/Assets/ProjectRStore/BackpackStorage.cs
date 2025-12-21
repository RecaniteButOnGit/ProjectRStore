using System.Collections;
using System.Collections.Generic;
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
    [Tooltip("Optional. Where stored items will be moved (if not deactivating).")]
    public Transform StorePoint;

    [Tooltip("Where dumped items appear when released.")]
    public Transform DumpSpawnPoint;

    [Header("Pause/Hide Mode")]
    public HideModeWhileStored HideMode = HideModeWhileStored.DeactivateGameObject;

    public enum HideModeWhileStored
    {
        // Best "pause": stops Update/coroutines/timers (grenade timer pauses).
        DeactivateGameObject,

        // Keeps GO active, but disables visuals + colliders + physics + scripts. (Less “true pause”.)
        DisableRenderAndPhysics
    }

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

        // For DisableRenderAndPhysics mode
        public bool rbWasKinematic;
        public Vector3 rbVel;
        public Vector3 rbAngVel;
        public readonly List<Collider> colliders = new();
        public readonly List<Renderer> renderers = new();
        public readonly List<Behaviour> behaviours = new(); // scripts/animators, etc
        public readonly List<bool> behaviourEnabled = new();
        public readonly List<bool> colliderEnabled = new();
        public readonly List<bool> rendererEnabled = new();
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
        // animate flap every frame (uses isOpen)
        AnimateFlap();

        // dumping only while open
        if (!isOpen) return;

        bool wantsDump = stored.Count > 0 && TiltAngleFromUpright() >= DumpStartAngle;
        bool shouldStop = isDumping && TiltAngleFromUpright() < DumpStopAngle;

        if (!isDumping && wantsDump)
            StartDumping();

        if (shouldStop)
            StopDumping();
    }

    // ----------------------------
    // INPUT (called by your HandGrabber reflection)
    // ----------------------------

    // B button should call this (via your reflection names list)
    public void Secondary()
    {
        ToggleOpen();
    }

    // harmless aliases
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
        if (isDumping) return; // no adding during dump

        if (!other || other == MouthTrigger) return;

        // Only accept items on Items layer (matching your setup)
        if (itemsLayer != -1 && other.gameObject.layer != itemsLayer) return;

        // Find a rigidbody to treat as "the item"
        Rigidbody rb = other.attachedRigidbody;
        if (!rb) rb = other.GetComponentInParent<Rigidbody>();
        if (!rb) return;

        // Don't store ourselves
        if (rb.transform.IsChildOf(transform)) return;

        // Store the whole rigidbody root object (usually your item root)
        GameObject root = rb.gameObject;
        StoreItem(root, rb);
    }

    private void StoreItem(GameObject root, Rigidbody rb)
    {
        if (!root) return;

        // Prevent double-store
        for (int i = 0; i < stored.Count; i++)
            if (stored[i].root == root)
                return;

        var s = new StoredItem
        {
            root = root,
            rb = rb
        };

        if (HideMode == HideModeWhileStored.DeactivateGameObject)
        {
            // "True pause": stops scripts/timers/coroutines/particles/audio.
            root.SetActive(false);
        }
        else
        {
            // Keep active but disable visuals/physics/scripts
            CacheAndDisable(root, s);

            if (StorePoint)
            {
                root.transform.SetParent(StorePoint, true);
                root.transform.localPosition = Vector3.zero;
                root.transform.localRotation = Quaternion.identity;
            }
            else
            {
                root.transform.SetParent(transform, true);
            }
        }

        stored.Add(s);
        Log($"Stored: {root.name} (count={stored.Count})");
    }

    private void CacheAndDisable(GameObject root, StoredItem s)
    {
        // Rigidbody
        if (s.rb)
        {
            s.rbWasKinematic = s.rb.isKinematic;
            s.rbVel = s.rb.velocity;
            s.rbAngVel = s.rb.angularVelocity;

            s.rb.isKinematic = true;
            s.rb.velocity = Vector3.zero;
            s.rb.angularVelocity = Vector3.zero;
        }

        // Colliders
        root.GetComponentsInChildren(true, s.colliders);
        for (int i = 0; i < s.colliders.Count; i++)
        {
            s.colliderEnabled.Add(s.colliders[i].enabled);
            s.colliders[i].enabled = false;
        }

        // Renderers
        root.GetComponentsInChildren(true, s.renderers);
        for (int i = 0; i < s.renderers.Count; i++)
        {
            s.rendererEnabled.Add(s.renderers[i].enabled);
            s.renderers[i].enabled = false;
        }

        // Behaviours (scripts, animators, audio, etc)
        var allBehaviours = root.GetComponentsInChildren<Behaviour>(true);
        foreach (var b in allBehaviours)
        {
            if (!b) continue;

            // Don’t disable this Backpack script
            if (b == this) continue;

            s.behaviours.Add(b);
            s.behaviourEnabled.Add(b.enabled);
            b.enabled = false;
        }
    }

    private void RestoreDisabled(StoredItem s)
    {
        // Behaviours
        for (int i = 0; i < s.behaviours.Count; i++)
        {
            if (!s.behaviours[i]) continue;
            s.behaviours[i].enabled = s.behaviourEnabled[i];
        }

        // Renderers
        for (int i = 0; i < s.renderers.Count; i++)
        {
            if (!s.renderers[i]) continue;
            s.renderers[i].enabled = s.rendererEnabled[i];
        }

        // Colliders
        for (int i = 0; i < s.colliders.Count; i++)
        {
            if (!s.colliders[i]) continue;
            s.colliders[i].enabled = s.colliderEnabled[i];
        }

        // Rigidbody
        if (s.rb)
        {
            s.rb.isKinematic = false;
            s.rb.velocity = Vector3.zero;
            s.rb.angularVelocity = Vector3.zero;
        }
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
            // LIFO: newest item first
            var s = stored[stored.Count - 1];
            stored.RemoveAt(stored.Count - 1);

            DumpOne(s);

            yield return new WaitForSeconds(DumpInterval);

            // If player stops tipping, stop quickly
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

        if (HideMode == HideModeWhileStored.DeactivateGameObject)
        {
            s.root.SetActive(true);
        }
        else
        {
            RestoreDisabled(s);
        }

        // Place item at spawn
        s.root.transform.SetParent(null, true);
        s.root.transform.position = spawn.position;
        s.root.transform.rotation = spawn.rotation;

        // Give it a little “fall out”
        if (s.rb)
        {
            Vector3 push = (spawn.forward * EjectForce) + (Vector3.down * DownForce);
            s.rb.AddForce(push, ForceMode.VelocityChange);
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

        // Convert current localEulerAngles.x to signed [-180,180] so lerp doesn’t flip at 360->0
        float curX = LidOrFlap.localEulerAngles.x;
        if (curX > 180f) curX -= 360f;

        // Smooth exponential lerp (frame-rate independent)
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
