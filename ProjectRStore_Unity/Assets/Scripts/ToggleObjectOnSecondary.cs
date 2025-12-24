using UnityEngine;

public class ToggleObjectOnSecondary : MonoBehaviour
{
    [SerializeField] private GameObject target;
    [SerializeField] private bool forceInitialState = false;
    [SerializeField] private bool initialState = true;

    private void Start()
    {
        if (forceInitialState)
        {
            GetTarget().SetActive(initialState);
        }
    }

    // Your HandGrabber calls public void Secondary()
    public void Secondary()
    {
        var go = GetTarget();
        bool newState = !go.activeSelf;
        go.SetActive(newState);
        Debug.Log($"[ToggleObjectOnSecondary] Toggled '{go.name}' -> {(newState ? "ON" : "OFF")}", this);
    }

    private GameObject GetTarget()
    {
        return target != null ? target : gameObject;
    }
}
