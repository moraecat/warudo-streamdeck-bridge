namespace WarudoBridge;

public enum ReceiverKind { Trigger, Toggle }
public sealed record ReceiverKey(ReceiverKind Kind, string Name);
public sealed record ReceiverSnapshot(IReadOnlyList<ReceiverKey> Receivers, IReadOnlyDictionary<string, bool> Toggles);
public sealed record ToggleUpdate(string Name, bool State, bool IsResponse);
public sealed class BridgeSettings
{
    public string Address { get; set; } = "ws://localhost:19069/";
    public List<ReceiverKey> Favorites { get; set; } = [];
}
