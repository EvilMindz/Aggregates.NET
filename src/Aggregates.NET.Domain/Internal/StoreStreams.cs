﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aggregates.Contracts;
using Aggregates.Exceptions;
using Aggregates.Extensions;
using Metrics;
using Metrics.Utils;
using Newtonsoft.Json;
using NServiceBus;
using NServiceBus.Logging;
using NServiceBus.MessageInterfaces;
using NServiceBus.ObjectBuilder;
using NServiceBus.Settings;

namespace Aggregates.Internal
{
    /// <summary>
    /// Permanently store streams to store using IStoreEvents
    /// </summary>
    class StoreStreams : IStoreStreams
    {
        private const string OobMetadataKey = "Aggregates.OOB";

        private static readonly Meter Saved = Metric.Meter("Saved Streams", Unit.Items, tags: "debug");
        private static readonly Meter HitMeter = Metric.Meter("Stream Cache Hits", Unit.Events);
        private static readonly Meter MissMeter = Metric.Meter("Stream Cache Misses", Unit.Events);

        // Todo: make a separate "OobDefinitionHandler" interface
        private static readonly ConcurrentDictionary<string, Tuple<DateTime, IEnumerable<OobDefinition>>> OobDefinitionCache =
            new ConcurrentDictionary<string, Tuple<DateTime, IEnumerable<OobDefinition>>>();
        private static Task _cacheExpiration;

        private static readonly ILog Logger = LogManager.GetLogger("StoreStreams");
        private readonly IStoreEvents _store;
        private readonly IMessagePublisher _publisher;
        private readonly IStoreSnapshots _snapstore;
        private readonly ICache _cache;
        private readonly StreamIdGenerator _streamGen;
        private readonly IEnumerable<IEventMutator> _mutators;
        private readonly Random _random;



        public StoreStreams(ICache cache, IStoreEvents store, IMessagePublisher publisher, IStoreSnapshots snapstore, StreamIdGenerator streamGen, IEnumerable<IEventMutator> mutators)
        {
            _cache = cache;
            _store = store;
            _publisher = publisher;
            _snapstore = snapstore;
            _streamGen = streamGen;
            _mutators = mutators;
            _random = new Random();


            _cacheExpiration = Timer.Repeat(() =>
            {
                var expired = OobDefinitionCache.Where(x => (DateTime.UtcNow - x.Value.Item1) > TimeSpan.FromMinutes(5)).Select(x => x.Key)
                    .ToList();

                Tuple<DateTime, IEnumerable<OobDefinition>> temp;
                foreach (var key in expired)
                    OobDefinitionCache.TryRemove(key, out temp);

                return Task.CompletedTask;
            }, TimeSpan.FromMinutes(5), "expires cached oob definitions from the cache");
        }


        public async Task<IEventStream> GetStream<T>(string bucket, Id streamId, IEnumerable<Id> parents = null) where T : class, IEventSource
        {
            parents = parents ?? new Id[] { };

            var streamName = _streamGen(typeof(T), StreamTypes.Domain, bucket, streamId, parents);
            Logger.Write(LogLevel.Debug, () => $"Retreiving stream [{streamId}] in bucket [{bucket}] for type {typeof(T).FullName}");

            var cached = _cache.Retreive(streamName) as IImmutableEventStream;
            if (cached != null)
            {
                HitMeter.Mark();
                Logger.Write(LogLevel.Debug, () => $"Found stream [{streamName}] in cache");
                return new EventStream<T>(cached);
            }
            MissMeter.Mark();

            // checking for frozen is another read we probably don't need to do
            //while (await CheckFrozen<T>(bucket, streamId, parents).ConfigureAwait(false))
            //{
            //    Logger.Write(LogLevel.Info, () => $"Stream [{streamId}] in bucket [{bucket}] is frozen - waiting");
            //    await Task.Delay(100).ConfigureAwait(false);
            //}
            Logger.Write(LogLevel.Debug, () => $"Stream [{streamId}] in bucket [{bucket}] not in cache - reading from store");

            ISnapshot snapshot = null;
            if (typeof(ISnapshotting).IsAssignableFrom(typeof(T)))
            {
                snapshot = await _snapstore.GetSnapshot<T>(bucket, streamId, parents).ConfigureAwait(false);
                Logger.Write(LogLevel.Debug, () =>
                {
                    if (snapshot != null)
                        return $"Retreived snapshot for entity id [{streamId}] bucket [{bucket}] version {snapshot.Version}";
                    return $"No snapshot found for entity id [{streamId}] bucket [{bucket}]";
                });
            }

            var events = await _store.GetEvents(streamName, start: snapshot?.Version).ConfigureAwait(false);


            Tuple<DateTime, IEnumerable<OobDefinition>> oobs = null;
            if (!OobDefinitionCache.TryGetValue(streamName, out oobs) || (DateTime.UtcNow - oobs.Item1).TotalSeconds > 30)
            {
                var oobMetadata = await _store.GetMetadata(streamName, OobMetadataKey).ConfigureAwait(false);

                if (!string.IsNullOrEmpty(oobMetadata))
                    oobs = new Tuple<DateTime, IEnumerable<OobDefinition>>(DateTime.UtcNow,
                        JsonConvert.DeserializeObject<IEnumerable<OobDefinition>>(oobMetadata));
                else
                    oobs = new Tuple<DateTime, IEnumerable<OobDefinition>>(DateTime.UtcNow, null);
                OobDefinitionCache.TryAdd(streamName, oobs);
            }

            var eventstream = new EventStream<T>(bucket, streamId, parents, oobs?.Item2, events, snapshot);

            _cache.Cache(streamName, eventstream.Clone());

            Logger.Write(LogLevel.Debug, () => $"Stream [{streamId}] in bucket [{bucket}] read - version is {eventstream.CommitVersion}");
            return eventstream;
        }

        public Task<IEventStream> NewStream<T>(string bucket, Id streamId, IEnumerable<Id> parents = null) where T : class, IEventSource
        {
            parents = parents ?? new Id[] { };
            Logger.Write(LogLevel.Debug, () => $"Creating new stream [{streamId}] in bucket [{bucket}] for type {typeof(T).FullName}");
            IEventStream stream = new EventStream<T>(bucket, streamId, parents, null, null);

            return Task.FromResult(stream);
        }

        public async Task<long> GetSize<T>(IEventStream stream, string oob) where T : class, IEventSource
        {
            var streamName = _streamGen(typeof(T), StreamTypes.OOB, stream.Bucket, stream.StreamId, stream.Parents);

            return (await Enumerable.Range(1, 10).ToArray().StartEachAsync(5, (vary) => _store.Size($"{streamName}-{oob}.{vary}")).ConfigureAwait(false)).Sum();
        }
        public async Task<IEnumerable<IFullEvent>> GetEvents<T>(IEventStream stream, long start, int count, string oob = null) where T : class, IEventSource
        {
            if (string.IsNullOrEmpty(oob))
            {
                var streamName = _streamGen(typeof(T), StreamTypes.Domain, stream.Bucket, stream.StreamId, stream.Parents);
                return await _store.GetEvents(streamName, start: start, count: count).ConfigureAwait(false);
            }

            var oobName = _streamGen(typeof(T), StreamTypes.OOB, stream.Bucket, stream.StreamId, stream.Parents);
            count = count / 10;
            // if take is 100, take 10 from each of 10 streams - see above
            var events = await Enumerable.Range(1, 10).ToArray()
                .StartEachAsync(5,
                    (vary) => _store.GetEvents($"{oobName}-{oob}.{vary}", start, count))
                .ConfigureAwait(false);
            return events.SelectMany(x => x);
        }
        public async Task<IEnumerable<IFullEvent>> GetEventsBackwards<T>(IEventStream stream, long start, int count, string oob = null) where T : class, IEventSource
        {
            if (string.IsNullOrEmpty(oob))
            {
                var streamName = _streamGen(typeof(T), StreamTypes.Domain, stream.Bucket, stream.StreamId, stream.Parents);
                return await _store.GetEventsBackwards(streamName, start: start, count: count).ConfigureAwait(false);
            }

            var oobName = _streamGen(typeof(T), StreamTypes.OOB, stream.Bucket, stream.StreamId, stream.Parents);
            count = count / 10;
            // if take is 100, take 10 from each of 10 streams - see above
            var events = await Enumerable.Range(1, 10).ToArray()
                .StartEachAsync(5,
                    (vary) => _store.GetEventsBackwards($"{oobName}-{oob}.{vary}", start, count))
                .ConfigureAwait(false);
            return events.SelectMany(x => x);
        }



        public async Task WriteStream<T>(Guid commitId, IEventStream stream, IDictionary<string, string> commitHeaders) where T : class, IEventSource
        {
            var streamName = _streamGen(typeof(T), StreamTypes.Domain, stream.Bucket, stream.StreamId, stream.Parents);
            Logger.Write(LogLevel.Debug,
                () =>
                    $"Writing {stream.Uncommitted.Count()} events to stream {stream.StreamId} bucket {stream.Bucket} with commit id {commitId}");
            
            Saved.Mark();

            var events = stream.Uncommitted.Select(writable =>
            {
                IMutating mutated = new Mutating(writable.Event, writable.Descriptor.Headers);
                foreach (var mutate in _mutators)
                {
                    Logger.Write(LogLevel.Debug,
                        () => $"Mutating outgoing event {writable.Event.GetType()} with mutator {mutate.GetType().FullName}");
                    mutated = mutate.MutateOutgoing(mutated);
                }

                // Todo: have some bool that is set true if they modified headers
                if (_mutators.Any())
                    foreach (var header in mutated.Headers)
                        writable.Descriptor.Headers[header.Key] = header.Value;
                return (IFullEvent)new WritableEvent
                {
                    Descriptor = writable.Descriptor,
                    Event = mutated.Message,
                    EventId = UnitOfWork.NextEventId(commitId)
                };
            }).ToList();

            var oobs = stream.Oobs.ToDictionary(x => x.Id, x => x);
            foreach (var oob in stream.PendingOobs)
                oobs[oob.Id] = oob;

            var domainEvents = events.Where(x => x.Descriptor.StreamType == StreamTypes.Domain);
            var oobEvents = events.Where(x => x.Descriptor.StreamType == StreamTypes.OOB);

            if (oobEvents.Any())
            {
                // Check that any published oob events already have a defined channel - if not clear the channel definition cache and throw
                foreach (var group in oobEvents.GroupBy(x => x.Descriptor.Headers[Defaults.OobHeaderKey]))
                {
                    if (!oobs.ContainsKey(group.Key))
                    {
                        Tuple<DateTime, IEnumerable<OobDefinition>> temp = null;
                        OobDefinitionCache.TryRemove(streamName, out temp);
                        throw new InvalidOperationException("Stream attempted to raise an oob event without defining the oob stream first");
                    }
                }
            }
            

            if (domainEvents.Any())
            {
                _cache.Evict(streamName);

                Logger.Write(LogLevel.Debug,
                    () =>
                        $"Event stream [{stream.StreamId}] in bucket [{stream.Bucket}] committing {domainEvents.Count()} events");
                await _store.WriteEvents(streamName, domainEvents, commitHeaders,
                        expectedVersion: stream.CommitVersion)
                    .ConfigureAwait(false);
            }
            // Todo: oob streams need to be reworked to not depend on multiple commits
            //      issue with internal events is basically snapshoting.  But there can be ways around that
            if (stream.PendingOobs.Any())
            {
                Tuple<DateTime, IEnumerable<OobDefinition>> temp = null;
                OobDefinitionCache.TryRemove(streamName, out temp);

                Logger.Write(LogLevel.Debug,
                    () => $"Defining oob on stream [{stream.StreamId}] in bucket [{stream.Bucket}] - definition: {JsonConvert.SerializeObject(oobs.Values)}");
                await _store.WriteMetadata(streamName, custom: new Dictionary<string, string>
                {
                    [OobMetadataKey] = JsonConvert.SerializeObject(oobs.Values)
                }).ConfigureAwait(false);
            }


            if (stream.PendingSnapshot != null)
            {
                Logger.Write(LogLevel.Debug,
                    () => $"Event stream [{stream.StreamId}] in bucket [{stream.Bucket}] committing snapshot");
                await _snapstore.WriteSnapshots<T>(stream.Bucket, stream.StreamId, stream.Parents, stream.StreamVersion, stream.PendingSnapshot, commitHeaders).ConfigureAwait(false);
            }
            if (oobEvents.Any())
            {
                Logger.Write(LogLevel.Debug,
                    () => $"Event stream [{stream.StreamId}] in bucket [{stream.Bucket}] publishing {oobEvents.Count()} out of band events");

                foreach (var group in oobEvents.GroupBy(x => x.Descriptor.Headers[Defaults.OobHeaderKey]))
                {
                    // OOB events of the same stream name don't need to all be written to the same stream
                    // if we parallelize the events into 10 known streams we can take advantage of internal
                    // ES optimizations and ES sharding
                    var vary = _random.Next(10) + 1;
                    var oobstream = $"{streamName}-{group.Key}.{vary}";


                    var definition = oobs[group.Key];
                    if (definition.Transient ?? false)
                        await _publisher.Publish<T>(oobstream, group, commitHeaders).ConfigureAwait(false);
                    else if (definition.DaysToLive.HasValue)
                    {
                        var version = await _store.WriteEvents(oobstream, group, commitHeaders).ConfigureAwait(false);
                        // if new stream, write metadata
                        if (version == (group.Count() - 1))
                            await _store.WriteMetadata(oobstream, maxAge: TimeSpan.FromDays(definition.DaysToLive.Value)).ConfigureAwait(false);
                    }
                    else
                        await _store.WriteEvents(oobstream, group, commitHeaders).ConfigureAwait(false);
                }

            }
        }

        public async Task VerifyVersion<T>(IEventStream stream)
            where T : class, IEventSource
        {
            // New streams dont need verification
            if (stream.CommitVersion == -1) return;
            Logger.Write(LogLevel.Debug, () => $"Stream [{stream.StreamId}] in bucket [{stream.Bucket}] for type {typeof(T).FullName} verifying stream version {stream.CommitVersion}");

            var streamName = _streamGen(typeof(T), StreamTypes.Domain, stream.Bucket, stream.StreamId, stream.Parents);

            var last = await _store.GetEventsBackwards(streamName, count: 1).ConfigureAwait(false);
            if (!last.Any())
                throw new VersionException($"Expected version {stream.CommitVersion} on stream [{streamName}] - but no stream found");
            if (last.First().Descriptor.Version != stream.CommitVersion)
            {
                if (last.First().Descriptor.Version < stream.CommitVersion)
                {
                    Logger.Write(LogLevel.Warn,
                        $"Stream [{streamName}] at the store is version {last.First().Descriptor.Version} - our stream is version {stream.CommitVersion} - which is weird");
                    Logger.Write(LogLevel.Warn, $"Stream [{streamName}] snapshot version is: {stream.Snapshot?.Version} - committed count is: {stream.Committed.Count()} - uncomitted is: {stream.Uncommitted.Count()}");
                }

                _cache.Evict(streamName);

                throw new VersionException(
                    $"Expected version {stream.CommitVersion} on stream [{streamName}] - but read {last.First().Descriptor.Version}");
            }
            Logger.Write(LogLevel.Debug, () => $"Verified version of stream [{stream.StreamId}] in bucket [{stream.Bucket}] for type {typeof(T).FullName}");
        }
        
    }
}
