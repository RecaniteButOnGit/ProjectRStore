using UnityEngine;

/// <summary>
/// Explosive
/// Destroys this GameObject 1 second after it spawns.
/// Save as: Explosive.cs
/// </summary>
public class Explosive : MonoBehaviour
{
    public float DestroyDelay = 1f;

    private void Awake()
    {
        Destroy(gameObject, Mathf.Max(0f, DestroyDelay));
    }
}
