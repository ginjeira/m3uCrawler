namespace m3uCrawler.Models
{
    /// <summary>
    /// Resultado de parsing de uma playlist M3U, conforme
    /// <c>04-PLAYLIST-STREAM.md</c> §7 e o contrato W3.
    /// <para>
    /// <see cref="Success"/> exige pelo menos uma entrada válida e zero
    /// entradas malformadas/inutilizáveis. <see cref="Partial"/> nunca
    /// equivale a sucesso (<c>19-FAILURE-MODEL.md:32-37</c>).
    /// </para>
    /// </summary>
    public enum M3uPlaylistStatus
    {
        Success,
        Partial,
        Failed,
    }
}
