using UnityEngine;

public class RedRollingBall : MonoBehaviour
{
    public float speed = 10f;

    private Rigidbody rb;

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.AddForce(Vector3.forward * speed, ForceMode.Impulse);
    }
}
