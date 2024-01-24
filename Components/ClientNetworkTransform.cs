using Unity.Netcode.Components;

// Returns false on OnIsServerAuthoritative, causing NGO to let
// clients update the transform values directly.
public class ClientNetworkTransform : NetworkTransform
{
    protected override bool OnIsServerAuthoritative()
    {
        return false;
    }
}