using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class GoblinBoardController : MonoBehaviour
{
    public float hoverForce = 10f;
    public float moveSpeed = 5f;
    public float turnSpeed = 50f;

    private Rigidbody rigidbody;

    private void Start()
    {
        rigidbody = GetComponent<Rigidbody>();
    }

    private void Update()
    {
        HandleMovement();
    }

    private void HandleMovement()
    {
        // Forward/backward movement
        float forwardMovement = Input.GetAxis("Vertical") * moveSpeed;
        Vector3 movement = transform.forward * forwardMovement * Time.deltaTime;
        rigidbody.MovePosition(rigidbody.position + movement);

        // Rotation
        float turn = Input.GetAxis("Horizontal") * turnSpeed * Time.deltaTime;
        Quaternion deltaRotation = Quaternion.Euler(0f, turn, 0f);
        rigidbody.MoveRotation(rigidbody.rotation * deltaRotation);

        // Hovering
        if (Input.GetKey(KeyCode.Space))
        {
            rigidbody.AddForce(Vector3.up * hoverForce, ForceMode.Acceleration);
        }
    }
}
