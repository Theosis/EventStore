// Copyright (c) 2012, Event Store LLP
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
// 
// Redistributions of source code must retain the above copyright notice,
// this list of conditions and the following disclaimer.
// Redistributions in binary form must reproduce the above copyright
// notice, this list of conditions and the following disclaimer in the
// documentation and/or other materials provided with the distribution.
// Neither the name of the Event Store LLP nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
// 

using System;
using System.Collections.Generic;
using System.Security.Principal;
using EventStore.Core.Bus;
using EventStore.Core.Data;
using EventStore.Core.Helpers;
using EventStore.Core.Messages;
using EventStore.Core.Messaging;
using EventStore.Core.Services;
using EventStore.Core.Services.TimerService;
using EventStore.Core.Services.UserManagement;
using EventStore.Core.TransactionLog.LogRecords;
using EventStore.Projections.Core.Messages;

namespace EventStore.Projections.Core.Services.Processing
{
    public class AllStreamsCatalogEventReader : EventReader, IHandle<ClientMessage.ReadStreamEventsForwardCompleted>
    {
        private const int MaxConcurrentMetaStreamReads = 50;

        abstract class OutItem
        {
            protected readonly AllStreamsCatalogEventReader Reader;

            protected OutItem(AllStreamsCatalogEventReader reader)
            {
                Reader = reader;
            }

            public abstract bool IsReady();
            public abstract void Complete();

        }

        private class DeliverEventOutItem : OutItem
        {
            private readonly IODispatcher _ioDispatcher;
            private readonly EventRecord _event;
            private readonly float _progress;
            private bool _ready;
            private EventRecord _metadata;
            private string _streamId;

            public DeliverEventOutItem(
                IODispatcher ioDispatcher, AllStreamsCatalogEventReader reader, EventRecord @event, float progress)
                : base(reader)
            {
                _ioDispatcher = ioDispatcher;
                _event = @event;
                _progress = progress;
            }

            public override bool IsReady()
            {
                return _ready;
            }

            public override void Complete()
            {
                Reader._maxReadCount--;
                Reader.DeliverEvent(_streamId, _event, _metadata, _progress);
            }

            public void BeginRead()
            {
                _streamId = SystemEventTypes.StreamReferenceEventToStreamId(_event.EventType, _event.Data);
                var requestId = _ioDispatcher.ReadBackward(
                    SystemStreams.MetastreamOf(_streamId), -1, 1, false, Reader.ReadAs, ReadCompleted);
                if (requestId != Guid.Empty)
                    Reader._activeReads.Add(requestId);
            }

            private void ReadCompleted(ClientMessage.ReadStreamEventsBackwardCompleted completed)
            {
                Reader._activeReads.Remove(completed.CorrelationId);
                switch (completed.Result)
                {
                    case ReadStreamResult.NoStream:
                    case ReadStreamResult.AccessDenied:
                    case ReadStreamResult.StreamDeleted:
                        _ready = true;
                        Reader.MetaStreamReadCompleted();
                        break;
                    case ReadStreamResult.Success:
                        _ready = true;
                        if (completed.Events.Length > 0)
                            _metadata = completed.Events[0].Event;
                        Reader.MetaStreamReadCompleted();
                        break;
                }
            }
        }

        private void MetaStreamReadCompleted()
        {
            ProcessOutQueue();
            PauseOrContinueProcessing(delay: false);
        }

        private class DeliverSafePositionToJoinoutItem : OutItem
        {
            private readonly long? _position;

            public DeliverSafePositionToJoinoutItem(AllStreamsCatalogEventReader reader, long? position)
                : base(reader)
            {
                _position = position;
            }

            public override bool IsReady()
            {
                return true;
            }

            public override void Complete()
            {
                Reader.DeliverSafeJoinPosition(_position);
            }
        }

        private abstract class SendOutItem : OutItem
        {
            private readonly Message _message;

            protected SendOutItem(AllStreamsCatalogEventReader reader, Message message)
                : base(reader)
            {
                _message = message;
            }

            public override bool IsReady()
            {
                return true;
            }

            public override void Complete()
            {
                Reader._publisher.Publish(_message);
            }
        }

        private class SendEofOutItem : SendOutItem
        {
            public SendEofOutItem(AllStreamsCatalogEventReader reader, bool maxEventsReached)
                : base(
                    reader,
                    new ReaderSubscriptionMessage.EventReaderEof(
                        reader.EventReaderCorrelationId, maxEventsReached: maxEventsReached))
            {
            }

            public override void Complete()
            {
                base.Complete();
                Reader.Dispose();
            }
        }

        private class SendIdleOutItem : SendOutItem
        {
            public SendIdleOutItem(AllStreamsCatalogEventReader reader)
                : base(
                    reader,
                    new ReaderSubscriptionMessage.EventReaderIdle(
                        reader.EventReaderCorrelationId, reader._timeProvider.Now))
            {
            }
        }

        private class SendOutNotAuthorizedOutItem : SendOutItem
        {
            public SendOutNotAuthorizedOutItem(AllStreamsCatalogEventReader reader)
                : base(reader, new ReaderSubscriptionMessage.EventReaderNotAuthorized(reader.EventReaderCorrelationId))
            {
            }

            public override void Complete()
            {
                if (Reader._disposed)
                    return;
                base.Complete();
                Reader.Dispose();
            }
        }

        private int _fromSequenceNumber;
        private readonly ITimeProvider _timeProvider;
        private readonly bool _resolveLinkTos;
        private readonly Queue<OutItem> _queue = new Queue<OutItem>();

        private bool _eventsRequested;
        private readonly HashSet<Guid> _activeReads = new HashSet<Guid>();
        private int _maxReadCount = 111;
        private int _enqueuedEventDeliveries;
        private readonly Queue<DeliverEventOutItem> _readMetaStreamItemsQueue = new Queue<DeliverEventOutItem>();
        private int _metaStreamReadsCount;
        private readonly IODispatcher _ioDispatcher;

        public AllStreamsCatalogEventReader(
            IODispatcher ioDispatcher, IPublisher publisher, Guid eventReaderCorrelationId, IPrincipal readAs,
            int fromSequenceNumber, ITimeProvider timeProvider, bool resolveLinkTos, bool stopOnEof = false,
            int? stopAfterNEvents = null)
            : base(ioDispatcher, publisher, eventReaderCorrelationId, readAs, stopOnEof, stopAfterNEvents)
        {
            if (fromSequenceNumber < 0) throw new ArgumentException("fromSequenceNumber");
            _ioDispatcher = ioDispatcher;
            _fromSequenceNumber = fromSequenceNumber;
            _timeProvider = timeProvider;
            _resolveLinkTos = resolveLinkTos;
        }

        protected override bool AreEventsRequested()
        {
            return _eventsRequested || _activeReads.Count > 0;
        }

        public void Handle(ClientMessage.ReadStreamEventsForwardCompleted message)
        {
            if (_disposed)
                return;
            if (!_eventsRequested)
                throw new InvalidOperationException("Read events has not been requested");
            if (Paused)
                throw new InvalidOperationException("Paused");
            _eventsRequested = false;
            NotifyIfStarting(message.TfLastCommitPosition);
            switch (message.Result)
            {
                case ReadStreamResult.NoStream:
                    EnqueueDeliverSafeJoinPosition(GetLastCommitPositionFrom(message)); // allow joining heading distribution
                    PauseOrContinueProcessing(delay: true);
                    EnqueueIdle();
                    EnqueueEof();
                    break;
                case ReadStreamResult.Success:
                    var oldFromSequenceNumber = _fromSequenceNumber;
                    _fromSequenceNumber = message.NextEventNumber;
                    var eof = message.Events.Length == 0;
                    var willDispose = eof && _stopOnEof;

                    if (eof)
                    {
                        // the end
                        EnqueueDeliverSafeJoinPosition(GetLastCommitPositionFrom(message));
                        EnqueueIdle();
                        EnqueueEof();
                    }
                    else
                    {
                        for (int index = 0; index < message.Events.Length; index++)
                        {
                            var @event = message.Events[index].Event;
                            var @link = message.Events[index].Link;
                            EnqueueDeliverEvent(
                                @event, @link, 100.0f*(link ?? @event).EventNumber/message.LastEventNumber,
                                ref oldFromSequenceNumber);
                            if (CheckEnough())
                                return;
                        }
                    }
                    //NOTE: unlike other readers all stream reader requests new reads after processing
                    // read results.  This is due to other potential reads generated by the results itself. 
                    // i.e. reading metastreams for each stream mentioned
                    if (!willDispose)
                    {
                        PauseOrContinueProcessing(delay: eof && _readMetaStreamItemsQueue.Count == 0);
                    }

                    break;
                case ReadStreamResult.AccessDenied:
                    EnqueueNotAuthorized();
                    return;
                default:
                    throw new NotSupportedException(
                        string.Format("ReadEvents result code was not recognized. Code: {0}", message.Result));
            }
        }

        private void EnqueueNotAuthorized()
        {
            Enqueue(new SendOutNotAuthorizedOutItem(this));
        }

        private void EnqueueEof(bool maxEventsReached = false)
        {
            if (_stopOnEof || _stopAfterNEvents != null)
                Enqueue(new SendEofOutItem(this, maxEventsReached));
        }

        private void EnqueueIdle()
        {
            Enqueue(new SendIdleOutItem(this));
        }

        private void Enqueue(OutItem outItem)
        {
            _queue.Enqueue(outItem);
            ProcessOutQueue();
        }

        private void ProcessOutQueue()
        {
            while (_queue.Count > 0 && _queue.Peek().IsReady())
                _queue.Dequeue().Complete();
        }

        private bool CheckEnough()
        {
            if (_stopAfterNEvents != null && _enqueuedEventDeliveries >= _stopAfterNEvents)
            {
                EnqueueEof(maxEventsReached: true);
                return true;
            }
            return false;
        }

        protected override void RequestEvents(bool delay)
        {
            if (_disposed) throw new InvalidOperationException("Disposed");
            if (PauseRequested || Paused)
                throw new InvalidOperationException("Paused or pause requested");

            while (_readMetaStreamItemsQueue.Count > 0 && _metaStreamReadsCount < MaxConcurrentMetaStreamReads)
            {
                var item = _readMetaStreamItemsQueue.Dequeue();
                item.BeginRead();
                _metaStreamReadsCount++;
            }

            //NOTE: we do not throw exception if read operation is already in progress as we have
            // a mix of read operations and allow multiple in progress. However, only one seauential read
            // from the catalog is permitted at any time
            if (_eventsRequested)
                return;
            _eventsRequested = true;

            if (_readMetaStreamItemsQueue.Count < MaxConcurrentMetaStreamReads)
            {

                var readEventsForward = CreateReadEventsMessage();
                if (delay)
                    _publisher.Publish(
                        TimerMessage.Schedule.Create(
                            TimeSpan.FromMilliseconds(250), new PublishEnvelope(_publisher, crossThread: true),
                            readEventsForward));
                else
                    _publisher.Publish(readEventsForward);
            }
        }

        private Message CreateReadEventsMessage()
        {
            return new ClientMessage.ReadStreamEventsForward(
                Guid.NewGuid(), EventReaderCorrelationId, new SendToThisEnvelope(this), SystemStreams.StreamsStream,
                _fromSequenceNumber, _maxReadCount, resolveLinkTos: false, requireMaster: false,
                validationStreamVersion: null, user: ReadAs);
        }

        private void DeliverSafeJoinPosition(long? safeJoinPosition)
        {
            _publisher.Publish(
                new ReaderSubscriptionMessage.CommittedEventDistributed(
                    EventReaderCorrelationId, null, safeJoinPosition, 100.0f, source: GetType()));
        }

        private void EnqueueDeliverSafeJoinPosition(long? safeJoinPosition)
        {
            if (_stopOnEof || _stopAfterNEvents != null || safeJoinPosition == null || safeJoinPosition == -1)
                return; //TODO: this should not happen, but StorageReader does not return it now
            Enqueue(new DeliverSafePositionToJoinoutItem(this, safeJoinPosition));
        }

        private void EnqueueDeliverEvent(EventRecord @event, EventRecord link, float progress, ref int sequenceNumber)
        {
            if (link != null) throw new Exception();
            _enqueuedEventDeliveries++;
            var positionEvent = @event;
            if (positionEvent.EventNumber != sequenceNumber)
                throw new InvalidOperationException(
                    string.Format(
                        "Event number {0} was expected in the stream {1}, but event number {2} was received",
                        sequenceNumber, SystemStreams.StreamsStream, positionEvent.EventNumber));
            sequenceNumber = positionEvent.EventNumber + 1;

            var item = new DeliverEventOutItem(_ioDispatcher, this, @event, @progress);
            EnqueueReadMetaStreamItem(item);
            Enqueue(item);
        }

        private void EnqueueReadMetaStreamItem(DeliverEventOutItem item)
        {
            _readMetaStreamItemsQueue.Enqueue(item);
        }

        private void DeliverEvent(string streamId, EventRecord link, EventRecord streamMetadata, float progress)
        {

            var resolvedLinkTo = true;
            byte[] streamMetadataData = streamMetadata != null ? streamMetadata.Data : null;

            byte[] positionMetadataData = link.Metadata;

            var data = new ResolvedEvent(
                link.EventStreamId, link.EventNumber, streamId, -1, resolvedLinkTo, new TFPos(-1, -1),
                new TFPos(-1, link.LogPosition), Guid.Empty, "", true, null, null, positionMetadataData,
                streamMetadataData, link.TimeStamp);

            _publisher.Publish(
                //TODO: publish both link and event data
                new ReaderSubscriptionMessage.CommittedEventDistributed(
                    EventReaderCorrelationId, data, _stopOnEof ? (long?) null : link.LogPosition, progress,
                    source: GetType()));
        }
    }
}
