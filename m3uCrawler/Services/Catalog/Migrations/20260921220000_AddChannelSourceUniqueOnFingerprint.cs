using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace m3uCrawler.Services.Catalog.Migrations
{
    /// <summary>
    /// W-REVIEW-02B — filtered UNIQUE em <c>channel_sources</c>.
    ///
    /// <para>
    /// Uses fluent <see cref="MigrationBuilder"/> API (rather than raw SQL)
    /// so that <c>dotnet ef migrations add</c> regenerates the
    /// <c>.Designer.cs</c> partial and the model snapshot in lock-step
    /// with the model. The filter <c>"Fingerprint" IS NOT NULL</c>
    /// matches the Decision Pack spec and applies uniqueness only to
    /// streams that have a calculable fingerprint, preserving the
    /// coexistence of legacy rows and non-fingerprintable streams.
    /// </para>
    ///
    /// <para>
    /// Índice único filtrado sobre
    /// <c>(CanonicalChannelId, SourceId, Fingerprint, FingerprintVersion)</c>,
    /// aplicando-se apenas a rows com <c>Fingerprint IS NOT NULL</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Porquê filtrado (WHERE Fingerprint IS NOT NULL):</b> a tabela
    /// <c>channel_sources</c> tem rows legadas (pré-W4) e rows não-fingerprintáveis
    /// (URLs não-http/https) onde <c>Fingerprint</c> permanece <c>NULL</c>.
    /// Essas rows são deduplicadas por
    /// <c>(CanonicalChannelId, SourceId, StreamUrl)</c> e devem poder coexistir
    /// em múltiplas rows sem trip o índice único. A semântica do filtro é
    /// "só aplica a unicidade a streams que tenham fingerprint calculável",
    /// preservando a coexistência de rows legadas e não-fingerprintáveis.
    /// </para>
    ///
    /// <para>
    /// <b>Porquê não (CanonicalChannelId, SourceId):</b> D2 (descoberta via
    /// ingestion) suporta múltiplas streams distintas (URLs distintas) sob o
    /// mesmo par (canal, source) — diferentes bitrates, mirrors,
    /// resoluções. Um índice único não-filtrado sobre (CanonicalChannelId,
    /// SourceId) seria demasiado restritivo e quebraria o modelo D2
    /// intencional. Apenas o fingerprint (URL canonizado) identifica o mesmo
    /// stream observacional, pelo que a unicidade deve aplicar-se à tupla
    /// completa, não ao par.
    /// </para>
    ///
    /// <para>
    /// <b>No-op data-wise:</b> o lookup existente em
    /// <c>RecordChannelSourceAsync</c> (passos 1+2 do dedup intra-Source)
    /// já garante, no caminho pré-SaveChanges, que não são criadas rows
    /// duplicadas para streams com fingerprint computável. Esta migration
    /// adiciona uma rede de segurança contra corridas entre múltiplos
    /// contexts concorrentes (e.g. ingestion + approval paralelos).
    /// </para>
    ///
    /// <para>
    /// <b>Operação em DB com duplicados existentes:</b> se a DB já tiver
    /// rows duplicadas que violem este índice, o CREATE falhará com
    /// <c>SQLite Error 19: UNIQUE constraint failed</c> e a migration
    /// aborta (transacção rolled back). NÃO é feita qualquer auto-deduplicação
    /// silenciosa: o operador deve inspeccionar e resolver manualmente
    /// antes de re-aplicar.
    /// </para>
    /// </summary>
    public partial class AddChannelSourceUniqueOnFingerprint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_channel_sources_Channel_Source_Fingerprint_Unique",
                table: "channel_sources",
                columns: new[] { "CanonicalChannelId", "SourceId", "Fingerprint", "FingerprintVersion" },
                unique: true,
                filter: "\"Fingerprint\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_channel_sources_Channel_Source_Fingerprint_Unique",
                table: "channel_sources");
        }
    }
}
