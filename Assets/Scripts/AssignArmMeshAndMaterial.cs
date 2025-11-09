using UnityEngine;

public class AssignArmMeshAndMaterial : MonoBehaviour
{
    public Mesh armMesh;
    public Material armMaterial;
    
    void Start()
    {
        GameObject arm = GameObject.Find("Player/Arm");
        if (arm != null)
        {
            MeshFilter mf = arm.GetComponent<MeshFilter>();
            MeshRenderer mr = arm.GetComponent<MeshRenderer>();
            
            if (mf != null)
            {
                mf.mesh = armMesh;
            }
            
            if (mr != null)
            {
                mr.material = armMaterial;
            }
        }
    }
}