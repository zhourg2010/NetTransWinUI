using System.Net;
using NetTrans.Download;

namespace NetTrans.Torrent;

// What a swarm reports about itself: one record for the whole torrent and one
// per peer. They sat in TorrentSwarm.cs, where nothing but their name suggested
// they were the shape the row and the inspector are drawn from.

/// <summary>
/// What one peer connection is doing, for the inspector's 连接 tab.
/// </summary>
/// <param name="Peer">The address, as the tracker gave it.</param>
/// <param name="Down">Bytes per second arriving from this peer.</param>
/// <param name="Up">Bytes per second going out to it.</param>
/// <param name="Interested">Whether it wants something we have.</param>
public sealed record PeerRate(IPEndPoint Peer, double Down, double Up, bool Interested);

/// <summary>How a torrent is going, for the row and the inspector.</summary>
/// <param name="Downloaded">Bytes of verified pieces.</param>
/// <param name="Total">Bytes the torrent holds.</param>
/// <param name="Pieces">Verified pieces.</param>
/// <param name="TotalPieces">Pieces the torrent has.</param>
/// <param name="ConnectedPeers">Peers currently talking to us.</param>
/// <param name="KnownPeers">Peers a tracker has told us about.</param>
/// <param name="Uploaded">Bytes served to peers, which is what a tracker counts as a share.</param>
public sealed record SwarmProgress(
    long Downloaded,
    long Total,
    int Pieces,
    int TotalPieces,
    int ConnectedPeers,
    int KnownPeers,
    long Uploaded = 0);
