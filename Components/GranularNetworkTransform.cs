using System;
using Unity.Netcode;
using UnityEngine;

[DefaultExecutionOrder(1000)]
public class GranularNetworkTransform : NetworkBehaviour
{
    public struct TransformSnapshotData : INetworkSerializable
    {
        public float SnapshotTime;
        public Vector3 Position;
        public Quaternion Rotation;

        // If this is true when received,
        // the object will be teleported to the new position
        // without any interpolation
        public bool Teleport;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref SnapshotTime);
            serializer.SerializeValue(ref Position);
            serializer.SerializeValue(ref Rotation);
            serializer.SerializeValue(ref Teleport);
        }
    }

    [Tooltip("If true, the owner can move the object. If false, "
        + "only the server can move the object.")]
    public bool OwnerAuthority = false;

    [Tooltip("If true, the object's position will be interpolated. "
        + "If false, the object will be snapped to the newest-received position.")]
    public bool Interpolate = true;

    [Tooltip("The position of the object will be synchronized if this distance is exceeded.")]
    public float PositionThreshold = 0.1f;

    [Tooltip("The rotation of the object will be synchronized if this angle is exceeded.")]
    public float RotationThreshold = 5.0f;

    private bool _shouldSnap = false;

    private bool _dirty;

    private string _snapshotDataMessageName;

    private TransformSnapshotData _newestSnapshot;
    private TransformSnapshotData _localSnapshot;

    private float _timeNow => NetworkManager.ServerTime.TimeAsFloat;

    private NetworkVariable<bool> _ownerAuthority = new();

    public void SetPositionAndRotation(Vector3 position, Quaternion rotation)
    {
        var snapShot = _localSnapshot;
        snapShot.Position = position;
        snapShot.Rotation = rotation;
        snapShot.SnapshotTime = _timeNow;
        _localSnapshot = snapShot;

        transform.position = position;
        transform.rotation = rotation;
    }

    public void SetPositionImmediate(Vector3 newPosition)
    {
        var snapShot = _localSnapshot;
        snapShot.Position = newPosition;
        snapShot.SnapshotTime = _timeNow;
        snapShot.Teleport = true;
        _localSnapshot = snapShot;

        transform.position = newPosition;
    }

    public void SetRotationImmediate(Quaternion newRotation)
    {
        var snapShot = _localSnapshot;
        snapShot.Rotation = newRotation;
        snapShot.SnapshotTime = _timeNow;
        snapShot.Teleport = true;
        _localSnapshot = snapShot;

        transform.rotation = newRotation;
    }

    public void SetScaleImmediate(Vector3 newScale)
    {
        // Not implemented
    }

    public override void OnNetworkSpawn()
    {
        _localSnapshot = new TransformSnapshotData
        {
            Position = transform.position,
            Rotation = transform.rotation,
            SnapshotTime = _timeNow
        };

        // register messaging first
        _snapshotDataMessageName = $"NT_SNAP_{NetworkObjectId}_{NetworkBehaviourId}";
        NetworkManager.CustomMessagingManager.RegisterNamedMessageHandler(_snapshotDataMessageName, OnSnapshotDataReceived);

        if (IsServer)
        {
            _ownerAuthority.Value = OwnerAuthority;
        }

        if (IsOwner && OwnerAuthority || IsServer && !OwnerAuthority)
        {
            TrySendSnapshotData(_localSnapshot);
        }
    }

    private void Update()
    {
        NetworkUpdate();
    }

    public void NetworkUpdate()
    {
        if (NetworkManager.Singleton == null)
        {
            return;
        }

        if (OwnerAuthority)
        {
            if (IsOwner)
            {
                // Update the object's position
                TryCommit();
            }
            else
            {
                ProgressInterpolation();
            }
        }
        else
        {
            if (IsServer)
            {
                // Update the object's position
                TryCommit();
            }
            else
            {
                ProgressInterpolation();
            }
        }
    }

    private void TryCommit()
    {
        if (IsServer && OwnerAuthority && OwnerClientId != NetworkManager.ServerClientId)
        {
            Debug.LogWarning("Server should not be calling TryCommit on transform with owner authority", this);
            return;
        }
        else if (!OwnerAuthority && !IsServer)
        {
            Debug.LogWarning("Clients should not be calling TryCommit on a transform with server authority", this);
            return;
        }

        // If it has been too long since the last snapshot, or the object has moved too much,
        // or the object has rotated too much, send a new snapshot
        if (_timeNow - _localSnapshot.SnapshotTime > NetworkManager.ServerTime.FixedDeltaTime
        || Vector3.Distance(_localSnapshot.Position, transform.position) > PositionThreshold
        || Quaternion.Angle(_localSnapshot.Rotation, transform.rotation) > RotationThreshold)
        {
            _localSnapshot.Position = transform.position;
            _localSnapshot.Rotation = transform.rotation;
            _localSnapshot.SnapshotTime = _timeNow;
            _dirty = true;
        }

        if (!_dirty)
            return;

        TrySendSnapshotData(_localSnapshot);
    }

    private void ProgressInterpolation()
    {
        if (_shouldSnap || !Interpolate)
        {
            // Snap to the newest-received position
            _localSnapshot.Position = _newestSnapshot.Position;
            _localSnapshot.Rotation = _newestSnapshot.Rotation;
            _localSnapshot.SnapshotTime = _timeNow;
            _shouldSnap = false;
        }
        else
        {
            InterpolatePosition();
            InterpolateRotation();
        }

        transform.SetPositionAndRotation(_localSnapshot.Position, _localSnapshot.Rotation);
    }

    private void InterpolatePosition()
    {
        // Interpolate towards the newest-received position
        Vector3 lastPos = _localSnapshot.Position;
        Vector3 targetPos = _newestSnapshot.Position;

        // Interpolation time is the time since the last snapshot was received
        float travelTime = _timeNow - _localSnapshot.SnapshotTime;

        if (travelTime <= 0.0f)
        {
            _localSnapshot.Position = targetPos;
        }

        // Estimate velocity
        Vector3 velocity = (targetPos - lastPos) / travelTime / NetworkManager.ServerTime.FixedDeltaTime;

        // Interpolate
        _localSnapshot.Position = Vector3.MoveTowards(lastPos, targetPos, velocity.magnitude);
    }

    private void InterpolateRotation()
    {
        // Interpolate towards the newest-received rotation
        Quaternion lastRot = _localSnapshot.Rotation;
        Quaternion targetRot = _newestSnapshot.Rotation;

        // Interpolation time is the time since the last snapshot was received
        float travelTime = _timeNow - _localSnapshot.SnapshotTime;

        // Estimate angular velocity
        float angle = Quaternion.Angle(lastRot, targetRot);
        float angularSpeed = angle / travelTime / NetworkManager.ServerTime.FixedDeltaTime;

        // Interpolate
        _localSnapshot.Rotation = Quaternion.RotateTowards(lastRot, targetRot, angularSpeed);
    }

    private void OnSnapshotDataReceived(ulong senderId, FastBufferReader payload)
    {
        if (NetworkManager.Singleton == null)
        {
            Debug.LogWarning("OnSnapshotDataReceived called but NetworkManager is null", this);
            return;
        }

        // If is server and owner authority, forward the snapshot to all clients
        if (IsServer && OwnerAuthority)
        {
            ForwardSnapshotData(ref payload);
        }

        // Then read the snapshot data
        payload.ReadNetworkSerializableInPlace(ref _newestSnapshot);

        _shouldSnap = _newestSnapshot.Teleport;
    }

    private void TrySendSnapshotData(TransformSnapshotData snapshot)
    {
        if (NetworkManager.Singleton == null)
            return;

        if (!IsServer && !IsOwner)
        {
            Debug.LogWarning("TrySendSnapshotData called on a client that is not the owner", this);
            return;
        }

        var writer = new FastBufferWriter(128, Unity.Collections.Allocator.Temp);
        using (writer)
        {
            writer.WriteNetworkSerializable(in snapshot);
            if (IsServer)
            {
                var numClients = NetworkManager.Singleton.ConnectedClientsList.Count;
                // Server sends to all clients
                for (int i = 0; i < numClients; i++)
                {
                    if (NetworkManager.Singleton == null)
                    {
                        Debug.LogWarning("NetworkManager.Singleton was null", this);
                        return;
                    }

                    var client = NetworkManager.Singleton.ConnectedClientsList[i];

                    // Don't send from server to itself
                    if (client.ClientId == NetworkManager.ServerClientId)
                        continue;

                    NetworkManager.Singleton.CustomMessagingManager
                        .SendNamedMessage(_snapshotDataMessageName, client.ClientId, writer);
                }
            }
            else if (IsOwner)
            {
                // Owner sends to server
                NetworkManager.Singleton.CustomMessagingManager
                    .SendNamedMessage(_snapshotDataMessageName, NetworkManager.ServerClientId, writer);
            }
        }
        _dirty = false;
    }

    private void ForwardSnapshotData(ref FastBufferReader payload)
    {
        if (NetworkManager.Singleton == null)
            return;

        if (!IsServer)
            return;

        var currentPosition = payload.Position;
        var msgSize = payload.Length;

        var snapData = new TransformSnapshotData();
        payload.ReadNetworkSerializableInPlace(ref snapData);

        var writer = new FastBufferWriter(msgSize, Unity.Collections.Allocator.Temp);

        // Using (writer) disposes the writer (unmanaged memory) after the block is exited
        using (writer)
        {
            // First write the snapshot data to the buffer
            writer.WriteNetworkSerializable(in snapData);
            var numClients = NetworkManager.Singleton.ConnectedClientsList.Count;
            for (int i = 0; i < numClients; i++)
            {
                var client = NetworkManager.Singleton.ConnectedClientsList[i];

                // We don't want to send the snapshot data to the server or the owner if it has authority
                if (client.ClientId == NetworkManager.ServerClientId || (OwnerAuthority && client.ClientId == OwnerClientId))
                    continue;

                // Then send the snapshot data to each client
                NetworkManager.Singleton.CustomMessagingManager
                    .SendNamedMessage(_snapshotDataMessageName, client.ClientId, writer);
            }
        }

        payload.Seek(currentPosition);
    }
}