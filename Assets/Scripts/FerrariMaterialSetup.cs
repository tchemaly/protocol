using UnityEngine;

public class FerrariMaterialSetup : MonoBehaviour
{
    public Material whiteFerrariMaterial;

    void Start()
    {
        Renderer[] renderers = GetComponentsInChildren<Renderer>();
        foreach (Renderer renderer in renderers)
        {
            renderer.material = whiteFerrariMaterial;
        }
    }
}
