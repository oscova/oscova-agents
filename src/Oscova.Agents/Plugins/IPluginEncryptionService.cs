namespace Oscova.Agents.Plugins;

/// <summary>Encrypts plugin-owned text for the user bound by the host.</summary>
public interface IPluginEncryptionService
{
    /// <summary>Encrypts text for this plugin and user, throwing when encryption is unavailable.</summary>
    string Encrypt(string plaintext);

    /// <summary>Decrypts this plugin's text for its user, throwing when the content cannot be opened.</summary>
    string Decrypt(string ciphertext);
}
