using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace WarudoBridge;

public sealed class ReceiverItem(ReceiverKey key, bool favorite) : INotifyPropertyChanged
{
    public ReceiverKey Key { get; } = key;
    private bool connected, busy, isFavorite = favorite;
    private bool? toggleState;
    public string Name => Key.Name;
    public string KindLabel => Key.Kind == ReceiverKind.Trigger ? "TRIGGER" : "TOGGLE";
    public bool IsToggle => Key.Kind == ReceiverKind.Toggle;
    public bool IsFavorite { get => isFavorite; set { isFavorite = value; Changed(); } }
    public bool IsConnected { get => connected; set { connected = value; Changed(); } }
    public bool IsBusy { get => busy; set { busy = value; Changed(); } }
    public bool? ToggleState { get => toggleState; set { toggleState = value; Changed(); } }
    public bool CanExecute => IsConnected && !IsBusy;
    public string FavoriteLabel => IsFavorite ? "★" : "☆";
    public string StateLabel => !IsConnected ? "연결 끊김" : !IsToggle ? "실행" : ToggleState switch { true => "ON", false => "OFF", null => "상태 확인 중" };
    public string ActionLabel => IsBusy ? "확인 중…" : IsToggle ? "토글 전환" : "트리거 실행";
    public bool Matches(string query, ReceiverKind? kind, bool favoritesOnly) =>
        (!kind.HasValue || Key.Kind == kind) && (!favoritesOnly || IsFavorite)
        && Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase);
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null)
    {
        PropertyChanged?.Invoke(this, new(property));
        foreach (var name in new[] { nameof(CanExecute), nameof(StateLabel), nameof(FavoriteLabel), nameof(ActionLabel) })
            PropertyChanged?.Invoke(this, new(name));
    }
}
