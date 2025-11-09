using UnityEngine;

public class FlyingPlane : MonoBehaviour
{
    public float speed = 2.0f;
    public float resetHeight = 10.0f;
    private Vector3 startPosition;
    
    void Start()
    {
        startPosition = transform.position;
    }

    void Update()
    {
        // Move the plane upwards
        transform.Translate(Vector3.up * speed * Time.deltaTime);
        
        // Reset the position if it goes too high
        if (transform.position.y >= resetHeight)
        {
            transform.position = startPosition;
        }
    }
}
