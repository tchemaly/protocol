// Assuming the model is at the root of the scene
using UnityEngine;

public class CarPositionScript : MonoBehaviour
{
    void Start()
    {
        transform.position = new Vector3(0.0f, 0.0f, 0.0f);
        transform.rotation = Quaternion.Euler(0.0f, 0.0f, 0.0f);
    }
}
