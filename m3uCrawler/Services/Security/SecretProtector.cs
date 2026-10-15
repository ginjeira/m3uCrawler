using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace m3uCrawler.Services.Security;

/// <summary>
/// Cifra e decifra segredos de aplicação com AES-256-GCM. A chave de
/// encriptação (KEK) é resolvida, por ordem: variável de ambiente
/// <c>M3UCRAWLER_ENCRYPTION_KEY</c> (base64 de 32 bytes) ou o ficheiro
/// <c>secret.key</c> no directório de dados de runtime (gerado com 32
/// bytes aleatórios na primeira utilização). O payload é
/// <c>[version=1][nonce 12][tag 16][ciphertext]</c> codificado em base64.
///
/// <para>
/// Quando a env existe mas é inválida (tamanho/base64) lança
/// <see cref="InvalidOperationException"/> — não há fallback silencioso
/// para o ficheiro. <see cref="Unprotect"/> nunca lança por payload
/// corrompido: devolve <c>null</c>. Nem a chave nem o plaintext são
/// alguma vez registados.
/// </para>
/// </summary>
public sealed class SecretProtector
{
    /// <summary>Versão do formato do payload cifrado.</summary>
    private const byte CurrentVersion = 1;
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Nome canónico da variável de ambiente da KEK.</summary>
    public const string EnvironmentVariableName = "M3UCRAWLER_ENCRYPTION_KEY";

    /// <summary>Nome do ficheiro de chave dentro do directório de runtime.</summary>
    public const string KeyFileName = "secret.key";

    private readonly byte[] _key;

    public SecretProtector(string runtimeDataDir)
    {
        if (runtimeDataDir is null) throw new ArgumentNullException(nameof(runtimeDataDir));

        var envValue = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            _key = ResolveFromEnvironment(envValue);
            KeySource = "env";
            return;
        }

        _key = ResolveFromFile(runtimeDataDir);
        KeySource = "file";
    }

    /// <summary>
    /// Origem da chave usada (<c>"env"</c> ou <c>"file"</c>). Apenas
    /// para diagnóstico; nunca expõe o valor da chave.
    /// </summary>
    public string KeySource { get; }

    public string Protect(string plaintext)
    {
        if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));

        using var aes = new AesGcm(_key, TagSize);

        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var payload = new byte[1 + NonceSize + TagSize + ciphertext.Length];
        payload[0] = CurrentVersion;
        nonce.CopyTo(payload, 1);
        tag.CopyTo(payload, 1 + NonceSize);
        ciphertext.CopyTo(payload, 1 + NonceSize + TagSize);

        return Convert.ToBase64String(payload);
    }

    public string? Unprotect(string payload)
    {
        if (string.IsNullOrEmpty(payload)) return null;

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            return null;
        }

        if (raw.Length < 1 + NonceSize + TagSize) return null;
        if (raw[0] != CurrentVersion) return null;

        var nonce = new ReadOnlySpan<byte>(raw, 1, NonceSize);
        var tag = new ReadOnlySpan<byte>(raw, 1 + NonceSize, TagSize);
        var ciphertext = new ReadOnlySpan<byte>(raw, 1 + NonceSize + TagSize, raw.Length - 1 - NonceSize - TagSize);
        var plaintextBytes = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintextBytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    private static byte[] ResolveFromEnvironment(string envValue)
    {
        var trimmed = envValue.Trim();

        byte[] key;
        try
        {
            key = Convert.FromBase64String(trimmed);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Environment variable {EnvironmentVariableName} is set but is not valid base64. " +
                $"Expected base64-encoded {KeySize} bytes.",
                ex);
        }

        if (key.Length != KeySize)
        {
            throw new InvalidOperationException(
                $"Environment variable {EnvironmentVariableName} is set but decodes to {key.Length} bytes; " +
                $"expected exactly {KeySize} bytes.");
        }

        return key;
    }

    private static byte[] ResolveFromFile(string runtimeDataDir)
    {
        var path = Path.Combine(runtimeDataDir, KeyFileName);
        if (File.Exists(path))
        {
            return ReadKeyFile(path);
        }

        var key = new byte[KeySize];
        RandomNumberGenerator.Fill(key);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Convert.ToBase64String(key), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        ApplyOwnerOnlyMode(path);
        return key;
    }

    private static byte[] ReadKeyFile(string path)
    {
        string contents;
        try
        {
            contents = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException(
                $"Unable to read encryption key file '{path}'.", ex);
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(contents.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                $"Encryption key file '{path}' is not valid base64.", ex);
        }

        if (key.Length != KeySize)
        {
            throw new InvalidOperationException(
                $"Encryption key file '{path}' decodes to {key.Length} bytes; expected exactly {KeySize} bytes.");
        }

        return key;
    }

    private static void ApplyOwnerOnlyMode(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Permissões restritivas são best-effort e não fatais.
        }
    }
}
