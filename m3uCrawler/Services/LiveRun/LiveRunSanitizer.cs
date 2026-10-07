using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace m3uCrawler.Services.LiveRun;

/// <summary>
/// PHASE 9C.4 — Redacção defensiva para a instrumentação do Live Run.
///
/// <para>
/// Regra invariante: nada do que é escrito em <c>LastMessage</c>,
/// <c>LiveRunStepEntity.Message</c>, <c>Activity.Message</c>,
/// <c>Activity.Metadata</c> ou <c>CountsJson</c> pode conter
/// credenciais, tokens, cookies, session IDs ou URLs sensíveis.
/// Reutiliza os mecanismos existentes
/// (<see cref="CredentialSanitizer.SanitizeText"/>) em vez de os
/// reimplementar.
/// </para>
/// </summary>
public static class LiveRunSanitizer
{
    /// <summary>Limite de caracteres de <c>LastMessage</c> e <c>Message</c> (schema: 200).</summary>
    public const int MaxMessageLength = 200;

    private const int MaxMetadataEntries = 16;
    private const int MaxMetadataValueLength = 120;
    private const int MaxMetadataKeyLength = 40;

    /// <summary>
    /// PHASE 9C.4 (subwave 7) — Redacção defensiva de segredos que
    /// aparecem em texto livre (não em URL): cabeçalhos
    /// <c>Authorization</c>/<c>Proxy-Authorization</c> com esquema, e
    /// pares <c>chave: valor</c> / <c>chave=valor</c> para nomes de
    /// campo que transportam credenciais. <see cref="CredentialSanitizer"/>
    /// cobre credenciais embutidas em URLs; este passo cobre o resto,
    /// para que o invariante documentado ("nada do que é escrito...
    /// pode conter credenciais, tokens, cookies, session IDs") seja
    /// verdadeiro também fora de URLs.
    /// </summary>
    private static readonly Regex SecretSchemeRegex = new(
        @"\b(?:Bearer|Basic)\s+[A-Za-z0-9\-._~+/=]{6,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SecretKeyValueRegex = new(
        @"(?<key>\b(?:password|passwd|pwd|token|api[_-]?key|apikey|secret|session(?:id|_id)?|authorization|cookie)\b)" +
        @"(?<sep>\s*[:=]\s*)(?<value>[^\s&;""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Aplica a redacção defensiva de segredos em texto livre.</summary>
    private static string RedactFreeTextSecrets(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var redacted = SecretSchemeRegex.Replace(text, m => m.Value.Split(' ')[0] + " ***");
        redacted = SecretKeyValueRegex.Replace(
            redacted,
            m => m.Groups["key"].Value + m.Groups["sep"].Value + "***");
        return redacted;
    }

    /// <summary>
    /// Sanitiza e trunca uma mensagem para uso em estado persistido.
    /// Remove quebras de linha (que degradariam leitura em coluna
    /// única) e nunca devolve <c>null</c>.
    /// </summary>
    public static string Message(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // SanitizeText cobre URLs embutidas (userinfo, /live/USER/PASS/,
        // query username/password/token); RedactFreeTextSecrets cobre
        // segredos fora de URLs (Authorization, cookie, api_key=…).
        var sanitized = RedactFreeTextSecrets(CredentialSanitizer.SanitizeText(text));
        var compact = Compact(sanitized);

        if (compact.Length <= MaxMessageLength) return compact;
        return compact[..MaxMessageLength];
    }

    /// <summary>
    /// Sanitiza o dicionário de metadados de uma actividade. Devolve
    /// <c>null</c> quando não há nada a reportar.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Metadata(
        IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0) return null;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in metadata)
        {
            if (result.Count >= MaxMetadataEntries) break;

            var key = Compact(pair.Key ?? string.Empty);
            if (key.Length == 0) continue;
            if (key.Length > MaxMetadataKeyLength) key = key[..MaxMetadataKeyLength];

            var value = Compact(RedactFreeTextSecrets(
                CredentialSanitizer.SanitizeText(pair.Value ?? string.Empty)));
            if (value.Length > MaxMetadataValueLength) value = value[..MaxMetadataValueLength];

            // Chaves duplicadas após compactação: mantém a primeira.
            result.TryAdd(key, value);
        }

        return result.Count == 0 ? null : result;
    }

    private static string Compact(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '\r' || c == '\n' || c == '\t')
            {
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }

        var compact = sb.ToString();
        // Colapsa espaços múltiplos criados pela substituição de newlines.
        while (compact.Contains("  ", StringComparison.Ordinal))
        {
            compact = compact.Replace("  ", " ", StringComparison.Ordinal);
        }

        return compact.Trim();
    }
}
