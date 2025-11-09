using UnityEngine;

public class CreatePlane : MonoBehaviour
{
    public float width = 10f;
    public float height = 10f;

    void Start()
    {
        GameObject plane = new GameObject("GeneratedPlane");
        MeshFilter meshFilter = plane.AddComponent<MeshFilter>();
        MeshRenderer meshRenderer = plane.AddComponent<MeshRenderer>();
        meshRenderer.material = new Material(Shader.Find("Standard"));

        Mesh mesh = new Mesh();

        Vector3[] vertices = {
            new Vector3(-width / 2, 0, -height / 2),
            new Vector3(width / 2, 0, -height / 2),
            new Vector3(width / 2, 0, height / 2),
            new Vector3(-width / 2, 0, height / 2)
        };

        int[] triangles = {
            0, 2, 1,
            0, 3, 2
        };

        Vector3[] normals = {
            Vector3.up,
            Vector3.up,
            Vector3.up,
            Vector3.up
        };

        Vector2[] uv = {
            new Vector2(0, 0),
            new Vector2(1, 0),
            new Vector2(1, 1),
            new Vector2(0, 1)
        };

        mesh.vertices = vertices;
        mesh.triangles = triangles;
        mesh.normals = normals;
        mesh.uv = uv;

        meshFilter.mesh = mesh;
    }
}
