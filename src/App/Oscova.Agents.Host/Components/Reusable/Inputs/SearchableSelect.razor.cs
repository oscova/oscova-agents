using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;
using Microsoft.JSInterop;
using Oscova.Agents.Host.Components.Reusable.Inputs.Models;
using Oscova.Agents.Host.Localization;

namespace Oscova.Agents.Host.Components.Reusable.Inputs;

/// <summary>
/// Provides a server-rendered combobox for long option lists where a native select is hard to scan.
/// </summary>
public partial class SearchableSelect : ComponentBase
{
    [Inject]
    private IJSRuntime JS { get; set; } = default!;

    [Inject]
    private IStringLocalizer<CommonText> CommonLoc { get; set; } = default!;

    [Parameter, EditorRequired]
    public IReadOnlyList<SearchableSelectOption> Options { get; set; } = [];

    [Parameter]
    public string? Value { get; set; }

    [Parameter]
    public string? AriaLabel { get; set; }

    [Parameter]
    public EventCallback<string?> ValueChanged { get; set; }

    [Parameter]
    public string? Placeholder { get; set; }

    [Parameter]
    public string? SearchPlaceholder { get; set; }

    [Parameter]
    public bool Disabled { get; set; }

    [Parameter]
    public string? Class { get; set; }

    private bool _isOpen;
    private string _searchText = string.Empty;
    private readonly string _listboxId = $"select-{Guid.NewGuid():N}";
    private int _activeIndex = -1;
    private string? ActiveOptionId => _isOpen && _activeIndex >= 0 ? GetOptionId(_activeIndex) : null;

    private string SelectedLabel =>
        Options.FirstOrDefault(option => option.Value == Value)?.Label ?? string.Empty;

    private string DisplayText => _isOpen ? _searchText : SelectedLabel;

    private string FieldPlaceholder =>
        _isOpen
            ? SearchPlaceholder ?? CommonLoc.GetOrDefault(CommonTextKeys.Search, "Search...")
            : Placeholder ?? string.Empty;

    private List<SearchableSelectOption> FilteredOptions()
    {
        if (string.IsNullOrWhiteSpace(_searchText))
        {
            return Options.ToList();
        }

        return Options
            .Where(option => option.Label.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void Open()
    {
        if (Disabled)
        {
            return;
        }

        if (!_isOpen)
        {
            _isOpen = true;
            _searchText = string.Empty;
        }
    }

    private void Close()
    {
        _activeIndex = -1;
        _isOpen = false;
        _searchText = string.Empty;
    }

    private void OnSearchInput(ChangeEventArgs args)
    {
        _activeIndex = -1;
        _searchText = args.Value?.ToString() ?? string.Empty;
        _isOpen = true;
    }

    // Keyboard selection commits only a listed option, just like pointer selection.
    private async Task OnKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key == "Escape") { Close(); return; }

        var filtered = FilteredOptions();
        if (filtered.Count == 0) { return; }

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
        else if (args.Key == "Enter" && _isOpen && _activeIndex >= 0 && _activeIndex < filtered.Count)
        {
            await SelectAsync(filtered[_activeIndex]);
        }
    }

    private string GetOptionId(int index) => $"{_listboxId}-option-{index}";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (ActiveOptionId is not { } optionId) { return; }

        try
        {
            // Keep the highlighted option visible without moving keyboard focus off the input.
            await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/ui-services.js");
            await module.InvokeVoidAsync("scrollElementIntoView", optionId);
        }
        catch (JSDisconnectedException)
        {
            // Navigation can disconnect the circuit while the option is being revealed.
        }
    }

    private async Task SelectAsync(SearchableSelectOption option)
    {
        Close();

        if (option.Value != Value)
        {
            Value = option.Value;
            await ValueChanged.InvokeAsync(option.Value);
        }
    }
}
