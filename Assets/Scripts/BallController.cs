using UnityEngine;

public class BallController : MonoBehaviour
{
    void Start()
    {
        GetComponent<Renderer>().material.color = Color.red;
    }
}
