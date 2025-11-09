using UnityEngine;

public class DuplicatePlayerOnStart : MonoBehaviour
{
    public GameObject playerToDuplicate;
    public Vector3 duplicateOffset = new Vector3(2, 0, 0);

    void Start()
    {
        if (playerToDuplicate != null)
        {
            Vector3 duplicatePosition = playerToDuplicate.transform.position + duplicateOffset;
            Instantiate(playerToDuplicate, duplicatePosition, playerToDuplicate.transform.rotation);
        }
    }
}