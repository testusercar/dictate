using Windows.Security.Credentials;

namespace Dictate.Platform;

/// <summary>The xAI API key, kept in Windows Credential Manager for the signed-in user.</summary>
internal sealed class ApiKeyStore
{
    private const string Resource = "Dictate";
    private const string UserName = "xai-api-key";

    private readonly PasswordVault _vault = new();
    private string? _cached;
    private bool _loaded;

    public string? Get()
    {
        if (_loaded) return _cached;
        try
        {
            var credential = _vault.Retrieve(Resource, UserName);
            credential.RetrievePassword();
            _cached = credential.Password;
        }
        catch (Exception)
        {
            // Retrieve throws when nothing is stored.
            _cached = null;
        }
        _loaded = true;
        return _cached;
    }

    public void Set(string key)
    {
        Clear();
        _vault.Add(new PasswordCredential(Resource, UserName, key));
        _cached = key;
        _loaded = true;
    }

    public void Clear()
    {
        try
        {
            foreach (var c in _vault.FindAllByResource(Resource)) _vault.Remove(c);
        }
        catch (Exception)
        {
            // FindAllByResource throws when there is nothing to remove.
        }
        _cached = null;
        _loaded = true;
    }
}
