using UnityEngine;
using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

public static class SupabaseAnalytics
{
    private static readonly HttpClient client = new HttpClient();
    private static readonly string SUPABASE_URL = "https://muefsrcijttbiuahjxwn.supabase.co";
    private static readonly string SUPABASE_KEY = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im11ZWZzcmNpanR0Yml1YWhqeHduIiwicm9sZSI6ImFub24iLCJpYXQiOjE3NDc4NjYwNzQsImV4cCI6MjA2MzQ0MjA3NH0.Nzx6my5ZONlYwUQEKrisZIgF5u_IORWoGTwhrbWuh1E";
    
    static SupabaseAnalytics()
    {
        client.DefaultRequestHeaders.Add("apikey", SUPABASE_KEY);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {SUPABASE_KEY}");
        client.DefaultRequestHeaders.Add("Prefer", "return=minimal");
    }

    public static async Task<bool> SendLog(PromptLogEntry log)
    {
        try
        {
            var json = JsonConvert.SerializeObject(log);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            
            Debug.Log($"[Analytics] Sending log to Supabase: {json}");
            
            var response = await client.PostAsync($"{SUPABASE_URL}/rest/v1/logs", content);
            var responseContent = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                Debug.LogError($"[Analytics] Failed to send log: {response.StatusCode}\nResponse: {responseContent}");
                return false;
            }
            
            Debug.Log($"[Analytics] Successfully sent log to Supabase");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Analytics] Error sending log: {ex.Message}\nStack trace: {ex.StackTrace}");
            return false;
        }
    }
}