using System;

namespace m3uCrawler.Services.Catalog;

/// <summary>
/// Excepção levantada pelas operações administrativas sobre o
/// catálogo (<see cref="CatalogResolver.CreateCanonicalChannelAsync"/>,
/// <see cref="CatalogResolver.UpdateCanonicalChannelAsync"/>,
/// <see cref="CatalogResolver.DeleteCanonicalChannelAsync"/>,
/// <see cref="CatalogResolver.AddAliasAsync"/>,
/// <see cref="CatalogResolver.RemoveAliasAsync"/>). O tipo
/// discrimina a categoria de erro para que a camada HTTP/API
/// traduza num status code apropriado (BadRequest vs Conflict vs
/// NotFound).
/// </summary>
public sealed class ChannelAdministrationException : Exception
{
    public ChannelAdministrationException(ChannelAdministrationError error, string message)
        : base(message)
    {
        Error = error;
    }

    public ChannelAdministrationError Error { get; }
}

public enum ChannelAdministrationError
{
    InvalidInput = 0,
    DuplicateKey = 1,
    AliasConflict = 2,
    ChannelNotFound = 3,
    AlreadyExists = 4,
    HasOwnership = 5,
}
