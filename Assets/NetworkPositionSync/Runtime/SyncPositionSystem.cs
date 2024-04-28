/*
MIT License

Copyright (c) 2021 James Frowen

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Mirage.Logging;
using Mirage.Serialization;
using Mirage.SocketLayer;
using UnityEngine;

namespace Mirage.SyncPosition
{
    /// <summary>
    /// Systems that sync <see cref="NetworkTransformBase"/>
    /// <para>
    /// Optimized version of <see cref="SyncPositionBehaviour_StandAlone"/> that sends Position of multiple objects instead of just one at once
    /// </para>
    /// </summary>
    [AddComponentMenu("Network/SyncPosition/SyncPositionSystem")]
    public class SyncPositionSystem : MonoBehaviour
    {
        private static readonly ILogger logger = LogFactory.GetLogger<SyncPositionSystem>();
        private static readonly List<NetworkTransformBase> _getCache = new List<NetworkTransformBase>();

        public NetworkClient Client;
        public NetworkServer Server;

        [SerializeField] private Settings _syncSettings = Settings.Default;

        [Tooltip("Sends write size with behaviour data. This will increase bandwidth, but avoids problems when a behaviour is not found when reading.")]
        [SerializeField] private bool _includeWriteSize = true;

        private int _maxItemSize;

        private double _nextSyncTime;
        private int _maxPacketSize;
        private double _previousTime;

        /// <summary>
        /// class that controls sending
        /// </summary>
        private Send _send;

        private readonly List<NetworkTransformBase> _behaviours = new List<NetworkTransformBase>();
        private readonly List<NetworkTransformBase> _clientAuthorityBehaviours = new List<NetworkTransformBase>();

        public InterpolationTime InterpolationTime { [MethodImpl(MethodImplOptions.AggressiveInlining)] get; private set; }
        public bool ClientActive => Client != null && Client.Active;
        public bool ServerActive => Server != null && Server.Active;

        private void Awake()
        {
            Setup(Server, Client, _syncSettings);
        }

        /// <summary>
        /// Method to setup System after awake is called
        /// </summary>
        public void Setup(NetworkServer server = null, NetworkClient client = null, Settings? settings = null)
        {
            if (server != null)
                Server = server;
            if (client != null)
                Client = client;
            if (settings.HasValue)
                _syncSettings = settings.Value;

            InterpolationTime = new InterpolationTime(_syncSettings.SyncInterval, tickDelay: _syncSettings.InterpolationDelay);

            Server?.Started.AddListener(ServerStarted);
            Server?.Stopped.AddListener(ServerStopped);

            Client?.Started.AddListener(ClientStarted);
            Client?.Disconnected.AddListener(ClientStopped);
        }

        private void OnDestroy()
        {
            Server?.Started.RemoveListener(ServerStarted);
            Server?.Stopped.RemoveListener(ServerStopped);
            Client?.Started.RemoveListener(ClientStarted);
            Client?.Disconnected.RemoveListener(ClientStopped);
        }

        private void ClientStarted()
        {
            // nothing to do in host mode
            if (ServerActive)
                return;

            _send = new SendAll(_clientAuthorityBehaviours, _maxPacketSize, sendIfEmpty: false, _includeWriteSize, (msg) => Client.Send(msg, Channel.Unreliable));

            // reset time when starting
            _nextSyncTime = Time.unscaledTime - _syncSettings.SyncInterval;

            _maxPacketSize = Client.SocketFactory.MaxPacketSize;
            AddWorldEvents(Client.World);
            Client.MessageHandler.RegisterHandler<PositionMessage>(ClientHandleNetworkPositionMessage);
        }

        private void ServerStarted()
        {
            switch (_syncSettings.SyncMode)
            {
                case SyncMode.SendToAll:
                    _send = new SendAll(_behaviours, _maxPacketSize, sendIfEmpty: true, _includeWriteSize, (msg) => Server.SendToMany(Server.Players.Where(x => x.IsAuthenticated), msg, excludeLocalPlayer: true, Channel.Unreliable));
                    break;
                case SyncMode.SendToObservers:
                    _send = new SendObservers(Server, _behaviours, _maxPacketSize, _includeWriteSize);
                    break;
            }

            // reset time when starting
            _nextSyncTime = Time.unscaledTime - _syncSettings.SyncInterval;

            _maxPacketSize = Server.SocketFactory.MaxPacketSize;
            AddWorldEvents(Server.World);
            Server.MessageHandler.RegisterHandler<PositionMessage>(ServerHandleNetworkPositionMessage);
        }

        private void ClientStopped(ClientStoppedReason arg0)
        {
            // nothing to do in host mode
            if (ServerActive)
                return;

            _send = null;
            _behaviours.Clear();
            _clientAuthorityBehaviours.Clear();
        }

        private void ServerStopped()
        {
            _send = null;
            _behaviours.Clear();
        }

        private void AddWorldEvents(NetworkWorld world)
        {
            world.onUnspawn += World_onUnspawn;
            world.AddAndInvokeOnSpawn(World_onSpawn);
            world.AddAndInvokeOnAuthorityChanged(World_onAuthorityChanged);
        }

        private void World_onSpawn(NetworkIdentity identity)
        {
            identity.gameObject.GetComponentsInChildren(true, _getCache);
            for (var i = 0; i < _getCache.Count; i++)
            {
                var behaviour = _getCache[i];
                _behaviours.Add(behaviour);
                behaviour.Setup();
                _maxItemSize = Math.Max(_maxItemSize, behaviour.MaxWriteSize);
            }
        }
        private void World_onUnspawn(NetworkIdentity identity)
        {
            identity.gameObject.GetComponentsInChildren(true, _getCache);
            for (var i = 0; i < _getCache.Count; i++)
                _behaviours.Remove(_getCache[i]);
        }
        private void World_onAuthorityChanged(NetworkIdentity identity, bool hasAuthority, INetworkPlayer owner)
        {
            if (!ClientActive)
                return;

            identity.gameObject.GetComponentsInChildren(true, _getCache);
            if (hasAuthority)
            {
                for (var i = 0; i < _getCache.Count; i++)
                    _clientAuthorityBehaviours.Add(_getCache[i]);
            }
            else
            {
                for (var i = 0; i < _getCache.Count; i++)
                    _behaviours.Remove(_getCache[i]);
            }
        }

        private void Update()
        {
            if (!ClientActive)
                return;

            var deltaTime = GetDeltaTime();
            InterpolationTime.OnUpdate(deltaTime);

            var snapshotTime = InterpolationTime.Time;
            var removeTime = snapshotTime - (InterpolationTime.ClientDelay * 1.5f);
            foreach (var behaviour in _behaviours)
            {
                behaviour.ClientUpdate(snapshotTime, removeTime);
            }
        }

        private float GetDeltaTime()
        {
            var now = Time.unscaledTimeAsDouble;
            var deltaTime = now - _previousTime;
            _previousTime = now;
            return (float)deltaTime;
        }

        private void LateUpdate()
        {
            // neither active
            if (!ClientActive && !ServerActive)
                return;

            // note: both server and client need to check send, because client auth
            var now = Time.unscaledTimeAsDouble;
            if (logger.LogEnabled()) logger.Log($"{name} Time till Sync: {_nextSyncTime - now:0.000}" + (now > _nextSyncTime ? "  Updating" : ""));
            if (now > _nextSyncTime)
            {
                Tmp_UpdateTime(_syncSettings.SyncInterval, _syncSettings.IntervalTiming, ref _nextSyncTime, now);

                double syncTime;
                if (ServerActive)
                {
                    syncTime = now;
                }
                else // client
                {
                    // Client must send message such that the time it arrives on the server is enough that server can lerp towards it
                    // what client knows:
                    // - NetworkTime.time (for RTT)
                    // - its local time
                    // - server time via
                    // note: client should never be sending localTime, because that is not linked to server in anyway
                    // todo it would be good to use InterpolationTime to update NetworkTime, instead of ping/pong, but currently no way to do this
                    var networkTime = Client.World.Time;
                    syncTime = InterpolationTime.LatestServerTime + networkTime.Rtt + networkTime.RttVar;
                }

                _send?.Update(syncTime, _maxItemSize);
                if (Server.IsHost)
                    InterpolationTime.OnMessage(now);
            }
        }
        // todo change back to Mirage.SyncSettings.UpdateTime after mirage changes to double
        public static void Tmp_UpdateTime(float interval, SyncTiming timing, ref double nextSyncTime, double now)
        {
            switch (timing)
            {
                case SyncTiming.Variable:
                    // atlesat Interval before next sync 
                    nextSyncTime = now + interval;
                    break;
                case SyncTiming.Fixed:
                    // just add Interval, so that it syncs 1/Interval times per second
                    // see SyncTiming.Fixed for example
                    nextSyncTime += interval;
                    break;
                default:
                case SyncTiming.NoInterval:
                    // always sync
                    nextSyncTime = now;
                    break;
            }
        }

        private void ClientHandleNetworkPositionMessage(PositionMessage msg)
        {
            // hostMode
            if (ServerActive)
                throw new InvalidOperationException("Server should not be sending message to host");

            var time = msg.Time;
            var lastTime = InterpolationTime.LatestServerTime;

            if (time < lastTime)
            {
                if (logger.LogEnabled()) logger.Log($"Received old message {time:0.000}, but latest receive was {lastTime:0.000}");
                return;
            }

            using (PooledNetworkReader metaReader = NetworkReaderPool.GetReader(msg.MetaPayload, Client.World),
                                       dataReader = NetworkReaderPool.GetReader(msg.DataPayload, Client.World))
            {
                NetworkTransformBase.ReadAll(time, metaReader, dataReader, _includeWriteSize);

                // if equal, message was fragmented, dont update time twice
                if (time != lastTime)
                    InterpolationTime.OnMessage(time);
            }
        }

        private readonly Dictionary<INetworkPlayer, double> clientAuthTime = new Dictionary<INetworkPlayer, double>();

        /// <summary>
        /// Position from client to server
        /// </summary>
        internal void ServerHandleNetworkPositionMessage(INetworkPlayer player, PositionMessage msg)
        {
            var time = msg.Time;
            if (clientAuthTime.TryGetValue(player, out var lastTime) && time < lastTime)
            {
                if (logger.LogEnabled()) logger.Log($"FromClient:{player}, Received old message {time:0.000}, but latest receive was {lastTime:0.000}");
                return;
            }
            clientAuthTime[player] = time;

            if (Server.IsHost)
            {
                if (time < InterpolationTime.ClientTime)
                    if (logger.WarnEnabled()) logger.LogWarning($"Message from client arrived too late for host, msg:{time:0.000} hostTime:{InterpolationTime.ClientTime}");
            }

            using (PooledNetworkReader metaReader = NetworkReaderPool.GetReader(msg.MetaPayload, Client.World),
                                       dataReader = NetworkReaderPool.GetReader(msg.DataPayload, Client.World))
            {
                NetworkTransformBase.ReadAll(time, metaReader, dataReader, _includeWriteSize);
            }
        }

        [Serializable]
        public struct Settings
        {
            [Tooltip("SendToAll option skips visibility and sends position to all ready connections.")]
            public SyncMode SyncMode;

            [Tooltip("How often server sends updates, or client if client had authority.\nNote: this should be set even if IntervalTiming is NoInterval, because Delay is a multiple of this interval")]
            public float SyncInterval;
            [Tooltip("How SyncInterval is used")]
            public SyncTiming IntervalTiming;

            [Tooltip("Delay client will use so that it always has snapshots. Multiple of 1/SyncInterval")]
            public float InterpolationDelay;

            public static Settings Default => new Settings
            {
                SyncMode = SyncMode.SendToAll,
                SyncInterval = 0.1f,
                IntervalTiming = SyncTiming.Variable,
                InterpolationDelay = 2.5f
            };
        }

        [Serializable]
        public enum SyncMode
        {
            SendToAll = 1,
            SendToObservers,
        }

        /// <summary>
        /// Note object metaData, and movement data are written to seperate writers.
        /// This is so that we can write the size of the data to meta after data has been written
        /// We need 2 writers for this, otherwise we will have to write a placeholder, and then go back and fill in in after, Which will take up more space because we can't compress size if we dont know what it is
        /// </summary>
        [NetworkMessage]
        public struct PositionMessage
        {
            /// <summary>
            /// how close to MTU we can get before sending 2 message instead of 1
            /// </summary>
            // msgId,time,payload sizes
            public const int HEADER_SIZE = 2 + 4 + (2 * 2);

            public double Time;

            /// <summary>
            /// Header values for each object. Their Netid, compIndex, write size (optional)
            /// </summary>
            public ArraySegment<byte> MetaPayload;
            /// <summary>
            /// Data for each object. the 2 payloads should be read a long side each other
            /// </summary>
            public ArraySegment<byte> DataPayload;
        }

        private abstract class Send
        {
            private readonly int _maxPacketSize;

            protected Send(int maxPacketSize)
            {
                _maxPacketSize = maxPacketSize;
            }

            protected bool IsFull(int maxItemSize, NetworkWriter metaWriter, NetworkWriter dataWriter)
            {
                var current = metaWriter.ByteLength + dataWriter.ByteLength + PositionMessage.HEADER_SIZE;
                // size of netid + compIndex + data length
                const int maxMetaSize = 4 + 1 + 2;
                var next = current + maxMetaSize + maxItemSize;

                return next > _maxPacketSize;
            }

            /// <summary>
            /// Checks for changes and send messages
            /// </summary>
            public abstract void Update(double time, int maxItemSize);
        }

        private class SendAll : Send
        {
            private readonly List<NetworkTransformBase> _behaviours;
            private readonly bool _sendIfEmpty;
            private readonly Action<PositionMessage> _send;
            private readonly bool _includeWriteSize;

            public SendAll(List<NetworkTransformBase> behaviours, int maxPacketSize, bool sendIfEmpty, bool includeWriteSize, Action<PositionMessage> send)
                : base(maxPacketSize)
            {
                _behaviours = behaviours;
                _sendIfEmpty = sendIfEmpty;
                _send = send;
                _includeWriteSize = includeWriteSize;
            }

            /// <summary>
            /// shared method for server and owner updates
            /// </summary>
            /// <param name=""></param>
            /// <param name="send"></param>
            public override void Update(double time, int maxItemSize)
            {
                if (_behaviours.Count == 0)
                    return;

                using (PooledNetworkWriter metaWriter = NetworkWriterPool.GetWriter(), dataWriter = NetworkWriterPool.GetWriter())
                {
                    var msg = new PositionMessage
                    {
                        Time = time,
                    };

                    var hasSent = false;
                    foreach (var behaviour in _behaviours)
                    {
                        behaviour.WriteIfDirty(metaWriter, dataWriter, _includeWriteSize);

                        // send if full
                        if (IsFull(maxItemSize, metaWriter, dataWriter))
                        {
                            hasSent = true;

                            Send(metaWriter, dataWriter, msg);

                            metaWriter.Reset();
                            dataWriter.Reset();
                        }
                    }

                    var empty = metaWriter.ByteLength == 0;
                    // small chance that we send msg above at max size, and then get here with empty writer.
                    // if empty, but has already send full payload previously
                    if (hasSent && empty)
                        return;

                    // if empty, then dont send if sendIfEmpty is false
                    if (empty && !_sendIfEmpty)
                        return;

                    // send even if empty, we always want too tell client the time
                    Send(metaWriter, dataWriter, msg);
                }
            }

            private void Send(PooledNetworkWriter metaWriter, PooledNetworkWriter dataWriter, PositionMessage msg)
            {
                msg.MetaPayload = metaWriter.ToArraySegment();
                msg.DataPayload = dataWriter.ToArraySegment();
                _send.Invoke(msg);
            }
        }

        private class SendObservers : Send
        {
            private readonly NetworkServer _server;
            private readonly List<NetworkTransformBase> _behaviours;
            private readonly bool _includeWriteSize;

            private readonly Dictionary<INetworkPlayer, WriteState> _observerState = new Dictionary<INetworkPlayer, WriteState>();
            private readonly Pool<WriteState> _pool = new Pool<WriteState>((p) => new WriteState(p), 1, 500);

            public SendObservers(NetworkServer server, List<NetworkTransformBase> behaviours, int maxPacketSize, bool includeWriteSize)
                : base(maxPacketSize)
            {
                _server = server;
                _behaviours = behaviours;
                _includeWriteSize = includeWriteSize;
            }

            /// <summary>
            /// Loops through all dirty objects, and then their observers and then writes that behaviouir to a cahced writer
            /// <para>But Packs once and copies bytes</para>
            /// </summary>
            /// <param name="time"></param>
            public override void Update(double time, int maxItemSize)
            {
                WriteAllBehaviours(time, maxItemSize);

                // send any data left in buffers, or empty message if none was sent
                FlushBuffers(time);

                ReleaseWriters();
            }

            private void WriteAllBehaviours(double time, int maxItemSize)
            {
                using (PooledNetworkWriter packMetaWriter = NetworkWriterPool.GetWriter(), packDataWriter = NetworkWriterPool.GetWriter())
                {
                    var hostPlayer = _server.LocalPlayer;
                    foreach (var behaviour in _behaviours)
                    {
                        // no observers, dont need to check if we should write
                        var observers = behaviour.Identity.observers;
                        var count = observers.Count;
                        if (count == 0)
                            continue;

                        // if only observer is host player, then skip
                        if (count == 1 && observers.Contains(hostPlayer))
                            continue;

                        // pack behaviour into writer
                        packMetaWriter.Reset();
                        packDataWriter.Reset();
                        behaviour.WriteIfDirty(packMetaWriter, packDataWriter, _includeWriteSize);

                        // copy from writer into buffers for each observers
                        foreach (var observer in observers)
                        {
                            // we never need to send from server to host player
                            if (observer == hostPlayer)
                                continue;

                            // get or create
                            if (!_observerState.TryGetValue(observer, out var state))
                            {
                                state = _pool.Take();
                                _observerState[observer] = state;
                            }

                            state.Meta.CopyFromWriter(packMetaWriter);
                            state.Data.CopyFromWriter(packDataWriter);

                            if (IsFull(maxItemSize, state.Meta, state.Data))
                            {
                                state.Send(observer, time);

                            }
                        }
                    }
                }
            }

            private void FlushBuffers(double time)
            {
                var hostPlayer = _server.LocalPlayer;
                foreach (var player in _server.Players)
                {
                    // dont send to host player
                    if (player == hostPlayer)
                        continue;

                    if (_observerState.TryGetValue(player, out var state))
                    {
                        state.Send(player, time);
                    }
                    else
                    {
                        // no data to send to this player, but we still need to send time
                        // player sitll needs time
                        var msg = new PositionMessage
                        {
                            Time = time,
                            MetaPayload = default,
                            DataPayload = default,
                        };

                        player.Send(msg, Channel.Unreliable);
                    }
                }
            }

            private void ReleaseWriters()
            {
                // release any extra writers,
                // there should be none, but it would be leak if we dont check
                foreach (var state in _observerState.Values)
                    state.Release();
                _observerState.Clear();
            }


            private class WriteState
            {
                public readonly NetworkWriter Meta;
                public readonly NetworkWriter Data;
                private readonly Pool<WriteState> _pool;
                public bool HasSent;

                public WriteState(Pool<WriteState> pool)
                {
                    Meta = new NetworkWriter(NetworkWriterPool.BufferSize.Value);
                    Data = new NetworkWriter(NetworkWriterPool.BufferSize.Value);
                    _pool = pool;
                }

                public void Release()
                {
                    _pool.Put(this);
                }

                /// <summary>
                /// sends if not empty
                /// </summary>
                /// <param name="player"></param>
                /// <param name="time"></param>
                public void Flush(INetworkPlayer player, double time)
                {
                    // if not sent yet, or has new data
                    if (!HasSent || Meta.BitPosition > 0)
                    {
                        Send(player, time);
                    }
                }

                public void Send(INetworkPlayer player, double time)
                {
                    var msg = new PositionMessage
                    {
                        Time = time,
                        MetaPayload = Meta.ToArraySegment(),
                        DataPayload = Data.ToArraySegment()
                    };

                    player.Send(msg, Channel.Unreliable);
                    Meta.Reset();
                    Data.Reset();
                    HasSent = true;
                }
            }
        }
    }
}
