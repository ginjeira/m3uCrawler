namespace m3uCrawler.Services.Auth;

/// <summary>
/// PHASE 9C.2 — Utilizador administrador da aplicação.
///
/// <para>
/// Nesta wave existe apenas o papel implícito de administrador: todas as
/// linhas desta tabela são administradores. Não há coluna de role/claims
/// porque nada no âmbito da 9C.2 a exige; a introdução de papéis será uma
/// migration aditiva futura.
/// </para>
/// </summary>
public sealed class AdminUserEntity
{
    public long Id { get; set; }

    /// <summary>Nome de utilizador único (case-sensitive como persistido).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Hash da password no formato versionado PHC-like
    /// <c>pbkdf2-sha256$&lt;iterations&gt;$&lt;base64(salt)&gt;$&lt;base64(hash)&gt;</c>.
    /// Inclui os parâmetros de derivação para permitir evolução futura.
    /// </summary>
    public string PasswordHash { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
}

/// <summary>
/// PHASE 9C.2 — Sessão de administrador persistida em SQLite.
///
/// <para>
/// O cookie transporta apenas <see cref="SessionId"/> (identificador
/// aleatório opaco de 256 bits). Nunca contém username, password ou hash.
/// A sessão sobrevive a restart, é revogável, expira e é removida no logout.
/// </para>
/// </summary>
public sealed class AdminSessionEntity
{
    public long Id { get; set; }

    /// <summary>Identificador opaco da sessão (base64url de 32 bytes).</summary>
    public string SessionId { get; set; } = string.Empty;

    public long AdminUserId { get; set; }

    /// <summary>Token CSRF por sessão, exigido em métodos mutantes.</summary>
    public string CsrfToken { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime LastSeenAtUtc { get; set; }
}
