using UnityEngine;

public class ShoulderRotation : MonoBehaviour
{
    public ArticulationBody shoulder;

    void Start()
    {
        RotateShoulderDown();
    }

    void RotateShoulderDown()
    {
        if (shoulder != null)
        {
            ArticulationDrive drive = shoulder.xDrive;
            drive.target = 90f; // Rotating 90 degrees downwards
            shoulder.xDrive = drive;
        }
        else
        {
            Debug.LogError("Shoulder ArticulationBody is not assigned.");
        }
    }
}