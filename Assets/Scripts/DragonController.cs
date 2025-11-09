using UnityEngine;

public class DragonController : MonoBehaviour
{
    public ParticleSystem fireBreath;
    public bool isBreathingFire;

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Space))
        {
            ToggleFireBreath();
        }
    }

    void ToggleFireBreath()
    {
        isBreathingFire = !isBreathingFire;
        if (isBreathingFire)
        {
            fireBreath.Play();
        }
        else
        {
            fireBreath.Stop();
        }
    }
}
