namespace Susurro.Core.Net;

/// <summary>
/// Ambas PCs pueden iniciar la conexión (más robusto si el firewall bloquea en un solo sentido).
/// Si se forman dos conexiones a la vez, ambos extremos deben quedarse con la MISMA sin hablar
/// entre sí. Regla determinista:
///  - La conexión "preferida" es la iniciada por la instancia con el InstanceId menor.
///  - Una conexión nueva preferida reemplaza a la existente (si la existente también era preferida,
///    que llegue otra significa que el otro lado la dio por muerta: gana la nueva).
///  - Una conexión nueva no preferida solo reemplaza a una existente no preferida.
///  - Una conexión nueva no preferida NO reemplaza a una preferida existente (se rechaza y se
///    sondea la existente con un ping, por si estaba muerta).
/// </summary>
public static class SessionArbiter
{
    public static bool IsPreferred(string dialerId, string idA, string idB) =>
        string.CompareOrdinal(dialerId, string.CompareOrdinal(idA, idB) <= 0 ? idA : idB) == 0;

    public static bool ShouldReplace(string existingDialerId, string candidateDialerId, string localId, string peerId)
    {
        var candidatePreferred = IsPreferred(candidateDialerId, localId, peerId);
        var existingPreferred = IsPreferred(existingDialerId, localId, peerId);
        return candidatePreferred || !existingPreferred;
    }
}
