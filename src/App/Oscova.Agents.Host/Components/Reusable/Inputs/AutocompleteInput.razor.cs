using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;

namespace Oscova.Agents.Host.Components.Reusable.Inputs;

/// <summary>Provides a free-entry text input with optional filtered suggestions.</summary>
public partial class AutocompleteInput : ComponentBase
{
    [Inject]
    private IStringLocalizer<CommonText> CommonLoc { get; set; } = default!;

    [Parameter, EditorRequired]
    public IReadOnlyList<string> Suggestions { get; set; } = [];

    [Parameter]
    public string? Value { get; set; }

    [Parameter]
    public EventCallback<string?> ValueChanged { get; set; }

    [Parameter]
    public string? AriaLabel { get; set; }

    [Parameter]
    public string? Placeholder { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public string? Class { get; set; }

    [Parameter]
    public string? IconClass { get; set; }

    private readonly string _listboxId = $"autocomplete-{Guid.NewGuid():N}";
    private int _activeIndex = -1;
    private bool _isOpen;

    private string? ActiveOptionId => _activeIndex >= 0 ? GetOptionId(_activeIndex) : null;

    private List<string> FilteredSuggestions() =>
        string.IsNullOrWhiteSpace(Value)
            ? Suggestions.ToList()
            : Suggestions
                .Where(suggestion => suggestion.Contains(Value, StringComparison.OrdinalIgnoreCase))
                .ToList();

    private void Open()
    {
        if (!Disabled)
        {
            _isOpen = true;
        }
    }

    private void Close()
    {
        _activeIndex = -1;
        _isOpen = false;
    }

    private async Task OnInputAsync(ChangeEventArgs args)
    {
        Value = args.Value?.ToString();
        _activeIndex = -1;
        _isOpen = true;
        await ValueChanged.InvokeAsync(Value);
    }

    private async Task OnKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Escape")
        {
            Close();
            return;
        }

        var filtered = FilteredSuggestions();
        if (filtered.Count == 0)
        {
            return;
        }

        if (args.Key == "ArrowDown")
        {
            _isOpen = true;
            _activeIndex = (_activeIndex + 1) % filtered.Count;
        }
        else if (args.Key == "ArrowUp")
        {
            _isOpen = true;
            _activeIndex = _activeIndex <= 0 ? filtered.Count - 1 : _activeIndex - 1;
        }
        else if (args.Key == "Enter" && _isOpen && _activeIndex >= 0)
        {
            await SelectAsync(filtered[_activeIndex]);
        }
    }

    private async Task SelectAsync(string suggestion)
    {
        Value = suggestion;
        Close();
        await ValueChanged.InvokeAsync(suggestion);
    }

    private string GetOptionId(int index) => $"{_listboxId}-option-{index}";
}
