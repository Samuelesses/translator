using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameAudioTranslator.Models;

namespace GameAudioTranslator.Services;

public static class SettingsStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameAudioTranslator");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings file: fall back to defaults rather than crash on startup.
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Dir);
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }

    public static string? DecryptApiKey(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
        {
            return null;
        }

        try
        {
            var encryptedBytes = Convert.FromBase64String(encrypted);
            var bytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Key was saved under a different user profile or is corrupted; treat as "not set".
            return null;
        }
    }

    public static string EncryptApiKey(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        var encryptedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encryptedBytes);
    }
}
