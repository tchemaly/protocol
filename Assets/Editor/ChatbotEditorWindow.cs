using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;
using System;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Compilation;
using System.Reflection;
using UnityEditor.SceneManagement;
using System.Net.Http;
using Newtonsoft.Json;

// 1) A small class to help parse the JSON from the streaming chunks.
[Serializable]
public class OpenAIStreamChunk
{
    public string id;
    public string @object;
    public long created;
    public string model;
    public Choice[] choices;

    [Serializable]
    public class Choice
    {
        public Delta delta;
        public int index;
        public string finish_reason;
    }

    [Serializable]
    public class Delta
    {
        public string role;
        public string content;
    }
}

// Add this class before the ChatbotEditorWindow class
public class StreamingMarkdownContainer
{
    private VisualElement container;
    private VisualElement contentContainer;
    public string currentText { get; private set; } = "";
    private bool inCodeBlock = false;
    private string currentCodeBlock = "";
    private string currentCodeBlockLanguage = "";

    public VisualElement parent => container;

    public StreamingMarkdownContainer(VisualElement parent)
    {
        container = new VisualElement
        {
            style =
            {
                marginBottom = 8,
                paddingLeft = 4,
                paddingRight = 4
            }
        };

        contentContainer = new VisualElement
        {
            style =
            {
                marginLeft = 4,
                marginRight = 4
            }
        };

        container.Add(contentContainer);
        parent.Add(container);
    }

    public void AppendText(string newText)
    {
        currentText += newText;
        UpdateDisplay();
    }

    private void UpdateDisplay()
    {
        contentContainer.Clear();

        // Split the text into blocks
        var blocks = SplitIntoBlocks(currentText);
        
        foreach (var block in blocks)
        {
            if (IsCodeBlock(block, out string language, out string code))
            {
                var codeBlockElement = MarkdownRenderer.RenderCodeBlock(language, code);
                contentContainer.Add(codeBlockElement);
            }
            else
            {
                var formattedContent = MarkdownRenderer.RenderMarkdown(block);
                contentContainer.Add(formattedContent);
            }
        }
    }

    private List<string> SplitIntoBlocks(string text)
    {
        var blocks = new List<string>();
        var lines = text.Split('\n');
        string currentBlock = "";
        bool inCodeBlock = false;
        string currentCodeBlock = "";

        foreach (var line in lines)
        {
            if (line.StartsWith("```"))
            {
                if (!inCodeBlock)
                {
                    // Start of code block
                    if (!string.IsNullOrWhiteSpace(currentBlock))
                    {
                        blocks.Add(currentBlock.Trim());
                        currentBlock = "";
                    }
                    inCodeBlock = true;
                    currentCodeBlock = line + "\n";
                }
                else
                {
                    // End of code block
                    currentCodeBlock += line;
                    // Only add the code block if it has content (not just the markers)
                    if (!string.IsNullOrWhiteSpace(currentCodeBlock.Replace("```", "").Trim()))
                    {
                        blocks.Add(currentCodeBlock);
                    }
                    currentCodeBlock = "";
                    inCodeBlock = false;
                }
                continue;
            }

            if (inCodeBlock)
            {
                currentCodeBlock += line + "\n";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (!string.IsNullOrWhiteSpace(currentBlock))
                    {
                        blocks.Add(currentBlock.Trim());
                        currentBlock = "";
                    }
                }
                else
                {
                    currentBlock += line + "\n";
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(currentBlock))
        {
            blocks.Add(currentBlock.Trim());
        }

        // Only add the code block if it has content (not just the markers)
        if (!string.IsNullOrWhiteSpace(currentCodeBlock.Replace("```", "").Trim()))
        {
            blocks.Add(currentCodeBlock);
        }

        return blocks;
    }

    private bool IsCodeBlock(string block, out string language, out string code)
    {
        language = "";
        code = "";

        if (!block.StartsWith("```") || !block.EndsWith("```"))
            return false;

        var lines = block.Split('\n');
        if (lines.Length <= 2)
            return false;

        // Extract language from first line
        string firstLine = lines[0].Trim();
        if (firstLine.Length > 3)
        {
            language = firstLine.Substring(3).Trim();
        }

        // Extract code content
        code = string.Join("\n", lines.Skip(1).Take(lines.Length - 2));
        
        // Don't consider it a code block if it's empty or just whitespace
        if (string.IsNullOrWhiteSpace(code))
            return false;
            
        return true;
    }
}

[InitializeOnLoad]
public class ChatbotEditorWindow : EditorWindow
{
    // Replace the streamingMessageLabel field with:
    private StreamingMarkdownContainer streamingContainer;

    // Add logging related fields
    private const string LOGS_DIRECTORY = "Logs";
    private const string PROMPT_LOGS_DIRECTORY = "prompt_logs";
    private const string ACTION_LOGS_DIRECTORY = "action_logs";
    private string currentLogFilePath;
    private string currentActionLogFilePath;

    // Add tracking and monitoring fields
    private string installationId;
    private string userId;
    private bool isLoggingEnabled = true;
    private bool isRemoteLoggingEnabled = true;
    private string remoteLogEndpoint = "https://muefsrcijttbiuahjxwn.supabase.co/rest/v1/logs";
    private int logTransmissionInterval = 300; // 5 minutes in seconds
    private DateTime lastLogTransmission = DateTime.MinValue;
    private string currentUserPrompt = string.Empty;  // Add this field
    private Queue<PromptLogEntry> pendingLogs = new Queue<PromptLogEntry>();

    [Serializable]
    private class LoggingConfig
    {
        public string installationId;
        public string userId;
        public bool isLoggingEnabled;
        public bool isRemoteLoggingEnabled;
        public string remoteLogEndpoint;
        public int logTransmissionInterval;
    }

    private void InitializeLogging()
    {
        // Load or create configuration
        LoadLoggingConfig();

        // Create logs directory if it doesn't exist
        string logsPath = Path.Combine(Application.dataPath, "..", LOGS_DIRECTORY);
        if (!Directory.Exists(logsPath))
        {
            Directory.CreateDirectory(logsPath);
        }

        // Create prompt_logs subdirectory
        string promptLogsPath = Path.Combine(logsPath, PROMPT_LOGS_DIRECTORY);
        if (!Directory.Exists(promptLogsPath))
        {
            Directory.CreateDirectory(promptLogsPath);
        }

        // Create action_logs subdirectory
        string actionLogsPath = Path.Combine(logsPath, ACTION_LOGS_DIRECTORY);
        if (!Directory.Exists(actionLogsPath))
        {
            Directory.CreateDirectory(actionLogsPath);
        }

        // Create a new log file with current date
        string dateStr = DateTime.Now.ToString("yyyy-MM-dd");
        currentLogFilePath = Path.Combine(promptLogsPath, $"prompts_{dateStr}.json");
        currentActionLogFilePath = Path.Combine(actionLogsPath, $"actions_{dateStr}.json");

        // Start the log transmission timer
        EditorApplication.update += OnEditorUpdate;
    }

    private void LoadLoggingConfig()
    {
        string configPath = Path.Combine(Application.dataPath, "..", LOGS_DIRECTORY, "logging_config.json");
        if (File.Exists(configPath))
        {
            try
            {
                string json = File.ReadAllText(configPath);
                var config = JsonUtility.FromJson<LoggingConfig>(json);
                installationId = config.installationId;
                userId = config.userId;
                isLoggingEnabled = config.isLoggingEnabled;
                isRemoteLoggingEnabled = config.isRemoteLoggingEnabled;
                remoteLogEndpoint = config.remoteLogEndpoint;
                logTransmissionInterval = config.logTransmissionInterval;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Logging System] Error loading config: {ex.Message}");
                CreateDefaultConfig();
            }
        }
        else
        {
            CreateDefaultConfig();
        }
    }

    private void CreateDefaultConfig()
    {
        // Generate a new installation ID if none exists
        if (string.IsNullOrEmpty(installationId))
        {
            installationId = Guid.NewGuid().ToString();
        }

        // Create default config
        var config = new LoggingConfig
        {
            installationId = installationId,
            userId = userId ?? "anonymous",
            isLoggingEnabled = true,
            isRemoteLoggingEnabled = true,
            remoteLogEndpoint = "https://muefsrcijttbiuahjxwn.supabase.co/rest/v1/logs",
            logTransmissionInterval = 300
        };

        // Save config
        string configPath = Path.Combine(Application.dataPath, "..", LOGS_DIRECTORY, "logging_config.json");
        string json = JsonUtility.ToJson(config, true);
        File.WriteAllText(configPath, json);
    }

    private void OnEditorUpdate()
    {
        if (!isRemoteLoggingEnabled || string.IsNullOrEmpty(remoteLogEndpoint))
            return;

        // Check if it's time to transmit logs
        if ((DateTime.Now - lastLogTransmission).TotalSeconds >= logTransmissionInterval)
        {
            TransmitPendingLogs();
        }
    }

    private async void TransmitPendingLogs()
    {
        if (pendingLogs.Count == 0)
            return;

        try
        {
            var logsToTransmit = new List<PromptLogEntry>();
            while (pendingLogs.Count > 0)
            {
                logsToTransmit.Add(pendingLogs.Dequeue());
            }

            // Create the request
            var request = new UnityWebRequest(remoteLogEndpoint, "POST");
            var logFile = new PromptLogFile { entries = logsToTransmit };
            var json = JsonConvert.SerializeObject(logFile); // Use JsonConvert instead of JsonUtility
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            // Send the request
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                await Task.Yield();
            }

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"[Logging System] Successfully transmitted {logsToTransmit.Count} logs");
                lastLogTransmission = DateTime.Now;
            }
            else
            {
                Debug.LogError($"[Logging System] Failed to transmit logs: {request.error}");
                // Put the logs back in the queue
                foreach (var log in logsToTransmit)
                {
                    pendingLogs.Enqueue(log);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Logging System] Error transmitting logs: {ex.Message}");
        }
    }

    private async Task LogAction(string actionType, string details = "")
    {
        if (!isLoggingEnabled)
            return;

        try
        {
            // Create log entry
            var logEntry = new PromptLogEntry
            {
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                prompt = "",
                sessionId = currentSessionIndex,
                sessionName = currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count 
                    ? chatSessions[currentSessionIndex].Name 
                    : "Unknown",
                actionType = actionType,
                details = details,
                installationId = installationId,
                userId = userId,
                platform = Application.platform.ToString(),
                unityVersion = Application.unityVersion,
                pluginVersion = "1.0.0" // Update this with your actual version
            };

            // Add to pending logs for remote transmission
            if (isRemoteLoggingEnabled)
            {
                await SupabaseAnalytics.SendLog(logEntry);
            }

            // Write to local file
            PromptLogFile logFile = new PromptLogFile();
            if (File.Exists(currentActionLogFilePath))
            {
                string existingContent = File.ReadAllText(currentActionLogFilePath);
                if (!string.IsNullOrEmpty(existingContent))
                {
                    logFile = JsonConvert.DeserializeObject<PromptLogFile>(existingContent);
                }
            }

            logFile.entries.Add(logEntry);
            string jsonContent = JsonConvert.SerializeObject(logFile, Formatting.Indented);
            File.WriteAllText(currentActionLogFilePath, jsonContent);

            Debug.Log($"[Action Log] Successfully logged {actionType} action to {currentActionLogFilePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Action Log] Error logging {actionType} action: {ex.Message}\nStack trace: {ex.StackTrace}");
        }
    }

    private async Task LogUserPrompt(string prompt, string aiResponse = "")
    {
        if (!isLoggingEnabled)
            return;

        try
        {
            // Create log entry with both prompt and response
            var logEntry = new PromptLogEntry
            {
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                prompt = prompt,
                response = aiResponse,
                sessionId = currentSessionIndex,
                sessionName = currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count 
                    ? chatSessions[currentSessionIndex].Name 
                    : "Unknown",
                actionType = "prompt",
                details = $"Prompt length: {prompt.Length}, Response length: {aiResponse.Length}",
                installationId = installationId,
                userId = userId,
                platform = Application.platform.ToString(),
                unityVersion = Application.unityVersion,
                pluginVersion = "1.0.0"
            };

            // Send to Supabase
            if (isRemoteLoggingEnabled)
            {
                await SupabaseAnalytics.SendLog(logEntry);
            }

            // Write to local file
            PromptLogFile logFile = new PromptLogFile();
            if (File.Exists(currentLogFilePath))
            {
                string existingContent = File.ReadAllText(currentLogFilePath);
                if (!string.IsNullOrEmpty(existingContent))
                {
                    logFile = JsonConvert.DeserializeObject<PromptLogFile>(existingContent);
                }
            }

            logFile.entries.Add(logEntry);
            string jsonContent = JsonConvert.SerializeObject(logFile, Formatting.Indented);
            File.WriteAllText(currentLogFilePath, jsonContent);

            Debug.Log($"[Prompt-Response Log] Successfully logged prompt and AI response to {currentLogFilePath}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Prompt-Response Log] Error logging prompt and response: {ex.Message}\nStack trace: {ex.StackTrace}");
        }
    }

    private void AddStreamingPlaceholderMessage()
    {
        // Create a container for the streaming message
        var messageContainer = new VisualElement
        {
            style =
            {
                marginBottom = 8,
                paddingLeft = 4,
                paddingRight = 4
            }
        };

        // Add the sender label
        var senderLabel = new Label("XeleR:")
        {
            style =
            {
                unityFontStyleAndWeight = FontStyle.Bold,
                marginBottom = 2
            }
        };
        messageContainer.Add(senderLabel);

        // Create and store the streaming container
        streamingContainer = new StreamingMarkdownContainer(messageContainer);

        // Add the container to the conversation view
        conversationScrollView.Add(messageContainer);
    }

    private void UpdateStreamingMessage(string newText)
    {
        if (streamingContainer != null)
        {
            streamingContainer.AppendText(newText);
            // Optionally scroll to bottom
            EditorApplication.delayCall += ScrollToBottom;
        }
        else
        {
            Debug.LogWarning("Streaming container is null. Unable to update streaming text.");
        }
    }


    // Static constructor that will be called when Unity starts or scripts are recompiled
    static ChatbotEditorWindow()
    {
        // Instead of doing work here, register for the first editor update
        EditorApplication.delayCall += OnFirstEditorUpdate;
    }
    
    // This method will be called once after Unity is fully initialized
    private static void OnFirstEditorUpdate()
    {
        // Clear any cached state about open windows
        EditorPrefs.DeleteKey("ChatbotEditorWindowOpen");
        
        // Use a delayed call to ensure Unity is fully initialized
        EditorApplication.delayCall += ForceOpenWindow;
        
        // Also subscribe to the projectOpened event to ensure it opens on project launch
        EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;
    }
    
    // This is a one-time check that runs when the project window is first drawn
    private static bool hasOpenedOnLaunch = false;
    private static void OnProjectWindowItemGUI(string guid, Rect selectionRect)
    {
        if (!hasOpenedOnLaunch)
        {
            hasOpenedOnLaunch = true;
            // Unsubscribe to prevent this from running again
            EditorApplication.projectWindowItemOnGUI -= OnProjectWindowItemGUI;
            // Open the window with a slight delay to ensure Unity is fully initialized
            EditorApplication.delayCall += ForceOpenWindow;
        }
    }
    
    // Force the window to open, regardless of any cached state
    private static void ForceOpenWindow()
    {
        // Close any existing instances first
        var existingWindows = Resources.FindObjectsOfTypeAll<ChatbotEditorWindow>();
        foreach (var window in existingWindows)
        {
            window.Close();
        }

        // Create a fresh window instance, docked next to the Inspector if possible
        var editorAssembly = typeof(UnityEditor.Editor).Assembly;
        var inspectorType = editorAssembly.GetType("UnityEditor.InspectorWindow");

        ChatbotEditorWindow newWindow;
        if (inspectorType == null)
        {
            // If Inspector isn't found at all, just open normally
            newWindow = GetWindow<ChatbotEditorWindow>("Chat x0", true);
        }
        else
        {
            // Attempt to dock next to the Inspector
            newWindow = GetWindow<ChatbotEditorWindow>("Chat x0", true, inspectorType);
        }

        newWindow.Show();
        newWindow.Focus();

        Debug.LogWarning("ChatbotEditorWindow: Forcing window to open");
        EditorPrefs.SetBool("ChatbotEditorWindowOpen", true);
    }
    
    // Add serialization for conversation history
    [SerializeField] private List<ChatMessage> conversationHistory = new List<ChatMessage>();
    
    // Add a list to store multiple chat sessions
    [SerializeField] private List<ChatSession> chatSessions = new List<ChatSession>();
    [SerializeField] private int currentSessionIndex = 0;
    
    // Add a key for EditorPrefs to store serialized chat sessions
    private const string CHAT_SESSIONS_KEY = "ChatbotEditorWindow_ChatSessions";
    private const string CURRENT_SESSION_INDEX_KEY = "ChatbotEditorWindow_CurrentSessionIndex";
    
    // Serializable class to store chat messages
    [Serializable]
    private class ChatMessage
    {
        public string Sender;
        public string Content;
        public bool IsFileContent;
        public string FileName;
    }
    
    // Serializable class to store a chat session
    [Serializable]
    private class ChatSession
    {
        public string Name;
        public List<ChatMessage> Messages = new List<ChatMessage>();
        public string LastLoadedScriptPath;
        public string LastLoadedScriptContent;
        public string LastLoadedScenePath;
        public bool IsSceneLoaded;
        public DateTime CreatedAt;
        public bool NeedsAutoNaming = false;
        
        public ChatSession(string name)
        {
            Name = name;
            CreatedAt = DateTime.Now;
            NeedsAutoNaming = name == "New Chat";
        }
    }

    // Combined model selections
    private class ModelInfo
    {
        public string Name { get; set; }
        public string Provider { get; set; }

        public override string ToString()
        {
            return Name;
        }
    }

    private List<ModelInfo> availableModels = new List<ModelInfo>
    {
        new ModelInfo { Name = "gpt-4o", Provider = "OpenAI" },
        new ModelInfo { Name = "gpt-4", Provider = "OpenAI" },
        new ModelInfo { Name = "gpt-4-turbo", Provider = "OpenAI" },
        new ModelInfo { Name = "claude-3-7-sonnet", Provider = "Claude" },
        new ModelInfo { Name = "claude-3-5-sonnet", Provider = "Claude" },

    };
    
    // Store selected model index for persistence
    [SerializeField] private int selectedModelIndex = 0;

    private const string PLACEHOLDER_TEXT = "Type your message...";
    private const string SCRIPTS_FOLDER = "Assets/Scripts";
    private const string SCENES_FOLDER = "Assets/Scenes";

    private ScrollView conversationScrollView;
    private TextField queryField;
    private Button sendButton;
    private Button browseScriptsButton;
    private Button browseScenesButton;
    private PopupField<ModelInfo> modelSelector;
    private PopupField<string> sessionSelector;
    private Button newChatButton;

    // Add these fields to store the last loaded script and scene information
    [SerializeField] private string lastLoadedScriptPath;
    [SerializeField] private string lastLoadedScriptContent;
    [SerializeField] private string lastLoadedScenePath;
    [SerializeField] private bool isSceneLoaded = false;

    private Button analyzeSceneButton;
    private Button spatialAnalysisButton;
    private Toggle includeSceneContextToggle;
    private bool includeSceneContext = true;  // Changed from false to true

    // Add this as a class member
    private Button contextMenuButton;
    private VisualElement contextMenuDropdown;
    private bool isContextMenuOpen = false;

    private string[] selectedFiles = new string[0];
    private VisualElement pickedContextItemsContainer;

    // Add a reference to the sessionContainer
    private VisualElement sessionContainer;

    // Class to store file snapshots
    [Serializable]
    private class FileSnapshot
    {
        public string FilePath;
        public string Contents;
        public DateTime Timestamp;
        public bool IsNewFile;  // Add this flag to track new files
    }
    
    // Change the FileSnapshot list to be serialized by Unity
    [SerializeField] private List<FileSnapshot> fileSnapshots = new List<FileSnapshot>();

    // Add persistence keys and wrapper for fileSnapshots
    private const string FILE_SNAPSHOTS_KEY = "ChatbotEditorWindow_FileSnapshots";

    [Serializable]
    private class FileSnapshotsWrapper
    {
        public List<FileSnapshot> Snapshots;
    }

    // Persist fileSnapshots to EditorPrefs
    private void SaveFileSnapshotsToEditorPrefs()
    {
        try
        {
            string json = JsonUtility.ToJson(new FileSnapshotsWrapper { Snapshots = fileSnapshots });
            EditorPrefs.SetString(FILE_SNAPSHOTS_KEY, json);
            Debug.Log($"[Undo System] Saved {fileSnapshots.Count} file snapshots to EditorPrefs");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Undo System] Error saving file snapshots: {ex.Message}");
        }
    }

    // Load fileSnapshots from EditorPrefs
    private void LoadFileSnapshotsFromEditorPrefs()
    {
        try
        {
            if (EditorPrefs.HasKey(FILE_SNAPSHOTS_KEY))
            {
                string json = EditorPrefs.GetString(FILE_SNAPSHOTS_KEY);
                var wrapper = JsonUtility.FromJson<FileSnapshotsWrapper>(json);
                if (wrapper != null && wrapper.Snapshots != null)
                {
                    fileSnapshots = wrapper.Snapshots;
                    Debug.Log($"[Undo System] Loaded {fileSnapshots.Count} file snapshots from EditorPrefs");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Undo System] Error loading file snapshots: {ex.Message}");
        }
    }

    [MenuItem("Window/Chatbox %i")]
    public static void ShowWindow()
    {
        // This overload attempts to dock it next to the Inspector
        var editorAssembly = typeof(UnityEditor.Editor).Assembly;
        var inspectorType = editorAssembly.GetType("UnityEditor.InspectorWindow");

        if (inspectorType == null)
        {
            // If for some reason we can't find InspectorWindow, just open normally
            Debug.LogWarning("InspectorWindow type not found; opening Chatbot without docking.");
            var wndFallback = GetWindow<ChatbotEditorWindow>("XeleR", true);
            wndFallback.Show();
        }
        else
        {
            // Dock next to Inspector using the reflected type
            var wnd = GetWindow<ChatbotEditorWindow>(
                "XeleR",
                true,
                inspectorType
            );
            wnd.Show();
        }
    }

    public void CreateGUI()
    {
        // Clear existing elements
        rootVisualElement.Clear();

        // Load and apply the stylesheet
        var styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>("Assets/Editor/ChatbotEditorWindow.uss");
        if (styleSheet != null)
        {
            rootVisualElement.styleSheets.Add(styleSheet);
            Debug.Log("ChatbotEditorWindow stylesheet loaded.");
        }
        else
        {
            Debug.LogWarning("ChatbotEditorWindow.uss not found. Make sure it's in Assets/Editor.");
        }
        
        // Set up the root visual element with flex layout
        rootVisualElement.style.flexDirection = FlexDirection.Column;
        rootVisualElement.style.flexGrow = 1;
        rootVisualElement.style.minHeight = 100; // Ensure minimum height
        rootVisualElement.style.overflow = Overflow.Hidden; // Prevent overflow

        // Load chat sessions from EditorPrefs
        LoadChatSessionsFromEditorPrefs();

        // Initialize chat sessions if empty
        if (chatSessions.Count == 0)
        {
            // Migrate existing conversation to a session if needed
            if (conversationHistory.Count > 0)
            {
                var initialSession = new ChatSession("New Chat");
                initialSession.Messages = new List<ChatMessage>(conversationHistory);
                initialSession.LastLoadedScriptPath = lastLoadedScriptPath;
                initialSession.LastLoadedScriptContent = lastLoadedScriptContent;
                initialSession.LastLoadedScenePath = lastLoadedScenePath;
                initialSession.IsSceneLoaded = isSceneLoaded;
                chatSessions.Add(initialSession);
            }
            else
            {
                chatSessions.Add(new ChatSession("New Chat"));
            }
            currentSessionIndex = 0;
            
            // Save the initial state
            SaveChatSessionsToEditorPrefs();
        }

        // Create a top bar for chat management and other controls (Session Name, History, New, Delete)
        var topBar = new VisualElement // This remains outside the main input area structure
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                justifyContent = Justify.SpaceBetween,
                alignItems = Align.Center,
                marginBottom = 8,
                marginLeft = 4,
                marginRight = 4,
                marginTop = 4,
                height = 24, // Fixed height for top bar
                position = Position.Relative, 
                top = 0,
                left = 0,
                right = 0,
                flexShrink = 0 
            }
        };
        // ... (Code for adding chatNameLabel, sessionContainer, etc. to topBar remains the same) ...
        // Create left side of top bar (could be used for title or other controls)
        var leftSideContainer = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                alignItems = Align.Center
            }
        };
        
        // Chat name label - will show the current chat's name
        var chatNameLabel = new Label(chatSessions[currentSessionIndex].Name)
        {
            style =
            {
                unityFontStyleAndWeight = FontStyle.Bold,
                fontSize = 14,
                paddingLeft = 4,
                paddingRight = 4,
                paddingTop = 2,
                paddingBottom = 2,
                backgroundColor = new Color(0.25f, 0.25f, 0.25f, 0.5f), // Add slight background to indicate it's clickable
                borderTopLeftRadius = 3,
                borderTopRightRadius = 3,
                borderBottomLeftRadius = 3,
                borderBottomRightRadius = 3
            },
            name = "chatNameLabel" // Add a name to make it easy to find later
        };
        
        // Make label appear clickable with hover effect
        chatNameLabel.RegisterCallback<MouseOverEvent>(evt => {
            chatNameLabel.style.backgroundColor = new Color(0.3f, 0.3f, 0.3f, 0.7f);
        });
        
        chatNameLabel.RegisterCallback<MouseOutEvent>(evt => {
            chatNameLabel.style.backgroundColor = new Color(0.25f, 0.25f, 0.25f, 0.5f);
        });
        
        // Make chat name editable on click
        chatNameLabel.RegisterCallback<ClickEvent>(evt => {
            // Create a text field to replace the label
            CreateChatNameEditField(chatNameLabel, leftSideContainer);
            
            // Stop propagation to prevent other handlers from interfering
            evt.StopPropagation();
        });
        
        leftSideContainer.Add(chatNameLabel);
        topBar.Add(leftSideContainer);
        
        // Create right side of top bar for chat management
        var rightSideContainer = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                alignItems = Align.Center,
                flexShrink = 0, // Prevent container from shrinking
                justifyContent = Justify.FlexEnd // Align contents to the right
            }
        };
        
        // Add session container to right side container
        sessionContainer = new VisualElement
        {
            style =
            {
                flexDirection = FlexDirection.Row,
                flexWrap = Wrap.NoWrap, // Prevent wrapping
                alignItems = Align.Center,
                minHeight = 22,
                flexShrink = 0 // Prevent container from shrinking
            }
        };

        // Create chat history button with icon
        var chatHistoryButton = new Button(OnChatHistoryButtonClicked) { text = "☰" };
        chatHistoryButton.style.width = 24;
        chatHistoryButton.style.height = 22;
        chatHistoryButton.style.fontSize = 14;
        chatHistoryButton.style.marginRight = 2; // Add small margin
        chatHistoryButton.tooltip = "Chat History";
        sessionContainer.Add(chatHistoryButton);

        // New chat button
        newChatButton = new Button(OnNewChatClicked) { text = "+" };
        newChatButton.style.width = 24;
        newChatButton.style.height = 22;
        newChatButton.style.fontSize = 14;
        newChatButton.style.marginRight = 2; // Add small margin
        newChatButton.tooltip = "New Chat";
        sessionContainer.Add(newChatButton);

        // Undo button
        var undoButton = new Button(OnUndoClicked) { text = "↺" };
        undoButton.style.width = 24;
        undoButton.style.height = 22;
        undoButton.style.fontSize = 14;
        undoButton.style.marginRight = 2; // Add small margin
        undoButton.tooltip = "Undo";
        sessionContainer.Add(undoButton);

        // Delete chat button
        var deleteChatButton = new Button(() =>
        {
            if (EditorUtility.DisplayDialog("Delete Chat", 
                "Are you sure you want to delete this chat session?", "Yes", "No"))
            {
                DeleteCurrentSession();
            }
        }) { text = "🗑" };

        deleteChatButton.style.width = 24;
        deleteChatButton.style.height = 22;
        deleteChatButton.style.fontSize = 14;
        deleteChatButton.tooltip = "Delete Chat";
        sessionContainer.Add(deleteChatButton);
        
        rightSideContainer.Add(sessionContainer);
        topBar.Add(rightSideContainer);

        rootVisualElement.Add(topBar); // Add the top bar first

        // Create main content container that will contain the scrollable area
        var mainContentContainer = new VisualElement
        {
            style =
            {
                flexGrow = 1,
                overflow = Overflow.Hidden, // Hide overflow
                flexDirection = FlexDirection.Column,
                position = Position.Relative
            }
        };
        rootVisualElement.Add(mainContentContainer);

        // Scrollview for conversation (sits inside the main content container)
        conversationScrollView = new ScrollView(ScrollViewMode.Vertical) { name = "conversationScrollView" };
        conversationScrollView.style.flexGrow = 1;
        conversationScrollView.style.flexShrink = 1; // Allow shrinking
        conversationScrollView.verticalScrollerVisibility = ScrollerVisibility.Auto;
        mainContentContainer.Add(conversationScrollView);

        // Create the context menu dropdown (initially hidden) - Positioned relative to root
        contextMenuDropdown = new VisualElement();
        contextMenuDropdown.style.position = Position.Absolute;
        contextMenuDropdown.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.95f);
        contextMenuDropdown.style.borderTopWidth = 1;
        contextMenuDropdown.style.borderBottomWidth = 1;
        contextMenuDropdown.style.borderLeftWidth = 1;
        contextMenuDropdown.style.borderRightWidth = 1;
        contextMenuDropdown.style.borderTopColor = new Color(0.3f, 0.3f, 0.3f);
        contextMenuDropdown.style.borderBottomColor = new Color(0.3f, 0.3f, 0.3f);
        contextMenuDropdown.style.borderLeftColor = new Color(0.3f, 0.3f, 0.3f);
        contextMenuDropdown.style.borderRightColor = new Color(0.3f, 0.3f, 0.3f);
        contextMenuDropdown.style.paddingTop = 5;
        contextMenuDropdown.style.paddingBottom = 5;
        contextMenuDropdown.style.display = DisplayStyle.None; // Hidden by default
        contextMenuDropdown.pickingMode = PickingMode.Position;
        rootVisualElement.Add(contextMenuDropdown);

        // --- Container 4: Main Input Area Container ---
        var mainInputAreaContainer = new VisualElement
        {
            name = "mainInputAreaContainer",
            style =
            {
                flexDirection = FlexDirection.Column, // Stacks the 3 inner containers vertically
                flexShrink = 0, // Prevent shrinking
                marginLeft = 4,
                marginRight = 4,
                marginBottom = 4
            }
        };

        // --- Container 1: Top Button Toolbar ---
        var topToolbarContainer = new VisualElement
        {
            name = "topToolbarContainer",
            style =
            {
                flexDirection = FlexDirection.Row,
                flexWrap = Wrap.Wrap,
                height = 22,
                marginBottom = 4,
                flexShrink = 0
            }
        };

        // Add buttons to the Top Toolbar
        // @ Context Button
        contextMenuButton = new Button(OnContextMenuButtonClicked) {
            text = "@ Add context", // Changed text to match image more closely
            name = "contextMenuButton"
        };
        topToolbarContainer.Add(contextMenuButton);

        // Quick Context Toggle Container
        var contextToggleContainer = new VisualElement
        {
             name = "quickContextContainer",
        };

        includeSceneContextToggle = new Toggle {
            value = true,  // Changed from includeSceneContext to true
            name = "includeSceneContextToggle"
        };

        includeSceneContextToggle.RegisterValueChangedCallback(evt => {
            includeSceneContext = evt.newValue;
            AddMessageToHistory("System", includeSceneContext ? "Scene context enabled." : "Scene context disabled.");
        });
        contextToggleContainer.Add(includeSceneContextToggle);

        var contextLabel = new Label("Quick Context") { // Keeping the label text distinct for now
            name = "quickContextLabel"
        };
        contextToggleContainer.Add(contextLabel);
        topToolbarContainer.Add(contextToggleContainer);

        // ADDED: Container for picked context items (selected files)
        pickedContextItemsContainer = new VisualElement
        {
            name = "pickedContextItemsContainer",
            style =
            {
                flexDirection = FlexDirection.Row, // Arrange file boxes horizontally
                flexWrap = Wrap.Wrap,             // Allow them to wrap to the next line
                alignItems = Align.Center,        // Center file boxes vertically if they have different heights
                marginLeft = 4                    // Add some space to its left
            }
        };
        topToolbarContainer.Add(pickedContextItemsContainer); // Add it to the toolbar

        // Model Selection Dropdown Container - Declared here, added later
        var modelContainer = new VisualElement
        {
            name = "modelContainer", // Give it a name for USS styling
            style =
            {
                flexDirection = FlexDirection.Row,
                alignItems = Align.Center, // Center items vertically in this sub-container
                height = 22 // Match button heights roughly
            }
        };
        modelSelector = new PopupField<ModelInfo>(availableModels, selectedModelIndex);
        modelSelector.style.height = 22; // Match height
        modelSelector.RegisterValueChangedCallback(OnModelChanged);
        modelContainer.Add(modelSelector);

        mainInputAreaContainer.Add(topToolbarContainer); // Add top toolbar to the main input area

        // --- Container 2: Text Input Container ---
        queryField = new TextField
        {
            name = "queryField", 
            multiline = true,
            style =
            {
                flexGrow = 1,
                marginBottom = 4
            }
        };
        queryField.SetValueWithoutNotify(PLACEHOLDER_TEXT);
        queryField.AddToClassList("placeholder-text");
        queryField.focusable = true;
        queryField.pickingMode = PickingMode.Position;
        queryField.RegisterCallback<KeyDownEvent>(OnQueryFieldKeyDown, TrickleDown.TrickleDown);
        queryField.RegisterCallback<FocusInEvent>(OnFocusInQueryField);
        queryField.RegisterCallback<FocusOutEvent>(OnFocusOutQueryField);
        
        mainInputAreaContainer.Add(queryField); // Add query field below top toolbar

        // --- Container 3: Bottom Button Container ---
        var bottomButtonContainer = new VisualElement
        {
            name = "bottomButtonContainer",
            style =
            {
                flexDirection = FlexDirection.Row,
                justifyContent = Justify.FlexStart, 
                alignItems = Align.Center, 
                height = 24 
            }
        };

        var settingsButton = new Button(ShowApiKeySettings) {
            text = "API Keys",
            name = "apiKeysButton"
        };
        bottomButtonContainer.Add(settingsButton); // Added first (left)

        // Add Model Container here (middle)
        bottomButtonContainer.Add(modelContainer);

        // Send button
        sendButton = new Button(async () => await OnSendButtonClicked()) { text = "Send ↩", name = "sendButton" };
        bottomButtonContainer.Add(sendButton); // Added last (right)

        mainInputAreaContainer.Add(bottomButtonContainer); // Add bottom buttons below query field

        // Add the main input area container to the root
        rootVisualElement.Add(mainInputAreaContainer);
        
        // Load API keys on startup
        string openAiKey = ApiKeyManager.GetKey(ApiKeyManager.OPENAI_KEY);
        string claudeKey = ApiKeyManager.GetKey(ApiKeyManager.CLAUDE_KEY);
        
        // If keys are missing, prompt user to enter them
        if (string.IsNullOrEmpty(openAiKey) && string.IsNullOrEmpty(claudeKey))
        {
            EditorApplication.delayCall += () => 
            {
                var settingsWindow = CreateInstance<ApiSettingsWindow>();
                settingsWindow.Initialize(this, "", "");
                settingsWindow.position = new Rect(Screen.width / 2, Screen.height / 2, 400, 200);
                settingsWindow.ShowModal();
                AddMessageToHistory("System", "Please set up your API keys to continue.");
            };
        }

        // Restore conversation history from the current session
        RestoreCurrentSession();
        
        // Ensure we scroll to the bottom after restoring history
        EditorApplication.delayCall += ScrollToBottom;

        // After adding all the buttons to sessionContainer, update the selected files display
        UpdateSelectedFilesDisplay();
    }

    private void OnModelChanged(ChangeEvent<ModelInfo> evt)
    {
        AddMessageToHistory("System", $"Model changed to {evt.newValue.Name} ({evt.newValue.Provider})");
    }

    private void ShowApiKeySettings()
    {
        // Create a simple popup window for API key settings
        var settingsWindow = CreateInstance<ApiSettingsWindow>();
        settingsWindow.Initialize(this, 
            ApiKeyManager.GetKey(ApiKeyManager.OPENAI_KEY), 
            ApiKeyManager.GetKey(ApiKeyManager.CLAUDE_KEY));
        
        // Center it on screen
        Vector2 mousePosition = GUIUtility.GUIToScreenPoint(Event.current.mousePosition);
        settingsWindow.position = new Rect(mousePosition.x, mousePosition.y, 400, 200);
        
        // Show as a normal window instead of popup for draggability
        settingsWindow.Show();
    }

    public void SetApiKeys(string newOpenAiKey, string newClaudeKey)
    {
        // No longer directly storing keys in class fields
        // Instead, update manager and perform any needed UI updates
        if (!string.IsNullOrEmpty(newOpenAiKey))
        {
            ApiKeyManager.SetKey(ApiKeyManager.OPENAI_KEY, newOpenAiKey);
        }
        
        if (!string.IsNullOrEmpty(newClaudeKey))
        {
            ApiKeyManager.SetKey(ApiKeyManager.CLAUDE_KEY, newClaudeKey);
        }
    }

    private void OnBrowseScriptsClicked()
    {
        // Create a dropdown menu with script files
        var menu = new GenericMenu();
        
        // Get all C# script files in the Scripts folder
        string[] scriptFiles = Directory.GetFiles(SCRIPTS_FOLDER, "*.cs", SearchOption.AllDirectories);
        
        foreach (string filePath in scriptFiles)
        {
            string relativePath = filePath.Replace("\\", "/"); // Normalize path for Unity
            menu.AddItem(new GUIContent(relativePath), false, () => LoadScriptFile(relativePath));
        }
        
        menu.ShowAsContext();
    }

    private void LoadScriptFile(string filePath)
    {
        try
        {
            string fileContent = File.ReadAllText(filePath);
            string fileName = Path.GetFileName(filePath);
            
            // Add the file path to the selected files array if not already in the array
            if (Array.IndexOf(selectedFiles, filePath) < 0) {
                Array.Resize(ref selectedFiles, selectedFiles.Length + 1);
                selectedFiles[selectedFiles.Length - 1] = filePath;
            }
            
            // Update the display of selected files
            UpdateSelectedFilesDisplay();
            
            // Add the file content to the conversation
            AddMessageToHistory("You", $"Show me the contents of {fileName}");
            AddFileContentToHistory(fileName, fileContent);
            
            // Store the file content and path for context in the next API call
            lastLoadedScriptPath = filePath;
            lastLoadedScriptContent = fileContent;
            
            // Optionally, ask the AI about the file
            string prompt = $"I'm looking at {fileName}. Can you explain what this script does?";
            queryField.SetValueWithoutNotify(prompt);
            queryField.RemoveFromClassList("placeholder-text");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error loading script file: {ex.Message}");
            AddMessageToHistory("System", $"Error loading file: {ex.Message}");
        }
    }

    private void AddFileContentToHistory(string fileName, string content)
    {
        // Add to UI
        AddFileContentToHistoryWithoutSaving(fileName, content);
        
        // Save to current session's history
        if (currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count)
        {
            chatSessions[currentSessionIndex].Messages.Add(new ChatMessage 
            { 
                Sender = "File", 
                Content = content,
                IsFileContent = true,
                FileName = fileName
            });
            
            // Save to EditorPrefs after adding file content
            SaveChatSessionsToEditorPrefs();
        }
    }

    private void AddFileContentToHistoryWithoutSaving(string fileName, string content)
    {
        var fileHeader = new Label
        {
            style =
            {
                whiteSpace = WhiteSpace.Normal,
                marginBottom = 4,
                marginTop = 8,
                unityFontStyleAndWeight = FontStyle.Bold
            },
            text = $"File: {fileName}"
        };
        
        var codeBlock = new TextField
        {
            multiline = true,
            style =
            {
                whiteSpace = WhiteSpace.Normal,
                marginBottom = 8,
                backgroundColor = new Color(0.2f, 0.2f, 0.2f),
                color = new Color(0.8f, 0.8f, 0.8f),
                paddingLeft = 8,
                paddingRight = 8,
                paddingTop = 4,
                paddingBottom = 4
            }
        };
        
        codeBlock.SetValueWithoutNotify(content);
        codeBlock.isReadOnly = true;
        
        conversationScrollView.Add(fileHeader);
        conversationScrollView.Add(codeBlock);
        
        // Scroll to bottom using the helper method
        EditorApplication.delayCall += ScrollToBottom;
    }

    private async Task OnSendButtonClicked()
    {
        // If placeholder, clear
        if (queryField.value == PLACEHOLDER_TEXT)
        {
            queryField.value = string.Empty;
        }
        var userText = queryField.value?.Trim();
        if (string.IsNullOrEmpty(userText)) return;

        currentUserPrompt = userText; 

        // Check if we're in play mode and the query has creation intent
        if (Application.isPlaying && HasCreationIntent(userText))
        {
            // Parse the query and create the requested object
            if (TryCreateObjectFromQuery(userText))
            {
                // Add system message showing what was created
                AddMessageToHistory("System", "Created object based on your request.");
                
                // Add user message to history
                AddMessageToHistory("User", userText);
                
                // Reset the query field
                queryField.value = "";
                return;
            }
        }
        
        // Reset the query field
        queryField.value = "";
        
        // Add user message to history
        AddMessageToHistory("User", userText);

        // Check if the chat needs auto-naming and this is the first user message
        if (currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count)
        {
            var currentSession = chatSessions[currentSessionIndex];
            if (currentSession.NeedsAutoNaming && 
                currentSession.Messages.Count > 0 && 
                currentSession.Messages.Count(m => m.Sender == "User") == 1)
            {
                // Start the auto-naming process
                AutoNameChat(userText);
            }
        }

        // Temporarily disable input
        queryField.SetEnabled(false);
        sendButton.SetEnabled(false);
        queryField.SetValueWithoutNotify(string.Empty);

        // Get the current model and provider
        var selectedModel = modelSelector.value;
        string provider = selectedModel.Provider;
        
        // Build context from selected files
        string filesContext = "";
        if (selectedFiles.Length > 0)
        {
            StringBuilder contextBuilder = new StringBuilder();
            contextBuilder.AppendLine("[Selected Files Context]");
            
            foreach (string filePath in selectedFiles)
            {
                if (filePath.EndsWith(".unity"))
                {
                    // Load the scene if it's not already loaded
                    if (lastLoadedScenePath != filePath)
                    {
                        contextBuilder.AppendLine($"Scene: {Path.GetFileName(filePath)}");
                        contextBuilder.AppendLine("[Scene Structure]");
                        contextBuilder.AppendLine(SceneAnalysisIntegration.GetSceneStructureSummary());
                        contextBuilder.AppendLine("[Spatial Information]");
                        contextBuilder.AppendLine(SceneAnalysisIntegration.GetSpatialInformation());
                    }
                    else if (isSceneLoaded)
                    {
                        contextBuilder.AppendLine($"Scene: {Path.GetFileName(filePath)}");
                        contextBuilder.AppendLine("[Scene Structure]");
                        contextBuilder.AppendLine(SceneAnalysisIntegration.GetSceneStructureSummary());
                        contextBuilder.AppendLine("[Spatial Information]");
                        contextBuilder.AppendLine(SceneAnalysisIntegration.GetSpatialInformation());
                    }
                }
                else if (filePath.EndsWith(".cs")) // Script file
                {
                    try
                    {
                        string fileContent = File.ReadAllText(filePath);
                        contextBuilder.AppendLine($"Script: {Path.GetFileName(filePath)}");
                        contextBuilder.AppendLine("```csharp");
                        contextBuilder.AppendLine(fileContent);
                        contextBuilder.AppendLine("```");
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error reading file {filePath}: {ex.Message}");
                    }
                }
            }
            
            filesContext = contextBuilder.ToString();
        }
        
        // Include scene context if the toggle is enabled
        string contextEnhancedPrompt = userText;
        if (includeSceneContext) {
            string sceneStructure = SceneAnalysisIntegration.GetSceneStructureSummary();
            string spatialInfo = SceneAnalysisIntegration.GetSpatialInformation();
            
            // Combine both types of information
            string combinedContext = $"[Scene Context]\n{sceneStructure}\n\n[Spatial Information]\n{spatialInfo}";
            
            // Add files context if available
            if (!string.IsNullOrEmpty(filesContext)) {
                contextEnhancedPrompt = $"{filesContext}\n\n{combinedContext}\n\n[User Query]\n{userText}";
            }
            else
            {
                contextEnhancedPrompt = $"{combinedContext}\n\n[User Query]\n{userText}";
            }
                
            // Add a system message to show the user we're including scene context
            AddMessageToHistory("System", "Including current scene context, spatial analysis, and selected files in this query.");
        }
        else if (!string.IsNullOrEmpty(filesContext))
        {
            // Only include files context
            contextEnhancedPrompt = $"{filesContext}\n\n[User Query]\n{userText}";
            AddMessageToHistory("System", "Including selected files in this query.");
        }
        
        // Send to the appropriate API based on the selected model's provider
        if (provider == "OpenAI")
        {
            SendQueryToOpenAIStreaming(contextEnhancedPrompt, selectedModel.Name, OnResponseReceived);
        }
        else if (provider == "Claude")
        {
            SendQueryToClaude(contextEnhancedPrompt, selectedModel.Name, OnResponseReceived);
        }
    }

    private void OnResponseReceived(string assistantReply, string providerName)
    {
        string displayName = $"XeleR";
        AddMessageToHistory(displayName, assistantReply);

        if (!string.IsNullOrEmpty(currentUserPrompt))
        {
            _ = LogUserPrompt(currentUserPrompt, assistantReply);
            currentUserPrompt = string.Empty;
        }
        
        // Check if the response contains code edits and apply them
        ProcessAndApplyCodeEdits(assistantReply);
        
        // Check if the response contains scene edits and apply them
        ProcessSceneEdits(assistantReply);

        // Re-enable input
        queryField.SetEnabled(true);
        sendButton.SetEnabled(true);

        if (string.IsNullOrEmpty(queryField.value))
        {
            queryField.SetValueWithoutNotify(PLACEHOLDER_TEXT);
            queryField.AddToClassList("placeholder-text");
        }
    }

    private void ProcessAndApplyCodeEdits(string assistantReply)
    {
        ApplyBatchChanges(() => 
        {
            // Pattern to match code blocks with file paths
            // Format: ```csharp:Assets/Scripts/SomeFile.cs ... ```
            var codeBlockPattern = new Regex(@"```(?:csharp|cs):([^\n]+)\n([\s\S]+?)```");
            var matches = codeBlockPattern.Matches(assistantReply);
            
            foreach (Match match in matches)
            {
                if (match.Groups.Count >= 3)
                {
                    string filePath = match.Groups[1].Value.Trim();
                    string codeContent = match.Groups[2].Value.Trim();
                    
                    // Skip empty code blocks
                    if (string.IsNullOrWhiteSpace(codeContent))
                        continue;
                    
                    // Apply the edit to the file
                    try
                    {
                        ApplyEditToFile(filePath, codeContent);
                        AddMessageToHistory("System", $"Applied changes to {filePath}");

                        // Try to find the GameObject that this script should be attached to
                        string fileName = Path.GetFileNameWithoutExtension(filePath);
                        GameObject targetObject = GameObject.Find(fileName);
                        
                        if (targetObject == null)
                        {
                            // Ask for user confirmation before creating a new GameObject
                            if (EditorUtility.DisplayDialog("Create GameObject", 
                                $"Would you like to create a new GameObject named '{fileName}' for this script?", 
                                "Yes", "No"))
                            {
                                targetObject = new GameObject(fileName);
                                AddMessageToHistory("System", $"Created new GameObject '{fileName}' for the script");
                            }
                            else
                            {
                                AddMessageToHistory("System", $"Skipped creating GameObject for script '{fileName}'");
                                return;
                            }
                        }

                        // Get the script type
                        Type scriptType = Type.GetType(fileName);
                        if (scriptType == null)
                        {
                            // Try finding the type in all loaded assemblies
                            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                scriptType = assembly.GetType(fileName);
                                if (scriptType != null) break;
                            }
                        }

                        if (scriptType != null)
                        {
                            // Check if the component already exists
                            Component existingComponent = targetObject.GetComponent(scriptType);
                            if (existingComponent == null)
                            {
                                // Ask for user confirmation before attaching the script
                                if (EditorUtility.DisplayDialog("Attach Script", 
                                    $"Would you like to attach the script '{fileName}' to GameObject '{targetObject.name}'?", 
                                    "Yes", "No"))
                                {
                                    // Add the component
                                    existingComponent = targetObject.AddComponent(scriptType);
                                    AddMessageToHistory("System", $"Attached script '{fileName}' to GameObject '{targetObject.name}'");
                                    
                                    // Initialize the component and its dependencies
                                    InitializeComponent(existingComponent, scriptType);
                                }
                                else
                                {
                                    AddMessageToHistory("System", $"Skipped attaching script '{fileName}' to GameObject '{targetObject.name}'");
                                }
                            }
                            else
                            {
                                AddMessageToHistory("System", $"Script '{fileName}' is already attached to GameObject '{targetObject.name}'");
                            }

                            // Look for prefab references in the script
                            var serializedFields = scriptType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                .Where(f => f.GetCustomAttribute<SerializeField>() != null || f.IsPublic);

                            foreach (var field in serializedFields)
                            {
                                // Check if the field type is a GameObject or a component type
                                if (field.FieldType == typeof(GameObject) || typeof(Component).IsAssignableFrom(field.FieldType))
                                {
                                    // Get the field name
                                    string fieldName = field.Name;
                                    Debug.Log($"[Prefab Assignment] Looking for prefab for field: {fieldName}");
                                    
                                    // Get field attributes for additional context
                                    var tooltipAttribute = field.GetCustomAttribute<TooltipAttribute>();
                                    string tooltip = tooltipAttribute?.tooltip ?? "";
                                    
                                    // Try different search patterns for the prefab
                                    List<string> searchPatterns = new List<string>();
                                    
                                    // Add exact matches first
                                    searchPatterns.Add(fieldName);
                                    searchPatterns.Add(fieldName.Replace("Prefab", ""));
                                    searchPatterns.Add(fieldName.Replace("prefab", ""));
                                    
                                    // Add variations based on the field name
                                    if (fieldName.Contains("Prefab"))
                                    {
                                        searchPatterns.Add(fieldName.Replace("Prefab", ""));
                                        searchPatterns.Add(fieldName.Replace("prefab", ""));
                                    }
                                    
                                    // Add variations based on the tooltip
                                    if (!string.IsNullOrEmpty(tooltip))
                                    {
                                        searchPatterns.Add(tooltip);
                                        searchPatterns.Add(tooltip.ToLower());
                                    }
                                    
                                    // Add variations based on the script name
                                    searchPatterns.Add(fileName);
                                    searchPatterns.Add(fileName.ToLower());
                                    
                                    // Add common variations
                                    searchPatterns.Add(fieldName.ToLower());
                                    searchPatterns.Add(fieldName.Replace("Prefab", "").ToLower());
                                    
                                    // Remove duplicates and empty patterns
                                    searchPatterns = searchPatterns.Distinct().Where(p => !string.IsNullOrEmpty(p)).ToList();
                                    
                                    Debug.Log($"[Prefab Assignment] Search patterns: {string.Join(", ", searchPatterns)}");

                                    UnityEngine.Object matchingPrefab = null;
                                    string matchingPath = null;
                                    int bestMatchScore = 0;

                                    foreach (string pattern in searchPatterns)
                                    {
                                        string[] prefabGuids = AssetDatabase.FindAssets($"{pattern} t:Prefab");
                                        Debug.Log($"[Prefab Assignment] Found {prefabGuids.Length} prefabs matching pattern: {pattern}");
                                        
                                        foreach (string guid in prefabGuids)
                                        {
                                            string prefabPath = AssetDatabase.GUIDToAssetPath(guid);
                                            UnityEngine.Object prefab = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(prefabPath);
                                            
                                            if (prefab != null)
                                            {
                                                Debug.Log($"[Prefab Assignment] Found prefab: {prefab.name} at {prefabPath}");
                                                
                                                // Calculate match score
                                                int matchScore = 0;
                                                
                                                // Exact match gets highest score
                                                if (prefab.name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                                                    matchScore += 100;
                                                
                                                // Contains field name
                                                if (prefab.name.Contains(fieldName, StringComparison.OrdinalIgnoreCase))
                                                    matchScore += 50;
                                                
                                                // Contains pattern
                                                if (prefab.name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                                                    matchScore += 30;
                                                
                                                // Contains tooltip
                                                if (!string.IsNullOrEmpty(tooltip) && 
                                                    prefab.name.Contains(tooltip, StringComparison.OrdinalIgnoreCase))
                                                    matchScore += 20;
                                                
                                                // Contains script name
                                                if (prefab.name.Contains(fileName, StringComparison.OrdinalIgnoreCase))
                                                    matchScore += 10;
                                                
                                                Debug.Log($"[Prefab Assignment] Match score for {prefab.name}: {matchScore}");
                                                
                                                if (matchScore > bestMatchScore)
                                                {
                                                    bestMatchScore = matchScore;
                                                    matchingPrefab = prefab;
                                                    matchingPath = prefabPath;
                                                }
                                            }
                                        }
                                    }

                                    if (matchingPrefab != null && bestMatchScore >= 30) // Only assign if we have a reasonable match
                                    {
                                        Debug.Log($"[Prefab Assignment] Best match found: {matchingPrefab.name} with score {bestMatchScore}");
                                        Debug.Log($"[Prefab Assignment] Attempting to assign prefab {matchingPrefab.name} to field {fieldName}");
                                        
                                        // Set the field value using SerializedObject
                                        SerializedObject serializedObject = new SerializedObject(existingComponent);
                                        SerializedProperty property = serializedObject.FindProperty(fieldName);
                                        
                                        if (property != null)
                                        {
                                            property.objectReferenceValue = matchingPrefab;
                                            bool success = serializedObject.ApplyModifiedProperties();
                                            
                                            if (success)
                                            {
                                                AddMessageToHistory("System", $"Assigned prefab '{matchingPrefab.name}' to field '{fieldName}' in '{fileName}'");
                                                Debug.Log($"[Prefab Assignment] Successfully assigned prefab {matchingPrefab.name} to field {fieldName}");
                                            }
                                            else
                                            {
                                                Debug.LogError($"[Prefab Assignment] Failed to apply modified properties for field {fieldName}");
                                            }
                                        }
                                        else
                                        {
                                            Debug.LogError($"[Prefab Assignment] Could not find property {fieldName} in serialized object");
                                        }
                                    }
                                    else
                                    {
                                        Debug.Log($"[Prefab Assignment] No suitable prefab found for field {fieldName} (best score: {bestMatchScore})");
                                        AddMessageToHistory("System", $"No suitable prefab found for field '{fieldName}' in '{fileName}'. Please assign manually.");
                                    }
                                }
                            }
                        }
                        else
                        {
                            AddMessageToHistory("System", $"Could not find script type '{fileName}'. Make sure the script name matches the class name.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"Error applying edit to {filePath}: {ex.Message}");
                        AddMessageToHistory("System", $"Error applying changes to {filePath}: {ex.Message}");
                    }
                }
            }
        });
        
        // Add scene edit processing
        ProcessSceneEdits(assistantReply);
    }

    private void ProcessSceneEdits(string assistantReply)
    {
        // Check if a scene is loaded directly from the active scene
        var activeScene = EditorSceneManager.GetActiveScene();
        bool sceneIsLoaded = activeScene.isLoaded && !string.IsNullOrEmpty(activeScene.path);
        
        Debug.Log($"[Scene Edit] Checking scene loaded status: isSceneLoaded={isSceneLoaded}, direct check={sceneIsLoaded}");
        Debug.Log($"[Scene Edit] Active scene name: {activeScene.name}");
        Debug.Log($"[Scene Edit] Scene is loaded: {activeScene.isLoaded}");
        Debug.Log($"[Scene Edit] Scene path: {activeScene.path}");
        
        if (!sceneIsLoaded)
        {
            Debug.Log("[Scene Edit] No scene is currently loaded. Scene edits will be ignored.");
            return;
        }
        
        Debug.Log("[Scene Edit] Processing scene edits from AI response...");
        Debug.Log($"[Scene Edit] Full AI response: {assistantReply}");
        
        // Pattern to match scene edit instructions
        // Format: ```scene:Command``` or scene:Command
        var sceneEditPattern = new Regex(@"(?:```)?scene:([^\n]+)(?:```)?");
        var matches = sceneEditPattern.Matches(assistantReply);
        
        Debug.Log($"[Scene Edit] Found {matches.Count} scene edit instructions");
        
        if (matches.Count > 0)
        {
            bool sceneModified = false;
            
            foreach (Match match in matches)
            {
                if (match.Groups.Count >= 2)
                {
                    string editInstruction = match.Groups[1].Value.Trim();
                    
                    // Skip empty edit instructions
                    if (string.IsNullOrWhiteSpace(editInstruction))
                        continue;
                        
                    // Remove any trailing backticks
                    editInstruction = editInstruction.TrimEnd('`');
                        
                    // If the instruction is just GameObject/Component, add the Create prefix
                    if (!editInstruction.Contains("=") && !editInstruction.StartsWith("Create"))
                    {
                        editInstruction = "Create/" + editInstruction;
                    }
                        
                    Debug.Log($"[Scene Edit] Processing instruction: {editInstruction}");
                    try
                    {
                        bool success = ApplySceneEdit(editInstruction);
                        if (success) sceneModified = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[Scene Edit] Error applying scene edit: {ex.Message}\nStack trace: {ex.StackTrace}");
                        AddMessageToHistory("System", $"Error applying scene edit: {ex.Message}");
                    }
                }
            }
            
            if (sceneModified)
            {
                // Mark the scene as dirty so Unity knows it needs to be saved
                EditorSceneManager.MarkSceneDirty(
                    EditorSceneManager.GetActiveScene());
                
                Debug.Log("[Scene Edit] Scene modifications applied successfully");
                AddMessageToHistory("System", "Scene modifications applied. Remember to save your scene.");
            }
            else
            {
                Debug.Log("[Scene Edit] No scene modifications were successfully applied");
            }
        }
    }

    private bool ApplySceneEdit(string editInstruction)
    {
        Debug.Log($"[Scene Edit] Applying edit: {editInstruction}");
        
        // Check if this is a create command
        if (editInstruction.StartsWith("Create/") || editInstruction.StartsWith("Create:"))
        {
            // Format: Create/GameObjectName/ComponentName or Create:GameObjectName:ComponentName
            string[] createParts = editInstruction.Split(new[] { '/', ':' }, StringSplitOptions.RemoveEmptyEntries);
            if (createParts.Length != 3)
            {
                Debug.LogError($"[Scene Edit] Invalid create command format: {editInstruction}");
                AddMessageToHistory("System", $"Invalid create command format: {editInstruction}");
                return false;
            }
            
            string createObjectName = createParts[1];
            string createComponentName = createParts[2];
            
            // Map common component names to their full type names
            Dictionary<string, string> componentNameMap = new Dictionary<string, string>
            {
                { "Rigidbody", "UnityEngine.Rigidbody" },
                { "BoxCollider", "UnityEngine.BoxCollider" },
                { "SphereCollider", "UnityEngine.SphereCollider" },
                { "CapsuleCollider", "UnityEngine.CapsuleCollider" },
                { "MeshCollider", "UnityEngine.MeshCollider" },
                { "MeshRenderer", "UnityEngine.MeshRenderer" },
                { "MeshFilter", "UnityEngine.MeshFilter" },
                { "Material", "UnityEngine.Material" },
                { "AudioSource", "UnityEngine.AudioSource" },
                { "AudioListener", "UnityEngine.AudioListener" },
                { "Camera", "UnityEngine.Camera" },
                { "Light", "UnityEngine.Light" },
                { "Animator", "UnityEngine.Animator" },
                { "Animation", "UnityEngine.Animation" },
                { "ParticleSystem", "UnityEngine.ParticleSystem" },
                { "Text", "UnityEngine.UI.Text" },
                { "Image", "UnityEngine.UI.Image" },
                { "Button", "UnityEngine.UI.Button" },
                { "Canvas", "UnityEngine.Canvas" },
                { "CanvasGroup", "UnityEngine.CanvasGroup" },
                { "RectTransform", "UnityEngine.RectTransform" },
                { "Transform", "UnityEngine.Transform" }
            };
            
            // If the component name is in our map, use the full type name
            if (componentNameMap.TryGetValue(createComponentName, out string fullTypeName))
            {
                createComponentName = fullTypeName;
            }
            
            // Find or create the GameObject
            GameObject createTargetObject = GameObject.Find(createObjectName);
            if (createTargetObject == null)
            {
                createTargetObject = new GameObject(createObjectName);
                Debug.Log($"[Scene Edit] Created new GameObject: {createObjectName}");
            }
            
            // Check if the component already exists
            Component existingComponent = createTargetObject.GetComponent(createComponentName);
            if (existingComponent != null)
            {
                Debug.Log($"[Scene Edit] Component {createComponentName} already exists on {createObjectName}");
                AddMessageToHistory("System", $"Component {createComponentName} already exists on {createObjectName}");
                return false;
            }
            
            // Add the component
            try
            {
                Type componentType = Type.GetType(createComponentName);
                if (componentType == null)
                {
                    // Try finding the type in all loaded assemblies
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        componentType = assembly.GetType(createComponentName);
                        if (componentType != null) break;
                    }
                }
                
                if (componentType == null)
                {
                    Debug.LogError($"[Scene Edit] Component type not found: {createComponentName}");
                    AddMessageToHistory("System", $"Component type not found: {createComponentName}");
                    return false;
                }
                
                // Before adding a component
                Undo.RecordObject(createTargetObject, $"Add {componentType.Name} to {createObjectName}");
                Component newComponent = createTargetObject.AddComponent(componentType);
                
                Debug.Log($"[Scene Edit] Added component {createComponentName} to {createObjectName}");
                AddMessageToHistory("System", $"Added component {createComponentName} to {createObjectName}");
                Undo.RegisterCreatedObjectUndo(newComponent, 
                    $"Add {createComponentName} to {createObjectName}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Scene Edit] Error adding component: {ex.Message}");
                AddMessageToHistory("System", $"Error adding component: {ex.Message}");
                return false;
            }
        }
        
        // Parse the edit instruction
        // Format: ObjectPath/Component/Property=Value or ObjectPath:Component:Property=Value
        string[] propertyParts = editInstruction.Split('=');
        if (propertyParts.Length != 2)
        {
            Debug.LogError($"[Scene Edit] Invalid scene edit format: {editInstruction}");
            AddMessageToHistory("System", $"Invalid scene edit format: {editInstruction}");
            return false;
        }
        
        string path = propertyParts[0];
        string value = propertyParts[1];
        
        string[] pathParts = path.Split(new[] { '/', ':' }, StringSplitOptions.RemoveEmptyEntries);
        if (pathParts.Length < 2)
        {
            Debug.LogError($"[Scene Edit] Invalid object path: {path}");
            AddMessageToHistory("System", $"Invalid object path: {path}");
            return false;
        }
        
        // The first part is the GameObject name
        string objectName = pathParts[0];
        
        // The second-to-last part is the component name
        string componentName = pathParts[pathParts.Length - 2];
        
        // The last part is the property name
        string propertyName = pathParts[pathParts.Length - 1];
        
        Debug.Log($"[Scene Edit] Looking for GameObject: {objectName}");
        Debug.Log($"[Scene Edit] Component: {componentName}");
        Debug.Log($"[Scene Edit] Property: {propertyName}");
        Debug.Log($"[Scene Edit] Value: {value}");
        
        // List all GameObjects in the scene for debugging
        Debug.Log("[Scene Edit] Listing all GameObjects in scene:");
        GameObject[] allObjects = GameObject.FindObjectsOfType<GameObject>();
        foreach (GameObject obj in allObjects)
        {
            Debug.Log($"[Scene Edit] Found GameObject: {obj.name} (Path: {GetFullPath(obj)})");
        }
        
        // Find the GameObject
        GameObject targetObject = GameObject.Find(objectName);
        if (targetObject == null)
        {
            // Try finding the object as a child of any GameObject in the scene
            GameObject[] sceneObjects = GameObject.FindObjectsOfType<GameObject>();
            foreach (GameObject obj in sceneObjects)
            {
                Transform foundChild = obj.transform.Find(objectName);
                if (foundChild != null)
                {
                    targetObject = foundChild.gameObject;
                    break;
                }
            }
            
            if (targetObject == null)
            {
                Debug.LogError($"[Scene Edit] GameObject not found: {objectName}");
                AddMessageToHistory("System", $"GameObject not found: {objectName}");
                return false;
            }
        }
        
        Debug.Log($"[Scene Edit] Found GameObject: {targetObject.name} at path {GetFullPath(targetObject)}");
        
        // Find the component in the target object or its children
        Component targetComponent = null;
        
        // Map common component names to their full type names
        Dictionary<string, string> propertyComponentNameMap = new Dictionary<string, string>
        {
            { "Rigidbody", "UnityEngine.Rigidbody" },
            { "BoxCollider", "UnityEngine.BoxCollider" },
            { "SphereCollider", "UnityEngine.SphereCollider" },
            { "CapsuleCollider", "UnityEngine.CapsuleCollider" },
            { "MeshCollider", "UnityEngine.MeshCollider" },
            { "MeshRenderer", "UnityEngine.MeshRenderer" },
            { "MeshFilter", "UnityEngine.MeshFilter" },
            { "Material", "UnityEngine.Material" },
            { "AudioSource", "UnityEngine.AudioSource" },
            { "AudioListener", "UnityEngine.AudioListener" },
            { "Camera", "UnityEngine.Camera" },
            { "Light", "UnityEngine.Light" },
            { "Animator", "UnityEngine.Animator" },
            { "Animation", "UnityEngine.Animation" },
            { "ParticleSystem", "UnityEngine.ParticleSystem" },
            { "Text", "UnityEngine.UI.Text" },
            { "Image", "UnityEngine.UI.Image" },
            { "Button", "UnityEngine.UI.Button" },
            { "Canvas", "UnityEngine.Canvas" },
            { "CanvasGroup", "UnityEngine.CanvasGroup" },
            { "RectTransform", "UnityEngine.RectTransform" },
            { "Transform", "UnityEngine.Transform" }
        };
        
        // Special case for Material - it's a property of MeshRenderer
        if (componentName == "Material")
        {
            // Find the MeshRenderer component
            MeshRenderer meshRenderer = targetObject.GetComponentInChildren<MeshRenderer>();
            if (meshRenderer == null)
            {
                Debug.LogError($"[Scene Edit] MeshRenderer not found on {objectName} or its children");
                AddMessageToHistory("System", $"MeshRenderer not found on {objectName} or its children");
                return false;
            }
            
            // Get the current material
            Material currentMaterial = meshRenderer.sharedMaterial;
            if (currentMaterial == null)
            {
                Debug.LogError($"[Scene Edit] No material found on MeshRenderer of {targetObject.name}");
                AddMessageToHistory("System", $"No material found on MeshRenderer of {targetObject.name}");
                return false;
            }

            // Create a new material instance
            Material newMaterial = new Material(currentMaterial);
            
            // Convert the color value
            Color newColor = ConvertValue(value, typeof(Color)) as Color? ?? Color.white;
            
            // Record the object for undo
            Undo.RecordObject(meshRenderer, $"Set material color on {targetObject.name}");
            
            // Apply the new color
            newMaterial.color = newColor;
            
            // Apply the new material
            meshRenderer.sharedMaterial = newMaterial;
            
            // Mark the object as dirty
            EditorUtility.SetDirty(meshRenderer);
            
            Debug.Log($"[Scene Edit] Successfully set material color = {value}");
            AddMessageToHistory("System", $"Set material color = {value} on {objectName}");
            return true;
        }
        
        // Get the full type name
        string propertyFullTypeName = componentName;
        if (propertyComponentNameMap.TryGetValue(componentName, out string mappedTypeName))
        {
            propertyFullTypeName = mappedTypeName;
        }
        
        // Find the type
        Type propertyComponentType = Type.GetType(propertyFullTypeName);
        if (propertyComponentType == null)
        {
            // Try finding the type in all loaded assemblies
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                propertyComponentType = assembly.GetType(propertyFullTypeName);
                if (propertyComponentType != null) break;
            }
        }
        
        if (propertyComponentType == null)
        {
            Debug.LogError($"[Scene Edit] Component type not found: {propertyFullTypeName}");
            AddMessageToHistory("System", $"Component type not found: {propertyFullTypeName}");
            return false;
        }
        
        // Find the component in the target object or its children
        Component[] components = targetObject.GetComponentsInChildren(propertyComponentType, true);
        if (components.Length > 0)
        {
            targetComponent = components[0];
            Debug.Log($"[Scene Edit] Found component in child: {targetComponent.gameObject.name}");
        }
        else
        {
            Debug.LogError($"[Scene Edit] Component not found: {componentName} on {objectName} or its children");
            AddMessageToHistory("System", $"Component not found: {componentName} on {objectName} or its children");
            return false;
        }
        
        Debug.Log($"[Scene Edit] Found component: {targetComponent.GetType().Name}");
        
        // Set the property value using reflection
        try
        {
            // Special case for Transform component
            if (targetComponent is Transform transform)
            {
                if (propertyName == "localScale" || propertyName == "scale")
                {
                    Vector3 newScale = ConvertValue(value, typeof(Vector3)) as Vector3? ?? Vector3.one;
                    
                    // Record the object for undo
                    Undo.RecordObject(transform, $"Set scale on {targetObject.name}");
                    
                    // Set the scale
                    transform.localScale = newScale;
                    
                    // Mark the object as dirty
                    EditorUtility.SetDirty(transform);
                    
                    Debug.Log($"[Scene Edit] Successfully set transform scale = {value}");
                    AddMessageToHistory("System", $"Set transform scale = {value} on {objectName}");
                    return true;
                }
                else if (propertyName == "position")
                {
                    Vector3 newPosition = ConvertValue(value, typeof(Vector3)) as Vector3? ?? Vector3.zero;
                    
                    // Record the object for undo
                    Undo.RecordObject(transform, $"Set position on {targetObject.name}");
                    
                    // Set the position
                    transform.position = newPosition;
                    
                    // Mark the object as dirty
                    EditorUtility.SetDirty(transform);
                    
                    Debug.Log($"[Scene Edit] Successfully set transform position = {value}");
                    AddMessageToHistory("System", $"Set transform position = {value} on {objectName}");
                    return true;
                }
                else if (propertyName == "rotation")
                {
                    Vector3 eulerAngles = ConvertValue(value, typeof(Vector3)) as Vector3? ?? Vector3.zero;
                    
                    // Record the object for undo
                    Undo.RecordObject(transform, $"Set rotation on {targetObject.name}");
                    
                    // Set the rotation
                    transform.rotation = Quaternion.Euler(eulerAngles);
                    
                    // Mark the object as dirty
                    EditorUtility.SetDirty(transform);
                    
                    Debug.Log($"[Scene Edit] Successfully set transform rotation = {value}");
                    AddMessageToHistory("System", $"Set transform rotation = {value} on {objectName}");
                    return true;
                }
            }
            
            // Special case for MeshRenderer material color
            if (targetComponent is MeshRenderer meshRenderer && (propertyName == "color" || propertyName == "material.color"))
            {
                // Get the current material
                Material currentMaterial = meshRenderer.sharedMaterial;
                if (currentMaterial == null)
                {
                    Debug.LogError($"[Scene Edit] No material found on MeshRenderer of {targetObject.name}");
                    AddMessageToHistory("System", $"No material found on MeshRenderer of {targetObject.name}");
                    return false;
                }

                // Create a new material instance
                Material newMaterial = new Material(currentMaterial);
                
                // Convert the color value
                Color newColor = ConvertValue(value, typeof(Color)) as Color? ?? Color.white;
                
                // Record the object for undo
                Undo.RecordObject(meshRenderer, $"Set material color on {targetObject.name}");
                
                // Apply the new color
                newMaterial.color = newColor;
                
                // Apply the new material
                meshRenderer.sharedMaterial = newMaterial;
                
                // Mark the object as dirty
                EditorUtility.SetDirty(meshRenderer);
                
                Debug.Log($"[Scene Edit] Successfully set material color = {value}");
                AddMessageToHistory("System", $"Set material color = {value} on {objectName}");
                return true;
            }
            
            var property = targetComponent.GetType().GetProperty(propertyName);
            if (property != null)
            {
                // Convert the value to the appropriate type
                object convertedValue = ConvertValue(value, property.PropertyType);
                
                // Record the object for undo
                Undo.RecordObject(targetComponent, $"Set {propertyName} on {targetObject.name}");
                
                // Make the change
                property.SetValue(targetComponent, convertedValue);
                
                // Mark as dirty
                EditorUtility.SetDirty(targetComponent);
                
                Debug.Log($"[Scene Edit] Successfully set property {propertyName} = {value}");
                AddMessageToHistory("System", $"Set {objectName}/{componentName}/{propertyName} = {value}");
                return true;
            }
            
            var field = targetComponent.GetType().GetField(propertyName);
            if (field != null)
            {
                // Convert the value to the appropriate type
                object convertedValue = ConvertValue(value, field.FieldType);
                
                // Record the object for undo
                Undo.RecordObject(targetComponent, $"Set {propertyName} on {targetObject.name}");
                
                // Make the change
                field.SetValue(targetComponent, convertedValue);
                
                // Mark as dirty
                EditorUtility.SetDirty(targetComponent);
                
                Debug.Log($"[Scene Edit] Successfully set field {propertyName} = {value}");
                AddMessageToHistory("System", $"Set {objectName}/{componentName}/{propertyName} = {value}");
                return true;
            }
            
            Debug.LogError($"[Scene Edit] Property or field not found: {propertyName} on {componentName}");
            AddMessageToHistory("System", $"Property or field not found: {propertyName} on {componentName}");
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Scene Edit] Error setting property: {ex.Message}");
            AddMessageToHistory("System", $"Error setting property: {ex.Message}");
            return false;
        }
    }

    private object ConvertValue(string value, Type targetType)
    {
        // Handle common Unity types
        if (targetType == typeof(float))
        {
            return float.Parse(value);
        }
        else if (targetType == typeof(int))
        {
            return int.Parse(value);
        }
        else if (targetType == typeof(bool))
        {
            return bool.Parse(value);
        }
        else if (targetType == typeof(string))
        {
            return value;
        }
        else if (targetType == typeof(Vector3))
        {
            // Format: (x,y,z)
            value = value.Trim('(', ')');
            string[] components = value.Split(',');
            if (components.Length == 3)
            {
                return new Vector3(
                    float.Parse(components[0]),
                    float.Parse(components[1]),
                    float.Parse(components[2])
                );
            }
        }
        else if (targetType == typeof(Vector2))
        {
            // Format: (x,y)
            value = value.Trim('(', ')');
            string[] components = value.Split(',');
            if (components.Length == 2)
            {
                return new Vector2(
                    float.Parse(components[0]),
                    float.Parse(components[1])
                );
            }
        }
        else if (targetType == typeof(Color))
        {
            // Format: (r,g,b,a) or (r,g,b)
            value = value.Trim('(', ')');
            string[] components = value.Split(',');
            if (components.Length >= 3)
            {
                if (components.Length == 4)
                {
                    return new Color(
                        float.Parse(components[0]),
                        float.Parse(components[1]),
                        float.Parse(components[2]),
                        float.Parse(components[3])
                    );
                }
                else
                {
                    return new Color(
                        float.Parse(components[0]),
                        float.Parse(components[1]),
                        float.Parse(components[2])
                    );
                }
            }
        }
        
        // For other types, try a general conversion
        return Convert.ChangeType(value, targetType);
    }

    private void ApplyEditToFile(string filePath, string newContent)
    {
        Debug.Log($"[Undo System] Starting file edit: {filePath}");
        
        // Take a snapshot before making changes
        if (File.Exists(filePath))
        {
            string oldContent = File.ReadAllText(filePath);
            
            // Record the EditorWindow itself for undo
            Undo.RecordObject(this, $"AI Edit: {Path.GetFileName(filePath)}");
            
            // Store snapshot in the serialized list
            var snapshot = new FileSnapshot { 
                FilePath = filePath, 
                Contents = oldContent,
                Timestamp = DateTime.Now,
                IsNewFile = false  // Existing file
            };
            fileSnapshots.Add(snapshot);
        }
        else
        {
            Debug.Log($"[Undo System] Creating new file: {filePath}");
            // Record the window for undo
            Undo.RecordObject(this, $"AI Create: {Path.GetFileName(filePath)}");
            
            // Create snapshot for new file
            var snapshot = new FileSnapshot { 
                FilePath = filePath, 
                Contents = "",  // Empty content for new file
                Timestamp = DateTime.Now,
                IsNewFile = true  // Mark as new file
            };
            fileSnapshots.Add(snapshot);
        }
        
        // Write the new content
        File.WriteAllText(filePath, newContent);
        AssetDatabase.Refresh();
    }

    private void ApplyPartialEdit(string filePath, string editContent)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }
        
        string originalContent = File.ReadAllText(filePath);
        string[] originalLines = originalContent.Split('\n');
        string[] editLines = editContent.Split('\n');
        
        // Find the section to edit by looking for the first non-comment, non-empty line
        int editStartIndex = -1;
        for (int i = 0; i < editLines.Length; i++)
        {
            string line = editLines[i].Trim();
            if (!string.IsNullOrEmpty(line) && !line.StartsWith("//"))
            {
                editStartIndex = i;
                break;
            }
        }
        
        if (editStartIndex == -1)
        {
            Debug.LogWarning("No valid edit content found in the provided code block");
            return;
        }
        
        // Find where this code exists in the original file
        string searchPattern = editLines[editStartIndex].Trim();
        int originalStartIndex = -1;
        
        for (int i = 0; i < originalLines.Length; i++)
        {
            if (originalLines[i].Trim() == searchPattern)
            {
                originalStartIndex = i;
                break;
            }
        }
        
        if (originalStartIndex == -1)
        {
            Debug.LogWarning("Could not find the edit location in the original file");
            return;
        }
        
        // Find the end of the edit section by looking for the next "existing code" marker
        int editEndIndex = -1;
        for (int i = editStartIndex + 1; i < editLines.Length; i++)
        {
            if (editLines[i].Contains("existing code"))
            {
                editEndIndex = i;
                break;
            }
        }
        
        if (editEndIndex == -1)
        {
            editEndIndex = editLines.Length;
        }
        
        // Find the end of the section in the original file
        int originalEndIndex = -1;
        for (int i = originalStartIndex + 1; i < originalLines.Length; i++)
        {
            if (originalLines[i].Contains("existing code"))
            {
                originalEndIndex = i;
                break;
            }
        }
        
        if (originalEndIndex == -1)
        {
            originalEndIndex = originalLines.Length;
        }
        
        // Combine the original content with the edit
        List<string> newLines = new List<string>();
        newLines.AddRange(originalLines.Take(originalStartIndex));
        newLines.AddRange(editLines.Skip(editStartIndex).Take(editEndIndex - editStartIndex));
        newLines.AddRange(originalLines.Skip(originalEndIndex));
        
        // Write the combined content back to the file
        File.WriteAllText(filePath, string.Join("\n", newLines));
        AssetDatabase.Refresh();
    }

    private async void SendQueryToOpenAIStreaming(string userMessage, string model, Action<string, string> onResponse)
    {
        const string url = "https://api.openai.com/v1/chat/completions";
        string apiKey = ApiKeyManager.GetKey(ApiKeyManager.OPENAI_KEY);
        if (string.IsNullOrEmpty(apiKey))
        {
            AddMessageToHistory("System", "<error: OpenAI API key not set. Click the API Settings button to configure it.>");
            return;
        }
        
        if (userMessage.ToLower().Contains("more example") ||
            userMessage.ToLower().Contains("more prompt") ||
            userMessage.ToLower().Contains("give example") ||
            userMessage.ToLower().Contains("show example"))
        {
            List<string> moreExamples = PromptRecommender.GetRandomPrompts(3);
            string examplesMessage = "Here are some more example prompts you can try:\n\n" +
                                    $"• {moreExamples[0]}\n" +
                                    $"• {moreExamples[1]}\n" +
                                    $"• {moreExamples[2]}";
            onResponse?.Invoke(examplesMessage, "OpenAI");
            return;
        }
        
        string escapedMessage = EscapeJson(userMessage);
        string systemPrompt = "You are a Unity development assistant that can help with code and scene modifications. " +
            "When suggesting code changes, use the format ```csharp:Assets/Scripts/FileName.cs\\n// code here\\n``` so the changes can be automatically applied. " +
            "\n\nFor scene modifications, you can make the following types of changes:\n" +
            "1. Add or modify components: Use ```scene:GameObjectPath/ComponentName/PropertyName=Value```\n" +
            "   Examples:\n" +
            "   - Add a Rigidbody: ```scene:Player/Rigidbody/mass=10```\n" +
            "   - Change camera field of view: ```scene:Main Camera/Camera/fieldOfView=60```\n" +
            "   - Set transform position: ```scene:Player/Transform/position=(1,2,3)```\n" +
            "   - Enable/disable components: ```scene:Player/Camera/enabled=true```\n\n" +
            "2. Create new GameObjects: Use ```scene:Create/GameObjectName/ComponentName```\n" +
            "   Examples:\n" +
            "   - Create empty GameObject: ```scene:Create/NewObject/Transform```\n" +
            "   - Create with component: ```scene:Create/Player/Rigidbody```\n\n" +
            "Important notes:\n" +
            "- GameObjectPath must be the full path in the hierarchy (e.g., 'Player/ChildObject')\n" +
            "- ComponentName must be the exact Unity component name (e.g., 'Rigidbody', 'Camera', 'Transform')\n" +
            "- PropertyName must be the exact property name on the component\n" +
            "- For vector values, use the format (x,y,z)\n" +
            "- For boolean values, use 'true' or 'false'\n" +
            "- For numeric values, use the number directly\n" +
            "- You can make multiple scene changes in one response by using multiple scene edit blocks";
        string sceneAnalyzerPrompt = SceneAnalysisIntegration.LoadMetaprompt("SceneAnalyzer_RequestAware");
        if (!string.IsNullOrEmpty(sceneAnalyzerPrompt))
        {
            systemPrompt += "\n\n" + sceneAnalyzerPrompt;
        }
        
        string contextMessage = "";
        if (!string.IsNullOrEmpty(lastLoadedScriptPath) && !string.IsNullOrEmpty(lastLoadedScriptContent))
        {
            contextMessage = $"I'm working with this file: {lastLoadedScriptPath}\\n```csharp\\n{EscapeJson(lastLoadedScriptContent)}\\n```\\n\\nMy question is: ";
        }
        if (isSceneLoaded && !string.IsNullOrEmpty(lastLoadedScenePath))
        {
            string sceneName = Path.GetFileName(lastLoadedScenePath);
            string sceneContext = SceneAnalysisIntegration.GetSceneStructureSummary();
            contextMessage += $"I'm working with the Unity scene: {sceneName}\n{sceneContext}\n\nMy question is: ";
        }
        
        string jsonPayload = @"{
            ""model"": """ + model + @""",
            ""stream"": true,
            ""messages"": [
                {
                    ""role"": ""system"",
                    ""content"": """ + systemPrompt + @"""
                },";
        
        if (!string.IsNullOrEmpty(contextMessage))
        {
            jsonPayload += @"
                {
                    ""role"": ""user"",
                    ""content"": """ + contextMessage + escapedMessage + @"""
                }";
        }
        else
        {
            jsonPayload += @"
                {
                    ""role"": ""user"",
                    ""content"": """ + escapedMessage + @"""
                }";
        }
        
        jsonPayload += @"
            ]
        }";
        
        jsonPayload = Regex.Replace(jsonPayload, @"\s+", " ").Replace(" \"", "\"").Replace("\" ", "\"");
        
        AddStreamingPlaceholderMessage();
        
        void OnChunkReceived(string chunk)
        {
            string processed = chunk.StartsWith("data:") ? chunk.Substring(5).Trim() : chunk;
            if (processed == "[DONE]")
                return;
        
            OpenAIStreamChunk chunkObj = null;
            try
            {
                chunkObj = JsonUtility.FromJson<OpenAIStreamChunk>(processed);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Failed to parse streaming JSON chunk: " + e.Message);
                return;
            }
        
            if (chunkObj?.choices != null && chunkObj.choices.Length > 0)
            {
                // // Text Streaming CodeBlock Error FIX: Instead of splitting on spaces:
                string content = chunkObj.choices[0].delta?.content;
                if (!string.IsNullOrEmpty(content))
                {
                    // Just append the raw chunk:
                    UpdateStreamingMessage(content);
                }
            }
        }
        
        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
            var streamingHandler = new StreamingDownloadHandler(OnChunkReceived);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = streamingHandler;
        
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + apiKey);
        
            var operation = request.SendWebRequest();
        
            Debug.Log("Sending streaming request to OpenAI with payload: " + jsonPayload);
        
            while (!operation.isDone)
                await Task.Yield();
        
            queryField.SetEnabled(true);
            sendButton.SetEnabled(true);
        
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("OpenAI Streaming API Error: " + request.error);
                Debug.LogError("Response body: " + request.downloadHandler.text);
                AddMessageToHistory("System", "<error: could not get response>");
                return;
            }

            if (streamingContainer != null)
            {
                string finalResponse = streamingContainer.currentText;
                if (streamingContainer.parent != null)
                    streamingContainer.parent.RemoveFromHierarchy();
                streamingContainer = null;

                // Then pass the actual text so ProcessAndApplyCodeEdits can detect code blocks
                onResponse?.Invoke(finalResponse, "OpenAI");
            }
            else
            {
                // If for some reason streamingContainer was null, just pass empty
                onResponse?.Invoke("", "OpenAI");
            }
        }
        // Extra closing brace added to fix missing }
    }

    private async void SendQueryToOpenAI(string userMessage, string model, Action<string, string> onResponse)
    {
        const string url = "https://api.openai.com/v1/chat/completions";
        
        // Get API key from manager
        string apiKey = ApiKeyManager.GetKey(ApiKeyManager.OPENAI_KEY);
        if (string.IsNullOrEmpty(apiKey))
        {
            onResponse?.Invoke("<error: OpenAI API key not set. Click the API Settings button to configure it.>", "OpenAI");
            return;
        }

        // Check if the user is asking for more examples
        if (userMessage.ToLower().Contains("more example") || 
            userMessage.ToLower().Contains("more prompt") || 
            userMessage.ToLower().Contains("give example") ||
            userMessage.ToLower().Contains("show example"))
        {
            // Get more example prompts
            List<string> moreExamples = PromptRecommender.GetRandomPrompts(3);
            string examplesMessage = "Here are some more example prompts you can try:\n\n" +
                                     $"• {moreExamples[0]}\n" +
                                     $"• {moreExamples[1]}\n" +
                                     $"• {moreExamples[2]}";
            
            onResponse?.Invoke(examplesMessage, "OpenAI");
            return;
        }
        
        // Properly escape the user message to avoid JSON formatting issues
        string escapedMessage = EscapeJson(userMessage);
        
        // Load scene analyzer metaprompt if available
        string systemPrompt = "You are a Unity development assistant that can help with code and scene modifications. " +
            "When suggesting code changes, use the format ```csharp:Assets/Scripts/FileName.cs\\n// code here\\n``` so the changes can be automatically applied. " +
            "\n\nFor scene modifications, you can make the following types of changes:\n" +
            "1. Add or modify components: Use ```scene:GameObjectPath/ComponentName/PropertyName=Value```\n" +
            "   Examples:\n" +
            "   - Add a Rigidbody: ```scene:Player/Rigidbody/mass=10```\n" +
            "   - Change camera field of view: ```scene:Main Camera/Camera/fieldOfView=60```\n" +
            "   - Set transform position: ```scene:Player/Transform/position=(1,2,3)```\n" +
            "   - Enable/disable components: ```scene:Player/Camera/enabled=true```\n\n" +
            "2. Create new GameObjects: Use ```scene:Create/GameObjectName/ComponentName```\n" +
            "   Examples:\n" +
            "   - Create empty GameObject: ```scene:Create/NewObject/Transform```\n" +
            "   - Create with component: ```scene:Create/Player/Rigidbody```\n\n" +
            "Important notes:\n" +
            "- GameObjectPath must be the full path in the hierarchy (e.g., 'Player/ChildObject')\n" +
            "- ComponentName must be the exact Unity component name (e.g., 'Rigidbody', 'Camera', 'Transform')\n" +
            "- PropertyName must be the exact property name on the component\n" +
            "- For vector values, use the format (x,y,z)\n" +
            "- For boolean values, use 'true' or 'false'\n" +
            "- For numeric values, use the number directly\n" +
            "- You can make multiple scene changes in one response by using multiple scene edit blocks";
        
        string sceneAnalyzerPrompt = SceneAnalysisIntegration.LoadMetaprompt("SceneAnalyzer_RequestAware");
        if (!string.IsNullOrEmpty(sceneAnalyzerPrompt))
        {
            systemPrompt += "\n\n" + sceneAnalyzerPrompt;
        }
        
        // Add script context if available
        string contextMessage = "";
        if (!string.IsNullOrEmpty(lastLoadedScriptPath) && !string.IsNullOrEmpty(lastLoadedScriptContent))
        {
            contextMessage = $"I'm working with this file: {lastLoadedScriptPath}\\n```csharp\\n{EscapeJson(lastLoadedScriptContent)}\\n```\\n\\nMy question is: ";
        }
        
        // Add scene context if available
        if (isSceneLoaded && !string.IsNullOrEmpty(lastLoadedScenePath))
        {
            string sceneName = Path.GetFileName(lastLoadedScenePath);
            string sceneContext = SceneAnalysisIntegration.GetSceneStructureSummary();
            
            contextMessage += $"I'm working with the Unity scene: {sceneName}\n{sceneContext}\n\nMy question is: ";
        }
        
        // Simplify the prompt to reduce potential formatting issues
        string jsonPayload = @"{
            ""model"": """ + model + @""",
            ""messages"": [
                {
                    ""role"": ""system"",
                    ""content"": """ + systemPrompt + @"""
                },";
        
        // Add context message if available
        if (!string.IsNullOrEmpty(contextMessage))
        {
            jsonPayload += @"
                {
                    ""role"": ""user"",
                    ""content"": """ + contextMessage + escapedMessage + @"""
                }";
        }
        else
        {
            jsonPayload += @"
                {
                    ""role"": ""user"",
                    ""content"": """ + escapedMessage + @"""
                }";
        }
        
        jsonPayload += @"
            ]
        }";
        
        // Remove whitespace from the JSON to ensure proper formatting
        jsonPayload = Regex.Replace(jsonPayload, @"\s+", " ");
        jsonPayload = jsonPayload.Replace(" \"", "\"").Replace("\" ", "\"");

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();

            // Headers
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Authorization", "Bearer " + apiKey);

            Debug.Log("Sending request to OpenAI with payload: " + jsonPayload);
            
            var operation = request.SendWebRequest();
            while (!operation.isDone)
                await Task.Yield();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("OpenAI API Error: " + request.error);
                Debug.LogError("Response body: " + request.downloadHandler.text);
                onResponse?.Invoke("<error: could not get response>", "OpenAI");
                return;
            }

            string responseJson = request.downloadHandler.text;
            string assistantText = ParseOpenAIReply(responseJson);
            onResponse?.Invoke(assistantText, "OpenAI");
        }
    }

    private async void SendQueryToClaudeStreaming(string userMessage, string model, Action<string, string> onResponse)
    {
        const string url = "https://api.anthropic.com/v1/messages";
        string apiKey = ApiKeyManager.GetKey(ApiKeyManager.CLAUDE_KEY);
        if (string.IsNullOrEmpty(apiKey))
        {
            AddMessageToHistory("System", "<error: Claude API key not set. Click the API Settings button to configure it.>");
            return;
        }

        // Check if the user is asking for more examples
        if (userMessage.ToLower().Contains("more example") ||
            userMessage.ToLower().Contains("more prompt") ||
            userMessage.ToLower().Contains("give example") ||
            userMessage.ToLower().Contains("show example"))
        {
            List<string> moreExamples = PromptRecommender.GetRandomPrompts(3);
            string examplesMessage = "Here are some more example prompts you can try:\n\n" +
                                    $"• {moreExamples[0]}\n" +
                                    $"• {moreExamples[1]}\n" +
                                    $"• {moreExamples[2]}";
            onResponse?.Invoke(examplesMessage, "Claude");
            return;
        }

        // Escape the user message to avoid JSON formatting issues
        string escapedMessage = EscapeJson(userMessage);

        // Load scene analyzer metaprompt if available
        string systemPrompt = "You are a Unity development assistant that can help with code and scene modifications. " +
            "When suggesting code changes, use the format ```csharp:Assets/Scripts/FileName.cs\\n// code here\\n``` so the changes can be automatically applied. " +
            "\n\nFor scene modifications, you can make the following types of changes:\n" +
            "1. Add or modify components: Use ```scene:GameObjectPath/ComponentName/PropertyName=Value```\n" +
            "   Examples:\n" +
            "   - Add a Rigidbody: ```scene:Player/Rigidbody/mass=10```\n" +
            "   - Change camera field of view: ```scene:Main Camera/Camera/fieldOfView=60```\n" +
            "   - Set transform position: ```scene:Player/Transform/position=(1,2,3)```\n" +
            "   - Enable/disable components: ```scene:Player/Camera/enabled=true```\n\n" +
            "2. Create new GameObjects: Use ```scene:Create/GameObjectName/ComponentName```\n" +
            "   Examples:\n" +
            "   - Create empty GameObject: ```scene:Create/NewObject/Transform```\n" +
            "   - Create with component: ```scene:Create/Player/Rigidbody```\n\n" +
            "Important notes:\n" +
            "- GameObjectPath must be the full path in the hierarchy (e.g., 'Player/ChildObject')\n" +
            "- ComponentName must be the exact Unity component name (e.g., 'Rigidbody', 'Camera', 'Transform')\n" +
            "- PropertyName must be the exact property name on the component\n" +
            "- For vector values, use the format (x,y,z)\n" +
            "- For boolean values, use 'true' or 'false'\n" +
            "- For numeric values, use the number directly\n" +
            "- You can make multiple scene changes in one response by using multiple scene edit blocks";
        string sceneAnalyzerPrompt = SceneAnalysisIntegration.LoadMetaprompt("SceneAnalyzer_RequestAware");
        if (!string.IsNullOrEmpty(sceneAnalyzerPrompt))
        {
            systemPrompt += "\n\n" + sceneAnalyzerPrompt;
        }

        // Add script context if available
        string contextMessage = "";
        if (!string.IsNullOrEmpty(lastLoadedScriptPath) && !string.IsNullOrEmpty(lastLoadedScriptContent))
        {
            contextMessage = $"I'm working with this file: {lastLoadedScriptPath}\\n```csharp\\n{EscapeJson(lastLoadedScriptContent)}\\n```\\n\\nMy question is: ";
        }

        // Add scene context if available
        if (isSceneLoaded && !string.IsNullOrEmpty(lastLoadedScenePath))
        {
            string sceneName = Path.GetFileName(lastLoadedScenePath);
            string sceneContext = SceneAnalysisIntegration.GetSceneStructureSummary();
            contextMessage += $"I'm working with the Unity scene: {sceneName}\n{sceneContext}\n\nMy question is: ";
        }

        // Construct JSON payload with streaming enabled
        string jsonPayload = @"{
            ""model"": """ + model + @""",
            ""stream"": true,
            ""max_tokens"": 1024,
            ""messages"": [
                {
                    ""role"": ""system"",
                    ""content"": """ + systemPrompt + @"""
                },";
        if (!string.IsNullOrEmpty(contextMessage))
        {
            jsonPayload += @"
                {
                    ""role"": ""user"",
                    ""content"": """ + contextMessage + escapedMessage + @"""
                }";
        }
        else
        {
            jsonPayload += @"
                {
                    ""role"": ""user"",
                    ""content"": """ + escapedMessage + @"""
                }";
        }
        jsonPayload += @"
            ]
        }";
        jsonPayload = Regex.Replace(jsonPayload, @"\s+", " ").Replace(" \"", "\"").Replace("\" ", "\"");

        // Create a placeholder message for streaming and store the label reference.
        AddStreamingPlaceholderMessage();

        // Define a callback to update the UI as chunks arrive.
        void OnChunkReceived(string chunk)
        {
            string processed = chunk.StartsWith("data:") ? chunk.Substring(5).Trim() : chunk;
            if (processed == "[DONE]")
                return;
            OpenAIStreamChunk chunkObj = null;
            try
            {
                chunkObj = JsonUtility.FromJson<OpenAIStreamChunk>(processed);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Failed to parse streaming JSON chunk from Claude: " + e.Message);
                return;
            }
            if (chunkObj?.choices != null && chunkObj.choices.Length > 0)
            {
                // Text Streaming CodeBlock Error FIX: Instead of splitting on spaces:
                string content = chunkObj.choices[0].delta?.content;
                if (!string.IsNullOrEmpty(content))
                {
                    // Just append the raw chunk:
                    UpdateStreamingMessage(content);
                }
            }
        }

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
            var streamingHandler = new StreamingDownloadHandler(OnChunkReceived);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = streamingHandler;

            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("x-api-key", apiKey);
            request.SetRequestHeader("anthropic-version", "2023-06-01");

            Debug.Log("Sending streaming request to Claude with payload: " + jsonPayload);
            var operation = request.SendWebRequest();
            while (!operation.isDone)
                await Task.Yield();

            queryField.SetEnabled(true);
            sendButton.SetEnabled(true);

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Claude Streaming API Error: " + request.error);
                Debug.LogError("Response body: " + request.downloadHandler.text);
                AddMessageToHistory("System", "<error: could not get response from Claude>");
                return;
            }
        }
    }

    private async void SendQueryToClaude(string userMessage, string model, Action<string, string> onResponse)
    {
        const string url = "https://api.anthropic.com/v1/messages";
        
        // Get API key from manager
        string apiKey = ApiKeyManager.GetKey(ApiKeyManager.CLAUDE_KEY);
        if (string.IsNullOrEmpty(apiKey))
        {
            onResponse?.Invoke("<error: Claude API key not set. Click the API Settings button to configure it.>", "Claude");
            return;
        }

        // Check if the user is asking for more examples
        if (userMessage.ToLower().Contains("more example") || 
            userMessage.ToLower().Contains("more prompt") || 
            userMessage.ToLower().Contains("give example") ||
            userMessage.ToLower().Contains("show example"))
        {
            // Get more example prompts
            List<string> moreExamples = PromptRecommender.GetRandomPrompts(3);
            string examplesMessage = "Here are some more example prompts you can try:\n\n" +
                                     $"• {moreExamples[0]}\n" +
                                     $"• {moreExamples[1]}\n" +
                                     $"• {moreExamples[2]}";
            
            onResponse?.Invoke(examplesMessage, "Claude");
            return;
        }
        
        // Properly escape the user message to avoid JSON formatting issues
        string escapedMessage = EscapeJson(userMessage);
        
        // Load scene analyzer metaprompt if available
        string systemPrompt = "You are a Unity development assistant that can help with code and scene modifications. " +
            "When suggesting code changes, use the format ```csharp:Assets/Scripts/FileName.cs\\n// code here\\n``` so the changes can be automatically applied. " +
            "\n\nFor scene modifications, you can make the following types of changes:\n" +
            "1. Add or modify components: Use ```scene:GameObjectPath/ComponentName/PropertyName=Value```\n" +
            "   Examples:\n" +
            "   - Add a Rigidbody: ```scene:Player/Rigidbody/mass=10```\n" +
            "   - Change camera field of view: ```scene:Main Camera/Camera/fieldOfView=60```\n" +
            "   - Set transform position: ```scene:Player/Transform/position=(1,2,3)```\n" +
            "   - Enable/disable components: ```scene:Player/Camera/enabled=true```\n\n" +
            "2. Create new GameObjects: Use ```scene:Create/GameObjectName/ComponentName```\n" +
            "   Examples:\n" +
            "   - Create empty GameObject: ```scene:Create/NewObject/Transform```\n" +
            "   - Create with component: ```scene:Create/Player/Rigidbody```\n\n" +
            "Important notes:\n" +
            "- GameObjectPath must be the full path in the hierarchy (e.g., 'Player/ChildObject')\n" +
            "- ComponentName must be the exact Unity component name (e.g., 'Rigidbody', 'Camera', 'Transform')\n" +
            "- PropertyName must be the exact property name on the component\n" +
            "- For vector values, use the format (x,y,z)\n" +
            "- For boolean values, use 'true' or 'false'\n" +
            "- For numeric values, use the number directly\n" +
            "- You can make multiple scene changes in one response by using multiple scene edit blocks";
        
        string sceneAnalyzerPrompt = SceneAnalysisIntegration.LoadMetaprompt("SceneAnalyzer_RequestAware");
        if (!string.IsNullOrEmpty(sceneAnalyzerPrompt))
        {
            systemPrompt += "\n\n" + sceneAnalyzerPrompt;
        }
        
        // Add script context if available
        string contextMessage = "";
        if (!string.IsNullOrEmpty(lastLoadedScriptPath) && !string.IsNullOrEmpty(lastLoadedScriptContent))
        {
            contextMessage = $"I'm working with this file: {lastLoadedScriptPath}\\n```csharp\\n{EscapeJson(lastLoadedScriptContent)}\\n```\\n\\nMy question is: ";
        }
        
        // Add scene context if available
        if (isSceneLoaded && !string.IsNullOrEmpty(lastLoadedScenePath))
        {
            string sceneName = Path.GetFileName(lastLoadedScenePath);
            string sceneContext = SceneAnalysisIntegration.GetSceneStructureSummary();
            
            contextMessage += $"I'm working with the Unity scene: {sceneName}\n{sceneContext}\n\nMy question is: ";
        }
        
        // Construct Claude API request
        string jsonPayload = @"{
            ""model"": """ + model + @""",
            ""max_tokens"": 1024,
            ""messages"": [
                {
                    ""role"": ""system"",
                    ""content"": """ + systemPrompt + @"""
                },";
        
        // Add context message if available
        if (!string.IsNullOrEmpty(contextMessage))
        {
            jsonPayload += @"
                {
                    ""role"": ""user"",
                    ""content"": """ + contextMessage + escapedMessage + @"""
                }";
        }
        else
        {
            jsonPayload += @"
                {
                    ""role"": ""user"",
                    ""content"": """ + escapedMessage + @"""
                }";
        }
        
        jsonPayload += @"
            ]
        }";
        
        // Remove whitespace from the JSON to ensure proper formatting
        jsonPayload = Regex.Replace(jsonPayload, @"\s+", " ");
        jsonPayload = jsonPayload.Replace(" \"", "\"").Replace("\" ", "\"");

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();

            // Headers for Claude API
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("x-api-key", apiKey);
            request.SetRequestHeader("anthropic-version", "2023-06-01");

            Debug.Log("Sending request to Claude with payload: " + jsonPayload);
            
            var operation = request.SendWebRequest();
            while (!operation.isDone)
                await Task.Yield();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("Claude API Error: " + request.error);
                Debug.LogError("Response body: " + request.downloadHandler.text);
                onResponse?.Invoke("<error: could not get response>", "Claude");
                return;
            }

            string responseJson = request.downloadHandler.text;
            string assistantText = ParseClaudeReply(responseJson);
            onResponse?.Invoke(assistantText, "Claude");
        }
    }

    private void AddMessageToHistory(string sender, string message)
    {
        // Create a container for the message
        var messageContainer = new VisualElement
        {
            style =
            {
                marginBottom = 8,
                paddingLeft = 4,
                paddingRight = 4
            }
        };

        // Add sender name with bold styling
        var senderLabel = new Label
        {
            style =
            {
                unityFontStyleAndWeight = FontStyle.Bold,
                marginBottom = 2
            },
            text = sender + ":"
        };
        messageContainer.Add(senderLabel);

        // Process message for markdown if it's from the AI
        if (sender == "XeleR")
        {
            // Create a content container for the message
            var contentContainer = new VisualElement
            {
                style =
                {
                    marginLeft = 4,
                    marginRight = 4
                }
            };
            
            // Use the markdown renderer to format the message
            var formattedContent = MarkdownRenderer.RenderMarkdown(message);
            contentContainer.Add(formattedContent);
            messageContainer.Add(contentContainer);
            
            // Process code blocks separately (these will be added directly to the conversation)
            ProcessCodeBlocksInMessage(message);
        }
        else
        {
            // For non-AI messages, just use a simple label
            var contentLabel = new Label
            {
                style =
                {
                    whiteSpace = WhiteSpace.Normal,
                    marginLeft = 4
                },
                text = message
            };
            messageContainer.Add(contentLabel);
        }

        conversationScrollView.Add(messageContainer);

        // Save to current session's history
        if (currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count)
        {
            chatSessions[currentSessionIndex].Messages.Add(new ChatMessage 
            { 
                Sender = sender, 
                Content = message,
                IsFileContent = false
            });
            
            // Save to EditorPrefs after adding a message
            SaveChatSessionsToEditorPrefs();
        }

        // Scroll to bottom using the helper method
        EditorApplication.delayCall += ScrollToBottom;
        
        // Mark as dirty
        EditorUtility.SetDirty(this);
    }
    private void ProcessCodeBlocksInMessage(string message)
    {
        var codeBlockPattern = new Regex(@"```(?:csharp|cs):([^\n]+)\n([\s\S]*?)```");
        var matches = codeBlockPattern.Matches(message);
        
        foreach (Match match in matches)
        {
            if (match.Groups.Count >= 3)
            {
                string filePath = match.Groups[1].Value.Trim();
                string codeContent = match.Groups[2].Value;
                VisualElement codeBlockElement = MarkdownRenderer.RenderCodeBlock(filePath, codeContent);
                conversationScrollView.Add(codeBlockElement);
            }
        }
    }


    // Add a formatted code block to the conversation
    private void AddCodeBlockToHistory(string filePath, string content)
    {
        var fileHeader = new Label
        {
            style =
            {
                whiteSpace = WhiteSpace.Normal,
                marginBottom = 4,
                marginTop = 8,
                unityFontStyleAndWeight = FontStyle.Bold,
                color = new Color(0.4f, 0.7f, 1.0f) // Light blue for code headers
            },
            text = $"Code: {Path.GetFileName(filePath)} ({filePath})"
        };
        
        var codeBlock = new TextField
        {
            multiline = true,
            style =
            {
                whiteSpace = WhiteSpace.Normal,
                marginBottom = 8,
                backgroundColor = new Color(0.15f, 0.15f, 0.15f), // Darker background for code
                color = new Color(0.9f, 0.9f, 0.9f), // Lighter text for code
                paddingLeft = 8,
                paddingRight = 8,
                paddingTop = 4,
                paddingBottom = 4,
                borderTopWidth = 1,
                borderBottomWidth = 1,
                borderLeftWidth = 1,
                borderRightWidth = 1,
                borderTopColor = new Color(0.3f, 0.3f, 0.3f),
                borderBottomColor = new Color(0.3f, 0.3f, 0.3f),
                borderLeftColor = new Color(0.3f, 0.3f, 0.3f),
                borderRightColor = new Color(0.3f, 0.3f, 0.3f)
            }
        };
        
        codeBlock.SetValueWithoutNotify(content);
        codeBlock.isReadOnly = true;
        
        conversationScrollView.Add(fileHeader);
        conversationScrollView.Add(codeBlock);
        
        // Scroll to bottom
        EditorApplication.delayCall += ScrollToBottom;
    }

    private void OnFocusInQueryField(FocusInEvent evt)
    {
        if (queryField.value == PLACEHOLDER_TEXT)
        {
            queryField.SetValueWithoutNotify(string.Empty);
            queryField.RemoveFromClassList("placeholder-text");
        }
    }

    private void OnFocusOutQueryField(FocusOutEvent evt)
    {
        if (string.IsNullOrEmpty(queryField.value))
        {
            queryField.SetValueWithoutNotify(PLACEHOLDER_TEXT);
            queryField.AddToClassList("placeholder-text");
        }
    }

    private string ParseOpenAIReply(string json)
    {
        try
        {
            // More robust parsing
            int contentStartIndex = json.IndexOf("\"content\":");
            if (contentStartIndex == -1)
            {
                Debug.LogError("Could not find content in response: " + json);
                return "<No content found in response>";
            }

            // Find the opening quote after "content":
            int openQuoteIndex = json.IndexOf('"', contentStartIndex + 10);
            if (openQuoteIndex == -1) return "<Invalid JSON format>";

            // Find the closing quote (accounting for escaped quotes)
            int closeQuoteIndex = openQuoteIndex + 1;
            bool foundClosingQuote = false;
            
            while (closeQuoteIndex < json.Length)
            {
                if (json[closeQuoteIndex] == '"' && json[closeQuoteIndex - 1] != '\\')
                {
                    foundClosingQuote = true;
                    break;
                }
                closeQuoteIndex++;
            }
            
            if (!foundClosingQuote) return "<Invalid JSON format>";

            string extracted = json.Substring(openQuoteIndex + 1, closeQuoteIndex - openQuoteIndex - 1);
            extracted = extracted
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
                
            return extracted;
        }
        catch (Exception ex)
        {
            Debug.LogError("Error parsing OpenAI response: " + ex.Message);
            Debug.LogError("JSON: " + json);
            return "<Error parsing response>";
        }
    }

    private string ParseClaudeReply(string json)
    {
        try
        {
            // Claude's response format is different from OpenAI
            int contentStartIndex = json.IndexOf("\"content\":");
            if (contentStartIndex == -1)
            {
                Debug.LogError("Could not find content in Claude response: " + json);
                return "<No content found in response>";
            }

            // The content is within the messages array in Claude's response
            int openBracketIndex = json.IndexOf('[', contentStartIndex);
            if (openBracketIndex == -1) return "<Invalid JSON format>";

            // Find the opening quote for content
            int openQuoteIndex = json.IndexOf('"', openBracketIndex + 10);
            if (openQuoteIndex == -1) return "<Invalid JSON format>";

            // Find the closing quote (accounting for escaped quotes)
            int closeQuoteIndex = openQuoteIndex + 1;
            bool foundClosingQuote = false;
            
            while (closeQuoteIndex < json.Length)
            {
                if (json[closeQuoteIndex] == '"' && json[closeQuoteIndex - 1] != '\\')
                {
                    foundClosingQuote = true;
                    break;
                }
                closeQuoteIndex++;
            }
            
            if (!foundClosingQuote) return "<Invalid JSON format>";

            string extracted = json.Substring(openQuoteIndex + 1, closeQuoteIndex - openQuoteIndex - 1);
            extracted = extracted
                .Replace("\\n", "\n")
                .Replace("\\r", "\r")
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\");
                
            return extracted;
        }
        catch (Exception ex)
        {
            Debug.LogError("Error parsing Claude response: " + ex.Message);
            Debug.LogError("JSON: " + json);
            return "<Error parsing response>";
        }
    }

    private string EscapeJson(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t")
            .Replace("\b", "\\b")
            .Replace("\f", "\\f");
    }

    // Add a method to clear the script context
    private void ClearScriptContext()
    {
        lastLoadedScriptPath = null;
        lastLoadedScriptContent = null;
        AddMessageToHistory("System", "Script context cleared.");
    }

    // Add a method to clear conversation history
    private void ClearConversationHistory()
    {
        if (currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count)
        {
            chatSessions[currentSessionIndex].Messages.Clear();
            if (conversationScrollView != null)
            {
                conversationScrollView.Clear();
            }
            AddMessageToHistory("System", "Conversation history cleared.");
        }
    }

    private void RestoreCurrentSession()
    {
        // Guard clause: ensure UI is initialized 
        if (conversationScrollView == null)
        {
            Debug.LogWarning("[Undo System] Skipping RestoreCurrentSession: conversationScrollView is null");
            return;
        }
        
        // Add debug logging to help track down the issue
        Debug.Log("[Undo System] RestoreCurrentSession called");
        
        if (currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count)
        {
            var currentSession = chatSessions[currentSessionIndex];
            Debug.Log($"[Undo System] Restoring session: {currentSession.Name} with {currentSession.Messages.Count} messages");
            
            // Clear the conversation view
            conversationScrollView.Clear();
            
            // Restore session state
            lastLoadedScriptPath = currentSession.LastLoadedScriptPath;
            lastLoadedScriptContent = currentSession.LastLoadedScriptContent;
            lastLoadedScenePath = currentSession.LastLoadedScenePath;
            isSceneLoaded = currentSession.IsSceneLoaded;
            
            // Restore messages
            foreach (var message in currentSession.Messages)
            {
                if (message.IsFileContent)
                {
                    // Restore file content display
                    AddFileContentToHistoryWithoutSaving(message.FileName, message.Content);
                }
                else
                {
                    // Restore regular message
                    AddMessageToHistoryWithoutSaving(message.Sender, message.Content);
                }
            }

            // If this is a new session with no messages, add the welcome message
            if (currentSession.Messages.Count == 0)
            {
                AddWelcomeMessage();
            }
           
            
            // Scroll to bottom
            EditorApplication.delayCall += ScrollToBottom;
        }
    }

    // Handle Enter key in the query field
    private void OnQueryFieldKeyDown(KeyDownEvent evt)
    {
        if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
        {
            // Don't send if Shift is held (allows for newlines)
            if (!evt.shiftKey)
            {
                evt.StopPropagation();
                OnSendButtonClicked();
            }
        }
    }

    // Helper method to scroll to the bottom of the conversation
    private void ScrollToBottom()
    {
        if (conversationScrollView != null)
        {
            float fullHeight = conversationScrollView.contentContainer.layout.height;
            conversationScrollView.scrollOffset = new Vector2(0, fullHeight);
        }
    }

    // Override OnEnable to ensure we restore state when the window is enabled
    private void OnEnable()
    {
        Debug.Log("[Undo System] ChatbotEditorWindow OnEnable called");
        
        // Initialize logging system
        InitializeLogging();
        
        // Check if a scene is loaded
        var activeScene = EditorSceneManager.GetActiveScene();
        isSceneLoaded = activeScene.isLoaded && !string.IsNullOrEmpty(activeScene.path);
        lastLoadedScenePath = activeScene.path;
        
        Debug.Log($"[Undo System] OnEnable - Active scene: {activeScene.name}");
        Debug.Log($"[Undo System] OnEnable - Scene path: {activeScene.path}");
        Debug.Log($"[Undo System] OnEnable - Scene is loaded: {activeScene.isLoaded}");
        Debug.Log($"[Undo System] OnEnable - isSceneLoaded flag: {isSceneLoaded}");
        
        // Load saved chat sessions - the UI restoration will happen in CreateGUI
        LoadChatSessionsFromEditorPrefs();
        // Load persisted file snapshots
        LoadFileSnapshotsFromEditorPrefs();
        
        // Subscribe to compilation events
        CompilationPipeline.compilationStarted += OnCompilationStarted;
        CompilationPipeline.compilationFinished += OnCompilationFinished;
        
        // Subscribe to undo/redo events to update the UI
        Undo.undoRedoPerformed += OnUndoRedoPerformed;
    }
    
    // Override OnDisable to save any state before the window is disabled
    private void OnDisable()
    {
        // Save the current session state and all sessions to EditorPrefs
        SaveChatSessionsToEditorPrefs();
        // Save persisted file snapshots
        SaveFileSnapshotsToEditorPrefs();
        
        // Unregister from compilation events
        CompilationPipeline.compilationStarted -= OnCompilationStarted;
        CompilationPipeline.compilationFinished -= OnCompilationFinished;
        
        // Clean up scene analysis components
        SceneAnalysisIntegration.Cleanup();
        
        // Unsubscribe to prevent memory leaks
        Undo.undoRedoPerformed -= OnUndoRedoPerformed;
        EditorApplication.update -= OnEditorUpdate;

        // Transmit any remaining logs
        if (isRemoteLoggingEnabled && pendingLogs.Count > 0)
        {
            TransmitPendingLogs();
        }
    }

    private void OnCompilationStarted(object obj)
    {
        // Save state before compilation starts
        SaveChatSessionsToEditorPrefs();
    }

    private void OnCompilationFinished(object obj)
    {
        // Load state after compilation finishes
        LoadChatSessionsFromEditorPrefs();
        
        // Restore UI if needed
        if (rootVisualElement != null && conversationScrollView != null)
        {
            RestoreCurrentSession();
        }
        
        // Scroll to bottom after compilation finishes
        EditorApplication.delayCall += ScrollToBottom;
    }

    // Combined scene analysis method
    private void OnSceneAnalysisClicked()
    {
        // Create a dropdown menu with scene analysis options
        var menu = new GenericMenu();
        
        menu.AddItem(new GUIContent("Scene Structure"), false, () => {
            string sceneStructure = SceneAnalysisIntegration.GetSceneStructureSummary();
            AddMessageToHistory("You", "Analyze the current scene structure");
            AddMessageToHistory("System", sceneStructure);
        });
        
        menu.AddItem(new GUIContent("Spatial Analysis"), false, () => {
            string spatialInfo = SceneAnalysisIntegration.GetSpatialInformation();
            AddMessageToHistory("You", "Perform spatial analysis on the scene");
            AddMessageToHistory("System", spatialInfo);
        });
        
        menu.AddItem(new GUIContent("Complete Analysis"), false, () => {
            string sceneStructure = SceneAnalysisIntegration.GetSceneStructureSummary();
            string spatialInfo = SceneAnalysisIntegration.GetSpatialInformation();
            AddMessageToHistory("You", "Perform complete scene analysis");
            AddMessageToHistory("System", "Scene Structure:\n" + sceneStructure + "\n\nSpatial Analysis:\n" + spatialInfo);
        });
        
        menu.ShowAsContext();
    }

    // Add a method to handle scene context in queries
    public string AddSceneContextToQuery(string query)
    {
        if (!includeSceneContext)
            return query;
        
        string sceneContext = SceneAnalysisIntegration.GetSceneStructureSummary();
        return $"[Scene Context]\n{sceneContext}\n\n[User Query]\n{query}";
    }

    private void OnBrowseScenesClicked()
    {
        // Create a dropdown menu with scene files
        var menu = new GenericMenu();
        
        // Ensure the Scenes folder exists
        if (!Directory.Exists(SCENES_FOLDER))
        {
            Directory.CreateDirectory(SCENES_FOLDER);
            AssetDatabase.Refresh();
        }
        
        // Get all Unity scene files in the Scenes folder
        string[] sceneFiles = Directory.GetFiles(SCENES_FOLDER, "*.unity", SearchOption.AllDirectories);
        
        foreach (string filePath in sceneFiles)
        {
            string relativePath = filePath.Replace("\\", "/"); // Normalize path for Unity
            menu.AddItem(new GUIContent(relativePath), false, () => LoadSceneFile(relativePath));
        }
        
        menu.ShowAsContext();
    }

    private void LoadSceneFile(string scenePath)
    {
        Debug.Log($"[Scene Edit] Loading scene file: {scenePath}");
        
        if (string.IsNullOrEmpty(scenePath))
        {
            Debug.LogError("[Scene Edit] Scene path is null or empty");
            return;
        }

        // Save the current scene if one is loaded
        if (!string.IsNullOrEmpty(lastLoadedScenePath))
        {
            SaveCurrentScene();
        }

        // Load the new scene
        var scene = EditorSceneManager.OpenScene(scenePath);
        lastLoadedScenePath = scenePath;
        isSceneLoaded = true;  // Set this to true when a scene is loaded
        
        Debug.Log($"[Scene Edit] Scene loaded successfully. isSceneLoaded={isSceneLoaded}");
        Debug.Log($"[Scene Edit] Scene name: {scene.name}");
        Debug.Log($"[Scene Edit] Scene path: {scene.path}");
        
        // Update the current session with the new scene information
        if (currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count)
        {
            chatSessions[currentSessionIndex].LastLoadedScenePath = scenePath;
            chatSessions[currentSessionIndex].IsSceneLoaded = true;
            SaveChatSessionsToEditorPrefs();
        }

        // Add a message to the conversation about the loaded scene
        AddMessageToHistory("System", $"Loaded scene: {Path.GetFileNameWithoutExtension(scenePath)}");
    }

    // Add a method to create a new GameObject in the scene
    private GameObject CreateGameObject(string name, Vector3 position)
    {
        if (!isSceneLoaded)
        {
            AddMessageToHistory("System", "No scene is currently loaded.");
            return null;
        }
        
        GameObject newObject = new GameObject(name);
        newObject.transform.position = position;
        
        AddMessageToHistory("System", $"Created new GameObject '{name}' at position {position}");
        
        // Mark the scene as dirty
        EditorSceneManager.MarkSceneDirty(
            EditorSceneManager.GetActiveScene());
        
        return newObject;
    }

    // Add a method to save the current scene
    private void SaveCurrentScene()
    {
        if (!isSceneLoaded)
        {
            AddMessageToHistory("System", "No scene is currently loaded.");
            return;
        }
        
        var currentScene = EditorSceneManager.GetActiveScene();
        
        if (string.IsNullOrEmpty(currentScene.path))
        {
            // This is a new scene that hasn't been saved yet
            string newPath = EditorUtility.SaveFilePanel(
                "Save Scene",
                SCENES_FOLDER,
                "NewScene.unity",
                "unity");
                
            if (string.IsNullOrEmpty(newPath))
            {
                // User cancelled the save dialog
                return;
            }
            
            // Convert to a project-relative path
            if (newPath.StartsWith(Application.dataPath))
            {
                newPath = "Assets" + newPath.Substring(Application.dataPath.Length);
            }
            
            EditorSceneManager.SaveScene(currentScene, newPath);
            lastLoadedScenePath = newPath;
        }
        else
        {
            // Save the existing scene
            EditorSceneManager.SaveScene(currentScene);
        }
        
        AddMessageToHistory("System", $"Scene saved to {currentScene.path}");
    }

    // Add a method to create a new scene
    private void CreateNewScene()
    {
        // Check if there are unsaved changes in the current scene
        if (EditorSceneManager.GetActiveScene().isDirty)
        {
            if (!EditorUtility.DisplayDialog("Unsaved Changes", 
                "The current scene has unsaved changes. Do you want to proceed and lose those changes?", 
                "Yes", "No"))
            {
                return;
            }
        }
        
        // Create a new empty scene
        EditorSceneManager.NewScene(
            NewSceneSetup.DefaultGameObjects,
            NewSceneMode.Single);
        
        isSceneLoaded = true;
        lastLoadedScenePath = "";
        
        AddMessageToHistory("System", "Created a new scene with default game objects.");
    }

    private void OnSessionChanged(ChangeEvent<string> evt)
    {
        // Find the index of the selected session
        int newIndex = chatSessions.FindIndex(s => s.Name == evt.newValue);
        if (newIndex >= 0 && newIndex < chatSessions.Count)
        {
            // Save current session state
            SaveCurrentSessionState();
            
            // Switch to the new session
            currentSessionIndex = newIndex;
            
            // Update the chat name label in the UI
            var chatNameLabel = rootVisualElement.Q<Label>("chatNameLabel");
            if (chatNameLabel != null)
            {
                chatNameLabel.text = chatSessions[currentSessionIndex].Name;
            }
            
            // Restore the selected session
            RestoreCurrentSession();
            
            // Save the updated state
            SaveChatSessionsToEditorPrefs();
        }
    }

    private void OnNewChatClicked()
    {
        // Save current session state
        SaveCurrentSessionState();
        
        // Create a new chat session
        var newSession = new ChatSession("New Chat");
        chatSessions.Add(newSession);
        
        // Switch to the new session
        currentSessionIndex = chatSessions.Count - 1;
        
        // Update the chat name label in the UI
        var chatNameLabel = rootVisualElement.Q<Label>("chatNameLabel");
        if (chatNameLabel != null)
        {
            chatNameLabel.text = "New Chat";
        }
        
        // Clear the conversation view and restore (which will be empty for a new chat)
        RestoreCurrentSession();
        
        // Add a welcome message
        AddMessageToHistory("System", $"Started new chat session: {newSession.Name}");
        
        // Save the updated state
        SaveChatSessionsToEditorPrefs();
    }

    // Add a method to display the welcome message with example prompts
    private void AddWelcomeMessage()
    {
        string welcomeMessage = PromptRecommender.GetWelcomeMessage();
        AddMessageToHistory("XeleR", welcomeMessage);
    }

    private void SaveCurrentSessionState()
    {
        if (currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count)
        {
            var currentSession = chatSessions[currentSessionIndex];
            
            // Update the session with current state
            currentSession.LastLoadedScriptPath = lastLoadedScriptPath;
            currentSession.LastLoadedScriptContent = lastLoadedScriptContent;
            currentSession.LastLoadedScenePath = lastLoadedScenePath;
            currentSession.IsSceneLoaded = isSceneLoaded;
            
            // Messages are already saved as they're added
        }
    }

    // Add a method to rename the current chat session
    private void RenameCurrentSession(string newName, bool silent = false)
    {
        if (currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count)
        {
            chatSessions[currentSessionIndex].Name = newName;
            
            // Update the chat name label in the UI
            var chatNameLabel = rootVisualElement.Q<Label>("chatNameLabel");
            if (chatNameLabel != null)
            {
                chatNameLabel.text = newName;
            }
            
            if (!silent)
            {
                AddMessageToHistory("System", $"Renamed session to: {newName}");
            }
            
            // Save the updated state
            SaveChatSessionsToEditorPrefs();
        }
    }

    // Add a method to delete the current chat session
    private void DeleteCurrentSession()
    {
        if (chatSessions.Count <= 1)
        {
            // Don't delete the last session, just clear it
            ClearConversationHistory();
            return;
        }
        
        if (currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count)
        {
            // Remove the current session
            chatSessions.RemoveAt(currentSessionIndex);
            
            // Adjust the current index if needed
            if (currentSessionIndex >= chatSessions.Count)
            {
                currentSessionIndex = chatSessions.Count - 1;
            }
            
            // Update the chat name label in the UI
            var chatNameLabel = rootVisualElement.Q<Label>("chatNameLabel");
            if (chatNameLabel != null)
            {
                chatNameLabel.text = chatSessions[currentSessionIndex].Name;
            }
            
            // Restore the new current session
            RestoreCurrentSession();
            
            AddMessageToHistory("System", "Chat session deleted");
            
            // Save the updated state
            SaveChatSessionsToEditorPrefs();
        }
    }

    // Helper method to restore messages without adding them to history again
    private void AddMessageToHistoryWithoutSaving(string sender, string message)
    {
        // Create a container for the message
        var messageContainer = new VisualElement
        {
            style =
            {
                marginBottom = 8,
                paddingLeft = 4,
                paddingRight = 4
            }
        };

        // Add sender name with bold styling
        var senderLabel = new Label
        {
            style =
            {
                unityFontStyleAndWeight = FontStyle.Bold,
                marginBottom = 2
            },
            text = sender + ":"
        };
        messageContainer.Add(senderLabel);

        // Process message for markdown if it's from the AI
        if (sender == "XeleR")
        {
            // Create a content container for the message
            var contentContainer = new VisualElement
            {
                style =
                {
                    marginLeft = 4,
                    marginRight = 4
                }
            };
            
            // Use the markdown renderer to format the message
            var formattedContent = MarkdownRenderer.RenderMarkdown(message);
            contentContainer.Add(formattedContent);
            messageContainer.Add(contentContainer);
            
            // Process code blocks separately
            ProcessCodeBlocksInMessage(message);
        }
        else
        {
            // For non-AI messages, just use a simple label
            var contentLabel = new Label
            {
                style =
                {
                    whiteSpace = WhiteSpace.Normal,
                    marginLeft = 4
                },
                text = message
            };
            messageContainer.Add(contentLabel);
        }

        conversationScrollView.Add(messageContainer);
    }

    // Add methods to save and load chat sessions from EditorPrefs
    private void SaveChatSessionsToEditorPrefs()
    {
        try
        {
            // Save current session state first
            SaveCurrentSessionState();
            
            // Serialize the chat sessions to JSON
            string sessionsJson = JsonConvert.SerializeObject(new ChatSessionsWrapper { Sessions = chatSessions });
            
            // Store in EditorPrefs
            EditorPrefs.SetString(CHAT_SESSIONS_KEY, sessionsJson);
            EditorPrefs.SetInt(CURRENT_SESSION_INDEX_KEY, currentSessionIndex);
            
            // Log success
            Debug.Log("Chat sessions saved to EditorPrefs");
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error saving chat sessions: {ex.Message}");
        }
    }
    
    private void LoadChatSessionsFromEditorPrefs()
    {
        try
        {
            // Check if we have saved sessions
            if (EditorPrefs.HasKey(CHAT_SESSIONS_KEY))
            {
                string sessionsJson = EditorPrefs.GetString(CHAT_SESSIONS_KEY);
                
                // Deserialize the chat sessions
                var wrapper = JsonConvert.DeserializeObject<ChatSessionsWrapper>(sessionsJson);
                if (wrapper != null && wrapper.Sessions != null && wrapper.Sessions.Count > 0)
                {
                    chatSessions = wrapper.Sessions;
                    
                    // Load current session index
                    if (EditorPrefs.HasKey(CURRENT_SESSION_INDEX_KEY))
                    {
                        currentSessionIndex = EditorPrefs.GetInt(CURRENT_SESSION_INDEX_KEY);
                        
                        // Ensure index is valid
                        if (currentSessionIndex >= chatSessions.Count)
                        {
                            currentSessionIndex = chatSessions.Count - 1;
                        }
                    }
                    
                    Debug.Log($"Loaded {chatSessions.Count} chat sessions from EditorPrefs");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Error loading chat sessions: {ex.Message}");
            
            // If loading fails, start with a fresh session
            chatSessions = new List<ChatSession> { new ChatSession("Chat 1") };
            currentSessionIndex = 0;
        }
    }
    
    // Wrapper class for serialization
    [Serializable]
    private class ChatSessionsWrapper
    {
        public List<ChatSession> Sessions;
    }

    // Add this method to handle the button click
    private void OnContextMenuButtonClicked()
    {
        if (isContextMenuOpen)
        {
            CloseContextMenu();
        }
        else
        {
            OpenContextMenu();
        }
    }

    private void OpenContextMenu()
    {
        // Position the dropdown above the @ button
        var buttonRect = contextMenuButton.worldBound; // Get button's screen position/size

        // Calculate the height of the dropdown based on the number of items
        // Ensure this calculation accurately reflects the content.
        float dropdownHeight = (2 * (24 + 4)) + 10; // Current calculation assumes 2 items

        // Calculate position relative to the button
        contextMenuDropdown.style.left = buttonRect.x; // Align left edges

        // Position the dropdown *above* the button
        // Increased the gap from 15 to 25 to move it even further up
        contextMenuDropdown.style.top = buttonRect.y - dropdownHeight - 25; // Position even further above

        contextMenuDropdown.style.width = 200;

        // Clear existing items
        contextMenuDropdown.Clear();

        // Add only the file browsing options
        AddContextMenuItem("Browse Scripts", OnBrowseScriptsClicked);
        AddContextMenuItem("Browse Scenes", OnBrowseScenesClicked);

        // Show the dropdown
        contextMenuDropdown.style.display = DisplayStyle.Flex;
        isContextMenuOpen = true;

        // Add a click event handler to the root to close the menu when clicking outside
        rootVisualElement.RegisterCallback<MouseDownEvent>(OnClickOutsideContextMenu);
    }

    private void CloseContextMenu()
    {
        contextMenuDropdown.style.display = DisplayStyle.None;
        isContextMenuOpen = false;
        rootVisualElement.UnregisterCallback<MouseDownEvent>(OnClickOutsideContextMenu);
    }

    private void OnClickOutsideContextMenu(MouseDownEvent evt)
    {
        // Check if the click is outside the dropdown
        if (!contextMenuDropdown.worldBound.Contains(evt.mousePosition))
        {
            CloseContextMenu();
        }
    }

    // Modify the AddContextMenuItem method to update the display after tracking clicks
    private void AddContextMenuItem(string label, Action clickAction)
    {
        var item = new Button(() => {
            // Just execute the original action without tracking menu clicks
            clickAction();
        }) { text = label };
        
        item.style.height = 24;
        item.style.width = 190;
        item.style.marginLeft = 5;
        item.style.marginRight = 5;
        item.style.marginTop = 2;
        item.style.marginBottom = 2;
        item.style.backgroundColor = new Color(0.25f, 0.25f, 0.25f);
        
        contextMenuDropdown.Add(item);
    }

    // Modify the UpdateSelectedFilesDisplay method to add close buttons to each file box
    private void UpdateSelectedFilesDisplay()
    {
        // Ensure the container is initialized (it should be by CreateGUI)
        if (pickedContextItemsContainer == null) return;

        // First, clear any existing option boxes from the new container
        var existingBoxes = pickedContextItemsContainer.Query(className: "selected-file-box").ToList();
        foreach (var box in existingBoxes)
        {
            pickedContextItemsContainer.Remove(box);
        }
        
        // Display all selected files
        for (int i = 0; i < selectedFiles.Length; i++)
        {
            string filePath = selectedFiles[i];
            string fileName = Path.GetFileName(filePath);
            bool isScene = filePath.EndsWith(".unity");
            
            var fileBox = new VisualElement();
            fileBox.AddToClassList("selected-file-box");
            fileBox.style.flexDirection = FlexDirection.Row; // Make it horizontal to fit the close button
            
            // Use different colors for scripts vs scenes
            if (isScene)
                fileBox.style.backgroundColor = new Color(0.5f, 0.3f, 0.7f); // Purple for scenes
            else
                fileBox.style.backgroundColor = new Color(0.3f, 0.5f, 0.7f); // Blue for scripts
            
            fileBox.style.borderTopLeftRadius = 3;
            fileBox.style.borderTopRightRadius = 3;
            fileBox.style.borderBottomLeftRadius = 3;
            fileBox.style.borderBottomRightRadius = 3;
            fileBox.style.paddingLeft = 4;
            fileBox.style.paddingRight = 2; // Reduced right padding to fit close button
            fileBox.style.paddingTop = 2;
            fileBox.style.paddingBottom = 2;
            fileBox.style.marginLeft = 4;
            fileBox.style.height = 18;
            
            // Create a container for the file name label
            var labelContainer = new VisualElement();
            labelContainer.style.flexGrow = 1; // Take up available space
            
            // Make the file box clickable to reload the file
            int index = i; // Capture the index for the click handler
            labelContainer.RegisterCallback<ClickEvent>((evt) => {
                if (isScene)
                    LoadSceneFile(selectedFiles[index]);
                else
                    LoadScriptFile(selectedFiles[index]);
            });
            
            var fileLabel = new Label(fileName);
            fileLabel.style.fontSize = 10;
            fileLabel.style.color = Color.white;
            fileLabel.style.unityTextAlign = TextAnchor.MiddleLeft;
            
            labelContainer.Add(fileLabel);
            fileBox.Add(labelContainer);
            
            // Add a close button
            var closeButton = new Button(() => RemoveSelectedFile(index)) { text = "×" };
            closeButton.AddToClassList("file-close-button");
            closeButton.style.width = 14;
            closeButton.style.height = 14;
            closeButton.style.fontSize = 10;
            closeButton.style.paddingLeft = 0;
            closeButton.style.paddingRight = 0;
            closeButton.style.paddingTop = 0;
            closeButton.style.paddingBottom = 0;
            closeButton.style.marginLeft = 2;
            closeButton.style.marginRight = 0;
            closeButton.style.marginTop = 0;
            closeButton.style.marginBottom = 0;
            closeButton.style.backgroundColor = new Color(0.7f, 0.3f, 0.3f); // Red for close button
            closeButton.style.color = Color.white;
            closeButton.style.borderTopLeftRadius = 2;
            closeButton.style.borderTopRightRadius = 2;
            closeButton.style.borderBottomLeftRadius = 2;
            closeButton.style.borderBottomRightRadius = 2;
            
            fileBox.Add(closeButton);
            pickedContextItemsContainer.Add(fileBox); // NEW LINE: Add to the new container
        }
    }

    // Add a method to remove a file from the selectedFiles array
    private void RemoveSelectedFile(int index)
    {
        if (index < 0 || index >= selectedFiles.Length)
            return;
        
        // Create a new array without the selected file
        string[] newSelectedFiles = new string[selectedFiles.Length - 1];
        
        // Copy all elements except the one at the specified index
        for (int i = 0, j = 0; i < selectedFiles.Length; i++)
        {
            if (i != index)
            {
                newSelectedFiles[j++] = selectedFiles[i];
            }
        }
        
        // Update the array
        selectedFiles = newSelectedFiles;
        
        // Update the UI
        UpdateSelectedFilesDisplay();
    }

    // Helper method to standardize button styling
    private void StyleButton(Button button)
    {
        button.style.height = 22;
        button.style.marginRight = 4;
        button.style.paddingLeft = 8;
        button.style.paddingRight = 8;
        button.style.backgroundColor = new Color(0.25f, 0.25f, 0.25f);
        button.style.color = Color.white;
        button.style.borderTopLeftRadius = 4;
        button.style.borderTopRightRadius = 4;
        button.style.borderBottomLeftRadius = 4;
        button.style.borderBottomRightRadius = 4;
        button.style.borderTopWidth = 1;
        button.style.borderBottomWidth = 1;
        button.style.borderLeftWidth = 1;
        button.style.borderRightWidth = 1;
        button.style.borderTopColor = new Color(0.4f, 0.4f, 0.4f);
        button.style.borderBottomColor = new Color(0.4f, 0.4f, 0.4f);
        button.style.borderLeftColor = new Color(0.4f, 0.4f, 0.4f);
        button.style.borderRightColor = new Color(0.4f, 0.4f, 0.4f);
    }

    private void AutoNameChat(string userMessage)
    {
        if (currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count)
        {
            ChatSession currentSession = chatSessions[currentSessionIndex];
            if (currentSession.NeedsAutoNaming)
            {
                string namingPrompt = $"Generate a short, concise 2-4 word name for a chat based on this first message. Respond with ONLY the name, no additional text, quotes, or punctuation. First message: '{userMessage}'";
                string model = availableModels[selectedModelIndex].Name;
                string provider = availableModels[selectedModelIndex].Provider;
                
                if (provider == "OpenAI")
                {
                    GenerateNameWithOpenAI(namingPrompt, model);
                }
                else if (provider == "Claude")
                {
                    GenerateNameWithClaude(namingPrompt, model);
                }
            }
        }
    }

    private async void GenerateNameWithOpenAI(string namingPrompt, string model)
    {
        try
        {
            string openAiKey = ApiKeyManager.GetKey(ApiKeyManager.OPENAI_KEY);
            if (string.IsNullOrEmpty(openAiKey))
            {
                Debug.LogWarning("OpenAI API key is not set.");
                return;
            }
            
            string apiUrl = "https://api.openai.com/v1/chat/completions";
            
            using (var client = new UnityWebRequest(apiUrl, "POST"))
            {
                // Construct the request JSON
                string requestJson = "{" +
                    $"\"model\": \"{model}\"," +
                    "\"messages\": [" +
                    "{" +
                    "\"role\": \"system\"," +
                    "\"content\": \"You generate concise 2-4 word chat names.\"" +
                    "}," +
                    "{" +
                    "\"role\": \"user\"," +
                    $"\"content\": \"{EscapeJson(namingPrompt)}\"" +
                    "}" +
                    "]," +
                    "\"temperature\": 0.7," +
                    "\"max_tokens\": 30" +
                    "}";
                
                byte[] bodyRaw = Encoding.UTF8.GetBytes(requestJson);
                client.uploadHandler = new UploadHandlerRaw(bodyRaw);
                client.downloadHandler = new DownloadHandlerBuffer();
                client.SetRequestHeader("Content-Type", "application/json");
                client.SetRequestHeader("Authorization", $"Bearer {openAiKey}");
                
                // Send the request
                await client.SendWebRequest();
                
                if (client.result == UnityWebRequest.Result.Success)
                {
                    string jsonResponse = client.downloadHandler.text;
                    string generatedName = ParseOpenAIReply(jsonResponse);
                    
                    generatedName = CleanupChatName(generatedName);
                    
                    // Update on the main thread
                    EditorApplication.delayCall += () => {
                        if (currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count)
                        {
                            RenameCurrentSession(generatedName, true);
                            
                            // Turn off auto-naming flag
                            chatSessions[currentSessionIndex].NeedsAutoNaming = false;
                            
                            // Save sessions to EditorPrefs
                            SaveChatSessionsToEditorPrefs();
                        }
                    };
                }
                else
                {
                    Debug.LogError($"Error generating chat name: {client.error}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Exception when generating chat name: {ex.Message}");
        }
    }

    private async void GenerateNameWithClaude(string namingPrompt, string model)
    {
        try
        {
            string claudeKey = ApiKeyManager.GetKey(ApiKeyManager.CLAUDE_KEY);
            if (string.IsNullOrEmpty(claudeKey))
            {
                Debug.LogWarning("Claude API key is not set.");
                return;
            }
            
            string apiUrl = "https://api.anthropic.com/v1/messages";
            
            using (var client = new UnityWebRequest(apiUrl, "POST"))
            {
                // Construct the request JSON
                string requestJson = "{" +
                    $"\"model\": \"{model}\"," +
                    "\"system\": \"You generate concise 2-4 word chat names.\"," +
                    $"\"messages\": [{{\"role\": \"user\", \"content\": \"{EscapeJson(namingPrompt)}\"}}]," +
                    "\"temperature\": 0.7," +
                    "\"max_tokens\": 30" +
                    "}";
                
                byte[] bodyRaw = Encoding.UTF8.GetBytes(requestJson);
                client.uploadHandler = new UploadHandlerRaw(bodyRaw);
                client.downloadHandler = new DownloadHandlerBuffer();
                client.SetRequestHeader("Content-Type", "application/json");
                client.SetRequestHeader("anthropic-version", "2023-06-01");
                client.SetRequestHeader("x-api-key", claudeKey);
                
                // Send the request
                await client.SendWebRequest();
                
                if (client.result == UnityWebRequest.Result.Success)
                {
                    string jsonResponse = client.downloadHandler.text;
                    string generatedName = ParseClaudeReply(jsonResponse);
                    
                    generatedName = CleanupChatName(generatedName);
                    
                    // Update on the main thread
                    EditorApplication.delayCall += () => {
                        if (currentSessionIndex >= 0 && currentSessionIndex < chatSessions.Count)
                        {
                            RenameCurrentSession(generatedName, true);
                            
                            // Turn off auto-naming flag
                            chatSessions[currentSessionIndex].NeedsAutoNaming = false;
                            
                            // Save sessions to EditorPrefs
                            SaveChatSessionsToEditorPrefs();
                        }
                    };
                }
                else
                {
                    Debug.LogError($"Error generating chat name: {client.error}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"Exception when generating chat name: {ex.Message}");
        }
    }

    // Helper method to clean up and format chat names
    private string CleanupChatName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "New Chat";
            
        // Remove any quotes around the name
        name = name.Trim('"', '\'', '`', ' ');
        
        // If name is too long, truncate it
        const int maxLength = 30;
        if (name.Length > maxLength)
        {
            name = name.Substring(0, maxLength - 3) + "...";
        }
        
        // Replace any invalid filename characters
        name = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        
        // If we end up with an empty string, use default
        if (string.IsNullOrWhiteSpace(name))
        {
            return "New Chat";
        }
        
        return name;
    }

    // Helper method to get the full path of a GameObject
    private string GetFullPath(GameObject obj)
    {
        string path = obj.name;
        Transform parent = obj.transform.parent;
        while (parent != null)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }

    // Add method to show chat history dropdown
    private void OnChatHistoryButtonClicked()
    {
        var menu = new GenericMenu();
        
        // Add all chat sessions to the menu
        for (int i = 0; i < chatSessions.Count; i++)
        {
            string chatName = chatSessions[i].Name;
            bool isSelected = i == currentSessionIndex;
            
            // Use a local copy of the index to prevent closure issues
            int sessionIndex = i;
            menu.AddItem(new GUIContent(chatName), isSelected, () => {
                // Switch to the selected chat
                if (sessionIndex != currentSessionIndex)
                {
                    // Save current session state
                    SaveCurrentSessionState();
                    
                    // Switch to the new session
                    currentSessionIndex = sessionIndex;
                    
                    // Update the chat name in the UI
                    var chatNameLabel = rootVisualElement.Q<Label>("chatNameLabel");
                    if (chatNameLabel != null)
                    {
                        chatNameLabel.text = chatSessions[currentSessionIndex].Name;
                    }
                    
                    // Restore the selected session
                    RestoreCurrentSession();
                    
                    // Save the updated state
                    SaveChatSessionsToEditorPrefs();
                }
            });
        }
        
        // Add a separator
        menu.AddSeparator("");
        
        // Add option to create a new chat
        menu.AddItem(new GUIContent("New Chat"), false, OnNewChatClicked);
        
        // Show the menu
        menu.ShowAsContext();
    }

    // Add a method to handle chat name editing
    private void CreateChatNameEditField(Label chatNameLabel, VisualElement container)
    {
        // Check if an edit field already exists
        if (container.Q<TextField>("chatNameEditField") != null)
            return;
            
        // Store the original name in case we need to revert
        string originalName = chatNameLabel.text;
            
        // Create a text field with the current chat name
        var editField = new TextField
        {
            value = originalName,
            style =
            {
                unityFontStyleAndWeight = FontStyle.Bold,
                fontSize = 14,
                width = Mathf.Max(150, chatNameLabel.resolvedStyle.width + 40), // Make it wide enough
                marginLeft = chatNameLabel.resolvedStyle.marginLeft,
                marginRight = chatNameLabel.resolvedStyle.marginRight
            },
            name = "chatNameEditField"
        };
        
        // Hide the original label
        chatNameLabel.style.display = DisplayStyle.None;
        
        // Add the edit field to the container
        container.Add(editField);
        
        // Use delayCall to ensure UI has updated before focusing
        EditorApplication.delayCall += () =>
        {
            // Focus the text field and select all text
            editField.Focus();
            editField.SelectAll();
        };
        
        // Handle Enter key press to save changes
        editField.RegisterCallback<KeyDownEvent>(evt => {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                ApplyChatNameEdit(editField, chatNameLabel, container, originalName);
                evt.StopPropagation();
            }
            else if (evt.keyCode == KeyCode.Escape)
            {
                // Cancel editing and revert
                chatNameLabel.style.display = DisplayStyle.Flex;
                container.Remove(editField);
                evt.StopPropagation();
            }
        });
        
        // Handle focus out to save changes
        editField.RegisterCallback<FocusOutEvent>(evt => {
            ApplyChatNameEdit(editField, chatNameLabel, container, originalName);
        });
    }
    
    private void ApplyChatNameEdit(TextField editField, Label chatNameLabel, VisualElement container, string originalName)
    {
        // Get the new name from the text field
        string newName = editField.value.Trim();
        
        // Only update if the name is not empty and different from original
        if (!string.IsNullOrWhiteSpace(newName) && newName != originalName)
        {
            // Clean up the name
            newName = CleanupChatName(newName);
            
            // Update the UI and save
            chatNameLabel.text = newName;
            RenameCurrentSession(newName);
        }
        
        // Show the label again
        chatNameLabel.style.display = DisplayStyle.Flex;
        
        // Remove the edit field
        container.Remove(editField);
    }

    // Add method to handle undo button click
    private void OnUndoClicked()
    {
        Debug.Log("[Undo System] Undo button clicked");

        if (fileSnapshots.Count > 0)
        {
            var snapshot = fileSnapshots[fileSnapshots.Count - 1];
            try
            {
                if (snapshot.IsNewFile)
                {
                    // Delete the new file
                    if (File.Exists(snapshot.FilePath))
                    {
                        File.Delete(snapshot.FilePath);
                        AssetDatabase.Refresh();
                        AddMessageToHistory("System", $"Undo: deleted new file '{Path.GetFileName(snapshot.FilePath)}'");
                    }
                }
                else
                {
                    // Restore existing file contents
                    File.WriteAllText(snapshot.FilePath, snapshot.Contents);
                    AssetDatabase.Refresh();
                    AddMessageToHistory("System", $"Undo: reverted code changes in '{Path.GetFileName(snapshot.FilePath)}'");
                }

                // Remove that snapshot from the stack
                fileSnapshots.RemoveAt(fileSnapshots.Count - 1);

                // Log the undo action
                LogAction("undo", $"File: {Path.GetFileName(snapshot.FilePath)}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Undo System] Failed to revert file {snapshot.FilePath}: {ex.Message}");
                AddMessageToHistory("System", $"Error undoing code in '{Path.GetFileName(snapshot.FilePath)}': {ex.Message}");
            }
        }
        else
        {
            // No code snapshots left → fall back to scene undo
            AddMessageToHistory("System", "Undo requested: revert last scene modification");
            Undo.PerformUndo();
            LogAction("undo", "Scene: " + EditorSceneManager.GetActiveScene().name);
        }

        // Refresh the window just in case
        Repaint();
    }

    // Determines if a user query has creation intent
    private bool HasCreationIntent(string userQuery)
    {
        // Normalize the query for consistent matching
        string normalizedQuery = userQuery.ToLower().Trim();
        
        // Keywords that suggest creation intent
        string[] creationKeywords = new string[] 
        { 
            "create", "add", "make", "place", "spawn", "generate", "put", 
            "instantiate", "build", "construct", "new" 
        };
        
        // Check if any creation keywords are present
        foreach (string keyword in creationKeywords)
        {
            // Check for keyword as a whole word (not part of another word)
            if (Regex.IsMatch(normalizedQuery, $@"\b{keyword}\b"))
            {
                // Also check for primitive objects to increase confidence
                string[] primitiveTypes = new string[] 
                { 
                    "cube", "sphere", "cylinder", "plane", "capsule", "quad" 
                };
                
                foreach (string primitive in primitiveTypes)
                {
                    if (normalizedQuery.Contains(primitive))
                    {
                        return true;
                    }
                }
                
                // If we have very clear creation phrases, return true even without primitives
                if (normalizedQuery.Contains($"{keyword} object") || 
                    normalizedQuery.Contains($"{keyword} a") || 
                    normalizedQuery.Contains($"{keyword} an"))
                {
                    return true;
                }
            }
        }
        
        return false;
    }
    
    // Attempts to parse and create an object from the user query
    private bool TryCreateObjectFromQuery(string query)
    {
        // Normalize the query
        string normalizedQuery = query.ToLower().Trim();
        
        // Try to identify the primitive type
        PrimitiveType? primitiveType = ExtractPrimitiveType(normalizedQuery);
        if (!primitiveType.HasValue)
        {
            Debug.Log("Failed to identify primitive type in: " + query);
            return false;
        }
        
        // Extract position (or use default)
        Vector3 position = ExtractPosition(normalizedQuery);
        
        // Extract scale (or use default)
        Vector3 scale = ExtractScale(normalizedQuery);
        
        // Extract color (or use default)
        Color color = ExtractColor(normalizedQuery);
        
        // Extract name (or use default)
        string objectName = ExtractName(normalizedQuery, primitiveType.Value.ToString());
        
        // Create the object
        GameObject createdObject = CreatePrimitiveObject(primitiveType.Value, position, scale, color, objectName);
        
        if (createdObject != null)
        {
            Debug.Log($"Created {primitiveType.Value} at {position} with scale {scale}");
            return true;
        }
        
        return false;
    }
    
    // Extract the primitive type from the query
    private PrimitiveType? ExtractPrimitiveType(string query)
    {
        Dictionary<string, PrimitiveType> primitiveMap = new Dictionary<string, PrimitiveType>
        {
            { "cube", PrimitiveType.Cube },
            { "box", PrimitiveType.Cube },
            { "square", PrimitiveType.Cube },
            { "sphere", PrimitiveType.Sphere },
            { "ball", PrimitiveType.Sphere },
            { "globe", PrimitiveType.Sphere },
            { "cylinder", PrimitiveType.Cylinder },
            { "tube", PrimitiveType.Cylinder },
            { "pipe", PrimitiveType.Cylinder },
            { "capsule", PrimitiveType.Capsule },
            { "pill", PrimitiveType.Capsule },
            { "plane", PrimitiveType.Plane },
            { "floor", PrimitiveType.Plane },
            { "ground", PrimitiveType.Plane },
            { "quad", PrimitiveType.Quad },
            { "panel", PrimitiveType.Quad }
        };
        
        foreach (var primitive in primitiveMap)
        {
            if (query.Contains(primitive.Key))
            {
                return primitive.Value;
            }
        }
        
        return null;
    }
    
    // Extract position from the query or return a default position
    private Vector3 ExtractPosition(string query)
    {
        // Default position: 3 units in front of the main camera
        Vector3 defaultPosition = Camera.main ? 
            Camera.main.transform.position + Camera.main.transform.forward * 3 : 
            new Vector3(0, 0, 0);
        
        // Try to find position values using regex
        // Looking for patterns like "at position (1,2,3)" or "at (1 2 3)" or "at x=1 y=2 z=3"
        Regex vectorRegex = new Regex(@"(at|position|pos)\s*\(?(\-?\d+\.?\d*)\s*,?\s*(\-?\d+\.?\d*)\s*,?\s*(\-?\d+\.?\d*)\)?");
        Match match = vectorRegex.Match(query);
        
        if (match.Success)
        {
            try
            {
                float x = float.Parse(match.Groups[2].Value);
                float y = float.Parse(match.Groups[3].Value);
                float z = float.Parse(match.Groups[4].Value);
                return new Vector3(x, y, z);
            }
            catch
            {
                Debug.LogWarning("Failed to parse position values in query.");
            }
        }
        
        // Try to find individual components
        Regex componentRegex = new Regex(@"(x|y|z)\s*=?\s*(\-?\d+\.?\d*)");
        MatchCollection matches = componentRegex.Matches(query);
        
        if (matches.Count > 0)
        {
            Vector3 position = defaultPosition;
            foreach (Match m in matches)
            {
                try
                {
                    float value = float.Parse(m.Groups[2].Value);
                    string component = m.Groups[1].Value.ToLower();
                    
                    if (component == "x") position.x = value;
                    else if (component == "y") position.y = value;
                    else if (component == "z") position.z = value;
                }
                catch
                {
                    Debug.LogWarning("Failed to parse position component in query.");
                }
            }
            return position;
        }
        
        return defaultPosition;
    }
    
    // Extract scale from the query or return a default scale
    private Vector3 ExtractScale(string query)
    {
        // Default scale: (1, 1, 1)
        Vector3 defaultScale = new Vector3(1, 1, 1);
        
        // Try to find scale values
        Regex scaleRegex = new Regex(@"(scale|size)\s*\(?(\-?\d+\.?\d*)\s*,?\s*(\-?\d+\.?\d*)\s*,?\s*(\-?\d+\.?\d*)\)?");
        Match match = scaleRegex.Match(query);
        
        if (match.Success)
        {
            try
            {
                float x = float.Parse(match.Groups[2].Value);
                float y = float.Parse(match.Groups[3].Value);
                float z = float.Parse(match.Groups[4].Value);
                return new Vector3(x, y, z);
            }
            catch
            {
                Debug.LogWarning("Failed to parse scale values in query.");
            }
        }
        
        // Try to find uniform scale
        Regex uniformScaleRegex = new Regex(@"(scale|size)\s*(\-?\d+\.?\d*)");
        match = uniformScaleRegex.Match(query);
        
        if (match.Success)
        {
            try
            {
                float scale = float.Parse(match.Groups[2].Value);
                return new Vector3(scale, scale, scale);
            }
            catch
            {
                Debug.LogWarning("Failed to parse uniform scale value in query.");
            }
        }
        
        return defaultScale;
    }
    
    // Extract color from the query or return a default color
    private Color ExtractColor(string query)
    {
        // Default color: white
        Color defaultColor = Color.white;
        
        // Check for common color names
        Dictionary<string, Color> colorMap = new Dictionary<string, Color>
        {
            { "red", Color.red },
            { "green", Color.green },
            { "blue", Color.blue },
            { "yellow", Color.yellow },
            { "cyan", Color.cyan },
            { "magenta", Color.magenta },
            { "white", Color.white },
            { "black", Color.black },
            { "grey", Color.grey },
            { "gray", Color.gray }
        };
        
        foreach (var color in colorMap)
        {
            if (query.Contains(color.Key))
            {
                return color.Value;
            }
        }
        
        // Try to find RGB values
        Regex rgbRegex = new Regex(@"color\s*\(?(\d+\.?\d*)\s*,?\s*(\d+\.?\d*)\s*,?\s*(\d+\.?\d*)\)?");
        Match match = rgbRegex.Match(query);
        
        if (match.Success)
        {
            try
            {
                float r = Mathf.Clamp01(float.Parse(match.Groups[1].Value) / 255f);
                float g = Mathf.Clamp01(float.Parse(match.Groups[2].Value) / 255f);
                float b = Mathf.Clamp01(float.Parse(match.Groups[3].Value) / 255f);
                return new Color(r, g, b);
            }
            catch
            {
                Debug.LogWarning("Failed to parse RGB color values in query.");
            }
        }
        
        return defaultColor;
    }
    
    // Extract a name from the query or use a default based on the primitive type
    private string ExtractName(string query, string primitiveType)
    {
        // Try to find name in patterns like "named X" or "call it X"
        Regex nameRegex = new Regex(@"(name|call|called|named)\s+(it|the)?\s*""?([a-zA-Z0-9_\s]+)""?");
        Match match = nameRegex.Match(query);
        
        if (match.Success)
        {
            string name = match.Groups[3].Value.Trim();
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }
        
        // Default name based on primitive type
        return primitiveType;
    }
    
    // Create a primitive object with the specified parameters
    private GameObject CreatePrimitiveObject(PrimitiveType type, Vector3 position, Vector3 scale, Color color, string name)
    {
        try
        {
            // Create the primitive GameObject
            GameObject obj = GameObject.CreatePrimitive(type);
            
            // Set name
            obj.name = name;
            
            // Set position
            obj.transform.position = position;
            
            // Set scale
            obj.transform.localScale = scale;
            
            // Set color (requires a material)
            Renderer renderer = obj.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.material.color = color;
            }
            
            return obj;
        }
        catch (Exception e)
        {
            Debug.LogError($"Error creating primitive object: {e.Message}");
            return null;
        }
    }

    private void OnUndoRedoPerformed()
    {
        Debug.Log("[Undo System] Unity undo/redo performed");
        
        // Refresh the UI
        Repaint();
        
        // Handle code undo first
        if (fileSnapshots.Count > 0)
        {
            var latestSnapshot = fileSnapshots[fileSnapshots.Count - 1];
            Debug.Log($"[Undo System] Reverting code changes for {latestSnapshot.FilePath}");
            if (File.Exists(latestSnapshot.FilePath))
            {
                File.WriteAllText(latestSnapshot.FilePath, latestSnapshot.Contents);
                AssetDatabase.Refresh();
                AddMessageToHistory("System", $"Undo: reverted code changes in '{latestSnapshot.FilePath}'");
            }
            else
            {
                Debug.LogWarning($"[Undo System] Cannot restore file {latestSnapshot.FilePath} - file no longer exists");
                AddMessageToHistory("System", $"Undo: attempted to revert code in '{latestSnapshot.FilePath}', but file was missing.");
            }
            fileSnapshots.RemoveAt(fileSnapshots.Count - 1);
            Debug.Log($"[Undo System] Removed applied snapshot, {fileSnapshots.Count} remaining");
        }
        else
        {
            // Scene undo fallback: report which scene was affected
            string sceneName = EditorSceneManager.GetActiveScene().name;
            AddMessageToHistory("System", $"Undo: reverted last modifications in scene '{sceneName}'");
        }
    }

    // Wrap multiple Undo operations into a single group for code and scene edits
    private void ApplyBatchChanges(Action batchAction)
    {
        int undoGroup = Undo.GetCurrentGroup();
        try
        {
            batchAction?.Invoke();
        }
        finally
        {
            Undo.SetCurrentGroupName("AI Batch Edits");
            Undo.CollapseUndoOperations(undoGroup);
            // Mark scene dirty if loaded
            var scene = EditorSceneManager.GetActiveScene();
            if (scene.isLoaded)
                EditorSceneManager.MarkSceneDirty(scene);
        }
    }

    // Add this new method to handle component initialization
    private void InitializeComponent(Component component, Type componentType)
    {
        if (component == null || componentType == null) return;

        // Get all serialized fields that need initialization
        var fields = componentType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => f.GetCustomAttribute<SerializeField>() != null || f.IsPublic);

        foreach (var field in fields)
        {
            // Handle different field types
            if (typeof(Component).IsAssignableFrom(field.FieldType))
            {
                // For component references, try to find or create the required component
                InitializeComponentReference(component, field);
            }
            else if (field.FieldType == typeof(GameObject))
            {
                // For GameObject references, try to find or create the required GameObject
                InitializeGameObjectReference(component, field);
            }
        }

        // Add common required components based on the script's name and type
        AddCommonComponents(component.gameObject, componentType);
    }

    private void InitializeComponentReference(Component component, FieldInfo field)
    {
        var targetType = field.FieldType;
        var targetObject = component.gameObject;

        // First try to find the component on the same GameObject
        var existingComponent = targetObject.GetComponent(targetType);
        if (existingComponent != null)
        {
            field.SetValue(component, existingComponent);
            AddMessageToHistory("System", $"Assigned existing {targetType.Name} to {field.Name} on {targetObject.name}");
            return;
        }

        // Then try to find it in the scene
        var sceneComponent = GameObject.FindObjectOfType(targetType);
        if (sceneComponent != null)
        {
            field.SetValue(component, sceneComponent);
            AddMessageToHistory("System", $"Assigned scene {targetType.Name} to {field.Name} on {targetObject.name}");
            return;
        }

        // If not found, create it
        var newComponent = targetObject.AddComponent(targetType);
        field.SetValue(component, newComponent);
        AddMessageToHistory("System", $"Created and assigned new {targetType.Name} to {field.Name} on {targetObject.name}");

        // Initialize the new component recursively
        InitializeComponent(newComponent, targetType);
    }

    private void InitializeGameObjectReference(Component component, FieldInfo field)
    {
        var targetObject = component.gameObject;
        string fieldName = field.Name;

        // Try to find a GameObject with a matching name
        var existingObject = GameObject.Find(fieldName);
        if (existingObject != null)
        {
            field.SetValue(component, existingObject);
            AddMessageToHistory("System", $"Assigned existing GameObject '{fieldName}' to {field.Name} on {targetObject.name}");
            return;
        }

        // If not found, create a new GameObject
        var newObject = new GameObject(fieldName);
        field.SetValue(component, newObject);
        AddMessageToHistory("System", $"Created and assigned new GameObject '{fieldName}' to {field.Name} on {targetObject.name}");
    }

    private void AddCommonComponents(GameObject targetObject, Type scriptType)
    {
        // Add common components based on script name or type
        string scriptName = scriptType.Name.ToLower();

        // Add Rigidbody for scripts that might need physics
        if (scriptName.Contains("player") || scriptName.Contains("character") || 
            scriptName.Contains("movement") || scriptName.Contains("physics"))
        {
            if (targetObject.GetComponent<Rigidbody>() == null)
            {
                var rb = targetObject.AddComponent<Rigidbody>();
                rb.constraints = RigidbodyConstraints.FreezeRotation;
                AddMessageToHistory("System", $"Added Rigidbody to {targetObject.name} for physics-based movement");
            }
        }

        // Add Collider for scripts that might need collision detection
        if (scriptName.Contains("player") || scriptName.Contains("character") || 
            scriptName.Contains("collision") || scriptName.Contains("physics"))
        {
            if (targetObject.GetComponent<Collider>() == null)
            {
                var collider = targetObject.AddComponent<BoxCollider>();
                collider.size = new Vector3(1f, 1f, 1f);
                AddMessageToHistory("System", $"Added BoxCollider to {targetObject.name} for collision detection");
            }
        }

        // Add AudioSource for scripts that might need sound
        if (scriptName.Contains("audio") || scriptName.Contains("sound") || 
            scriptName.Contains("music") || scriptName.Contains("player"))
        {
            if (targetObject.GetComponent<AudioSource>() == null)
            {
                targetObject.AddComponent<AudioSource>();
                AddMessageToHistory("System", $"Added AudioSource to {targetObject.name} for audio playback");
            }
        }

        // Add Animator for scripts that might need animation
        if (scriptName.Contains("animation") || scriptName.Contains("animator") || 
            scriptName.Contains("player") || scriptName.Contains("character"))
        {
            if (targetObject.GetComponent<Animator>() == null)
            {
                targetObject.AddComponent<Animator>();
                AddMessageToHistory("System", $"Added Animator to {targetObject.name} for animations");
            }
        }
    }
}
