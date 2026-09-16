using System;
using System.Security.Cryptography;

namespace m3uCrawler.Services.Auth;

/// <summary>
/// PHASE 9C.2 — Derivação e verificação de passwords com PBKDF2-HMAC-SHA256
/// nativo do .NET (sem dependências novas).
///
/// <para>
/// Formato persistido versionado (PHC-like):
/// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;base64(salt)&gt;$&lt;base64(hash)&gt;</c>.
/// Os parâmetros vivem no próprio valor, o que permite aumentar iterações
/// ou trocar de algoritmo no futuro sem migration de dados.
/// </para>
/// </summary>
public static class PasswordHasher
{
    public const string AlgorithmId = "pbkdf2-sha256";
    public const int DefaultIterations = 210_000;
    public const int SaltSizeBytes = 16;
    public const int HashSizeBytes = 32;

    private static readonly Lazy<string> DummyHash =
        new(() => Hash("m3ucrawler-timing-uniformity-dummy-secret"));

    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, DefaultIterations, HashAlgorithmName.SHA256, HashSizeBytes);
        return $"{AlgorithmId}${DefaultIterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>
    /// Verifica uma password contra um hash persistido. Devolve <c>false</c>
    /// (nunca excepção) se o formato for inválido ou desconhecido.
    /// </summary>
    public static bool Verify(string password, string? storedHash)
    {
        if (password == null || string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        if (!TryParse(storedHash, out var iterations, out var salt, out var expected))
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Verificação "dummy" para utilizador inexistente/inactivo: executa a
    /// mesma derivação para uniformizar o tempo de resposta e evitar
    /// enumeração de utilizadores. Devolve sempre <c>false</c>.
    /// </summary>
    public static bool VerifyDummy(string password)
    {
        _ = Verify(password ?? string.Empty, DummyHash.Value);
        return false;
    }

    private static bool TryParse(string stored, out int iterations, out byte[] salt, out byte[] hash)
    {
        iterations = 0;
        salt = Array.Empty<byte>();
        hash = Array.Empty<byte>();

        var parts = stored.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], AlgorithmId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out iterations) || iterations <= 0)
        {
            return false;
        }

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        return salt.Length > 0 && hash.Length > 0;
    }
}

/// <summary>
/// PHASE 9C.2 — Política de password e username. Devolve uma chave de erro
/// estável (sem detalhes internos) ou <c>null</c> se válido.
/// </summary>
public static class CredentialPolicy
{
    public const int MinimumPasswordLength = 12;
    public const int MaximumUsernameLength = 64;
    public const int MaximumPasswordLength = 256;

    public static string? ValidatePassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return "password-empty";
        if (string.IsNullOrWhiteSpace(password)) return "password-whitespace";
        if (password.Length < MinimumPasswordLength) return "password-too-short";
        if (password.Length > MaximumPasswordLength) return "password-too-long";
        return null;
    }

    public static string? ValidateUsername(string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return "username-empty";
        var trimmed = username.Trim();
        if (trimmed.Length > MaximumUsernameLength) return "username-too-long";
        foreach (var ch in trimmed)
        {
            if (!char.IsLetterOrDigit(ch) && ch != '.' && ch != '_' && ch != '-' && ch != '@')
            {
                return "username-invalid-chars";
            }
        }
        return null;
    }
}
