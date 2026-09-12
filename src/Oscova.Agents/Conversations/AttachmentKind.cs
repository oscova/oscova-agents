namespace Oscova.Agents.Conversations;

/// <summary>Specifies how a chat attachment is represented to tools and manifests.</summary>
public enum AttachmentKind
{
    /// <summary>Identifies an image attachment.</summary>
    Image,
    /// <summary>Identifies a text attachment.</summary>
    Text,
    /// <summary>Identifies a PDF attachment.</summary>
    Pdf
}
