﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using Metrics;
using Aggregates.Extensions;
using NServiceBus.Logging;
using Timer = System.Threading.Timer;

namespace Aggregates.Internal
{
    class PersistentClient : IDisposable
    {
        private static readonly ILog Logger = LogManager.GetLogger("PersistentClient");
        private static readonly Counter QueuedEvents = Metric.Counter("Queued Events", Unit.Events);

        private readonly Metrics.Counter Queued;
        private readonly Metrics.Counter Processed;
        private readonly Metrics.Counter Acknowledged;
        private readonly Metrics.Timer Idle;

        private readonly IEventStoreConnection _client;
        private readonly string _stream;
        private readonly string _group;
        private readonly int _index;
        private readonly CancellationToken _token;
        private readonly Task _acknowledger;
        private readonly ConcurrentQueue<ResolvedEvent> _waitingEvents;

        // Todo: Change to List<Guid> when/if PR 1143 is published
        private List<ResolvedEvent> _toAck;

        private EventStorePersistentSubscriptionBase _subscription;
        private TimerContext _idleContext;

        public bool Live { get; private set; }
        public string Id => $"{_client.Settings.GossipSeeds[0].EndPoint.Address}.{_stream.Substring(_stream.LastIndexOf(".") + 1)}.{_index}";

        private bool _disposed;

        public PersistentClient(IEventStoreConnection client, string stream, string group, int index, CancellationToken token)
        {
            _client = client;
            _stream = stream;
            _index = index;
            _group = group;
            _token = token;
            _toAck = new List<ResolvedEvent>();
            _waitingEvents = new ConcurrentQueue<ResolvedEvent>();

            Queued = Metric.Context("Subscription Clients").Context(Id).Counter("Queued", Unit.Events);
            Processed = Metric.Context("Subscription Clients").Context(Id).Counter("Processed", Unit.Events);
            Acknowledged = Metric.Context("Subscription Clients").Context(Id).Counter("Acknowledged", Unit.Events);
            Idle = Metric.Context("Subscription Clients").Context(Id).Timer("Idle", Unit.None);


            _acknowledger = Timer.Repeat(state =>
            {
                var info = (PersistentClient)state;

                ResolvedEvent[] toAck = Interlocked.Exchange(ref _toAck, new List<ResolvedEvent>()).ToArray();

                if (!toAck.Any())
                    return Task.CompletedTask;

                if (!info.Live)
                    throw new InvalidOperationException(
                        "Subscription was stopped while events were waiting to be ACKed");

                Acknowledged.Increment(toAck.Length);
                Logger.Write(LogLevel.Info, () => $"Acknowledging {toAck.Length} events to {Id}");

                var page = 0;
                while (page < toAck.Length)
                {
                    var working = toAck.Skip(page).Take(2000);
                    info._subscription.Acknowledge(working);
                    page += 2000;
                }
                return Task.CompletedTask;
            }, this, TimeSpan.FromSeconds(5), token);

        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _subscription.Stop(TimeSpan.FromSeconds(30));
            _acknowledger.Dispose();
        }

        private void EventAppeared(EventStorePersistentSubscriptionBase sub, ResolvedEvent e)
        {
            _token.ThrowIfCancellationRequested();

            Logger.Write(LogLevel.Debug,
                () =>
                        $"Event appeared {e.Event.EventId} type {e.Event.EventType} stream [{e.Event.EventStreamId}] number {e.Event.EventNumber} projection event number {e.OriginalEventNumber}");
            Queued.Increment();
            QueuedEvents.Increment();
            _waitingEvents.Enqueue(e);
        }

        private void SubscriptionDropped(EventStorePersistentSubscriptionBase sub, SubscriptionDropReason reason, Exception ex)
        {
            Live = false;

            Logger.Write(LogLevel.Info, () => $"Disconnected from subscription.  Reason: {reason} Exception: {ex}");

            // Todo: is it possible to ACK an event from a reconnection?
            if (_toAck.Any())
                throw new InvalidOperationException(
                    $"Eventstore subscription dropped and we need to ACK {_toAck.Count} more events");

            // Need to clear ReadyEvents of events delivered but not processed before disconnect
            ResolvedEvent e;
            while (!_waitingEvents.IsEmpty)
            {
                Queued.Decrement();
                QueuedEvents.Decrement();
                _waitingEvents.TryDequeue(out e);
            }

            if (reason == SubscriptionDropReason.UserInitiated) return;

            // Run in task.Run because mixing .Wait and async methods is bad bad 
            Task.Run(Connect, _token).Wait(_token);
        }
        public async Task Connect()
        {
            Logger.Write(LogLevel.Info,
                () =>
                        $"Connecting to subscription group [{_group}] on client {_client.Settings.GossipSeeds[0].EndPoint.Address}");
            // Todo: play with buffer size?
            _subscription = await _client.ConnectToPersistentSubscriptionAsync(_stream, _group,
                eventAppeared: EventAppeared,
                subscriptionDropped: SubscriptionDropped,
                bufferSize: 100,
                autoAck: false).ConfigureAwait(false);
            Live = true;
        }

        public void Acknowledge(ResolvedEvent @event)
        {
            if (!Live)
                throw new InvalidOperationException("Cannot ACK an event, subscription is dead");

            Processed.Increment();
            _idleContext.Dispose();
            _toAck.Add(@event);
        }

        public bool TryDequeue(out ResolvedEvent e)
        {
            e = default(ResolvedEvent);
            if (Live && _waitingEvents.TryDequeue(out e))
            {
                Queued.Decrement();
                QueuedEvents.Decrement();
                _idleContext = Idle.NewContext();
                return true;
            }
            return false;
        }

    }
}
