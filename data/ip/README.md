# Offline IP reference data

IP Geolocation and ASN data by [DB-IP.com](https://db-ip.com/).

The application reads the bundled files locally. IP analysis does not contact DB-IP, perform DNS requests, or download updates.

| File | Reference edition | Source |
| --- | --- | --- |
| `dbip-country-lite-2026-10.mmdb` | 2026-10-01 | [DB-IP Country Lite](https://db-ip.com/db/download/ip-to-country-lite) |
| `dbip-asn-lite-2026-10.mmdb` | 2026-10-01 | [DB-IP ASN Lite](https://db-ip.com/db/download/ip-to-asn-lite) |
| `asn-organizations-2026-10.db` | 2026-10-01 | Derived ASN/organization index from the bundled ASN Lite database |

The original databases are redistributed without modification. The ASN index is a format conversion: one organization name per ASN found in the ASN Lite database, retaining the first occurrence when several records use the same ASN. ASNs absent from that database are reported as unknown; the index is not a complete ASN allocation registry. Organization names identify the recorded autonomous-system operator and may differ from an IP address user's ISP or legal owner.

These DB-IP Lite files and the derived ASN index are licensed under [Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/). The complete license is included in `LICENSE-CC-BY-4.0.txt`. Attribution and the reference date are included in analysis results and appended CSV columns. See `metadata.json` for source URLs, original database build timestamps, and SHA-256 checksums.

Country results estimate the network's country or territory. They do not identify a person's nationality or physical location. Lite data has reduced coverage and accuracy; VPNs, proxies, routing changes, and database age can affect the result.

Private, loopback, link-local, shared CGNAT, documentation, multicast, unspecified, and common reserved addresses are classified separately and are not assigned a country or ASN. Classification follows the [IANA IPv4 Special-Purpose Address Registry](https://www.iana.org/assignments/iana-ipv4-special-registry/) and [IANA IPv6 Special-Purpose Address Registry](https://www.iana.org/assignments/iana-ipv6-special-registry/), consulted on 2026-10-17. These classifications are fixed in the application and are not an exhaustive or live routing-policy registry.

The database reader is [MaxMind.Db](https://github.com/maxmind/MaxMind-DB-Reader-dotnet), licensed under Apache-2.0. It accesses the MMDB files through memory mapping; the application uses a bounded lookup cache and a disk-backed ASN index.
