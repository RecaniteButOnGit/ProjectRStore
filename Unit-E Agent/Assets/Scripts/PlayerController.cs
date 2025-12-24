using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class PlayerController : MonoBehaviour
{
    [Header("Movement")]
    public float walkSpeed = 5f;
    public float sprintSpeed = 9f;
    public float acceleration = 10f; // how fast to reach target speed

    [Header("Jump & Gravity")]
    public float jumpHeight = 1.5f;
    public float gravity = -9.81f; // negative value
    public float groundedGravity = -2f; // small downward force when grounded to keep grounded

    [Header("Mouse Look")]
    public Transform playerCamera;
    public float mouseSensitivity = 2.0f;
    public float maxLookAngle = 85f;

    [Header("Options")]
    public bool lockCursor = true;

    CharacterController controller;

    float velocityY = 0f;
    float currentSpeed = 0f;

    float pitch = 0f; // camera X (up/down)
    float yaw = 0f;   // player Y (left/right)

    void Start()
    {
        controller = GetComponent<CharacterController>();

        if (playerCamera == null && Camera.main != null)
            playerCamera = Camera.main.transform;

        // initialize yaw/pitch from current rotation
        yaw = transform.eulerAngles.y;
        if (playerCamera != null)
            pitch = playerCamera.localEulerAngles.x;

        if (lockCursor)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void Update()
    {
        HandleMouseLook();
        HandleMovement();
    }

    void HandleMouseLook()
    {
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

        yaw += mouseX;
        pitch -= mouseY;
        pitch = Mathf.Clamp(pitch, -maxLookAngle, maxLookAngle);

        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        if (playerCamera != null)
            playerCamera.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    void HandleMovement()
    {
        // Input
        Vector2 input = new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical"));
        input = input.normalized; // prevent faster diagonal

        bool sprint = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        float targetSpeed = sprint ? sprintSpeed : walkSpeed;

        // Smooth speed change
        float horizontalSpeed = new Vector2(controller.velocity.x, controller.velocity.z).magnitude;
        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed * input.magnitude, acceleration * Time.deltaTime);

        // Movement relative to player orientation
        Vector3 move = (transform.right * input.x + transform.forward * input.y).normalized;
        Vector3 horizontalMove = move * currentSpeed;

        // Gravity & Jump
        if (controller.isGrounded)
        {
            if (velocityY < 0f)
                velocityY = groundedGravity; // small downward force so isGrounded stays consistent

            if (Input.GetButtonDown("Jump"))
            {
                // v = sqrt(2 * -gravity * height)
                velocityY = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
        }
        else
        {
            velocityY += gravity * Time.deltaTime;
        }

        Vector3 finalVelocity = horizontalMove + Vector3.up * velocityY;

        controller.Move(finalVelocity * Time.deltaTime);
    }
}
