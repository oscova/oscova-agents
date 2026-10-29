using Oscova.Agents.Host.Server.Security;
using System.Globalization;
using Oscova.Agents.Conversations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Oscova.Agents.Core.Settings;
using Oscova.Agents.Core.Avatar;
using Oscova.Agents.Core.Conversation;
using Oscova.Agents.Core.Hooks;
using Oscova.Agents.Core.Interfaces;
using Oscova.Agents.Core.Utils;
using Oscova.Agents.Core.ApiContracts.Chat;
using Oscova.Agents.Definitions;
using Oscova.Agents.Llm;
using Oscova.Agents.Infrastructure.Users;
using Oscova.Agents.Host.Services.Api;
using Oscova.Agents.Host.Services;
using Oscova.Agents.Host.Constants;
using Oscova.Agents.Host.Components.Settings;
using Oscova.Agents.Host.Services.UI;
using Oscova.Agents.Host.Components.Prompts;
using Oscova.Agents.Host.Components.Dialogs;
using Oscova.Agents.Host.Localization;
using Oscova.Agents.Host.Server.Chat;
using Oscova.Agents.Host.Server.Agents;
using Oscova.Agents.Policies;

namespace Oscova.Agents.Host.Components.Chat;

public partial class ChatPanel : ComponentBase, IAsyncDisposable
{
    [Inject] private SettingsPageRegistry SettingsRegistry { get; set; } = default!;
    [Inject] private ChatDraftState Drafts { get; set; } = default!;

    // Title, description, action and prompt are English fallbacks for Chat.Start.Card.{Id}.* keys.
    private sealed record StarterCard(string Id, string Icon, string Title, string Description, string Action, string Prompt);

    private sealed record StartOpeningText(string Greeting, string? Question, bool IsReady, bool IsProblem = false);

    private sealed record StartOpeningVariant(string Id, bool HasQuestion);

    // Captures every mutable selection that determines one outbound chat command.
    private sealed record PendingSend(
        ChatThread Thread,
        string ThreadId,
        string? ProjectId,
        string? AgentId,
        string? AgentName,
        string? ModelProviderId,
        string UserText,
        string ComposerTextToRestore,
        bool ClearComposer,
        IReadOnlyList<PendingAttachment> Attachments,
        ToolApprovalRunMode ApprovalRunMode,
        bool ContinueSession = false,
        string? RecipientAgentId = null,
        IReadOnlyList<WorkspaceFileReference>? Files = null);

    private sealed record OptimisticRunRollback(
        ChatThread Thread,
        ChatDisplayMessage UserMessage,
        string InputText,
        IReadOnlyList<PendingAttachment> Attachments,
        string Title,
        string? AgentId,
        string? AgentName,
        bool ClearComposer,
        bool IsAdmitted = false,
        bool AdmissionStarted = false,
        IReadOnlyList<WorkspaceFileReference>? Files = null);

    private sealed record RegenerationRollback(
        ChatThread Thread,
        string UserMessageId,
        IReadOnlyList<ChatDisplayMessage> ResponseTail);

    private enum ChatRunState
    {
        Idle,
        Streaming,
        AwaitingElicitation,
        AwaitingToolApproval,
        AwaitingSubagents,
        Continuing
    }

    private enum StartOpeningTimeBucket
    {
        Morning,
        Afternoon,
        Evening,
        Night
    }

    private static readonly StartOpeningVariant[] CommonStartOpenings = [
        new("Common.Returns", HasQuestion: true),
        new("Common.Alright", HasQuestion: false),
        new("Common.ThereYouAre", HasQuestion: true),
        new("Common.ReadyWhen", HasQuestion: true),
        new("Common.FloorYours", HasQuestion: true),
        new("Common.BackBoard", HasQuestion: true),
        new("Common.LetsBegin", HasQuestion: true),
        new("Common.YouMadeIt", HasQuestion: true)
    ];

    private static readonly StartOpeningVariant FirstChatStartOpening = new("FirstChat.Welcome", HasQuestion: true);

    private static readonly StarterCard TourStarterCard = new("Tour", "lucide:compass", "Get to know Oscova",
        "See what Oscova can do and what to set up first.", "Take the tour",
        "I'm new to Oscova. What can you help me with, and what should I set up first?");

    // Every card must be doable with the main agent's default tools.
    private static readonly StarterCard[] StarterCardPool = [
        TourStarterCard,
        new("Plan", "lucide:sparkles", "Plan a project", "Turn a rough idea into a practical next-step plan.", "Draft a plan",
            "Help me turn a rough project idea into a practical plan with clear next steps."),
        new("Summarize", "lucide:file-text", "Summarize a document", "Extract the important points from notes, docs, or files.", "Summarize notes",
            "Help me summarize this document and list the key decisions, risks, and next actions."),
        new("Write", "lucide:message-square-text", "Write a message", "Shape an email, reply, announcement, or short update.", "Draft copy",
            "Help me draft a clear, friendly message for this situation."),
        new("EmailReply", "lucide:mail", "Reply to an email", "Paste an email and get a clear, polite reply.", "Draft a reply",
            "Help me reply to an email. I'll paste it next. Keep the reply clear and polite."),
        new("MeetingNotes", "lucide:users", "Sort out meeting notes", "Turn rough notes into decisions, owners and next steps.", "Organize notes",
            "I'll paste my meeting notes. Turn them into a short summary with decisions, owners and next steps."),
        new("Agenda", "lucide:clipboard-list", "Prepare a meeting agenda", "Get a timed agenda for a meeting you're running.", "Build an agenda",
            "Help me prepare an agenda for a meeting I'm running, with time for each topic."),
        new("Reminder", "lucide:bell", "Set a daily reminder", "Get a weekday nudge to review your priorities.", "Schedule it",
            "Remind me every weekday at 9 AM to review my priorities for the day."),
        new("Week", "lucide:calendar-clock", "Plan my week", "Turn this week's commitments into a realistic plan.", "Plan the week",
            "Help me plan my week. I'll list my commitments, and you turn them into a realistic day-by-day plan."),
        new("Chart", "lucide:chart-column", "Chart some numbers", "See numbers as a chart you can compare at a glance.", "Make a chart",
            "Make a chart from some numbers I'll share so I can compare them at a glance."),
        new("Budget", "lucide:receipt", "Make a simple budget", "Lay out monthly income and spending in one view.", "Start a budget",
            "Help me put together a simple monthly budget from my income and expenses."),
        new("Quiz", "lucide:graduation-cap", "Quiz me on a topic", "Learn by answering questions one at a time.", "Start a quiz",
            "Quiz me on a topic I'm learning, one question at a time, and explain anything I get wrong."),
        new("Interview", "lucide:briefcase", "Practice an interview", "Rehearse answers with feedback after each question.", "Start practicing",
            "Run a mock job interview with me for a role I'll describe. Ask one question at a time and give feedback after each answer."),
        new("Explain", "lucide:lightbulb", "Explain something simply", "Understand a tricky topic with a plain example.", "Explain it",
            "Explain a topic I'm curious about in plain language, with a simple example."),
        new("WebPage", "lucide:globe", "Summarize a web page", "Get the key points from a link.", "Read a link",
            "Read a web page I'll link and give me its key points."),
        new("Pdf", "lucide:file-search", "Search a PDF", "Find and quote the parts of a PDF that matter.", "Search a PDF",
            "I'll attach a PDF. Find the parts about a topic I name and quote them."),
        new("Csv", "lucide:table", "Explore a spreadsheet", "Spot trends and odd values in a CSV file.", "Analyze data",
            "I'll attach a CSV file. Tell me what's in it and point out any trends or odd values."),
        new("Polish", "lucide:pen-line", "Polish my writing", "Improve clarity and tone, with the main changes explained.", "Polish text",
            "Improve the clarity and tone of some writing I'll paste, and explain the main changes."),
        new("Decision", "lucide:scale", "Weigh a decision", "Compare options with pros, cons and a recommendation.", "Compare options",
            "Help me weigh a decision by listing the options, their pros and cons, and what you'd recommend."),
        new("Checklist", "lucide:list-checks", "Make a checklist", "Break a task into clear steps you can tick off.", "Make a checklist",
            "Make a step-by-step checklist for a task I'm about to do."),
        new("Translate", "lucide:languages", "Translate text", "Translate while keeping the tone natural.", "Translate",
            "Translate some text I'll paste and keep its tone natural."),
        new("Trip", "lucide:plane", "Plan a trip", "Get a day-by-day outline and a packing list.", "Plan a trip",
            "Help me plan a short trip with a day-by-day outline and a packing list."),
        new("Meals", "lucide:chef-hat", "Plan the week's meals", "Simple meals and one combined shopping list.", "Plan meals",
            "Plan simple meals for the week and give me one combined shopping list."),
        new("Code", "lucide:code", "Explain some code", "Walk through what code does and spot likely bugs.", "Review code",
            "Explain what a piece of code I'll paste does, step by step, and point out any likely bugs."),
        new("Skill", "lucide:puzzle", "Teach Oscova a skill", "Find and add a skill for a task you do often.", "Find a skill",
            "Find a skill that would help with a task I do often, and set it up for me."),
        new("ConnectApp", "lucide:plug", "Connect an app", "Let Oscova work with an app you already use.", "Connect an app",
            "I'd like you to work with an app I use. Find a way to connect it and set it up."),
        new("Research", "lucide:telescope", "Research a topic", "Get a short sourced report on a question you name.", "Start research",
            "Research a topic I'll name and give me a short report with sources.")
    ];

    private static readonly StartOpeningVariant[] MorningStartOpenings = [
        new("Morning.GoodMorning", HasQuestion: true),
        new("Morning.FreshStart", HasQuestion: true),
        new("Morning.YoureEarly", HasQuestion: true),
        new("Morning.Morning", HasQuestion: true)
    ];

    private static readonly StartOpeningVariant[] AfternoonStartOpenings = [
        new("Afternoon.GoodAfternoon", HasQuestion: true),
        new("Afternoon.NextThing", HasQuestion: false),
        new("Afternoon.Focus", HasQuestion: true),
        new("Afternoon.Useful", HasQuestion: true)
    ];

    private static readonly StartOpeningVariant[] EveningStartOpenings = [
        new("Evening.GoodEvening", HasQuestion: true),
        new("Evening.QuietHours", HasQuestion: true),
        new("Evening.StillThinking", HasQuestion: false),
        new("Evening.WrapOrRefine", HasQuestion: true)
    ];

    private static readonly StartOpeningVariant[] NightStartOpenings = [
        new("Night.LateMode", HasQuestion: true),
        new("Night.UpLate", HasQuestion: false),
        new("Night.NightShift", HasQuestion: true),
        new("Night.QuietHours", HasQuestion: true)
    ];

    private const string ResearchCommand = "/research";

    private static readonly (string Name, string Icon, string DescriptionKey, string Fallback)[] SlashCommands =
    [
        ("/goal", "lucide:target", ChatTextKeys.SlashGoalDescription, "Keep working toward a goal"),
        (ResearchCommand, "lucide:telescope", ChatTextKeys.SlashResearchDescription, "Research a topic and save a sourced report"),
        ("/compact", "lucide:shrink", ChatTextKeys.SlashCompactDescription, "Summarize older context now"),
        ("/context", "lucide:gauge", ChatTextKeys.SlashContextDescription, "Show estimated context usage"),
        ("/new", "lucide:plus", ChatTextKeys.SlashNewDescription, "Start a new chat"),
        ("/rename", "lucide:pencil", ChatTextKeys.SlashRenameDescription, "Rename this chat")
    ];

    [Parameter, EditorRequired] public ChatThread ChatThread { get; set; } = default!;
    [Parameter] public ChatThreadViewResponse? ThreadView { get; set; }
    [Parameter] public bool IsConversationLoading { get; set; }
    private bool _waitingForThreadView;
    private bool _loadingOlderMessages;
    private double? _restoreScrollTop;
    private double[]? _prependScrollMetrics;
    [Parameter] public bool CanChangeIncognitoMode { get; set; }
    [Parameter, EditorRequired] public AgentDefinition MainAgent { get; set; } = new();
    [Parameter] public IReadOnlyList<AgentDefinition> CustomAgents { get; set; } = [];
    [Parameter] public IReadOnlyList<ModelProviderConfig> ModelProviders { get; set; } = [];
    [Parameter] public Guid? SelectedModelProviderId { get; set; }
    [Parameter] public Guid? DefaultModelProviderId { get; set; }
    [Parameter] public string? SelectedAgentId { get; set; }
    [Parameter] public EventCallback<Guid?> OnSelectProvider { get; set; }
    [Parameter] public EventCallback<string?> OnSelectAgent { get; set; }
    [Parameter] public Func<Task<bool>>? OnBeforeSelectAgent { get; set; }
    [Parameter] public EventCallback<ChatThread> OnCurrentThreadChanged { get; set; }
    [Parameter] public EventCallback OnToolActivityChanged { get; set; }
    [Parameter] public EventCallback<string> OnOpenArtifact { get; set; }
    [Parameter] public EventCallback OnRunFinished { get; set; }
    [Parameter] public ProjectDefinition? Project { get; set; }
    [Parameter] public EventCallback OnStartNewChat { get; set; }
    [Parameter] public EventCallback<(string ThreadId, string Title)> OnRenameThread { get; set; }
    [Parameter] public bool IsServiceRunning { get; set; }
    [Parameter] public bool IsCheckingService { get; set; }
    [Parameter] public string ServiceStatusMessage { get; set; } = string.Empty;
    [Parameter] public EventCallback OnRetryServiceCheck { get; set; }
    [Parameter] public string? ThreadDrawerToggleId { get; set; }
    [Parameter] public EventCallback OnShowChats { get; set; }
    /// <summary>Gets or sets whether the user has no chats yet, which switches the start screen to its welcome.</summary>
    [Parameter] public bool IsFirstChat { get; set; }

    [Inject] private UserServiceScope UserScope { get; set; } = default!;
    [Inject] private IToastService ToastService { get; set; } = default!;
    [Inject] private BrowserViewerAccess BrowserAccess { get; set; } = default!;
    [Inject] private SoundCuePlayer Sound { get; set; } = default!;
    [Inject] private ILogger<ChatPanel> Logger { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IChatSpeechService SpeechService { get; set; } = default!;
    [Inject] private IDownloadService DownloadService { get; set; } = default!;
    [Inject] private IAvatarBridgeClient AvatarBridge { get; set; } = default!;
    [Inject] private IAvatarRuntimeSettingsService AvatarRuntimeSettings { get; set; } = default!;
    [Inject] private IWebHostInfoProvider WebHostInfo { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;
    [Inject] private ChatApiClient ChatApiClient { get; set; } = default!;
    [Inject] private ChatRunProjectionStore RunProjections { get; set; } = default!;
    [Inject] private ThreadRunGuard RunGuard { get; set; } = default!;
    [Inject] private ProfilePictureRefreshState ProfilePictureRefreshState { get; set; } = default!;
    [Inject] private IdleLogoutActivityState IdleLogoutActivity { get; set; } = default!;

    private static readonly TimeSpan ScrollThrottleInterval = TimeSpan.FromMilliseconds(100);

    private readonly string _audioElementId = $"tts-audio-{Guid.NewGuid():N}";
    private readonly HashSet<Guid> _pendingElicitationIds = [];
    private readonly HashSet<string> _pendingToolApprovalIds = new(StringComparer.Ordinal);

    private string? _liveAnnouncement;
    private bool _isDisposed;
    private IReadOnlyList<string> _avatarEmotions = [];
    private string? _avatarEmotionsRunId;
    private bool _isSubmitting;
    private bool _slashMenuDismissed;
    private int _selectedSlashCommandIndex;
    private bool _isIncognitoDisclosureExpanded;
    private bool _followLatest = true;
    private bool _shouldScrollToLatest;
    private DateTime _lastAutoScrollRequestTime = DateTime.MinValue;
    private string _inputText = string.Empty;

    // Holds text until its accepting run displays it or returns it to its original composer.
    private sealed class PendingFollowUp(string threadId, ChatFollowUpView message)
    {
        public string ThreadId { get; } = threadId;
        public ChatFollowUpView Message { get; } = message;
        public string? RunId { get; set; }
    }

    private IReadOnlyList<ChatFollowUpView> _queuedFollowUps = [];
    private readonly List<PendingFollowUp> _pendingFollowUps = [];
    private readonly Dictionary<string, string> _returnedFollowUpText = new(StringComparer.OrdinalIgnoreCase);
    private int _followUpRefreshVersion;

    private bool HasLiveRun => _runState != ChatRunState.Idle && !string.IsNullOrWhiteSpace(_activeOperationId);

    private IEnumerable<ChatFollowUpView> SteeringFollowUps =>
        _pendingFollowUps.Where(item => item.ThreadId == ChatThread.Id && item.RunId is not null)
            .Select(item => item.Message);

    private bool ShowFollowUpPanel => _queuedFollowUps.Count > 0 || SteeringFollowUps.Any();

    private ChatEasterEgg? _chatEasterEgg;
    private readonly string _attachmentInputId = $"chat-attachment-input-{Guid.NewGuid():N}";
    private ChatRunState _runState = ChatRunState.Idle;
    [Inject] private NodesApiClient NodesApiClient { get; set; } = default!;
    private ChatPermissions? _chatPermissions;
    private string? _permissionsThreadId;
    private ToolApprovalRunMode _toolApprovalRunMode = ToolApprovalRunMode.UseAgentPolicy;
    private string? _activeOperationId;
    private CancellationTokenSource? _runStartCts;
    private readonly List<PendingAttachment> _pendingAttachments = [];
    private readonly List<WorkspaceFileReference> _pendingFiles = [];

    /// <summary>Adds an existing workspace file to the current unsent composer.</summary>
    public async Task AddFileAsync(WorkspaceFileReference file)
    {
        if (_pendingFiles.Contains(file)) { return; }

        if (_pendingFiles.Count >= ChatAttachmentLimits.MaxFilesPerMessage)
        {
            ToastService.Show(FormatChatText(ChatTextKeys.TooManyFilesAttached, "You can attach at most {0} files per message.", ChatAttachmentLimits.MaxFilesPerMessage), ToastType.Warning);
            return;
        }

        _pendingFiles.Add(file);
        await InvokeAsync(StateHasChanged);
    }

    private bool _loadingAttachments;
    private IReadOnlyList<ChatTodoItem> _todos = [];
    private string? _hydratedTodoKey;
    private ChatContextPreflightResponse? _contextUsage;
    private string? _contextUsageKey;
    private long _contextUsageRequestVersion;
    private IDisposable? _runProjectionSubscription;
    private string? _selectedProjectionThreadId;
    private string? _observedRunId;
    private long _observedProjectionInstanceId;
    private long _lastProjectionSequence = -1;
    private long _projectionSignalVersion;
    private long _appliedProjectionSignalVersion;
    private int _projectionRefreshScheduled;
    private int _projectionStructuralRefreshPending;
    private readonly HashSet<string> _projectionMessageIds = new(StringComparer.Ordinal);
    private OptimisticRunRollback? _optimisticRunRollback;
    private RegenerationRollback? _regenerationRollback;
    private string? _locallyStartedEffectRunId;
    private ChatRunOutcome _lastAppliedOutcome;
    private bool _showFailedRunNotice;
    private bool _nodeUnavailableFailure;
    private bool _projectFolderFailure;
    private Oscova.Agents.Core.Mcp.McpConnectionException? _mcpFailure;
    private string FailedRunMessage => _nodeUnavailableFailure
        ? Loc.GetOrDefault(ChatTextKeys.ProjectNodeOffline, "The selected node is offline. Start Oscova node on that device and try again.")
        : _projectFolderFailure
            ? Loc.GetOrDefault(ChatTextKeys.ProjectFolderMissing, "The project folder is missing. Restore it, then try again.")
        : _mcpFailure is { } failure
            ? failure.DockerMissing
                ? FormatChatText(ChatTextKeys.McpDockerMissing, "MCP connection '{0}' could not start. Install Docker or select native MCP execution on the execution device.", McpServerLabel(failure.ServerId))
                : FormatChatText(ChatTextKeys.McpConnectionFailed, "MCP connection '{0}' could not start. Open MCP server settings, check its configuration on the execution device, and reconnect.", McpServerLabel(failure.ServerId))
            : Loc.GetOrDefault(ChatTextKeys.ResponseFailed, "The chat response could not be completed.");
    private string? _failedUserMessageId;
    private bool _lastProjectedToolActivity;

    private string McpServerLabel(string serverId) =>
        UserScope.McpToolSettings.GetAvailable()
            .FirstOrDefault(server => string.Equals(server.Id.ToString(), serverId, StringComparison.OrdinalIgnoreCase))?.Name
        ?? serverId;

    private sealed class PendingAttachment
    {
        public required string Id { get; init; }
        public required IBrowserFile File { get; init; }
    }

    // Allow attachment-only turns: send is valid if there's text OR at least one pending file.
    private bool CanSend => !string.IsNullOrWhiteSpace(TextWithoutPendingMention) || _pendingAttachments.Count > 0 || _pendingFiles.Count > 0;

    private IReadOnlyList<(string Name, string Icon, string DescriptionKey, string Fallback)> AvailableSlashCommands =>
        [.. SlashCommands.Where(command =>
            command.Name switch
            {
                "/compact" => ThreadView?.HasSession == true || ChatThread.SessionElement is not null,
                "/context" => SelectedProvider is not null,
                _ => true
            })];

    private IReadOnlyList<(string Name, string Icon, string DescriptionKey, string Fallback)> FilteredSlashCommands =>
        [.. AvailableSlashCommands.Where(command => command.Name.StartsWith(_inputText, StringComparison.OrdinalIgnoreCase))];

    private bool IsSlashCommandMenuOpen =>
        !_slashMenuDismissed &&
        !IsInputLocked &&
        !IsVoiceModeActive &&
        _inputText.StartsWith("/", StringComparison.Ordinal) &&
        !_inputText.Any(char.IsWhiteSpace);

    private bool IsAttachmentInputDisabled =>
        _goalEditing is not null || IsInputLocked ||
        IsVoiceModeActive ||
        (ShouldShowStartScreen
            ? !IsStartScreenReady || _isStartScreenTransitioning
            : !IsServiceRunning);

    private bool HasNoModelProviders => ModelProviders.Count == 0;

    private bool IsModelProviderSelectionRequired => !HasNoModelProviders && SelectedProvider is null;

    private bool IsModelProviderSetupRequired => HasNoModelProviders || IsModelProviderSelectionRequired;

    private bool CanRetryServiceCheck => !IsCheckingService && !IsServiceRunning && !IsModelProviderSetupRequired;

    private static readonly string AttachmentAccept = string.Join(",", ChatAttachmentLimits.AllowedExtensions.Keys);

    private SpeechSettings SpeechSettings => UserScope.SpeechSettings;

    internal IJSObjectReference? _utilsModule;
    internal IJSObjectReference? _audioQueueModule;
    internal IJSObjectReference? _audioRecorderModule;
    internal DotNetObjectReference<ChatPanel>? _dotNetRef;
    private ElementReference _chatScrollBody;

    protected override void OnInitialized()
    {
        InputPlaceholder = GetDefaultInputPlaceholder();
        _dotNetRef = DotNetObjectReference.Create(this);
        ProfilePictureRefreshState.Changed += OnProfilePictureChanged;
        _runProjectionSubscription = RunProjections.Subscribe(
            UserScope.UserId,
            OnRunSignalAsync);
        DownloadService.DownloadsChanged += OnDownloadsChangedForSpeech;
        _goalStore = UserScope.ConversationGoals;
        _goalStore.Changed += OnGoalChanged;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (!string.Equals(
                Volatile.Read(ref _selectedProjectionThreadId),
                ChatThread.Id,
                StringComparison.OrdinalIgnoreCase) || (_waitingForThreadView && !IsConversationLoading))
        {
            _isIncognitoDisclosureExpanded = false;
            _questionAttempt = null;
            _questionNeedsRefresh = false;
            await SwitchProjectionThreadAsync(ChatThread.Id);
        }

        if (IsConversationLoading) { return; }

        if (_goalThreadId != ChatThread.Id) { await RefreshGoalAsync(); }

        if (_permissionsThreadId != ChatThread.Id)
        {
            _permissionsThreadId = ChatThread.Id;
            ApplyPermissions(null);
            await RefreshPermissionsAsync();
        }

        if (_budgetSelectionKey != $"{ChatThread.Id}|{ComposerProvider?.Id}")
        {
            await RefreshBudgetAsync();
        }

        await RefreshContextSelectionAsync();

        var todoKey = $"{ChatThread.Id}|{ActiveAgentDefinition.Id}|{ActiveAgentDefinition.IsTodoEnabled}";
        if (string.Equals(_hydratedTodoKey, todoKey, StringComparison.Ordinal))
        {
            return;
        }

        _hydratedTodoKey = todoKey;
        _todos = [];

        if (!string.IsNullOrWhiteSpace(ChatThread.Id) && ActiveAgentDefinition.IsTodoEnabled)
        {
            await RefreshTodosAsync();
        }

        await RefreshAgentConversationsAsync();
    }

    private void OnDownloadsChangedForSpeech(object? sender, EventArgs args)
    {
        if (_isDisposed)
        {
            return;
        }

        _ = InvokeAsync(async () =>
        {
            await UpdateSpeechAvailabilityAsync();
            StateHasChanged();
        });
    }

    private ElementReference _chatRoot;
    private ElementReference _composerInput;
    private bool _restoreComposerFocus;
    private IJSObjectReference? _browserLinks;

    /// <summary>Opens a file reference only in the chat that emitted the clicked link.</summary>
    [JSInvokable]
    public Task OpenArtifactLink(string chatId, string artifactId) =>
        _isDisposed || chatId != ChatThread.Id ? Task.CompletedTask : OnOpenArtifact.InvokeAsync(artifactId);

    /// <summary>Opens an ordinary chat hyperlink in its owning browser workspace.</summary>
    [JSInvokable]
    public async Task OpenBrowserLink(string chatId, string url)
    {
        if (_isDisposed || chatId != ChatThread.Id || UserScope.BrowserWorkspace is not { IsAvailable: true } browser) { return; }

        try { await BrowserAccess.RequireAsync(); await browser.ExecuteAsync(chatId, new("open", Text: url)); }
        catch (Exception exception) { ToastService.Show(exception.Message, ToastType.Error); }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_isDisposed)
        {
            return;
        }

        if (firstRender)
        {
            try
            {
                _audioQueueModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/audioQueue.js");
                await _audioQueueModule.InvokeVoidAsync("init", _audioElementId);
                _utilsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/utils.js");
                await _utilsModule.InvokeVoidAsync("initializeChatComposerEnterGuard");

                if (_dotNetRef is not null && !_isDisposed)
                {
                    _browserLinks = await _utilsModule.InvokeAsync<IJSObjectReference>("initializeBrowserLinks", _chatRoot, _dotNetRef);
                    await _audioQueueModule.InvokeVoidAsync("setDotNetRef", _dotNetRef);
                }

                await UpdateSpeechAvailabilityAsync();
                RequestAutoScroll();
                await InvokeAsync(StateHasChanged);
                // Loaded last because an unreachable Engine can take seconds to refuse the connection.
                if (ShouldRouteSpeechToAvatar)
                {
                    _avatarEmotions = await AvatarBridge.GetEmotionsAsync();
                    if (_avatarEmotions.Count > 0)
                    {
                        // History loaded before the catalog must re-render without its tags.
                        await InvokeAsync(StateHasChanged);
                    }
                }
            }
            catch (JSDisconnectedException)
            {
                return;
            }
            catch (ObjectDisposedException) when (_isDisposed)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to initialize chat panel.");
            }
        }

        // Keep pending scroll restoration until the initial render loads the JavaScript module.
        if (_utilsModule is null) { return; }

        try
        {
            // Preflight disables the opener, so return to the draft once it is editable again.
            if (_restoreComposerFocus && !IsInputLocked)
            {
                _restoreComposerFocus = false;
                await _composerInput.FocusAsync();
            }

            if (!IsConversationLoading && _prependScrollMetrics is { Length: 2 } metrics)
            {
                _prependScrollMetrics = null;
                await _utilsModule!.InvokeVoidAsync("restorePrependedChatScroll", _chatScrollBody, metrics[0], metrics[1]);
            }
            else if (!IsConversationLoading && _restoreScrollTop is double top)
            {
                _restoreScrollTop = null;
                _shouldScrollToLatest = false;
                var thread = ChatThread;
                var utils = _utilsModule!;
                await utils.InvokeVoidAsync("restoreChatScroll", _chatScrollBody, top);
                var nearBottom = await utils.InvokeAsync<bool>("isScrolledNearBottom", _chatScrollBody);
                if (!_isDisposed && ReferenceEquals(ChatThread, thread) && _followLatest != nearBottom)
                {
                    _followLatest = nearBottom;
                    StateHasChanged();
                }
            }
            else if (_shouldScrollToLatest && !ShouldShowStartScreen && !IsConversationLoading)
            {
                _shouldScrollToLatest = false;
                await ScrollToLatestChat();
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (OperationCanceledException)
        {
            // Scroll restoration is optional when interop times out or the page is leaving.
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_goalStore is not null) { _goalStore.Changed -= OnGoalChanged; }

        RestoreGoalComposer();
        // Upload staging has not submitted a run, so its draft remains safe to recover.
        if (_optimisticRunRollback is { AdmissionStarted: false }) { RollbackOptimisticRun(); }

        SaveComposerDraft();
        _isDisposed = true;
        _runProjectionSubscription?.Dispose();
        CancelPendingRunStart();
        ResetActiveRun();

        var sessionToStop = _ttsSessionId;
        _ttsSessionId = null;
        ResetSpeechPlaybackGeneration();
        if (_emotionCues is not null)
        {
            await _emotionCues.CancelAsync();
        }

        if (!string.IsNullOrWhiteSpace(sessionToStop))
        {
            try
            {
                await SpeechService.ClearCacheAsync(sessionToStop);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to clear speech cache for session {SessionId} during chat disposal.", sessionToStop);
            }

            try
            {
                await SpeechService.StopSessionAsync(sessionToStop);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "Failed to stop speech session {SessionId} during chat disposal.", sessionToStop);
            }
        }

        ProfilePictureRefreshState.Changed -= OnProfilePictureChanged;
        DownloadService.DownloadsChanged -= OnDownloadsChangedForSpeech;

        // Stop VAD before disposing the module to release the microphone
        if (_voiceModeState != VoiceModeState.Inactive && _voiceModeModule is not null)
        {
            _ = _voiceModeModule.InvokeVoidAsync("stop").AsTask();
        }

        _voiceModeState = VoiceModeState.Inactive;

        if (_audioRecorderModule is not null)
        {
            // Releasing the JS reference alone does not release the browser microphone.
            try { await _audioRecorderModule.InvokeVoidAsync("stop"); }
            catch (JSDisconnectedException) { }
            catch (Exception ex) { Logger.LogWarning(ex, "Failed to stop microphone capture during chat disposal."); }
        }

        _isRecordingAudio = false;

        if (_browserLinks is not null)
        {
            try { await _browserLinks.InvokeVoidAsync("dispose"); await _browserLinks.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }

        _dotNetRef?.Dispose();
        _ = _voiceModeModule?.DisposeAsync().AsTask();
        _ = _audioQueueModule?.DisposeAsync().AsTask();
        _ = _audioRecorderModule?.DisposeAsync().AsTask();
        _ = _utilsModule?.DisposeAsync().AsTask();
        GC.SuppressFinalize(this);
    }

    private AgentDefinition ActiveAgentDefinition =>
        AgentSelectionResolver.Resolve(
            MainAgent,
            CustomAgents,
            SelectedAgentId,
            ChatThread.AgentId,
            ChatThread.DisplayMessages.Count > 0);

    private string? ActiveAgentPictureUrl => GetAgentPictureUrl(ActiveAgentDefinition.Id);

    private string ActiveAgentName =>
        string.IsNullOrWhiteSpace(ActiveAgentDefinition.Name) ? Loc.GetOrDefault(ChatTextKeys.AssistantFallbackName, "Assistant") : ActiveAgentDefinition.Name;

    private string ActiveAgentInitials =>
        string.IsNullOrWhiteSpace(ActiveAgentName) ? "A" : ActiveAgentName[..1].ToUpperInvariant();

    private string UserPictureUrl => $"/{ApiRoutes.ProfilePictures.User}?v={ProfilePictureRefreshState.Version}";

    private void OnProfilePictureChanged(object? sender, EventArgs args)
    {
        _ = InvokeAsync(StateHasChanged);
    }

    private ModelProviderConfig? SelectedProvider =>
        ResolveSelectedProviderId() is { } providerId
            ? ModelProviders.FirstOrDefault(provider => provider.Id == providerId)
            : null;

    private Guid? ResolveSelectedProviderId() => SelectedModelProviderId ?? DefaultModelProviderId;

    private string? CurrentContextUsageKey => ShouldShowStartScreen
        ? null
        : $"{ChatThread.Id}|{SelectedRecipientId ?? ActiveAgentDefinition.Id}|{ComposerProvider?.Id}";

    private int ContextUsageRingValue => Math.Clamp(_contextUsage?.EstimatedUsagePercentage ?? 0, 0, 100);

    private string ContextUsageColorClass => _contextUsage switch
    {
        null or { IsContextWindowKnown: false } => "text-base-content/40",
        { IsOverBudget: true } => "text-error",
        _ => "text-primary"
    };

    private string ContextUsageLabel => _contextUsage is { IsContextWindowKnown: true } usage
        ? string.Format(
            CultureInfo.CurrentCulture,
            Loc.GetOrDefault(
                ChatTextKeys.ContextUsageAriaLabelFormat,
                "Estimated context usage: {0:N0}% used, {1:N0}% free"),
            usage.EstimatedUsagePercentage,
            usage.EstimatedFreeInputPercentage)
        : Loc.GetOrDefault(ChatTextKeys.ContextUsageUnavailableLabel, "Context usage unavailable");

    private bool IsRunActive => _runState is ChatRunState.Streaming or ChatRunState.Continuing;

    private bool ShouldShowActiveRunPlaceholder =>
        IsRunActive &&
        ChatThread.DisplayMessages.LastOrDefault()?.From != MessageOwner.Bot;

    // The parent waits for its workers, or for the answer to a question it posted while they run.
    private string? SubagentWaitStatus => _runState != ChatRunState.AwaitingSubagents ? null
        : ChatThread.DisplayMessages.Any(message => message.Segments.Any(segment => UserQuestions.TryRead(message, segment, out var card) &&
            UserQuestions.GetState(ChatThread, card!) == UserQuestions.CardState.Pending))
            ? Loc.GetOrDefault(ChatTextKeys.WaitingForAnswerAndSubagentsStatus, "Waiting for your answer and subagents")
            : Loc.GetOrDefault(ChatTextKeys.WaitingForSubagentsStatus, "Waiting for subagents");

    private bool IsInputLocked => IsConversationLoading || _loadingAttachments || _isSubmitting || _runState != ChatRunState.Idle;

    private ChatDisplayMessage? FailedUserMessage =>
        _showFailedRunNotice && !string.IsNullOrWhiteSpace(_failedUserMessageId)
            ? ChatThread.DisplayMessages.FirstOrDefault(message =>
                message.From == MessageOwner.User &&
                string.Equals(message.MessageId, _failedUserMessageId, StringComparison.Ordinal))
            : null;

    private bool CanRetryFailedRun =>
        !IsInputLocked &&
        !IsPushToTalkActive &&
        IsServiceRunning &&
        !IsModelProviderSetupRequired &&
        FailedUserMessage is { } message &&
        !string.IsNullOrWhiteSpace(message.Text);

    private bool IsSessionInspectorAvailable =>
        ThreadView?.HasSession == true || ChatThread.SessionElement is not null || !string.IsNullOrWhiteSpace(ChatThread.SystemInstruction);

    private bool ShowTodoPanel => !IsConversationLoading && _todos.Count > 0;

    private int CompletedTodoCount => _todos.Count(todo => todo.IsComplete);

    private string TodoPanelProgressText =>
        FormatChatText(ChatTextKeys.TodoPanelProgressFormat, "{0} of {1} done", CompletedTodoCount, _todos.Count);

    private bool ShouldShowStartScreen =>
        ChatThread.DisplayMessages.Count == 0 &&
        !IsConversationLoading &&
        !IsRunActive;

    private bool IsStartScreenReady =>
        !IsCheckingService &&
        IsServiceRunning &&
        !IsModelProviderSetupRequired;

    private string StartScreenClass => _isStartScreenTransitioning ? "chat-start-screen-leaving" : string.Empty;

    private string ChatSurfaceClass => _isStartScreenTransitioning ? "chat-surface-entering" : string.Empty;

    private string StartQuestionText
    {
        get
        {
            if (IsCheckingService)
            {
                return Loc.GetOrDefault(ChatTextKeys.StartCheckingQuestion, "Getting your assistant ready...");
            }

            if (IsModelProviderSetupRequired)
            {
                return GetModelProviderSetupMessage();
            }

            if (!IsServiceRunning)
            {
                return string.IsNullOrWhiteSpace(ServiceStatusMessage)
                    ? Loc.GetOrDefault(ChatTextKeys.ServiceNotRunning, "The chat service is not running. Start it and retry.")
                    : ServiceStatusMessage;
            }

            return Loc.GetOrDefault(ChatTextKeys.StartQuestion, "How can I assist you today?");
        }
    }

    private string StartInputPlaceholderText =>
        IsCheckingService
            ? Loc.GetOrDefault(ChatTextKeys.CheckingChatServiceStatus, "Checking chat service status...")
            : Loc.GetOrDefault(ChatTextKeys.StartInputPlaceholder, "Let us know what you need...");

    private bool _isStartScreenTransitioning;

    private string DisplayFirstName
    {
        get
        {
            // The profile's preferred name is used as written, the account name falls back to its first word.
            var preferredName = UserScope.ProfileSettings.Nickname;
            if (!string.IsNullOrWhiteSpace(preferredName)) { return preferredName.Trim(); }

            var displayName = string.IsNullOrWhiteSpace(UserScope.DisplayName)
                ? UserScope.UserName
                : UserScope.DisplayName;

            if (string.IsNullOrWhiteSpace(displayName))
            {
                return CommonLoc.GetOrDefault(CommonTextKeys.User, "User");
            }

            var trimmedName = displayName.Trim();
            var firstSpaceIndex = trimmedName.IndexOf(' ', StringComparison.Ordinal);
            return firstSpaceIndex > 0 ? trimmedName[..firstSpaceIndex] : trimmedName;
        }
    }

    private StartOpeningText GetStartOpeningText()
    {
        if (!IsStartScreenReady)
        {
            return new StartOpeningText(
                StartQuestionText,
                Question: null,
                IsReady: false,
                IsProblem: !IsCheckingService && !IsModelProviderSetupRequired && !IsServiceRunning);
        }

        var userNow = DateTimeOffset.UtcNow.ToUserTime(UserScope.UiSettings.GetTimeZone());
        var bucket = GetStartOpeningTimeBucket(userNow.Hour);
        var selectedVariant = IsFirstChat
            ? FirstChatStartOpening
            : PickStartOpeningVariant(bucket, UserScope.UserId, userNow);

        return FormatStartOpeningVariant(selectedVariant);
    }

    private static StartOpeningTimeBucket GetStartOpeningTimeBucket(int hour) =>
        hour switch
        {
            >= 5 and < 12 => StartOpeningTimeBucket.Morning,
            >= 12 and < 17 => StartOpeningTimeBucket.Afternoon,
            >= 17 and < 21 => StartOpeningTimeBucket.Evening,
            _ => StartOpeningTimeBucket.Night
        };

    private static StartOpeningVariant PickStartOpeningVariant(
        StartOpeningTimeBucket bucket,
        string userId,
        DateTimeOffset userNow)
    {
        var bucketOpenings = GetBucketStartOpenings(bucket);
        var hash = GetStartScreenSeed(userId, userNow);
        var index = (int)((uint)hash % (uint)(CommonStartOpenings.Length + bucketOpenings.Count));
        return index < CommonStartOpenings.Length
            ? CommonStartOpenings[index]
            : bucketOpenings[index - CommonStartOpenings.Length];
    }

    private static IReadOnlyList<StartOpeningVariant> GetBucketStartOpenings(StartOpeningTimeBucket bucket) =>
        bucket switch
        {
            StartOpeningTimeBucket.Morning => MorningStartOpenings,
            StartOpeningTimeBucket.Afternoon => AfternoonStartOpenings,
            StartOpeningTimeBucket.Evening => EveningStartOpenings,
            _ => NightStartOpenings
        };

    private StartOpeningText FormatStartOpeningVariant(StartOpeningVariant variant)
    {
        var greeting = FormatChatStartText(
            $"{ChatStartTextKeys.DynamicPrefix}{variant.Id}.Greeting",
            Loc.GetOrDefault(ChatTextKeys.StartGreeting, "Hi there, {0}"),
            DisplayFirstName);

        var question = variant.HasQuestion
            ? StartLoc.GetOrDefault($"{ChatStartTextKeys.DynamicPrefix}{variant.Id}.Question", StartQuestionText)
            : null;

        return new StartOpeningText(greeting, question, IsReady: true);
    }

    // Keep decorative copy stable across refreshes without persisting browser or server state.
    private static int GetStartScreenSeed(string userId, DateTimeOffset userNow) =>
        HashCode.Combine(userId, userNow.Year, userNow.DayOfYear, userNow.Hour);

    private IReadOnlyList<StarterCard> StarterCards
    {
        get
        {
            var userNow = DateTimeOffset.UtcNow.ToUserTime(UserScope.UiSettings.GetTimeZone());
            var shuffled = StarterCardPool.ToArray();
            new Random(GetStartScreenSeed(UserScope.UserId, userNow)).Shuffle(shuffled);

            // A first chat always leads with the tour so a new user can ask what Oscova does.
            IEnumerable<StarterCard> selected = IsFirstChat
                ? [TourStarterCard, .. shuffled.Where(card => card != TourStarterCard).Take(2)]
                : shuffled.Take(3);

            return [.. selected.Select(LocalizeStarterCard)];
        }
    }

    private StarterCard LocalizeStarterCard(StarterCard card)
    {
        var prefix = $"{ChatTextKeys.StartCardPrefix}{card.Id}.";
        return card with
        {
            Title = Loc.GetOrDefault(prefix + "Title", card.Title),
            Description = Loc.GetOrDefault(prefix + "Description", card.Description),
            Action = Loc.GetOrDefault(prefix + "Action", card.Action),
            Prompt = Loc.GetOrDefault(prefix + "Prompt", card.Prompt)
        };
    }

    private string GetAssistantPresenceLabel()
    {
        if (IsCheckingService)
        {
            return Loc.GetOrDefault(ChatTextKeys.CheckingAvailability, "Checking availability");
        }

        if (IsModelProviderSetupRequired)
        {
            return HasNoModelProviders
                ? Loc.GetOrDefault(ChatTextKeys.NoModelProvidersPresence, "Model provider needed")
                : Loc.GetOrDefault(ChatTextKeys.ModelProviderSelectionRequiredPresence, "Choose a model provider");
        }

        return IsServiceRunning ? Loc.GetOrDefault(ChatTextKeys.LiveAndReady, "Live and ready") : Loc.GetOrDefault(ChatTextKeys.ServiceOffline, "Service offline");
    }

    private string GetModelProviderSetupMessage()
    {
        return HasNoModelProviders
            ? Loc.GetOrDefault(ChatTextKeys.AddModelProviderToStartChatting, "Add a model provider to start chatting.")
            : Loc.GetOrDefault(ChatTextKeys.SelectModelProviderToStartChatting, "Choose a model provider below to start chatting.");
    }

    private string GetModelProviderSetupButtonText()
    {
        return Loc.GetOrDefault(ChatTextKeys.AddModelProviderAction, "Add provider");
    }

    private void OpenModelProviderSettings()
    {
        NavigationManager.NavigateTo(SettingsRegistry.GetSectionRoute("providers"));
    }

    private string GetInputStateClass()
    {
        if (IsVoiceModeActive)
        {
            return _voiceModeState switch
            {
                VoiceModeState.Listening => "textarea-accent",
                VoiceModeState.Transcribing => "textarea-info",
                VoiceModeState.Processing => "textarea-info",
                VoiceModeState.Speaking => "textarea-accent",
                _ => ""
            };
        }

        if (_isRecordingAudio) return "textarea-warning";
        if (_isTranscribing) return "textarea-info";
        return "";
    }

    internal async Task ScrollToLatestChat(bool smooth = false)
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            _utilsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/utils.js");
            if (_isDisposed)
            {
                return;
            }

            await _utilsModule.InvokeVoidAsync("scrollToLatest", _chatScrollBody, smooth);
        }
        catch (JSDisconnectedException)
        {
        }
        catch (OperationCanceledException)
        {
            // Scrolling is optional when interop times out or the page is leaving.
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
        }
    }

    private void RequestAutoScroll(bool force = true)
    {
        // Callers trigger the render after mutating chat state; OnAfterRenderAsync scrolls once the DOM is current.
        if (_isDisposed || (!force && !_followLatest))
        {
            return;
        }

        if (force)
        {
            _followLatest = true;
        }

        var now = DateTime.UtcNow;
        if (!force && now - _lastAutoScrollRequestTime < ScrollThrottleInterval)
        {
            return;
        }

        _lastAutoScrollRequestTime = now;
        _shouldScrollToLatest = true;
    }

    private async Task ScrollLatestThrottledAsync()
    {
        if (_isDisposed || !_followLatest)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastAutoScrollRequestTime < ScrollThrottleInterval)
        {
            return;
        }

        _lastAutoScrollRequestTime = now;
        await ScrollToLatestChat();
    }

    private async Task OnChatScrolledAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        try
        {
            _utilsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/utils.js");
            if (_isDisposed)
            {
                return;
            }

            var threadId = ChatThread.Id;
            var metrics = await _utilsModule.InvokeAsync<double[]>("getChatScrollMetrics", _chatScrollBody);
            if (!IsConversationLoading && ChatThread.Id == threadId && metrics is { Length: 2 })
            {
                Drafts.SetScrollPosition(threadId, metrics[0]);
            }

            var nearBottom = await _utilsModule.InvokeAsync<bool>("isScrolledNearBottom", _chatScrollBody);
            if (_isDisposed || _followLatest == nearBottom)
            {
                return;
            }

            _followLatest = nearBottom;
            StateHasChanged();
        }
        catch (JSDisconnectedException)
        {
        }
        catch (OperationCanceledException)
        {
            // A canceled visual query leaves the current follow preference intact.
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
        }
    }

    private async Task JumpToLatestAsync()
    {
        _followLatest = true;
        _lastAutoScrollRequestTime = DateTime.UtcNow;
        await ScrollToLatestChat(smooth: true);
    }

    private async Task SelectProviderAsync(Guid? providerId)
    {
        await CloseDropdownsAsync();
        await OnSelectProvider.InvokeAsync(providerId);
    }

    private async Task ToggleIncognitoMode()
    {
        if (!CanChangeIncognitoMode) { return; }

        var thread = ChatThread;
        var previous = thread.IsIncognito;
        try
        {
            var permissions = await ChatApiClient.GetPermissionsAsync(thread.Id);
            thread.IsIncognito = !previous;
            if (permissions is not null)
            {
                await ChatApiClient.CreateThreadAsync(thread);
                ApplyPermissions(await ChatApiClient.GetPermissionsAsync(thread.Id));
            }

            if (!thread.IsIncognito) { _isIncognitoDisclosureExpanded = false; }
        }
        catch (Exception ex)
        {
            thread.IsIncognito = previous;
            ToastService.Show(ex.Message, ToastType.Error);
        }
    }

    private async Task SelectApprovalMode(ChatPermissionPreset preset)
    {
        var thread = ChatThread;
        try
        {
            if (await ChatApiClient.GetPermissionsAsync(thread.Id) is null)
            {
                await ChatApiClient.CreateThreadAsync(thread);
            }

            var saved = await ChatApiClient.SavePermissionsAsync(thread.Id, preset);
            if (!ReferenceEquals(ChatThread, thread)) { return; }

            ApplyPermissions(saved);
            await CloseDropdownsAsync();
        }
        catch (Exception ex) { ToastService.Show(ex.Message, ToastType.Error); }
    }

    private void ApplyPermissions(ChatPermissions? permissions)
    {
        _chatPermissions = permissions;
        _toolApprovalRunMode = permissions?.Preset switch
        {
            ChatPermissionPreset.AutoApprove or ChatPermissionPreset.FullAccess => ToolApprovalRunMode.AutoApproveRequired,
            ChatPermissionPreset.AutoReject => ToolApprovalRunMode.AutoRejectRequired,
            _ => ToolApprovalRunMode.UseAgentPolicy
        };
    }

    private async Task RefreshPermissionsAsync()
    {
        var thread = ChatThread;
        try
        {
            var permissions = await ChatApiClient.GetPermissionsAsync(thread.Id);
            if (permissions is null)
            {
                var devices = await NodesApiClient.GetAsync();
                var machine = Project?.NodeId is { } nodeId
                    ? devices.Nodes.FirstOrDefault(node => node.Node.Id == nodeId)?.Node.Name ?? Loc[ChatTextKeys.ProjectDevice]
                    : Environment.MachineName;
                permissions = new(ChatPermissionPreset.UseAgentPolicy, devices.IsOperator, machine, []);
            }

            if (ReferenceEquals(ChatThread, thread)) { ApplyPermissions(permissions); }
        }
        catch (Exception ex) { ToastService.Show(ex.Message, ToastType.Error); }
    }

    private async Task RevokePermissionAsync(string? grantId)
    {
        try
        {
            await ChatApiClient.RevokePermissionAsync(ChatThread.Id, grantId);
            await RefreshPermissionsAsync();
        }
        catch (Exception ex) { ToastService.Show(ex.Message, ToastType.Error); }
    }

    private async Task SelectAgentAsync(string? agentId)
    {
        if (!ShouldShowStartScreen || IsInputLocked)
        {
            return;
        }

        var activeAgent = string.IsNullOrWhiteSpace(agentId)
            ? MainAgent
            : CustomAgents.FirstOrDefault(agent => string.Equals(agent.Id, agentId, StringComparison.Ordinal)) ?? MainAgent;
        // Confirm file drafts while the original agent still owns the workspace scope.
        if (activeAgent.Id == ChatThread.AgentId || OnBeforeSelectAgent is not null && !await OnBeforeSelectAgent()) { return; }

        ChatThread.AgentId = activeAgent.Id;
        ChatThread.AgentName = activeAgent.Name;

        await CloseDropdownsAsync();
        await OnSelectAgent.InvokeAsync(agentId);
    }

    private async Task CloseDropdownsAsync()
    {
        _utilsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/utils.js");
        await _utilsModule.InvokeVoidAsync("closeDropdowns");
    }

    private async Task HandleBooleanElicitationResponse(BooleanElicitationChoice choice)
    {
        await SubmitElicitationResponseAsync(
            choice.RequestId,
            () => ChatApiClient.SubmitBooleanElicitationAsync(choice.RequestId, choice.Value, choice.IsCancel),
            CommonLoc.GetOrDefault(CommonTextKeys.DialogUnableToSubmitChoice, "Unable to submit that choice."));
    }

    private async Task HandleStringElicitationResponse(StringElicitationChoice choice)
    {
        await SubmitElicitationResponseAsync(
            choice.RequestId,
            () => ChatApiClient.SubmitStringElicitationAsync(choice.RequestId, choice.Values, choice.IsCancel),
            CommonLoc.GetOrDefault(CommonTextKeys.DialogUnableToSubmitChoice, "Unable to submit that choice."));
    }

    private async Task HandleNumberElicitationResponse(NumberElicitationChoice choice)
    {
        var values = choice.Values?.ToDictionary(
                static pair => pair.Key,
                static pair => new Core.ApiContracts.Chat.NumberElicitationValue
                {
                    Value = pair.Value.Value,
                    IsInteger = pair.Value.IsInteger
                },
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, Core.ApiContracts.Chat.NumberElicitationValue>(StringComparer.OrdinalIgnoreCase);

        await SubmitElicitationResponseAsync(
            choice.RequestId,
            () => ChatApiClient.SubmitNumberElicitationAsync(choice.RequestId, values, choice.IsCancel),
            CommonLoc.GetOrDefault(CommonTextKeys.DialogUnableToSubmitChoice, "Unable to submit that choice."));
    }

    private async Task HandleEnumElicitationResponse(EnumElicitationChoice choice)
    {
        await SubmitElicitationResponseAsync(
            choice.RequestId,
            () => ChatApiClient.SubmitEnumElicitationAsync(choice.RequestId, choice.Values, choice.IsCancel),
            CommonLoc.GetOrDefault(CommonTextKeys.DialogUnableToSubmitChoice, "Unable to submit that choice."));
    }

    private async Task HandleToolApprovalResponse(ToolApprovalChoice choice)
    {
        try
        {
            await ChatApiClient.SubmitToolApprovalAsync(choice.RequestId, choice.Decision);
            await RefreshPermissionsAsync();
        }
        catch
        {
            ToastService.Show(
                Loc.GetOrDefault(ChatTextKeys.ToolApprovalSubmitFailed, "Unable to submit that approval."),
                ToastType.Error);
        }
    }

    private async Task OnAttachmentsSelected(InputFileChangeEventArgs args)
    {
        if (IsInputLocked)
        {
            return;
        }

        var threadId = ChatThread.Id;
        _loadingAttachments = true;
        try
        {
            // The loop enforces the combined pending-file cap, so inspect the full picker selection here.
            foreach (var file in args.GetMultipleFiles(maximumFileCount: args.FileCount))
            {
                if (_pendingAttachments.Count >= ChatAttachmentLimits.MaxFilesPerMessage)
                {
                    ToastService.Show(FormatChatText(ChatTextKeys.TooManyFilesAttached, "You can attach at most {0} files per message.", ChatAttachmentLimits.MaxFilesPerMessage), ToastType.Warning);
                    break;
                }

                var extension = Path.GetExtension(file.Name);
                if (string.IsNullOrEmpty(extension) || !ChatAttachmentLimits.AllowedExtensions.ContainsKey(extension))
                {
                    ToastService.Show(FormatChatText(ChatTextKeys.UnsupportedFileType, "'{0}' isn't a supported file type yet.", file.Name), ToastType.Warning);
                    continue;
                }

                if (file.Size > ChatAttachmentLimits.MaxFileBytes)
                {
                    ToastService.Show(FormatChatText(ChatTextKeys.FileSizeExceeded, "'{0}' is over the {1} size limit.", file.Name, ByteSize.Format(ChatAttachmentLimits.MaxFileBytes)), ToastType.Warning);
                    continue;
                }

                try
                {
                    var buffered = await ChatDraftState.BufferAttachmentAsync(file, ChatAttachmentLimits.MaxFileBytes);
                    var id = Guid.NewGuid().ToString("N");
                    if (_isDisposed || ChatThread.Id != threadId) { return; }

                    _pendingAttachments.Add(new PendingAttachment { Id = id, File = buffered });
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Could not read attachment {FileName} for thread {ThreadId}.", file.Name, threadId);
                    if (!_isDisposed)
                    {
                        ToastService.Show(FormatChatText(ChatTextKeys.AttachmentReadFailed, "Couldn't attach '{0}'. Select it again.", file.Name), ToastType.Warning);
                    }
                }
            }
        }
        finally { _loadingAttachments = false; }
    }

    private void RemovePendingAttachment(string id)
    {
        if (IsInputLocked)
        {
            return;
        }

        _pendingAttachments.RemoveAll(attachment => attachment.Id == id);
    }

    private async Task<bool> ConfirmContextWindowFitAsync(PendingSend send)
    {
        ChatContextPreflightResponse preflight;
        try
        {
            preflight = await ChatApiClient.GetContextPreflightAsync(
                send.ThreadId,
                BuildContextPreflightRequest(send));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to estimate context window usage for thread {ThreadId}.", send.ThreadId);
            return true;
        }

        if (!preflight.IsContextWindowKnown || !preflight.IsOverBudget)
        {
            return true;
        }

        var result = await DialogService.ShowAsync<ContextWindowWarningDialog>(
            new Dictionary<string, object?> { ["Preflight"] = preflight });
        _restoreComposerFocus = result != DialogResult.Ok;
        return result == DialogResult.Ok;
    }

    /// <summary>Invalidates context estimates when the chat, recipient or model selection changes.</summary>
    private async Task RefreshContextSelectionAsync()
    {
        var key = CurrentContextUsageKey;
        if (string.Equals(_contextUsageKey, key, StringComparison.Ordinal)) { return; }

        _contextUsageKey = key;
        _contextUsage = null;
        if (key is not null) { await RefreshContextUsageAsync(key); }
    }

    /// <summary>Refreshes the estimated context for the current chat selection.</summary>
    private async Task RefreshContextUsageAsync(string expectedKey)
    {
        var providerId = SelectedProvider?.Id;
        if (string.IsNullOrWhiteSpace(ChatThread.Id) || providerId is null)
        {
            return;
        }

        var threadId = ChatThread.Id;
        var requestVersion = Interlocked.Increment(ref _contextUsageRequestVersion);
        try
        {
            // Exclude unsent input so this measures only context that exists now.
            var response = await ChatApiClient.GetContextPreflightAsync(
                threadId,
                new ChatContextPreflightRequest
                {
                    AgentId = ActiveAgentDefinition.Id,
                    RecipientAgentId = SelectedRecipientId,
                    ProjectId = ChatThread.ProjectId,
                    ModelProviderId = providerId.Value.ToString(),
                    PromptText = string.Empty,
                    Attachments = []
                });

            // A slower request must not replace the estimate for a newer chat selection.
            if (!_isDisposed &&
                requestVersion == Volatile.Read(ref _contextUsageRequestVersion) &&
                string.Equals(_contextUsageKey, expectedKey, StringComparison.Ordinal))
            {
                _contextUsage = response;
                StateHasChanged();
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to estimate context window usage for thread {ThreadId}.", threadId);
        }
    }

    private async Task ShowContextUsageAsync()
    {
        if (_contextUsage is null)
        {
            return;
        }

        await DialogService.ShowAsync<ContextUsageDialog>(
            new Dictionary<string, object?> { ["ContextUsage"] = _contextUsage });
    }

    private void OnComposerInputChanged()
    {
        ForgetStrippedMention();
        _recipientMenuDismissed = false;
        _slashMenuDismissed = false;
        _selectedSlashCommandIndex = 0;
    }

    private void MoveSlashCommandSelection(int delta)
    {
        var count = FilteredSlashCommands.Count;
        if (count == 0)
        {
            return;
        }

        _selectedSlashCommandIndex = (_selectedSlashCommandIndex + delta + count) % count;
    }

    private void CompleteSelectedSlashCommand()
    {
        var commands = FilteredSlashCommands;
        if (commands.Count == 0)
        {
            return;
        }

        _selectedSlashCommandIndex = Math.Min(_selectedSlashCommandIndex, commands.Count - 1);
        _inputText = commands[_selectedSlashCommandIndex].Name;
    }

    private async Task ExecuteSelectedSlashCommandAsync()
    {
        var commands = FilteredSlashCommands;
        if (commands.Count == 0)
        {
            return;
        }

        _selectedSlashCommandIndex = Math.Min(_selectedSlashCommandIndex, commands.Count - 1);
        await ExecuteSlashCommandAsync(commands[_selectedSlashCommandIndex].Name);
    }

    private async Task<bool> TryExecuteSlashCommandAsync()
    {
        if (!_inputText.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        var input = _inputText.Trim();
        // The topic form is sent to the agent as an ordinary message; the bundled research skill explains it.
        if (input.Length > ResearchCommand.Length && input.StartsWith(ResearchCommand, StringComparison.OrdinalIgnoreCase)
            && char.IsWhiteSpace(input[ResearchCommand.Length]))
        {
            return false;
        }

        var command = AvailableSlashCommands.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, input, StringComparison.OrdinalIgnoreCase));
        if (command.Name is null)
        {
            var message = FormatChatText(
                ChatTextKeys.SlashUnknownCommandFormat,
                "Unknown or unavailable command: {0}",
                input);
            _liveAnnouncement = message;
            ToastService.Show(message, ToastType.Warning);
            return true;
        }

        await ExecuteSlashCommandAsync(command.Name);
        return true;
    }

    // A bare /research is half-typed, so it completes to "/research " instead of being sent or queued as a follow-up.
    // Focus is restored directly because the deferred restore waits for an active run to end.
    private async Task<bool> TryCompleteBareResearchCommandAsync()
    {
        if (!_inputText.Trim().Equals(ResearchCommand, StringComparison.OrdinalIgnoreCase)) { return false; }

        _inputText = ResearchCommand + " ";
        await _composerInput.FocusAsync();
        return true;
    }

    private async Task<bool> TryExecuteLocalComposerActionAsync()
    {
        if (await TryExecuteGoalCommandAsync()) { return true; }

        if (!IsVoiceModeActive &&
            _chatEasterEgg is not null &&
            await _chatEasterEgg.TryActivateAsync(_inputText))
        {
            _inputText = string.Empty;
            return true;
        }

        return await TryExecuteSlashCommandAsync();
    }

    private async Task ExecuteSlashCommandAsync(string command)
    {
        _inputText = string.Empty;
        _slashMenuDismissed = true;
        _isSubmitting = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            switch (command)
            {
                case "/goal":
                    await RefreshGoalAsync();
                    if (_goal is null) { BeginGoalComposer(false, string.Empty); }

                    break;
                case "/compact":
                    await CompactSessionAsync();
                    break;
                case "/context":
                    await RefreshAndShowContextUsageAsync();
                    break;
                case "/new":
                    await OnStartNewChat.InvokeAsync();
                    break;
                case "/rename":
                    await RenameCurrentThreadAsync();
                    break;
                case ResearchCommand:
                    _inputText = ResearchCommand + " ";
                    _restoreComposerFocus = true;
                    break;
            }
        }
        finally
        {
            _isSubmitting = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task CompactSessionAsync()
    {
        try
        {
            var result = await ChatApiClient.CompactThreadAsync(ChatThread.Id, SelectedProvider?.Id, confirmBudget: ConfirmBudgetAsync);
            if (result is null) { return; }

            var changed = result.Value;
            var message = changed
                ? Loc.GetOrDefault(ChatTextKeys.SlashCompactComplete, "Context compacted.")
                : Loc.GetOrDefault(ChatTextKeys.SlashCompactNoChange, "There is no older context to compact.");
            _liveAnnouncement = message;
            ToastService.Show(message, changed ? ToastType.Success : ToastType.Info);

            if (CurrentContextUsageKey is { } contextKey)
            {
                await RefreshContextUsageAsync(contextKey);
                await RefreshBudgetAsync();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to compact chat session {ThreadId}.", ChatThread.Id);
            var message = Loc.GetOrDefault(ChatTextKeys.SlashCompactFailed, "The context could not be compacted.");
            _liveAnnouncement = message;
            ToastService.Show(message, ToastType.Error);
        }
    }

    private async Task RefreshAndShowContextUsageAsync()
    {
        var provider = SelectedProvider;
        if (provider is null)
        {
            return;
        }

        try
        {
            _contextUsage = await ChatApiClient.GetContextPreflightAsync(
                ChatThread.Id,
                new ChatContextPreflightRequest
                {
                    AgentId = ActiveAgentDefinition.Id,
                    RecipientAgentId = SelectedRecipientId,
                    ProjectId = ChatThread.ProjectId,
                    ModelProviderId = provider.Id.ToString(),
                    PromptText = string.Empty,
                    Attachments = []
                });
            await ShowContextUsageAsync();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load context usage for thread {ThreadId}.", ChatThread.Id);
            var message = Loc.GetOrDefault(ChatTextKeys.SlashContextFailed, "Context usage could not be loaded.");
            _liveAnnouncement = message;
            ToastService.Show(message, ToastType.Error);
        }
    }

    private async Task RenameCurrentThreadAsync()
    {
        var result = await DialogService.ShowAsync<RenameThreadDialog, string>(
            new Dictionary<string, object?> { ["CurrentTitle"] = ChatThread.Title });
        if (!result.IsOk || string.IsNullOrWhiteSpace(result.Data))
        {
            return;
        }

        if (ChatThread.DisplayMessages.Count == 0 && ChatThread.SessionElement is null && ThreadView?.HasSession != true)
        {
            ChatThread.Title = result.Data;
            await OnCurrentThreadChanged.InvokeAsync(ChatThread);
            return;
        }

        await OnRenameThread.InvokeAsync((ChatThread.Id, result.Data));
    }

    private static ChatContextPreflightRequest BuildContextPreflightRequest(PendingSend send)
    {
        return new ChatContextPreflightRequest
        {
            AgentId = send.AgentId,
            RecipientAgentId = send.RecipientAgentId,
            ProjectId = send.ProjectId,
            ModelProviderId = send.ModelProviderId,
            PromptText = send.UserText,
            Attachments = [.. send.Attachments.Select(CreatePendingAttachmentSummary)],
            WorkspaceFiles = send.Files ?? []
        };
    }

    private PendingSend CapturePendingSend()
    {
        var agent = ActiveAgentDefinition;
        return new PendingSend(
            ChatThread,
            ChatThread.Id,
            ChatThread.ProjectId,
            agent.Id,
            agent.Name,
            SelectedModelProviderId?.ToString(),
            _inputText.Trim(),
            _inputText,
            ClearComposer: true,
            Attachments: _pendingAttachments.ToList(),
            ApprovalRunMode: _toolApprovalRunMode,
            RecipientAgentId: SelectedRecipientId,
            Files: _pendingFiles.ToArray());
    }

    private static PendingChatAttachmentSummary CreatePendingAttachmentSummary(PendingAttachment attachment)
    {
        return new PendingChatAttachmentSummary
        {
            FileName = attachment.File.Name,
            MediaType = string.IsNullOrWhiteSpace(attachment.File.ContentType)
                ? "application/octet-stream"
                : attachment.File.ContentType,
            SizeBytes = attachment.File.Size
        };
    }

    private async Task ProcessInput()
    {
        if (IsInputLocked || IsPushToTalkActive)
        {
            return;
        }

        if (await TryExecuteLocalComposerActionAsync() || !CanSend)
        {
            return;
        }

        await ProcessPendingSendAsync(CapturePendingSend());
    }

    private async Task ProcessPendingSendAsync(PendingSend send)
    {
        if (IsInputLocked || IsPushToTalkActive)
        {
            return;
        }

        _isSubmitting = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            if (!await ConfirmContextWindowFitAsync(send) ||
                !await EnsureThreadExistsBeforeFirstRunAsync(send))
            {
                return;
            }

            await SendInputAsync(send);
        }
        finally
        {
            _isSubmitting = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task RetryFailedRunAsync()
    {
        if (!CanRetryFailedRun || FailedUserMessage is not { } message)
        {
            return;
        }

        var agent = ActiveAgentDefinition;
        await ProcessPendingSendAsync(new PendingSend(
            ChatThread,
            ChatThread.Id,
            ChatThread.ProjectId,
            agent.Id,
            agent.Name,
            SelectedModelProviderId?.ToString(),
            message.Text,
            TextWithoutPendingMention,
            ClearComposer: false,
            Attachments: [],
            ApprovalRunMode: _toolApprovalRunMode,
            RecipientAgentId: message.RecipientAgentId));
    }

    private bool _isBranching;

    // Regenerating the copied answer keeps the divider at the end of its original user turn.
    private string? GetBranchBoundaryMessageId()
    {
        if (ChatThread.BranchOrigin is not { } origin) { return null; }

        var messages = ChatThread.DisplayMessages;
        var anchor = messages.FirstOrDefault(message => message.MessageId == origin.UserMessageId);
        if (anchor is null) { return null; }

        var nextUser = messages.Skip(messages.IndexOf(anchor) + 1)
            .FirstOrDefault(message => message.From == MessageOwner.User && !message.IsInteractionResponse);
        return nextUser is null ? messages.LastOrDefault()?.MessageId :
            messages[messages.IndexOf(nextUser) - 1].MessageId;
    }

    private async Task BranchResponseAsync(ChatDisplayMessage response)
    {
        if (_isBranching || IsInputLocked ||
            !ChatThread.BranchableMessageIds.Contains(response.MessageId, StringComparer.Ordinal))
        {
            return;
        }

        var source = ChatThread;
        _isBranching = true;
        try
        {
            var child = await ChatApiClient.BranchThreadAsync(source.Id, response.MessageId);
            if (_isDisposed || !ReferenceEquals(ChatThread, source)) { return; }

            NavigationManager.NavigateTo(NavigationManager.GetUriWithQueryParameter("threadId", child.Id));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to branch chat {ThreadId} at reply {MessageId}.", source.Id, response.MessageId);
            if (!_isDisposed && ReferenceEquals(ChatThread, source))
            {
                ToastService.Show(Loc.GetOrDefault(ChatTextKeys.BranchFailed,
                    "The chat could not be branched. Try again."), ToastType.Error);
            }
        }
        finally
        {
            _isBranching = false;
        }
    }

    private async Task RegenerateResponseAsync(ChatDisplayMessage response)
    {
        var messages = ChatThread.DisplayMessages;
        if (IsInputLocked ||
            messages.Count == 0 ||
            !ReferenceEquals(messages[^1], response) ||
            response.From != MessageOwner.Bot || response.IsInteractionResponse)
        {
            return;
        }

        ChatDisplayMessage? userMessage = null;
        for (var index = messages.Count - 2; index >= 0; index--)
        {
            var candidate = messages[index];
            if (candidate.From == MessageOwner.User && !candidate.IsInteractionResponse)
            {
                userMessage = candidate;
                break;
            }
        }

        if (userMessage is null)
        {
            return;
        }

        IdleLogoutActivity.RecordSubmit();
        HideRegenerationTail(userMessage.MessageId);

        var runId = $"run_{Guid.NewGuid():N}";
        var startCts = BeginLocalRun(runId);
        var accepted = false;
        try
        {
            RequestAutoScroll();
            await InvokeAsync(StateHasChanged);

            var started = await ChatApiClient.TryStartRunAsync(
                ChatThread.Id,
                new StartChatRunRequest
                {
                    RunId = runId,
                    UserMessageId = userMessage.MessageId,
                    AgentId = ActiveAgentDefinition.Id,
                    ModelProviderId = SelectedModelProviderId?.ToString(),
                    ApprovalRunMode = _toolApprovalRunMode,
                    RegenerateAssistantMessageId = response.MessageId
                },
                startCts.Token, ConfirmBudgetAsync);
            if (!started)
            {
                RollbackRegeneration();
                _locallyStartedEffectRunId = null;
                await ApplyLatestProjectionAsync(ChatThread.Id, hydration: true);
                await InvokeAsync(StateHasChanged);
                return;
            }

            accepted = true;
            _showFailedRunNotice = false;
            _failedUserMessageId = null;
        }
        catch (OperationCanceledException)
        {
            if (!accepted)
            {
                RollbackRegeneration();
                _locallyStartedEffectRunId = null;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "There was a problem regenerating the response.");
            if (!string.Equals(_activeOperationId, runId, StringComparison.Ordinal))
            {
                return;
            }

            ToastService.Show(ex.Message, ToastType.Error);
            _ = Sound.PlayAsync(SoundCue.Error);
            RollbackRegeneration();
            _locallyStartedEffectRunId = null;
            RequestAutoScroll();
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            if (ReferenceEquals(_runStartCts, startCts))
            {
                _runStartCts = null;
            }

            startCts.Dispose();
        }
    }

    private async Task SendInputAsync(PendingSend send)
    {
        // Navigation invalidates work that has not reached server run admission.
        if (_runState != ChatRunState.Idle ||
            !string.Equals(send.ThreadId, ChatThread.Id, StringComparison.OrdinalIgnoreCase) ||
            !send.ContinueSession && string.IsNullOrWhiteSpace(send.UserText) && send.Attachments.Count == 0 && (send.Files?.Count ?? 0) == 0)
        {
            return;
        }

        IdleLogoutActivity.RecordSubmit();

        // The start-screen path plays its own "enter" cue over the blend; voice mode uses TTS, so skip the send cue there.
        if (!_isStartScreenTransitioning && !IsVoiceModeActive)
        {
            _ = Sound.PlayAsync(SoundCue.Send);
        }

        var userText = send.UserText;
        if (send.ClearComposer &&
            string.Equals(_inputText, send.ComposerTextToRestore, StringComparison.Ordinal))
        {
            _inputText = string.Empty;
        }

        // Snapshot pending attachments so send starts with a stable list even if the user
        // picks more files while the request is in flight.
        var attachmentsToSend = send.Attachments;
        _pendingFiles.RemoveAll(file => send.Files?.Contains(file) == true);
        _pendingAttachments.RemoveAll(attachment =>
            send.Attachments.Any(sent => string.Equals(sent.Id, attachment.Id, StringComparison.Ordinal)));

        // Continuation uses a fresh request identity without adding its envelope to the transcript.
        var userMessageId = $"msg_{Guid.NewGuid():N}";
        var runId = $"run_{Guid.NewGuid():N}";
        if (!send.ContinueSession)
        {
            var userMessage = new ChatDisplayMessage
            {
                MessageId = userMessageId,
                Channel = HookExecutionKinds.WebChat,
                From = MessageOwner.User,
                Text = userText,
                RecipientAgentId = send.RecipientAgentId ?? send.AgentId,
                Attachments = [.. attachmentsToSend.Select(CreatePendingAttachmentPreview)],
                WorkspaceFiles = send.Files ?? []
            };

            _optimisticRunRollback = new OptimisticRunRollback(
                send.Thread,
                userMessage,
                send.ComposerTextToRestore,
                send.Attachments,
                send.Thread.Title,
                send.Thread.AgentId,
                send.Thread.AgentName,
                send.ClearComposer,
                Files: send.Files);
            send.Thread.DisplayMessages.Add(userMessage);

            if (string.IsNullOrWhiteSpace(send.Thread.Title))
            {
                send.Thread.Title = ChatDisplayMessageFormatter.BuildPlaceholderTitle(userText, userMessage.Attachments, userMessage.WorkspaceFiles);
                send.Thread.AgentId = send.AgentId;
                send.Thread.AgentName = send.AgentName;
            }
        }

        var startCts = BeginLocalRun(runId);
        var accepted = false;

        try
        {
            RequestAutoScroll();
            await InvokeAsync(StateHasChanged);

            IReadOnlyList<string> stagedIds = Array.Empty<string>();
            if (attachmentsToSend.Count > 0)
            {
                var pendingPayload = attachmentsToSend
                    .Select(attachment => new ChatApiClient.PendingChatAttachment
                    {
                        FileName = attachment.File.Name,
                        ContentType = string.IsNullOrWhiteSpace(attachment.File.ContentType)
                            ? "application/octet-stream"
                            : attachment.File.ContentType,
                        Content = attachment.File.OpenReadStream(maxAllowedSize: ChatAttachmentLimits.MaxFileBytes)
                    })
                    .ToList();

                var staged = await ChatApiClient.StageAttachmentsAsync(
                    send.ThreadId,
                    pendingPayload,
                    startCts.Token);
                stagedIds = staged.Select(s => s.Id).ToList();
            }

            if (_optimisticRunRollback is { } pendingRollback)
            {
                _optimisticRunRollback = pendingRollback with { AdmissionStarted = true };
            }

            var started = await ChatApiClient.TryStartRunAsync(
                send.ThreadId,
                new StartChatRunRequest
                {
                    RunId = runId,
                    UserMessageId = userMessageId,
                    UserText = userText,
                    AgentId = send.AgentId,
                    RecipientAgentId = send.RecipientAgentId,
                    ModelProviderId = send.ModelProviderId,
                    ApprovalRunMode = send.ApprovalRunMode,
                    AttachmentIds = stagedIds,
                    WorkspaceFiles = send.Files ?? [],
                    ContinueSession = send.ContinueSession
                },
                startCts.Token, ConfirmBudgetAsync);
            if (!string.Equals(_activeOperationId, runId, StringComparison.Ordinal))
            {
                return;
            }

            if (!started)
            {
                RollbackOptimisticRun();
                if (send.ContinueSession) { ResetActiveRun(); }

                _locallyStartedEffectRunId = null;
                await ApplyLatestProjectionAsync(send.ThreadId, hydration: true);
                await InvokeAsync(StateHasChanged);
                return;
            }

            accepted = true;
            if (SelectedRecipientId == send.RecipientAgentId) { await RemoveRecipientAsync(); }
            // Keep the submitted bubble until publication without restoring an admitted send as a draft.
            if (_optimisticRunRollback is { } rollback) { _optimisticRunRollback = rollback with { IsAdmitted = true }; }

            _showFailedRunNotice = false;
            _failedUserMessageId = null;
        }
        catch (OperationCanceledException)
        {
            if (!accepted && string.Equals(_activeOperationId, runId, StringComparison.Ordinal))
            {
                RollbackOptimisticRun();
                if (send.ContinueSession) { ResetActiveRun(); }

                _locallyStartedEffectRunId = null;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "There was a problem processing the input.");
            if (!string.Equals(_activeOperationId, runId, StringComparison.Ordinal))
            {
                return;
            }

            ToastService.Show(ex.Message, ToastType.Error);
            _ = Sound.PlayAsync(SoundCue.Error);
            RollbackOptimisticRun();
            if (send.ContinueSession) { ResetActiveRun(); }

            _locallyStartedEffectRunId = null;
            RequestAutoScroll();
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            if (ReferenceEquals(_runStartCts, startCts))
            {
                _runStartCts = null;
            }

            startCts.Dispose();
        }
    }

    private void FillStarterPrompt(StarterCard starterCard)
    {
        if (!IsStartScreenReady || IsInputLocked || IsVoiceModeActive)
        {
            return;
        }

        _inputText = starterCard.Prompt;
        _ = Sound.PlayAsync(SoundCue.Tap);
    }

    private async Task ProcessStartScreenInputAsync()
    {
        if (!IsStartScreenReady || IsInputLocked || IsPushToTalkActive)
        {
            return;
        }

        if (await TryExecuteLocalComposerActionAsync() || !CanSend)
        {
            return;
        }

        var send = CapturePendingSend();
        _isSubmitting = true;
        await InvokeAsync(StateHasChanged);
        try
        {
            if (!await ConfirmContextWindowFitAsync(send))
            {
                return;
            }

            if (!await EnsureThreadExistsBeforeFirstRunAsync(send))
            {
                return;
            }

            _isStartScreenTransitioning = true;
            // Ride the "suck into the chat" cue over the start-screen blend.
            _ = Sound.PlayAsync(SoundCue.Enter);
            await InvokeAsync(StateHasChanged);
            await Task.Delay(260);

            await SendInputAsync(send);
        }
        finally
        {
            _isStartScreenTransitioning = false;
            _isSubmitting = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task<bool> EnsureThreadExistsBeforeFirstRunAsync(PendingSend send)
    {
        if (send.Thread.DisplayMessages.Count > 0)
        {
            return true;
        }

        try
        {
            // Persist the client-created thread identity before Stop can race run admission.
            await ChatApiClient.CreateThreadAsync(send.Thread);
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create chat thread {ThreadId} before starting its first run.", send.ThreadId);
            ToastService.Show(ex.Message, ToastType.Error);
            _ = Sound.PlayAsync(SoundCue.Error);
            return false;
        }
    }

    private async Task HandleStartComposerKeyDown(KeyboardEventArgs e)
    {
        if (await HandleSlashCommandKeyAsync(e))
        {
            return;
        }

        if (e.Key != "Enter" || e.ShiftKey)
        {
            return;
        }

        await ProcessStartScreenInputAsync();
    }

    // Keydown, not keyup: keyup reports the modifier state at release, so releasing Shift before Enter reads as a plain Enter and sends.
    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !e.ShiftKey)
        {
            if (!RemovePendingMention() || await TryExecuteGoalCommandAsync() || await TryCompleteBareResearchCommandAsync()) { return; }
        }

        if (await HandleRecipientKeyAsync(e)) { return; }

        if (await HandleSlashCommandKeyAsync(e))
        {
            return;
        }

        // Escape is the stop key; Enter must never destroy a reply the user is waiting for.
        if (e.Key == "Escape")
        {
            if (HasLiveRun)
            {
                await CancelResponse();
            }

            return;
        }

        if (e.Key != "Enter" || e.ShiftKey)
        {
            return;
        }

        if (HasLiveRun)
        {
            var mode = UserScope.UiSettings.ChatFollowUpMode;
            // Ctrl+Enter overrides the saved behavior for this message.
            if (e.CtrlKey)
            {
                mode = mode == ChatFollowUpMode.Steer ? ChatFollowUpMode.Queue : ChatFollowUpMode.Steer;
            }

            await SendFollowUpAsync(mode);
            return;
        }

        if (IsRunActive)
        {
            return;
        }

        await ProcessInput();
    }

    private async Task<bool> HandleSlashCommandKeyAsync(KeyboardEventArgs e)
    {
        if (!IsSlashCommandMenuOpen)
        {
            return false;
        }

        switch (e.Key)
        {
            case "Escape":
                _slashMenuDismissed = true;
                return true;
            case "ArrowUp":
                MoveSlashCommandSelection(-1);
                return true;
            case "ArrowDown":
                MoveSlashCommandSelection(1);
                return true;
            case "Tab":
                CompleteSelectedSlashCommand();
                return true;
            case "Enter" when !e.ShiftKey:
                await ExecuteSelectedSlashCommandAsync();
                return FilteredSlashCommands.Count > 0;
            default:
                return false;
        }
    }

    private async Task CancelResponse()
    {
        if (!HasLiveRun)
        {
            return;
        }

        var threadId = ChatThread.Id;
        var runId = _activeOperationId;
        if (string.IsNullOrWhiteSpace(runId))
        {
            ResetActiveRun();
            return;
        }

        try
        {
            await ChatApiClient.StopRunAsync(threadId, runId);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to stop active run for thread {ThreadId}.", threadId);
        }
    }

    // Preserves message identity so open question cards retain their component state.
    private void MergePersistedMessages(ChatThread refreshed)
    {
        var messages = new List<ChatDisplayMessage>();
        foreach (var source in refreshed.DisplayMessages)
        {
            var target = ChatThread.DisplayMessages.FirstOrDefault(message => message.MessageId == source.MessageId);
            if (target is null)
            {
                target = source;
            }
            else
            {
                CopyProjectedMessage(source, target);
            }

            messages.Add(target);
        }

        ChatThread.DisplayMessages = messages;
        // A persisted read may finish after a newer reply was already projected.
        if (RunProjections.TryGetSnapshot(UserScope.UserId, ChatThread.Id, out var snapshot) &&
            snapshot is { RunMessages: not null } && snapshot.InstanceId == _observedProjectionInstanceId &&
            snapshot.Sequence <= _lastProjectionSequence && !HasLaterPersistedTurn(snapshot))
        {
            var changed = new List<ChatDisplayMessage>();
            ReconcileProjectionMessages(snapshot.RunMessages, changed);
            foreach (var message in changed) { BotMessageView.NotifyChanged(message); }
        }
    }

    // Channel turns can advance the saved conversation beyond its last web projection.
    private bool HasLaterPersistedTurn(ChatRunSnapshot snapshot)
    {
        if (snapshot.Phase != ChatRunProjectionPhase.Terminal) { return false; }

        var laterMessages = ChatThread.DisplayMessages
            .SkipWhile(message => message.MessageId != snapshot.UserMessageId).Skip(1);
        if (!ChatThread.DisplayMessages.Any(message => message.MessageId == snapshot.UserMessageId))
        {
            var projectedUser = snapshot.RunMessages?.FirstOrDefault(message => message.MessageId == snapshot.UserMessageId);
            if (projectedUser is null) { return false; }

            laterMessages = ChatThread.DisplayMessages.Where(message => message.CreatedAt > projectedUser.CreatedAt);
        }

        return laterMessages.Any(message =>
            message.From == MessageOwner.User && !message.IsInteractionResponse &&
            snapshot.RunMessages?.Any(projected => projected.MessageId == message.MessageId) != true);
    }

    /// <summary>Refreshes the loaded display window without expanding it to the complete transcript.</summary>
    private async Task<ChatThread?> ReadDisplayedThreadAsync(string threadId)
    {
        var thread = ChatThread;
        if (ThreadView is null) { return await ChatApiClient.GetThreadAsync(threadId); }

        var firstMessageId = thread.DisplayMessages.FirstOrDefault()?.MessageId;
        var view = await ChatApiClient.GetThreadViewAsync(threadId, firstMessageId: firstMessageId);
        if (_isDisposed || !ReferenceEquals(ChatThread, thread) || view is null) { return null; }
        // Keep pages prepended while the display refresh was awaiting its response.
        if (firstMessageId is not null && thread.DisplayMessages.FirstOrDefault()?.MessageId != firstMessageId)
        {
            var prefix = thread.DisplayMessages.TakeWhile(message => message.MessageId != firstMessageId);
            view.Thread.DisplayMessages = prefix.Concat(view.Thread.DisplayMessages).ToList();
            view.Thread.BranchableMessageIds = thread.BranchableMessageIds.Concat(view.Thread.BranchableMessageIds).Distinct(StringComparer.Ordinal).ToArray();
            view.HasOlderMessages = ThreadView.HasOlderMessages;
        }

        ThreadView.HasOlderMessages = view.HasOlderMessages;
        ThreadView.HasSession = view.HasSession;
        ThreadView.CanContinue = view.CanContinue;
        ThreadView.ContinuationRecipientAgentId = view.ContinuationRecipientAgentId;
        ChatThread.BranchableMessageIds = view.Thread.BranchableMessageIds;
        return view.Thread;
    }

    /// <summary>Prepends older exchanges while retaining the reader's current position.</summary>
    private async Task LoadOlderMessagesAsync()
    {
        if (_loadingOlderMessages || ThreadView?.HasOlderMessages != true || IsConversationLoading) { return; }

        var thread = ChatThread;
        var firstMessageId = thread.DisplayMessages.FirstOrDefault()?.MessageId;
        if (firstMessageId is null) { return; }

        _loadingOlderMessages = true;
        try
        {
            var page = await ChatApiClient.GetThreadViewAsync(thread.Id, beforeMessageId: firstMessageId);
            if (_isDisposed || !ReferenceEquals(ChatThread, thread) || page is null) { return; }

            _utilsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "/js/utils.js");
            var metrics = await _utilsModule.InvokeAsync<double[]>("getChatScrollMetrics", _chatScrollBody);
            if (_isDisposed || !ReferenceEquals(ChatThread, thread)) { return; }

            var existing = thread.DisplayMessages.Select(message => message.MessageId).ToHashSet(StringComparer.Ordinal);
            var older = page.Thread.DisplayMessages.Where(message => !existing.Contains(message.MessageId));
            thread.DisplayMessages = older.Concat(thread.DisplayMessages).ToList();
            thread.BranchableMessageIds = page.Thread.BranchableMessageIds.Concat(thread.BranchableMessageIds).Distinct(StringComparer.Ordinal).ToArray();
            ThreadView!.HasOlderMessages = page.HasOlderMessages;
            _prependScrollMetrics = metrics;
            _shouldScrollToLatest = false;
        }
        catch (JSDisconnectedException)
        {
        }
        catch (OperationCanceledException)
        {
            // A canceled scroll measurement leaves the current history window intact.
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JSException)
        {
            Logger.LogWarning(ex, "Older messages could not be loaded for thread {ThreadId}.", thread.Id);
            if (ReferenceEquals(ChatThread, thread))
            {
                ToastService.Show(Loc.GetOrDefault(ChatTextKeys.LoadOlderMessagesFailed, "Older messages could not be loaded. Try again."), ToastType.Error);
            }
        }
        finally
        {
            if (!_isDisposed && ReferenceEquals(ChatThread, thread)) { _loadingOlderMessages = false; }
        }
    }

    private bool CanContinueSession
    {
        get
        {
            if (CanSend || HasLiveRun || _isSubmitting || IsInputLocked ||
                IsVoiceModeActive || IsPushToTalkActive ||
                !ChatThread.DisplayMessages.Any(message => message.From == MessageOwner.User && !message.IsInteractionResponse))
            {
                return false;
            }

            // A current run snapshot overrides saved state until its terminal result is known.
            return RunProjections.TryGetSnapshot(UserScope.UserId, ChatThread.Id, out var snapshot) && snapshot is not null
                ? snapshot.Phase == ChatRunProjectionPhase.Terminal && snapshot.Outcome == ChatRunOutcome.Stopped &&
                    (snapshot.RunMessages is not null || ThreadView?.CanContinue == true ||
                     ChatThreadAgentSessionStore.GetRunOutcome(ChatThread.SessionElement) == ChatRunOutcome.Stopped)
                : ThreadView?.CanContinue == true || ChatThreadAgentSessionStore.GetRunOutcome(ChatThread.SessionElement) == ChatRunOutcome.Stopped;
        }
    }

    /// <summary>Continues the selected stopped session through ordinary server admission.</summary>
    private Task ContinueStoppedRunAsync() => CanContinueSession
        ? ProcessPendingSendAsync(CapturePendingSend() with
        {
            ContinueSession = true, ClearComposer = false,
            RecipientAgentId = ThreadView?.ContinuationRecipientAgentId ?? ChatParticipantSessions.Read(ChatThread.SessionElement).LastRecipientId
        })
        : Task.CompletedTask;

    private async Task SubmitComposerAsync()
    {
        if (!RemovePendingMention() || await TryExecuteGoalCommandAsync() || await TryCompleteBareResearchCommandAsync()) { return; }

        if (HasLiveRun) { await SendFollowUpAsync(UserScope.UiSettings.ChatFollowUpMode); }
        else if (CanContinueSession) { await ContinueStoppedRunAsync(); }
        else { await ProcessInput(); }
    }

    // Registers ownership before awaiting, because the run may finish before the HTTP response arrives.
    private async Task SendFollowUpAsync(ChatFollowUpMode mode)
    {
        var text = _inputText.Trim();
        if (string.IsNullOrWhiteSpace(text) && _pendingFiles.Count == 0 || IsPushToTalkActive)
        {
            return;
        }

        var thread = ChatThread;
        var followUp = new ChatFollowUpView { MessageId = $"msg_{Guid.NewGuid():N}", Text = text, RecipientAgentId = SelectedRecipientId, WorkspaceFiles = _pendingFiles.ToArray() };
        var recipientId = SelectedRecipientId;
        var pending = new PendingFollowUp(thread.Id, followUp);
        _pendingFollowUps.Add(pending);
        _inputText = string.Empty;
        _pendingFiles.Clear();
        ChatFollowUpResult result;
        try
        {
            result = await ChatApiClient.SendFollowUpAsync(
                thread.Id,
                new SendChatFollowUpRequest
                {
                    MessageId = followUp.MessageId,
                    Text = followUp.Text,
                    WorkspaceFiles = followUp.WorkspaceFiles,
                    Mode = mode,
                    AgentId = ActiveAgentDefinition.Id,
                    RecipientAgentId = recipientId,
                    ModelProviderId = SelectedModelProviderId?.ToString(),
                    ApprovalRunMode = _toolApprovalRunMode
                });
        }
        catch (Exception ex)
        {
            _pendingFollowUps.Remove(pending);
            ReturnFollowUpText(thread.Id, text, followUp.WorkspaceFiles);
            ToastService.Show(ex.Message, ToastType.Error);
            return;
        }

        if (Drafts.GetRecipient(thread.Id)?.Id == recipientId)
        {
            Drafts.SetRecipient(thread.Id, null);
            if (ChatThread.Id == thread.Id && SelectedRecipientId == recipientId) { await RemoveRecipientAsync(); }
        }

        await ApplyFollowUpResultAsync(thread, pending, result);
    }

    private async Task SteerFollowUpAsync(ChatFollowUpView followUp)
    {
        var thread = ChatThread;
        if (_pendingFollowUps.Any(item => item.ThreadId == thread.Id && item.Message.MessageId == followUp.MessageId))
        {
            return;
        }

        var pending = new PendingFollowUp(thread.Id, followUp);
        _pendingFollowUps.Add(pending);
        ChatFollowUpResult result;
        try
        {
            result = await ChatApiClient.SteerFollowUpAsync(thread.Id, followUp.MessageId);
        }
        catch (Exception ex)
        {
            // The server restores a rejected transfer to the queue.
            _pendingFollowUps.Remove(pending);
            ToastService.Show(ex.Message, ToastType.Error);
            await RefreshFollowUpsAsync(thread);
            return;
        }

        await ApplyFollowUpResultAsync(thread, pending, result);
    }

    private async Task DeleteFollowUpAsync(ChatFollowUpView followUp, bool edit = false)
    {
        var thread = ChatThread;
        if (_pendingFollowUps.Any(item => item.ThreadId == thread.Id && item.Message.MessageId == followUp.MessageId))
        {
            return;
        }

        try
        {
            var removed = await ChatApiClient.DeleteFollowUpAsync(thread.Id, followUp.MessageId);
            if (edit && removed)
            {
                ReturnFollowUpText(thread.Id, followUp.Text, followUp.WorkspaceFiles);
                await RestoreRecipientAsync(thread.Id, followUp.RecipientAgentId);
            }
        }
        catch (Exception ex)
        {
            ToastService.Show(ex.Message, ToastType.Error);
        }

        await RefreshFollowUpsAsync(thread);
    }

    // Editing removes the queued row before returning its text to its originating composer.
    private Task EditFollowUpAsync(ChatFollowUpView followUp) => DeleteFollowUpAsync(followUp, edit: true);

    private async Task ApplyFollowUpResultAsync(ChatThread thread, PendingFollowUp pending, ChatFollowUpResult result)
    {
        // A transfer invalidates queue reads that started before its response.
        if (ReferenceEquals(ChatThread, thread)) { _followUpRefreshVersion++; }

        if (result.Disposition == ChatFollowUpDisposition.Steered)
        {
            pending.RunId = result.RunId;
            if (ReferenceEquals(ChatThread, thread))
            {
                _queuedFollowUps = _queuedFollowUps.Where(item => item.MessageId != pending.Message.MessageId).ToArray();
            }
        }
        else
        {
            _pendingFollowUps.Remove(pending);
        }

        try
        {
            // Reconcile even when the terminal projection was processed before this response.
            await ReconcileFollowUpsAsync(thread.Id);
            await RefreshFollowUpsAsync(thread);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to reconcile follow-ups for thread {ThreadId}.", thread.Id);
        }

        if (!_isDisposed)
        {
            StateHasChanged();
        }
    }

    // The shared run guard also recognizes channel runs that have no web projection.
    private async Task ReconcileFollowUpsAsync(string threadId)
    {
        foreach (var pending in _pendingFollowUps.Where(item => item.ThreadId == threadId && item.RunId is not null).ToArray())
        {
            RunProjections.TryGetSnapshot(UserScope.UserId, threadId, out var snapshot);
            if (snapshot?.RunMessages?.Any(message => message.MessageId == pending.Message.MessageId) == true)
            {
                _pendingFollowUps.Remove(pending);
                continue;
            }

            var ended = snapshot?.RunId == pending.RunId && snapshot!.Phase == ChatRunProjectionPhase.Terminal;
            if (!ended && RunGuard.GetActiveRunId(UserScope.UserId, threadId) == pending.RunId)
            {
                continue;
            }

            var persisted = await ChatApiClient.GetThreadAsync(threadId);
            if (persisted is null)
            {
                continue;
            }

            // Claim the result after awaiting so concurrent refreshes cannot return the same text twice.
            if (_pendingFollowUps.Remove(pending) &&
                !persisted.DisplayMessages.Any(message => message.MessageId == pending.Message.MessageId))
            {
                ReturnFollowUpText(threadId, pending.Message.Text, pending.Message.WorkspaceFiles);
                await RestoreRecipientAsync(threadId, pending.Message.RecipientAgentId);
            }
        }
    }

    private async Task RefreshFollowUpsAsync(ChatThread thread)
    {
        if (string.IsNullOrWhiteSpace(thread.Id))
        {
            return;
        }

        var refreshVersion = ReferenceEquals(ChatThread, thread) ? ++_followUpRefreshVersion : _followUpRefreshVersion;
        try
        {
            var queued = await ChatApiClient.GetFollowUpsAsync(thread.Id);
            if (_isDisposed || !ReferenceEquals(ChatThread, thread) || refreshVersion != _followUpRefreshVersion)
            {
                return;
            }

            _queuedFollowUps = queued.ExceptBy(SteeringFollowUps.Select(item => item.MessageId), item => item.MessageId,
                StringComparer.Ordinal).ToArray();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to load queued follow-ups for thread {ThreadId}.", thread.Id);
        }
    }

    // Recovery appends to a newer draft and never writes into a different selected thread.
    private void ReturnFollowUpText(string threadId, string text, IReadOnlyList<WorkspaceFileReference> files)
    {
        if (!_isDisposed && ChatThread.Id == threadId)
        {
            foreach (var file in files) { if (!_pendingFiles.Contains(file)) { _pendingFiles.Add(file); } }
        }
        else if (files.Count > 0)
        {
            Drafts.SetFiles(threadId, Drafts.TakeFiles(threadId).Concat(files).Distinct().ToArray());
        }

        if (!_isDisposed && ChatThread.Id == threadId)
        {
            AppendToComposer(text);
            return;
        }

        _returnedFollowUpText.TryGetValue(threadId, out var prior);
        _returnedFollowUpText[threadId] = string.IsNullOrWhiteSpace(prior) ? text : $"{prior}\n{text}";
    }

    private void AppendToComposer(string text) =>
        _inputText = string.IsNullOrWhiteSpace(_inputText) ? text : $"{_inputText}\n{text}";

    private async Task SwitchProjectionThreadAsync(string threadId)
    {
        RestoreGoalComposer();
        _goal = null;
        _goalThreadId = null;
        _loadingOlderMessages = false;
        _prependScrollMetrics = null;
        _restoreScrollTop = Drafts.GetScrollPosition(threadId);
        _waitingForThreadView = IsConversationLoading;
        _speechCaptureGeneration++;
        if (_voiceModeState == VoiceModeState.Transcribing)
        {
            await ResumeVoiceModeListeningAsync();
        }

        CancelPendingRunStart();
        RollbackOptimisticRun();
        RollbackRegeneration();
        SaveComposerDraft();
        if (!string.IsNullOrWhiteSpace(_locallyStartedEffectRunId))
        {
            await DiscardQueuedSpeechAsync();
        }

        Volatile.Write(ref _selectedProjectionThreadId, threadId);
        _observedRunId = null;
        _observedProjectionInstanceId = 0;
        _lastProjectionSequence = -1;
        _projectionMessageIds.Clear();
        _pendingElicitationIds.Clear();
        _pendingToolApprovalIds.Clear();
        Interlocked.Exchange(ref _projectionStructuralRefreshPending, 0);
        _locallyStartedEffectRunId = null;
        _lastAppliedOutcome = ChatRunOutcome.None;
        _lastProjectedToolActivity = false;
        _showFailedRunNotice = false;
        _failedUserMessageId = null;
        _inputText = Drafts.GetText(threadId);
        _selectedRecipient = Drafts.GetRecipient(threadId);
        _recipientAgentId = _selectedRecipient?.Id;
        _recipientThreadId = threadId;
        if (_returnedFollowUpText.Remove(threadId, out var returnedText))
        {
            AppendToComposer(returnedText);
        }

        _slashMenuDismissed = false;
        _selectedSlashCommandIndex = 0;
        _pendingAttachments.Clear();
        _pendingFiles.Clear();
        _pendingFiles.AddRange(Drafts.TakeFiles(threadId));
        _pendingAttachments.AddRange(Drafts.TakeAttachments(threadId).Select(attachment =>
            new PendingAttachment { Id = attachment.Id, File = attachment.File }));
        _queuedFollowUps = [];
        _selectedAgentConversationKey = null;
        _followLatest = _restoreScrollTop is null;
        ResetActiveRun();
        if (IsConversationLoading) { return; }

        await ApplyLatestProjectionAsync(threadId, hydration: true);
        await RefreshFollowUpsAsync(ChatThread);
        if (_followLatest) { RequestAutoScroll(); }
    }

    /// <summary>Keeps the ordinary composer and its uploads together in the circuit draft owner.</summary>
    private void SaveComposerDraft()
    {
        // The composer is leaving this thread, so the browser's strip no longer applies to it.
        var text = TextWithoutPendingMention;
        _pendingMention = null;
        if (_selectedProjectionThreadId is not { } threadId) { return; }

        Drafts.SetText(threadId, text);
        Drafts.SetFiles(threadId, _pendingFiles.ToArray());
        Drafts.SetAttachments(threadId, _pendingAttachments.Select(attachment => (attachment.Id, attachment.File)).ToArray());
    }

    private Task OnRunSignalAsync(ChatRunSignal signal)
    {
        if (_isDisposed ||
            !string.Equals(
                signal.ThreadId,
                Volatile.Read(ref _selectedProjectionThreadId),
                StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        if (signal.Kind != ChatRunChangeKind.Delta)
        {
            Interlocked.Exchange(ref _projectionStructuralRefreshPending, 1);
        }

        Interlocked.Increment(ref _projectionSignalVersion);
        QueueProjectionRefresh();
        return Task.CompletedTask;
    }

    private void QueueProjectionRefresh()
    {
        if (_isDisposed ||
            Interlocked.CompareExchange(ref _projectionRefreshScheduled, 1, 0) != 0)
        {
            return;
        }

        // The dispatcher loop re-reads the latest snapshot and closes the signal/clear lost-wakeup race.
        _ = InvokeAsync(RefreshProjectionLoopAsync);
    }

    private async Task RefreshProjectionLoopAsync()
    {
        try
        {
            while (!_isDisposed)
            {
                var capturedVersion = Interlocked.Read(ref _projectionSignalVersion);
                var structural = Interlocked.Exchange(ref _projectionStructuralRefreshPending, 0) != 0;
                var threadId = Volatile.Read(ref _selectedProjectionThreadId);
                if (!string.IsNullOrWhiteSpace(threadId))
                {
                    await ApplyLatestProjectionAsync(threadId, hydration: false, structural);
                }

                Interlocked.Exchange(ref _appliedProjectionSignalVersion, capturedVersion);
                if (Interlocked.Read(ref _projectionSignalVersion) <= capturedVersion &&
                    Volatile.Read(ref _projectionStructuralRefreshPending) == 0)
                {
                    break;
                }
            }
        }
        catch (JSDisconnectedException)
        {
        }
        catch (ObjectDisposedException) when (_isDisposed)
        {
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to refresh the live chat projection.");
        }
        finally
        {
            Interlocked.Exchange(ref _projectionRefreshScheduled, 0);
            if (!_isDisposed &&
                (Interlocked.Read(ref _projectionSignalVersion) >
                 Interlocked.Read(ref _appliedProjectionSignalVersion) ||
                 Volatile.Read(ref _projectionStructuralRefreshPending) != 0))
            {
                QueueProjectionRefresh();
            }
        }
    }

    private async Task ApplyLatestProjectionAsync(
        string threadId,
        bool hydration,
        bool refreshTodos = false)
    {
        if (_isDisposed ||
            !string.Equals(ChatThread.Id, threadId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await ReconcileFollowUpsAsync(threadId);
        await RefreshGoalAsync();
        if (_isDisposed || !string.Equals(ChatThread.Id, threadId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!RunProjections.TryGetSnapshot(UserScope.UserId, threadId, out var snapshot) ||
            snapshot is null)
        {
            ResetActiveRun();
            _showFailedRunNotice = false;
            _failedUserMessageId = null;
            StateHasChanged();
            return;
        }

        if (!string.Equals(ChatThread.Id, threadId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var isReplacement = snapshot.InstanceId != _observedProjectionInstanceId;
        if (!isReplacement &&
            (!string.Equals(snapshot.RunId, _observedRunId, StringComparison.Ordinal) ||
             snapshot.Sequence <= _lastProjectionSequence))
        {
            if (refreshTodos)
            {
                await RefreshTodosAsync();
                StateHasChanged();
            }

            return;
        }

        if (isReplacement)
        {
            var thread = ChatThread;
            var refreshed = await ReadDisplayedThreadAsync(threadId);
            if (_isDisposed || !ReferenceEquals(ChatThread, thread) || refreshed is null)
            {
                return;
            }

            // A drained queue can replace several completed runs before the browser reads a signal.
            if (!RunProjections.TryGetSnapshot(UserScope.UserId, threadId, out var latest) ||
                latest is null || latest.InstanceId != snapshot.InstanceId)
            {
                QueueProjectionRefresh();
                return;
            }

            snapshot = latest;
            MergePersistedMessages(refreshed);
            // Starting metadata can arrive before the submitted message is saved.
            if (snapshot.RunMessages is null &&
                _optimisticRunRollback is { } pending && ReferenceEquals(pending.Thread, thread) &&
                pending.UserMessage.MessageId == snapshot.UserMessageId &&
                !thread.DisplayMessages.Any(message => message.MessageId == snapshot.UserMessageId))
            {
                thread.DisplayMessages.Add(pending.UserMessage);
            }

            await RefreshFollowUpsAsync(thread);
            if (_isDisposed || !ReferenceEquals(ChatThread, thread))
            {
                return;
            }

            if (!RunProjections.TryGetSnapshot(UserScope.UserId, threadId, out latest) ||
                latest is null || latest.InstanceId != snapshot.InstanceId)
            {
                QueueProjectionRefresh();
                return;
            }

            snapshot = latest;

            if (HasLaterPersistedTurn(snapshot))
            {
                ResetActiveRun();
                _showFailedRunNotice = false;
                _failedUserMessageId = null;
                StateHasChanged();
                return;
            }

            HideRegenerationTail(snapshot.UserMessageId);
            _observedRunId = snapshot.RunId;
            _observedProjectionInstanceId = snapshot.InstanceId;
            _lastProjectionSequence = -1;
            _projectionMessageIds.Clear();
            _lastAppliedOutcome = ChatRunOutcome.None;
            _showFailedRunNotice = false;
            _failedUserMessageId = null;
        }

        var changedAssistantMessages = new List<ChatDisplayMessage>();
        if (snapshot.RunMessages is not null)
        {
            ReconcileProjectionMessages(snapshot.RunMessages, changedAssistantMessages);
            ClearClosedQuestionAttempt();
            _optimisticRunRollback = null;
            _regenerationRollback = null;
        }
        else if (snapshot.Phase == ChatRunProjectionPhase.Terminal &&
                 string.Equals(
                     _regenerationRollback?.UserMessageId,
                     snapshot.UserMessageId,
                     StringComparison.Ordinal))
        {
            RollbackRegeneration();
        }
        else if (snapshot.Phase == ChatRunProjectionPhase.Terminal &&
                 snapshot.Outcome is ChatRunOutcome.Stopped or ChatRunOutcome.Failed &&
                 string.Equals(
                     _optimisticRunRollback?.UserMessage.MessageId,
                     snapshot.UserMessageId,
                     StringComparison.Ordinal))
        {
            RollbackOptimisticRun();
        }

        _pendingElicitationIds.Clear();
        _pendingToolApprovalIds.Clear();
        foreach (var message in snapshot.RunMessages ?? [])
        {
            if (message.Elicitation is { } elicitation)
            {
                _pendingElicitationIds.Add(elicitation.RequestId);
            }

            if (message.ToolApproval is { RequestId: { Length: > 0 } approvalId })
            {
                _pendingToolApprovalIds.Add(approvalId);
            }
        }

        if (_chatPermissions is not null)
        {
            _chatPermissions = _chatPermissions with
            {
                RunningPreset = snapshot.Phase == ChatRunProjectionPhase.Terminal ? null : snapshot.PermissionPreset
            };
        }

        _runState = snapshot.Phase switch
        {
            ChatRunProjectionPhase.AwaitingElicitation => ChatRunState.AwaitingElicitation,
            ChatRunProjectionPhase.AwaitingToolApproval => ChatRunState.AwaitingToolApproval,
            ChatRunProjectionPhase.AwaitingSubagents => ChatRunState.AwaitingSubagents,
            ChatRunProjectionPhase.Continuing => ChatRunState.Continuing,
            ChatRunProjectionPhase.Terminal => ChatRunState.Idle,
            _ => ChatRunState.Streaming
        };
        _activeOperationId = snapshot.Phase == ChatRunProjectionPhase.Terminal
            ? null
            : snapshot.RunId;
        EnsureAgentConversationPolling();
        _showFailedRunNotice = snapshot.Outcome == ChatRunOutcome.Failed;
        _nodeUnavailableFailure = snapshot.NodeUnavailable;
        _projectFolderFailure = snapshot.ProjectFolderUnavailable;
        _mcpFailure = snapshot.McpFailure;
        _failedUserMessageId = _showFailedRunNotice ? snapshot.UserMessageId : null;
        _lastProjectionSequence = snapshot.Sequence;
        _observedRunId = snapshot.RunId;

        var hasToolActivity = snapshot.RunMessages?.Any(message =>
            message.Segments.Any(segment =>
                segment.Type == ChatSegmentType.ToolCall &&
                segment.FunctionCall is not null &&
                segment.ExecutionState is ToolExecutionState.Pending or ToolExecutionState.Executing)) == true;
        var activityChanged = hasToolActivity != _lastProjectedToolActivity;
        _lastProjectedToolActivity = hasToolActivity;
        var previousOutcome = _lastAppliedOutcome;
        _lastAppliedOutcome = snapshot.Outcome;

        if (snapshot.RunMessages is not null)
        {
            RequestAutoScroll(force: false);
        }

        StateHasChanged();
        foreach (var message in changedAssistantMessages)
        {
            BotMessageView.NotifyChanged(message);
        }

        if (activityChanged)
        {
            await OnToolActivityChanged.InvokeAsync();
        }

        if (refreshTodos)
        {
            await RefreshTodosAsync();
            StateHasChanged();
        }

        var ownsEffects = string.Equals(
            snapshot.RunId,
            _locallyStartedEffectRunId,
            StringComparison.Ordinal);
        if (ownsEffects && ShouldRouteSpeechToAvatar && _avatarEmotionsRunId != snapshot.RunId)
        {
            _avatarEmotionsRunId = snapshot.RunId;
            // Same cached readiness check the server makes before injecting the catalog into the prompt.
            if (await AvatarRuntimeSettings.EnsureReadyAsync())
            {
                _avatarEmotions = await AvatarBridge.GetEmotionsAsync();
            }
        }
        // Explicit tool speech is independent of automatic reply reading.
        _ = ownsEffects && snapshot.RunMessages is not null &&
            await QueueExplicitSpeechAsync(snapshot.RunId, snapshot.RunMessages);
        if (ownsEffects && snapshot.RunMessages is not null && IsSpeechSynthesisEnabled)
        {
            foreach (var message in snapshot.RunMessages.Where(message => message.From == MessageOwner.Bot))
            {
                await QueueSpeechForMessageAsync(
                    message,
                    snapshot.Phase == ChatRunProjectionPhase.Terminal);
            }
        }

        if (snapshot.Phase != ChatRunProjectionPhase.Terminal ||
            previousOutcome == snapshot.Outcome)
        {
            return;
        }

        var completedThread = ChatThread;
        await OnRunFinished.InvokeAsync();
        if (_isDisposed || !ReferenceEquals(ChatThread, completedThread))
        {
            return;
        }

        await RefreshFollowUpsAsync(completedThread);
        if (_isDisposed || !ReferenceEquals(ChatThread, completedThread))
        {
            return;
        }

        await RefreshSessionInspectorMetadataAsync();
        // Child sessions reach storage when the parent run saves, so the read-only views refresh once per completed turn.
        await RefreshAgentConversationsAsync();
        StateHasChanged();
        if (!hydration && CurrentContextUsageKey is { } contextKey)
        {
            _contextUsageKey = contextKey;
            await RefreshContextUsageAsync(contextKey);
            await RefreshBudgetAsync();
        }

        if (hydration || !ownsEffects)
        {
            return;
        }

        try
        {
            switch (snapshot.Outcome)
            {
                case ChatRunOutcome.Completed:
                    if (IsVoiceModeActive)
                    {
                        await OnVoiceModeResponseCompletedAsync();
                    }
                    else
                    {
                        _ = Sound.PlayAsync(SoundCue.Receive);
                    }

                    // Silent while speech synthesis is on so the announcement never overlaps the spoken reply.
                    if (!IsSpeechSynthesisEnabled)
                    {
                        _liveAnnouncement = Loc.GetOrDefault(ChatTextKeys.ResponseComplete, "Response complete.");
                    }

                    break;
                case ChatRunOutcome.Stopped:
                    await DiscardQueuedSpeechAsync();
                    if (IsVoiceModeActive)
                    {
                        ResetVoiceModeTurnState();
                        _voiceModeState = VoiceModeState.Listening;
                        InputPlaceholder = GetListeningPlaceholder();
                        await ResumeVadAsync();
                    }

                    break;
                case ChatRunOutcome.Failed:
                    // A failed accepted run ends the voice turn just like a stopped run.
                    await DiscardQueuedSpeechAsync();
                    if (IsVoiceModeActive) { await ResumeVoiceModeListeningAsync(); }

                    _liveAnnouncement = FailedRunMessage;
                    ToastService.Show(_liveAnnouncement, ToastType.Error);
                    _ = Sound.PlayAsync(SoundCue.Error);
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "A chat completion media effect failed for run {RunId}.", snapshot.RunId);
        }
        finally
        {
            _locallyStartedEffectRunId = null;
        }
    }

    private void ReconcileProjectionMessages(
        IReadOnlyList<ChatDisplayMessage> runMessages,
        List<ChatDisplayMessage> changedAssistantMessages)
    {
        var incomingIds = runMessages
            .Select(message => message.MessageId)
            .ToHashSet(StringComparer.Ordinal);
        for (var index = ChatThread.DisplayMessages.Count - 1; index >= 0; index--)
        {
            var existing = ChatThread.DisplayMessages[index];
            if (_projectionMessageIds.Contains(existing.MessageId) &&
                !incomingIds.Contains(existing.MessageId))
            {
                ChatThread.DisplayMessages.RemoveAt(index);
            }
        }

        var ordered = new List<ChatDisplayMessage>(runMessages.Count);
        foreach (var source in runMessages)
        {
            var target = ChatThread.DisplayMessages.FirstOrDefault(message =>
                string.Equals(message.MessageId, source.MessageId, StringComparison.Ordinal));
            if (target is null)
            {
                target = source;
            }
            else
            {
                CopyProjectedMessage(source, target);
                if (target.From == MessageOwner.Bot)
                {
                    changedAssistantMessages.Add(target);
                }
            }

            ChatThread.DisplayMessages.Remove(target);
            ordered.Add(target);
        }

        foreach (var message in ordered)
        {
            ChatThread.DisplayMessages.Add(message);
        }

        _projectionMessageIds.Clear();
        _projectionMessageIds.UnionWith(incomingIds);
    }

    private static void CopyProjectedMessage(ChatDisplayMessage source, ChatDisplayMessage target)
    {
        target.Channel = source.Channel;
        target.CreatedAt = source.CreatedAt;
        target.From = source.From;
        target.AgentId = source.AgentId;
        target.AgentName = source.AgentName;
        target.RecipientAgentId = source.RecipientAgentId;
        target.IsInteractionResponse = source.IsInteractionResponse;
        target.HistorySummarized = source.HistorySummarized;
        target.Text = source.Text;
        target.Segments = [.. source.Segments];
        target.Elicitation = source.Elicitation;
        target.ToolApproval = source.ToolApproval;
        target.Attachments = [.. source.Attachments];
        target.WorkspaceFiles = source.WorkspaceFiles;
        target.InputTokens = source.InputTokens;
        target.OutputTokens = source.OutputTokens;
        target.TotalTokens = source.TotalTokens;
    }

    private void RollbackOptimisticRun()
    {
        var rollback = _optimisticRunRollback;
        _optimisticRunRollback = null;
        if (rollback is not { IsAdmitted: false })
        {
            return;
        }

        rollback.Thread.DisplayMessages.Remove(rollback.UserMessage);
        rollback.Thread.Title = rollback.Title;
        rollback.Thread.AgentId = rollback.AgentId;
        rollback.Thread.AgentName = rollback.AgentName;
        foreach (var file in rollback.Files ?? [])
        {
            if (!_pendingFiles.Contains(file)) { _pendingFiles.Add(file); }
        }
        // Recovery must retain text entered while admission was pending.
        if (TextWithoutPendingMention == rollback.InputText) { _inputText = rollback.InputText; }
        else if (rollback.ClearComposer) { AppendToComposer(rollback.InputText); }

        foreach (var attachment in rollback.Attachments)
        {
            if (_pendingAttachments.All(candidate =>
                    !string.Equals(candidate.Id, attachment.Id, StringComparison.Ordinal)))
            {
                _pendingAttachments.Add(attachment);
            }
        }

        ResetActiveRun();
    }

    private CancellationTokenSource BeginLocalRun(string runId)
    {
        _runState = ChatRunState.Streaming;
        _activeOperationId = runId;
        _locallyStartedEffectRunId = runId;
        // Cleared per run so a repeated outcome is still a change the live region announces.
        _liveAnnouncement = null;
        _runStartCts?.Dispose();
        var startCts = new CancellationTokenSource();
        _runStartCts = startCts;
        return startCts;
    }

    private void HideRegenerationTail(string userMessageId)
    {
        var messages = ChatThread.DisplayMessages;
        var userIndex = -1;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var message = messages[index];
            if (message.From == MessageOwner.User &&
                !message.IsInteractionResponse &&
                string.Equals(message.MessageId, userMessageId, StringComparison.Ordinal))
            {
                userIndex = index;
                break;
            }
        }

        if (userIndex < 0 || userIndex == messages.Count - 1)
        {
            return;
        }

        var responseTail = messages.Skip(userIndex + 1).ToArray();
        // A persisted-history refresh can reintroduce the old reply while replacement admission is pending.
        _regenerationRollback ??= new RegenerationRollback(ChatThread, userMessageId, responseTail);
        while (messages.Count > userIndex + 1)
        {
            messages.RemoveAt(messages.Count - 1);
        }
    }

    private void RollbackRegeneration()
    {
        var rollback = _regenerationRollback;
        _regenerationRollback = null;
        if (rollback is null)
        {
            return;
        }

        foreach (var message in rollback.ResponseTail)
        {
            rollback.Thread.DisplayMessages.Add(message);
        }

        ResetActiveRun();
    }

    private void CancelPendingRunStart()
    {
        try
        {
            _runStartCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ShowSessionInspectorAsync()
    {
        var thread = ChatThread;
        if (!IsSessionInspectorAvailable)
        {
            await RefreshSessionInspectorMetadataAsync();
        }

        if (!IsSessionInspectorAvailable || _isDisposed || !ReferenceEquals(thread, ChatThread))
        {
            return;
        }

        var snapshot = await ChatApiClient.GetSessionInspectorAsync(thread.Id);
        // Inspection cannot combine a prior conversation's snapshot with the selected identity.
        if (_isDisposed || !ReferenceEquals(thread, ChatThread)) { return; }

        await DialogService.ShowAsync<SessionInspectorDialog>(
            new Dictionary<string, object?>
            {
                ["Messages"] = snapshot.Messages,
                ["SystemInstruction"] = snapshot.SystemInstruction,
                ["EgressEntries"] = snapshot.EgressEntries,
                ["ThreadId"] = thread.Id
            });
    }

    private void ResetActiveRun()
    {
        _pendingElicitationIds.Clear();
        _pendingToolApprovalIds.Clear();
        _activeOperationId = null;
        _runState = ChatRunState.Idle;
    }

    private async Task RefreshTodosAsync()
    {
        var thread = ChatThread;
        if (string.IsNullOrWhiteSpace(ChatThread.Id) || !ActiveAgentDefinition.IsTodoEnabled)
        {
            _todos = [];
            return;
        }

        try
        {
            var todos = await ChatApiClient.GetTodosAsync(
                thread.Id,
                SelectedProvider?.Id.ToString());
            // Task lists remain attached to their originating conversation across navigation.
            if (!_isDisposed && ReferenceEquals(thread, ChatThread)) { _todos = todos; }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to refresh todos for thread {ThreadId}.", thread.Id);
            if (!_isDisposed && ReferenceEquals(thread, ChatThread)) { _todos = []; }
        }
    }

    private async Task RefreshSessionInspectorMetadataAsync()
    {
        var thread = ChatThread;

        if (string.IsNullOrWhiteSpace(thread.Id))
        {
            return;
        }

        try
        {
            var refreshedThread = await ReadDisplayedThreadAsync(thread.Id);
            // A delayed response must not update a newly selected conversation.
            if (refreshedThread is null || _isDisposed || !ReferenceEquals(ChatThread, thread))
            {
                return;
            }

            ChatThread.Title = refreshedThread.Title;
            ChatThread.AgentId = refreshedThread.AgentId;
            ChatThread.AgentName = refreshedThread.AgentName;
            ChatThread.SessionElement = refreshedThread.SessionElement;
            ChatThread.SystemInstruction = refreshedThread.SystemInstruction;
            ChatThread.UpdatedAt = refreshedThread.UpdatedAt;
            ChatThread.BranchOrigin = refreshedThread.BranchOrigin;
            ChatThread.SourceThreadAvailable = refreshedThread.SourceThreadAvailable;
            ChatThread.BranchableMessageIds = refreshedThread.BranchableMessageIds;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Failed to refresh session inspector metadata for thread {ThreadId}.", thread.Id);
        }
    }

    private async Task SubmitElicitationResponseAsync(
        Guid requestId,
        Func<Task> submitAction,
        string errorMessage)
    {
        try
        {
            await submitAction();
        }
        catch
        {
            ToastService.Show(errorMessage, ToastType.Error);
        }
    }

    private static string GetAttachmentIcon(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Image => "lucide:image",
        AttachmentKind.Pdf => "lucide:file-text",
        _ => "lucide:file-text"
    };

    private string GetAttachmentKindLabel(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Image => Loc.GetOrDefault(ChatTextKeys.ImageAttachmentKind, "Image"),
        AttachmentKind.Pdf => "PDF",
        _ => Loc.GetOrDefault(ChatTextKeys.TextAttachmentKind, "Text")
    };

    private string GetPendingAttachmentCountLabel()
    {
        return _pendingAttachments.Count + _pendingFiles.Count == 1
            ? Loc.GetOrDefault(ChatTextKeys.OneFileReadyLabel, "1 file ready")
            : FormatChatText(ChatTextKeys.MultipleFilesReadyLabel, "{0} files ready", _pendingAttachments.Count + _pendingFiles.Count);
    }

    private string FormatChatText(string key, string fallback, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Loc.GetOrDefault(key, fallback), args);
    }

    private string FormatChatStartText(string key, string fallback, params object?[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, StartLoc.GetOrDefault(key, fallback), args);
    }

    private static AttachmentKind GetPendingAttachmentKind(PendingAttachment attachment)
    {
        var extension = Path.GetExtension(attachment.File.Name);
        return string.IsNullOrWhiteSpace(extension)
            ? AttachmentKind.Text
            : ChatAttachmentLimits.KindFor(extension);
    }

    private static string FormatAttachmentSize(long sizeBytes) => ByteSize.Format(sizeBytes);

    private static ChatAttachment CreatePendingAttachmentPreview(PendingAttachment attachment)
    {
        return new ChatAttachment
        {
            DisplayName = attachment.File.Name,
            MediaType = string.IsNullOrWhiteSpace(attachment.File.ContentType)
                ? "application/octet-stream"
                : attachment.File.ContentType,
            SizeBytes = attachment.File.Size,
            Kind = GetPendingAttachmentKind(attachment)
        };
    }

}
