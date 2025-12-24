using UnityEngine;
using UnityEngine.Events;
using Photon.Pun;

[RequireComponent(typeof(PhotonView))]
public class BreakableObject : MonoBehaviour
{
    [Header("Durability")]
    public float MaxDurability = 100f;
    public float Durability = 0f;
    public bool BreakOnce = true;
    public float BreakCooldown = 0.05f;

    [Header("Weapon Filter")]
    [Tooltip("If true, ONLY objects tagged Weapon can damage/break this.")]
    public bool OnlyDamageFromWeaponTag = true;

    [Tooltip("Tag required on the hitter (or its parents).")]
    public string WeaponTag = "Weapon";

    [Header("Break Trigger - Collision")]
    public bool BreakOnCollision = true;

    [Tooltip("Uses the hitter's per-frame movement velocity (FrameVelocityTracker) when available.")]
    public bool PreferFrameBasedHitterVelocity = true;

    [Tooltip("Collision speed required to apply collision damage / break.")]
    public float MinCollisionSpeed = 6f;

    [Tooltip("If enabled, collision directly breaks object when speed >= MinCollisionSpeed (ignores durability).")]
    public bool CollisionInstantBreak = false;

    [Tooltip("If NOT instant break, collision damage = speed * CollisionDamageMultiplier.")]
    public float CollisionDamageMultiplier = 10f;

    [Tooltip("Optional: only collisions with these layers can break it.")]
    public LayerMask CollisionLayerMask = ~0;

    [Header("Broken Replacement")]
    public GameObject BrokenPrefab;
    public bool DestroyOriginal = true;
    public bool DisableInsteadOfDestroy = false;

    [Header("Debris Physics")]
    public bool ApplyExplosionForce = true;
    public float ExplosionForce = 250f;
    public float ExplosionRadius = 2.0f;
    public float UpwardsModifier = 0.15f;
    public bool InheritVelocity = true;
    public float RandomTorque = 10f;

    [Header("FX")]
    public AudioSource AudioSource;
    public AudioClip BreakSfx;
    public ParticleSystem BreakVfx;

    [Header("Drops (Optional)")]
    public GameObject[] DropPrefabs;
    public int DropCount = 0;
    public float DropScatterRadius = 0.25f;

    [Header("Events")]
    public UnityEvent OnBroken;

    private bool _broken;
    private float _lastBreakTime = -999f;
    private Rigidbody _rb;
    private Collider[] _colliders;
    private Renderer[] _renderers;
    private PhotonView _pv;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _colliders = GetComponentsInChildren<Collider>(true);
        _renderers = GetComponentsInChildren<Renderer>(true);
        _pv = GetComponent<PhotonView>();

        if (Durability <= 0f)
            Durability = MaxDurability;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!BreakOnCollision) return;
        if (_broken && BreakOnce) return;
        if (Time.time - _lastBreakTime < BreakCooldown) return;

        // Layer filter
        int otherLayerBit = 1 << collision.gameObject.layer;
        if ((CollisionLayerMask.value & otherLayerBit) == 0) return;

        // Weapon-only filter
        if (OnlyDamageFromWeaponTag && !CollisionHasWeaponTag(collision))
            return;

        Vector3 hitPoint = collision.GetContact(0).point;
        Vector3 hitNormal = collision.GetContact(0).normal;

        Vector3 hitterVel = GetHitterVelocityWorld(collision);
        float speed = hitterVel.magnitude;

        if (speed < MinCollisionSpeed) return;

        if (CollisionInstantBreak)
        {
            Break(hitPoint, hitNormal);
        }
        else
        {
            float damage = speed * CollisionDamageMultiplier;
            ApplyDamageInternal(damage, hitPoint, hitNormal);
        }
    }

    // ---------- Public damage API (weapon-gated) ----------

    /// <summary>
    /// Apply damage, but ONLY if the source has the Weapon tag (when OnlyDamageFromWeaponTag is true).
    /// </summary>
    public void ApplyDamageFrom(GameObject source, float damage, Vector3 hitPoint, Vector3 hitNormal)
    {
        if (OnlyDamageFromWeaponTag && !ObjectHasTagInParents(source, WeaponTag))
            return;

        ApplyDamageInternal(damage, hitPoint, hitNormal);
    }

    /// <summary>
    /// Convenience overload (weapon-gated). If OnlyDamageFromWeaponTag is true, this will do nothing
    /// unless you use ApplyDamageFrom(source,...).
    /// </summary>
    public void ApplyDamage(float damage)
    {
        if (OnlyDamageFromWeaponTag) return; // enforce weapon-only rule
        ApplyDamageInternal(damage, transform.position, Vector3.up);
    }

    /// <summary>
    /// Convenience overload (weapon-gated). If OnlyDamageFromWeaponTag is true, this will do nothing
    /// unless you use ApplyDamageFrom(source,...).
    /// </summary>
    public void ApplyDamage(float damage, Vector3 hitPoint, Vector3 hitNormal)
    {
        if (OnlyDamageFromWeaponTag) return; // enforce weapon-only rule
        ApplyDamageInternal(damage, hitPoint, hitNormal);
    }

    private void ApplyDamageInternal(float damage, Vector3 hitPoint, Vector3 hitNormal)
    {
        if (_broken && BreakOnce) return;
        if (Time.time - _lastBreakTime < BreakCooldown) return;
        if (damage <= 0f) return;

        Durability -= damage;

        if (Durability <= 0f)
            Break(hitPoint, hitNormal);
    }

    // ---------- Velocity logic ----------

    private Vector3 GetHitterVelocityWorld(Collision collision)
    {
        if (PreferFrameBasedHitterVelocity)
        {
            if (collision.rigidbody != null)
            {
                var t = collision.rigidbody.GetComponent<FrameVelocityTracker>();
                if (t != null) return t.Velocity;
            }

            var tc = collision.collider != null ? collision.collider.GetComponentInParent<FrameVelocityTracker>() : null;
            if (tc != null) return tc.Velocity;

            var to = collision.gameObject.GetComponentInParent<FrameVelocityTracker>();
            if (to != null) return to.Velocity;
        }

        Vector3 thisVel = (_rb != null) ? _rb.velocity : Vector3.zero;

        if (collision.rigidbody != null)
        {
            Vector3 rbVel = collision.rigidbody.velocity;
            if (rbVel.sqrMagnitude > 0.0001f) return rbVel;
        }

        // relativeVelocity = otherVel - thisVel  => otherVel ~= relativeVelocity + thisVel
        return collision.relativeVelocity + thisVel;
    }

    // ---------- Weapon tag helpers ----------

    private bool CollisionHasWeaponTag(Collision c)
    {
        // Check rigidbody object + its parents
        if (c.rigidbody != null && ObjectHasTagInParents(c.rigidbody.gameObject, WeaponTag))
            return true;

        // Check collider object + its parents
        if (c.collider != null && ObjectHasTagInParents(c.collider.gameObject, WeaponTag))
            return true;

        // Check collision root object + its parents
        return ObjectHasTagInParents(c.gameObject, WeaponTag);
    }

    private bool ObjectHasTagInParents(GameObject go, string tag)
    {
        if (go == null || string.IsNullOrEmpty(tag)) return false;

        Transform t = go.transform;
        while (t != null)
        {
            // CompareTag is faster + safer than ==, but make sure the tag exists in Unity.
            if (t.CompareTag(tag)) return true;
            t = t.parent;
        }

        return false;
    }

    // ---------- Break ----------

    public void Break() => Break(transform.position, Vector3.up);

    public void Break(Vector3 hitPoint, Vector3 hitNormal)
    {
        if (_pv != null && _pv.IsMine)
        {
            _pv.RPC("RpcBreak", RpcTarget.All, hitPoint, hitNormal);
        }
        else
        {
            RpcBreak(hitPoint, hitNormal);
        }
    }

    [PunRPC]
    private void RpcBreak(Vector3 hitPoint, Vector3 hitNormal)
    {
        BreakInternal(hitPoint, hitNormal);
    }

    private void BreakInternal(Vector3 hitPoint, Vector3 hitNormal)
    {
        if (_broken && BreakOnce) return;
        if (Time.time - _lastBreakTime < BreakCooldown) return;

        _broken = true;
        _lastBreakTime = Time.time;

        if (BreakVfx != null)
        {
            BreakVfx.transform.position = hitPoint;
            BreakVfx.transform.forward = hitNormal;
            BreakVfx.Play(true);
        }

        if (AudioSource != null && BreakSfx != null)
            AudioSource.PlayOneShot(BreakSfx);

        GameObject brokenInstance = null;
        if (BrokenPrefab != null)
            brokenInstance = Instantiate(BrokenPrefab, transform.position, transform.rotation);

        if (DropPrefabs != null && DropPrefabs.Length > 0 && DropCount > 0)
        {
            for (int i = 0; i < DropCount; i++)
            {
                var prefab = DropPrefabs[Random.Range(0, DropPrefabs.Length)];
                if (prefab == null) continue;

                Vector3 offset = Random.insideUnitSphere * DropScatterRadius;
                offset.y = Mathf.Abs(offset.y) * 0.5f;
                Instantiate(prefab, hitPoint + offset, Random.rotation);
            }
        }

        if (brokenInstance != null)
        {
            Rigidbody[] debrisBodies = brokenInstance.GetComponentsInChildren<Rigidbody>(true);

            Vector3 inheritedVel = Vector3.zero;
            if (InheritVelocity && _rb != null)
                inheritedVel = _rb.velocity;

            foreach (var body in debrisBodies)
            {
                if (InheritVelocity)
                    body.velocity += inheritedVel;

                if (ApplyExplosionForce)
                    body.AddExplosionForce(ExplosionForce, hitPoint, ExplosionRadius, UpwardsModifier, ForceMode.Impulse);

                if (RandomTorque > 0f)
                    body.AddTorque(Random.onUnitSphere * RandomTorque, ForceMode.Impulse);
            }
        }

        OnBroken?.Invoke();

        if (DestroyOriginal && !DisableInsteadOfDestroy)
        {
            Destroy(gameObject);
            return;
        }

        if (DisableInsteadOfDestroy)
        {
            foreach (var r in _renderers) r.enabled = false;
            foreach (var c in _colliders) c.enabled = false;

            if (_rb != null)
            {
                _rb.velocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                _rb.isKinematic = true;
            }
        }
    }

    public void ResetBreakable()
    {
        if (!DisableInsteadOfDestroy) return;

        _broken = false;
        Durability = MaxDurability;

        foreach (var r in _renderers) r.enabled = true;
        foreach (var c in _colliders) c.enabled = true;

        if (_rb != null)
            _rb.isKinematic = false;
    }
}

/// <summary>
/// Attach this to any object you want accurate “moved this frame” velocity for
/// (especially kinematic or transform-driven movers).
/// </summary>
public class FrameVelocityTracker : MonoBehaviour
{
    public enum TickMode { FixedUpdate, Update, LateUpdate }

    [Tooltip("If the object is moved by physics (Rigidbody), use FixedUpdate. If moved in Update, choose Update.")]
    public TickMode Mode = TickMode.FixedUpdate;

    public Vector3 Velocity { get; private set; }

    private Vector3 _lastPos;
    private bool _inited;

    private void OnEnable()
    {
        _lastPos = transform.position;
        Velocity = Vector3.zero;
        _inited = true;
    }

    private void FixedUpdate()
    {
        if (Mode != TickMode.FixedUpdate) return;
        Tick(Time.fixedDeltaTime);
    }

    private void Update()
    {
        if (Mode != TickMode.Update) return;
        Tick(Time.deltaTime);
    }

    private void LateUpdate()
    {
        if (Mode != TickMode.LateUpdate) return;
        Tick(Time.deltaTime);
    }

    private void Tick(float dt)
    {
        if (!_inited)
        {
            _lastPos = transform.position;
            _inited = true;
            Velocity = Vector3.zero;
            return;
        }

        if (dt <= 0.000001f)
        {
            Velocity = Vector3.zero;
            return;
        }

        Vector3 pos = transform.position;
        Velocity = (pos - _lastPos) / dt;
        _lastPos = pos;
    }
}
