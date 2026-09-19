using System.Net;
using System.Net.Sockets;

namespace m3uCrawler.Services.Validation;

/// <summary>
/// Classes núcleo de endereço usadas pela política SSRF
/// (<c>docs/Reestructure/17-SECURITY.md §2</c>, <c>35-SECURITY-MODEL.md</c>).
///
/// Apenas <see cref="Allowed"/> é utilizável para ligação. As restantes
/// classes são proibidas e provocam fail-closed.
/// </summary>
public enum IpAddressClass
{
    Allowed = 0,
    Loopback = 1,
    Private = 2,
    LinkLocal = 3,

    /// <summary>
    /// Reservado à classe "metadata". Não existe CIDR de metadata hardcoded:
    /// o endpoint convencional (169.254.169.254) é coberto por
    /// <see cref="LinkLocal"/>. Listas de metadata são PARAMETER_GAP.
    /// </summary>
    Metadata = 4,
    Other = 5,
}

/// <summary>
/// Classificador data-driven de endereços IPv4/IPv6 nas classes núcleo SSRF.
///
/// Regras incondicionais (<c>17-SECURITY.md:29-37</c>):
/// <list type="bullet">
///   <item>IPv4 loopback 127.0.0.0/8, private 10/8, 172.16/12, 192.168/16,
///         link-local 169.254/16, 0.0.0.0/8 e 255.255.255.255;</item>
///   <item>IPv6 ::1, fe80::/10, fc00::/7, :: (unspecified);</item>
///   <item>IPv4-mapped ::ffff:a.b.c.d é desempacotado e classificado como o
///         IPv4 embutido.</item>
/// </list>
///
/// Não há CIDRs extra nem listas de metadata: a política mantém-se
/// data-driven apenas com as classes núcleo.
/// </summary>
public static class AddressClassifier
{
    public static IpAddressClass Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // ::ffff:a.b.c.d — validar o IPv4 efectivo, não a forma mapeada.
            if (address.IsIPv4MappedToIPv6)
            {
                return Classify(address.MapToIPv4());
            }

            var v6 = address.GetAddressBytes();

            // ::1
            if (IsIPv6Loopback(v6)) return IpAddressClass.Loopback;
            // fe80::/10
            if (IsIPv6LinkLocal(v6)) return IpAddressClass.LinkLocal;
            // fc00::/7 (unique local)
            if (IsIPv6UniqueLocal(v6)) return IpAddressClass.Private;
            // :: (unspecified)
            if (IsIPv6Unspecified(v6)) return IpAddressClass.Other;

            return IpAddressClass.Allowed;
        }

        var b = address.GetAddressBytes();
        if (b.Length != 4) return IpAddressClass.Other;

        if (b[0] == 127) return IpAddressClass.Loopback;            // 127.0.0.0/8
        if (b[0] == 10) return IpAddressClass.Private;              // 10.0.0.0/8
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31)                // 172.16.0.0/12
            return IpAddressClass.Private;
        if (b[0] == 192 && b[1] == 168)                             // 192.168.0.0/16
            return IpAddressClass.Private;
        if (b[0] == 169 && b[1] == 254)                             // 169.254.0.0/16 (inclui metadata)
            return IpAddressClass.LinkLocal;
        if (b[0] == 0) return IpAddressClass.Other;                 // 0.0.0.0/8
        if (b[0] == 255 && b[1] == 255 && b[2] == 255 && b[3] == 255)
            return IpAddressClass.Other;                            // 255.255.255.255

        return IpAddressClass.Allowed;
    }

    /// <summary>True apenas para endereços que a política permite ligar.</summary>
    public static bool IsAllowed(IPAddress address)
        => Classify(address) == IpAddressClass.Allowed;

    private static bool IsIPv6Loopback(byte[] b)
    {
        for (var i = 0; i < 15; i++)
        {
            if (b[i] != 0) return false;
        }
        return b[15] == 1;
    }

    private static bool IsIPv6LinkLocal(byte[] b)
        => b[0] == 0xFE && (b[1] & 0xC0) == 0x80;                   // fe80::/10

    private static bool IsIPv6UniqueLocal(byte[] b)
        => (b[0] & 0xFE) == 0xFC;                                   // fc00::/7

    private static bool IsIPv6Unspecified(byte[] b)
    {
        foreach (var t in b)
        {
            if (t != 0) return false;
        }
        return true;
    }
}
