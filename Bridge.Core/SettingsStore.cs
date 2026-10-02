using System.Text.Json;

namespace WarudoBridge;

public sealed class SettingsStore(string path)
{
    private bool preserveBeforeSave;
    public string? Warning { get; private set; }
    public BridgeSettings Load()
    {
        Warning = null;
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(path))
                ?? throw new JsonException("빈 설정입니다.");
            if (!Uri.TryCreate(settings.Address, UriKind.Absolute, out var address) || address.Scheme is not ("ws" or "wss")
                || settings.Favorites is null || settings.Favorites.Any(f => f is null || string.IsNullOrWhiteSpace(f.Name) || !Enum.IsDefined(f.Kind)))
                throw new JsonException("유효하지 않은 설정입니다.");
            settings.Favorites = settings.Favorites.Distinct().ToList();
            return settings;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            preserveBeforeSave = true;
            Warning = "설정을 읽지 못해 기본값을 사용합니다.";
            if (e is JsonException)
            {
                try
                {
                    PreserveOriginal();
                    Warning = "손상된 설정을 별도 보관하고 기본값을 사용합니다.";
                }
                catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException)
                { Warning = "손상된 설정을 보관하지 못했습니다. 저장 권한을 확인하세요."; }
            }
            return new();
        }
    }

    public void Save(BridgeSettings settings)
    {
        // A temporary sharing lock may have prevented the recovery backup at load time.
        // Never overwrite the only original when that happens; retry preservation first.
        if (preserveBeforeSave && File.Exists(path)) PreserveOriginal();
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void PreserveOriginal()
    {
        File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
        preserveBeforeSave = false;
    }
}
