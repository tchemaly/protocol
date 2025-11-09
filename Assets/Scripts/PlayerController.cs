using UnityEngine;

public class PlayerController : MonoBehaviour
{
    private Animator animator;

    void Start()
    {
        animator = GetComponent<Animator>();
    }

    public void LieDown()
    {
        if (animator != null)
        {
            animator.Play("LieDownAnimation");
        }
    }
}