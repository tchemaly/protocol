using UnityEngine;

public class TurnManager : MonoBehaviour
{
    enum TurnState
    {
        PlayerTurn,
        EnemyTurn
    }
    
    private TurnState currentTurn = TurnState.PlayerTurn;

    void Update()
    {
        switch (currentTurn)
        {
            case TurnState.PlayerTurn:
                // Handle player input and actions here
                if (PlayerHasFinishedTurn())
                {
                    currentTurn = TurnState.EnemyTurn;
                    Debug.Log("Switching to Enemy Turn");
                }
                break;
                
            case TurnState.EnemyTurn:
                // Execute enemy actions automatically
                ExecuteEnemyActions();
                
                // When enemy actions are done, switch to player turn
                currentTurn = TurnState.PlayerTurn;
                Debug.Log("Switching to Player Turn");
                break;
        }
    }

    private bool PlayerHasFinishedTurn()
    {
        // Check if player has completed their turn (e.g., by ending turn button)
        return Input.GetKeyDown(KeyCode.Space); // Simple example using space key
    }

    private void ExecuteEnemyActions()
    {
        // Implement enemy AI behavior here
        Debug.Log("Enemy is taking their turn.");
    }
}
