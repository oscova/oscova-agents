using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Oscova.Agents.Core;

namespace Oscova.Agents.Infrastructure.Secrets;

/// <summary>The shared deletion selection sealed under an account's duress credentials.</summary>
public sealed record DuressActions
{
    public bool DeleteAllThreads { get; init; }
    public IReadOnlyList<string> AgentIds { get; init; } = [];
    public IReadOnlyList<string> TaskIds { get; init; } = [];
    public IReadOnlyList<string> Workspaces { get; init; } = [];
    /// <summary>Empties the host-wide shared folder. Only the operator can select it, and older records lack it.</summary>
    public bool ClearSharedFolder { get; init; }
}

/// <summary>Seals one deletion selection under separate duress password and PIN slots.</summary>
public sealed class DuressRecordProtector
{
    private const int RecordVersion = 2;
    private const string OwnerPurpose = "Oscova.DuressOwnerSlot.v2";
    private const string PasswordPurpose = "Oscova.DuressPasswordSlot.v2";
    private const string PinPurpose = "Oscova.DuressPinSlot.v2";
    private const int OwnerPayloadLength = 32;
    private const int DuressPayloadLength = 8192;

    public const int MaxAgentIds = 32;
    public const int MaxTaskIds = 32;
    public const int MaxWorkspaces = 16;
    public const int MaxIdLength = 64;

    private readonly JsonSerializerOptions _jsonOptions = PassphraseEnvelope.JsonOptions;

    /// <summary>Builds a record with two unusable duress slots.</summary>
    public string Create(string userId, string accountPassword) =>
        CreateRecord(userId, accountPassword, CreateDummySlot(userId, PasswordPurpose), CreateDummySlot(userId, PinPurpose));

    private static string CreateDummySlot(string userId, string purpose)
    {
        var unknowable = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var duressPayload = RandomNumberGenerator.GetBytes(DuressPayloadLength);
        try
        {
            return PassphraseEnvelope.Create(
                duressPayload,
                unknowable,
                SlotAssociatedData(userId, purpose));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(duressPayload);
        }
    }

    /// <summary>Reseals the owner slot under a new account password.</summary>
    public string ResealOwnerSlot(string userId, string currentRecord, string newAccountPassword)
    {
        if (!TryParseRecord(currentRecord, out var record))
        {
            return Create(userId, newAccountPassword);
        }

        return CreateRecord(userId, newAccountPassword, record.PasswordSlot, record.PinSlot);
    }

    /// <summary>Validates that every requested action fits the sealed payload.</summary>
    public static bool ValidateActions(DuressActions actions, out string? error)
    {
        if (!TryNormalize(actions.AgentIds, MaxAgentIds, out var agentIds, out error) ||
            !TryNormalize(actions.TaskIds, MaxTaskIds, out var taskIds, out error) ||
            !TryNormalize(actions.Workspaces, MaxWorkspaces, out var workspaces, out error, StringComparer.Ordinal))
        {
            return false;
        }

        if (workspaces.Any(workspace => string.Equals(
            workspace,
            UserFileNames.DefaultWorkspaceDirectory,
            StringComparison.OrdinalIgnoreCase)))
        {
            error = "The default workspace cannot be selected.";
            return false;
        }

        var normalized = new DuressActions
        {
            DeleteAllThreads = actions.DeleteAllThreads,
            ClearSharedFolder = actions.ClearSharedFolder,
            AgentIds = agentIds,
            TaskIds = taskIds,
            Workspaces = workspaces
        };
        if (JsonSerializer.SerializeToUtf8Bytes(normalized, PassphraseEnvelope.JsonOptions).Length > DuressPayloadLength)
        {
            error = "Your selection is too large. Remove some items and try again.";
            return false;
        }

        error = null;
        return true;
    }

    /// <summary>Replaces both action slots and binds them to the account password.</summary>
    public string SetDuressCredentials(
        string userId,
        string accountPassword,
        string duressPassword,
        DuressActions actions,
        string? duressPin = null)
    {
        if (!ValidateActions(actions, out var error))
        {
            throw new InvalidOperationException(error);
        }

        var normalized = NormalizeActions(actions);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(normalized, _jsonOptions);
        var payload = new byte[DuressPayloadLength];
        try
        {
            serialized.CopyTo(payload, 0);
            payload.AsSpan(serialized.Length).Fill(0x20);
            var passwordSlot = PassphraseEnvelope.Create(payload, duressPassword, SlotAssociatedData(userId, PasswordPurpose));
            var pinSlot = duressPin is null
                ? CreateDummySlot(userId, PinPurpose)
                : PassphraseEnvelope.Create(payload, duressPin, SlotAssociatedData(userId, PinPurpose));
            return CreateRecord(userId, accountPassword, passwordSlot, pinSlot);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serialized);
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Returns whether the owner slot opens under the supplied account password.</summary>
    public bool OwnerSlotOpens(string userId, string record, string accountPassword)
    {
        if (!TryParseRecord(record, out var parsed))
        {
            return false;
        }

        try
        {
            var payload = PassphraseEnvelope.TryOpen(
                parsed.OwnerSlot,
                accountPassword,
                OwnerAssociatedData(userId, parsed.PasswordSlot, parsed.PinSlot),
                OwnerPayloadLength);
            if (payload is null)
            {
                return false;
            }

            CryptographicOperations.ZeroMemory(payload);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Matches the password slot while distinguishing unusable actions from a wrong secret.</summary>
    public (bool Matched, DuressActions? Actions) OpenPasswordSlot(string userId, string record, string password) =>
        OpenSlot(userId, record, password, false);

    /// <summary>Matches the PIN slot while distinguishing unusable actions from a wrong secret.</summary>
    public (bool Matched, DuressActions? Actions) OpenPinSlot(string userId, string record, string pin) =>
        OpenSlot(userId, record, pin, true);

    private (bool Matched, DuressActions? Actions) OpenSlot(string userId, string record, string secret, bool isPin)
    {
        if (!TryParseRecord(record, out var parsed))
        {
            return (false, null);
        }

        byte[]? payload = null;
        var matched = false;
        try
        {
            payload = PassphraseEnvelope.TryOpen(
                isPin ? parsed.PinSlot : parsed.PasswordSlot,
                secret,
                SlotAssociatedData(userId, isPin ? PinPurpose : PasswordPurpose),
                DuressPayloadLength);
            if (payload is null)
            {
                return (false, null);
            }

            matched = true;

            var length = payload.Length;
            while (length > 0 && payload[length - 1] == 0x20)
            {
                length--;
            }

            var actions = JsonSerializer.Deserialize<DuressActions>(
                payload.AsSpan(0, length),
                _jsonOptions);
            if (actions?.AgentIds is null || actions.TaskIds is null || actions.Workspaces is null)
            {
                return (true, null);
            }

            var filtered = actions with
            {
                Workspaces = actions.Workspaces
                    .Where(workspace => !string.Equals(
                        workspace,
                        UserFileNames.DefaultWorkspaceDirectory,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray()
            };
            return (true, ValidateActions(filtered, out _) ? NormalizeActions(filtered) : null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return (matched, null);
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    private string CreateRecord(string userId, string accountPassword, string passwordSlot, string pinSlot)
    {
        var ownerPayload = RandomNumberGenerator.GetBytes(OwnerPayloadLength);
        try
        {
            var ownerSlot = PassphraseEnvelope.Create(
                ownerPayload,
                accountPassword,
                OwnerAssociatedData(userId, passwordSlot, pinSlot));
            return JsonSerializer.Serialize(new Record
            {
                OwnerSlot = ownerSlot,
                PasswordSlot = passwordSlot,
                PinSlot = pinSlot
            }, _jsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ownerPayload);
        }
    }

    private bool TryParseRecord(string record, out Record parsed)
    {
        if (string.IsNullOrWhiteSpace(record))
        {
            parsed = null!;
            return false;
        }

        try
        {
            var candidate = JsonSerializer.Deserialize<Record>(record, _jsonOptions);
            if (candidate is null)
            {
                parsed = null!;
                return false;
            }

            parsed = candidate;
            return parsed.Version == RecordVersion &&
                !string.IsNullOrWhiteSpace(parsed.OwnerSlot) &&
                !string.IsNullOrWhiteSpace(parsed.PasswordSlot) &&
                !string.IsNullOrWhiteSpace(parsed.PinSlot);
        }
        catch (JsonException)
        {
            parsed = null!;
            return false;
        }
    }

    private static bool TryNormalize(
        IReadOnlyList<string> values,
        int maximum,
        out string[] normalized,
        out string? error,
        StringComparer? comparer = null)
    {
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            normalized = [];
            error = "One of the selected items is no longer available. Reopen the form and choose again.";
            return false;
        }

        normalized = values
            .Select(value => value.Trim())
            .Distinct(comparer ?? StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalized.Length > maximum)
        {
            error = "Your selection is too large. Remove some items and try again.";
            return false;
        }

        if (normalized.Any(value => value.Length > MaxIdLength))
        {
            error = "A selected name is too long.";
            return false;
        }

        error = null;
        return true;
    }

    private static DuressActions NormalizeActions(DuressActions actions)
    {
        _ = TryNormalize(actions.AgentIds, MaxAgentIds, out var agentIds, out _);
        _ = TryNormalize(actions.TaskIds, MaxTaskIds, out var taskIds, out _);
        _ = TryNormalize(actions.Workspaces, MaxWorkspaces, out var workspaces, out _, StringComparer.Ordinal);
        return actions with { AgentIds = agentIds, TaskIds = taskIds, Workspaces = workspaces };
    }

    private static byte[] OwnerAssociatedData(string userId, string passwordSlot, string pinSlot)
        => Encoding.UTF8.GetBytes(
            $"{OwnerPurpose}\0{userId}\0{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(passwordSlot)))}\0{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(pinSlot)))}");

    private static byte[] SlotAssociatedData(string userId, string purpose)
        => Encoding.UTF8.GetBytes($"{purpose}\0{userId}");

    private sealed class Record
    {
        public int Version { get; init; } = RecordVersion;
        public string OwnerSlot { get; init; } = string.Empty;
        public string PasswordSlot { get; init; } = string.Empty;
        public string PinSlot { get; init; } = string.Empty;
    }
}
