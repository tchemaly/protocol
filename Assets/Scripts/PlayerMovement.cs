using UnityEngine;

public class PlayerMovement : MonoBehaviour
{
    public float moveSpeed = 5f;
    private Rigidbody playerRigidbody;

    private void Start()
    {
        playerRigidbody = GetComponent<Rigidbody>();
    }

    private void Update()
    {
        float moveHorizontal = Input.GetAxis("Horizontal");
        float moveVertical = Input.GetAxis("Vertical");

        Vector3 movement = new Vector3(moveHorizontal, 0.0f, moveVertical);
        playerRigidbody.linearVelocity = movement * moveSpeed;

        if (movement != Vector3.zero)
        {
            transform.forward = movement;
        }
    }
}
