using System;
using System.Collections.Generic;
using Newtonsoft.Json;

[Serializable]
public class PromptLogEntry
{
    [JsonProperty("timestamp")]
    public string timestamp;
    
    [JsonProperty("prompt")]
    public string prompt;
    
    [JsonProperty("response")]
    public string response;
    
    [JsonProperty("session_id")]
    public int sessionId;
    
    [JsonProperty("session_name")]
    public string sessionName;
    
    [JsonProperty("action_type")]
    public string actionType;
    
    [JsonProperty("details")]
    public string details;
    
    [JsonProperty("installation_id")]
    public string installationId;
    
    [JsonProperty("user_id")]
    public string userId;
    
    [JsonProperty("platform")]
    public string platform;
    
    [JsonProperty("unity_version")]
    public string unityVersion;
    
    [JsonProperty("plugin_version")]
    public string pluginVersion;
}

[Serializable]
public class PromptLogFile
{
    public List<PromptLogEntry> entries = new List<PromptLogEntry>();
}